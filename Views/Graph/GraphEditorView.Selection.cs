using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels.Graph;
using System.Text.Json;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls.Templates;
using Avalonia.Threading;

namespace LiberaFx.Views.Graph;

public partial class GraphEditorView : UserControl
{
    private void OnGraphCanvasDragOver(object? sender, DragEventArgs e)
    {
        if (!OwnsInput(e.Source)) return;
        e.DragEffects = TryGetNodeKind(e, out _) || TryGetGroupId(e, out _)
            ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnGraphCanvasDrop(object? sender, DragEventArgs e)
    {
        if (!OwnsInput(e.Source)) return;
        if (DataContext is not GraphWorkspaceViewModel graph) return;

        var position = ToGraphPoint(e.GetPosition(GraphCanvas));
        try
        {
            if (FindHitNode(e.Source)?.DataContext is GraphNodeViewModel { ChildGraph: not null } owner)
            {
                graph = graph.OpenInlineGraph(owner);
                position = new Point(80, 80);
            }
            if (TryGetGroupId(e, out var assetId)) graph.CreateGroupAssetNode(assetId, position.X, position.Y);
            else if (TryGetNodeKind(e, out var kind)) graph.CreateNode(kind, position.X, position.Y,
                e.DataTransfer.TryGetText()?.StartsWith(NodeCatalogEntryViewModel.PolymorphicPrefix, StringComparison.Ordinal) == true);
            else return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   InvalidOperationException or ArgumentException)
        {
            graph.ReportStatus(ex.Message);
        }
        e.Handled = true;
    }

    private bool TryGetNodeKind(DragEventArgs e, out GraphNodeKind kind)
    {
        var kindName = e.DataTransfer.TryGetText();
        if (kindName?.StartsWith(NodeCatalogEntryViewModel.PolymorphicPrefix, StringComparison.Ordinal) == true)
            kindName = kindName[NodeCatalogEntryViewModel.PolymorphicPrefix.Length..];
        if (GraphNodeDefinition.TryFind(kindName, out var definition,
            (DataContext as GraphWorkspaceViewModel)?.AvailableNodeDefinitions ?? GraphNodeDefinition.Core))
        {
            kind = definition!.Kind;
            return true;
        }

        kind = default;
        return false;
    }

    private static bool TryGetGroupId(DragEventArgs e, out Guid id)
    {
        var text = e.DataTransfer.TryGetText();
        id = Guid.Empty;
        return text is not null && text.StartsWith(NodeCatalogEntryViewModel.GroupPrefix, StringComparison.Ordinal) &&
               Guid.TryParse(text[NodeCatalogEntryViewModel.GroupPrefix.Length..], out id) && id != Guid.Empty;
    }

    private void OnBackgroundPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(GraphCanvas).Properties.IsLeftButtonPressed &&
            sender is Control background && DataContext is GraphWorkspaceViewModel graph)
        {
            Focus();
            var clickPoint = ToGraphPoint(e.GetPosition(GraphCanvas));
            if (e.ClickCount >= 2 &&
                GraphSceneLayer.HitTestConnection(graph, clickPoint, WireHitTolerance()) is { } hitConnection)
            {
                graph.CancelConnection();
                graph.RunAuthoringAction(() => graph.InsertRerouteOnConnection(hitConnection, clickPoint.X, clickPoint.Y));
                e.Handled = true;
                return;
            }
            if (GraphSceneLayer.HitTestConnection(graph, clickPoint, WireHitTolerance()) is { } wire)
            {
                graph.CancelConnection();
                graph.SelectConnection(wire, e.KeyModifiers.HasFlag(KeyModifiers.Control));
                e.Handled = true;
                return;
            }
            graph.CancelConnection();
            _isSelecting = true;
            _selectionStart = e.GetPosition(GraphCanvas);
            _selectionBefore = e.KeyModifiers.HasFlag(KeyModifiers.Control)
                ? graph.SelectedNodes.ToArray()
                : [];
            _wireSelectionBefore = e.KeyModifiers.HasFlag(KeyModifiers.Control)
                ? graph.SelectedConnections.ToArray()
                : [];
            _pastePosition = clickPoint;
            e.Pointer.Capture(background);
            e.Handled = true;
        }
    }

    private void OnBackgroundPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isSelecting)
        {
            return;
        }

        var rect = MakeSelectionRect(_selectionStart, e.GetPosition(GraphCanvas));
        SelectionBox.IsVisible = rect.Width >= 3 || rect.Height >= 3;
        Canvas.SetLeft(SelectionBox, rect.X);
        Canvas.SetTop(SelectionBox, rect.Y);
        SelectionBox.Width = rect.Width;
        SelectionBox.Height = rect.Height;
        e.Handled = true;
    }

    private void OnBackgroundPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isSelecting || DataContext is not GraphWorkspaceViewModel graph)
        {
            return;
        }

        var rect = MakeSelectionRect(_selectionStart, e.GetPosition(GraphCanvas));
        var selected = _selectionBefore.AsEnumerable();
        var selectedWires = _wireSelectionBefore.AsEnumerable();
        if (rect.Width >= 3 || rect.Height >= 3)
        {
            var matches = GraphCanvas.GetVisualDescendants().OfType<GraphNodeView>()
                .Where(view => ReferenceEquals(view.FindAncestorOfType<GraphEditorView>(), this) &&
                               view.DataContext is GraphNodeViewModel &&
                               view.TranslatePoint(default, GraphCanvas) is { } origin &&
                               rect.Intersects(new Rect(origin, view.Bounds.Size)))
                .Select(view => (GraphNodeViewModel)view.DataContext!);
            selected = selected.Concat(matches);
            var topLeft = ToGraphPoint(rect.TopLeft);
            var bottomRight = ToGraphPoint(rect.BottomRight);
            var graphRect = MakeSelectionRect(topLeft, bottomRight);
            selectedWires = selectedWires.Concat(graph.Connections.Where(edge =>
                GraphSceneLayer.IntersectsConnection(edge, graphRect)));
        }

        _isSelecting = false;
        SelectionBox.IsVisible = false;
        e.Pointer.Capture(null);
        graph.SetSelection(selected.ToArray(), selectedWires.ToArray());
        e.Handled = true;
    }

    private void OnBackgroundPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _isSelecting = false;
        SelectionBox.IsVisible = false;
    }

    private static Rect MakeSelectionRect(Point start, Point end) =>
        new(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
            Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));

    private void OnGraphCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!OwnsInput(e.Source)) return;
        if (!e.GetCurrentPoint(GraphCanvas).Properties.IsRightButtonPressed ||
            DataContext is not GraphWorkspaceViewModel graph)
        {
            return;
        }

        _pastePosition = ToGraphPoint(e.GetPosition(GraphCanvas));
        var hitNode = FindHitNode(e.Source);
        var blankCanvasOrComment = hitNode is null || hitNode.IsBlankCommentArea(e);
        _contextMenuConnection = blankCanvasOrComment ? GraphSceneLayer.HitTestConnection(graph, _pastePosition, WireHitTolerance()) : null;
        NewGraphGroupMenu.IsVisible = hitNode is null && graph.CanCreateNode(GraphNodeKind.GraphGroup, out string? _);
        NewSpawnableGroupMenu.IsVisible = hitNode is null;
        AddRerouteMenu.IsVisible = _contextMenuConnection is not null || graph.SelectedConnections.Count > 0;
        PopulateAddNodeMenu(graph);
        if (hitNode?.DataContext is GraphNodeViewModel node && !node.IsSelected && graph.SelectedNodes.Count < 2)
        {
            graph.SelectNode(node);
        }
    }

    private GraphNodeView? FindHitNode(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<GraphNodeView>()
            .FirstOrDefault(view => ReferenceEquals(view.FindAncestorOfType<GraphEditorView>(), this));

    internal GraphNodeViewModel? FindDropContainer(Point point, IReadOnlyCollection<GraphNodeViewModel> dragged)
    {
        return this.GetVisualDescendants().OfType<GraphNodeView>()
            .Where(view => view.IsEffectivelyVisible && ReferenceEquals(view.FindAncestorOfType<GraphEditorView>(), this) &&
                view.DataContext is GraphNodeViewModel { ChildGraph: not null } node && !dragged.Contains(node))
            .Reverse().FirstOrDefault(view => view.TranslatePoint(default, this) is { } origin &&
                new Rect(origin, view.Bounds.Size).Contains(point))?.DataContext as GraphNodeViewModel;
    }

}
