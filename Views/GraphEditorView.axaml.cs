using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using LiberaFx.Models.Graph;
using LiberaFx.ViewModels;
using LiberaFx.Services;
using System.Text.Json;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Threading;

namespace LiberaFx.Views;

public partial class GraphEditorView : UserControl
{
    private bool _isPanning;
    private Point _panStart;
    private Vector _panOffset;
    private double _zoom = 1;
    private bool _isSelecting;
    private Point _selectionStart;
    private GraphNodeViewModel[] _selectionBefore = [];
    private Point _pastePosition = new(80, 80);
    private Point? _lastGraphPointer;
    private Point _nodeSearchPosition;
    private GraphWorkspaceViewModel? _observedGraph;
    private readonly HashSet<GraphNodeViewModel> _observedNodes = [];
    private const string ClipboardPrefix = "LIBERA-FX-GRAPH-1\n";
    public GraphEditorView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_observedGraph is not null) _observedGraph.Nodes.CollectionChanged -= OnNodesChanged;
        foreach (var node in _observedNodes) node.PropertyChanged -= OnNodePositionChanged;
        _observedNodes.Clear();
        _observedGraph = DataContext as GraphWorkspaceViewModel;
        if (_observedGraph is not null) _observedGraph.Nodes.CollectionChanged += OnNodesChanged;
        SynchronizeNodes();
        base.OnDataContextChanged(e);
    }

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e) => SynchronizeNodes();

    private void SynchronizeNodes()
    {
        var current = _observedGraph?.Nodes.ToHashSet() ?? [];
        foreach (var node in _observedNodes.Except(current).ToArray())
        {
            node.PropertyChanged -= OnNodePositionChanged;
            _observedNodes.Remove(node);
        }
        foreach (var node in current.Except(_observedNodes))
        {
            node.PropertyChanged += OnNodePositionChanged;
            _observedNodes.Add(node);
        }
        UpdateCanvasExtent();
    }

    private void OnNodePositionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GraphNodeViewModel.DisplayX) or nameof(GraphNodeViewModel.DisplayY))
            UpdateCanvasExtent();
    }

    private void UpdateCanvasExtent()
    {
        if (GraphCanvas is null) return;
        GraphCanvas.Width = Math.Clamp(_observedNodes.Select(n => n.DisplayX + GraphNodeViewModel.NodeWidth + 200).DefaultIfEmpty(1600).Max(), 1600, 10_000_000);
        GraphCanvas.Height = Math.Clamp(_observedNodes.Select(n => n.DisplayY + 300).DefaultIfEmpty(1000).Max(), 1000, 10_000_000);
    }

    private void OnGraphCanvasDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = TryGetNodeKind(e, out _) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnGraphCanvasDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not GraphWorkspaceViewModel graph ||
            !TryGetNodeKind(e, out var kind))
        {
            return;
        }

        var position = e.GetPosition(GraphCanvas);
        try { graph.CreateNode(kind, position.X, position.Y); }
        catch (InvalidOperationException ex) { graph.ReportStatus(ex.Message); }
        e.Handled = true;
    }

    private static bool TryGetNodeKind(DragEventArgs e, out GraphNodeKind kind)
    {
        var kindName = e.DataTransfer.TryGetText();
        if (GraphNodeDefinition.TryFind(kindName, out var definition))
        {
            kind = definition!.Kind;
            return true;
        }
        kind = default;
        return false;
    }

    private void OnBackgroundPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(GraphCanvas).Properties.IsLeftButtonPressed &&
            sender is Control background && DataContext is GraphWorkspaceViewModel graph)
        {
            Focus();
            graph.CancelConnection();
            _isSelecting = true;
            _selectionStart = e.GetPosition(GraphCanvas);
            _selectionBefore = e.KeyModifiers.HasFlag(KeyModifiers.Control)
                ? graph.SelectedNodes.ToArray()
                : [];
            _pastePosition = _selectionStart;
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
        if (rect.Width >= 3 || rect.Height >= 3)
        {
            var matches = GraphCanvas.GetVisualDescendants().OfType<GraphNodeView>()
                .Where(view => view.DataContext is GraphNodeViewModel &&
                               view.TranslatePoint(default, GraphCanvas) is { } origin &&
                               rect.Intersects(new Rect(origin, view.Bounds.Size)))
                .Select(view => (GraphNodeViewModel)view.DataContext!);
            selected = selected.Concat(matches);
        }

        _isSelecting = false;
        SelectionBox.IsVisible = false;
        e.Pointer.Capture(null);
        graph.SetSelection(selected.ToArray());
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
        if (!e.GetCurrentPoint(GraphCanvas).Properties.IsRightButtonPressed ||
            DataContext is not GraphWorkspaceViewModel graph)
        {
            return;
        }

        _pastePosition = e.GetPosition(GraphCanvas);
        if (e.Source is Visual visual &&
            visual.GetSelfAndVisualAncestors().OfType<GraphNodeView>().FirstOrDefault()?.DataContext
                is GraphNodeViewModel node && !node.IsSelected)
        {
            graph.SelectNode(node);
        }
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e) => await CopySelectionAsync(false);

    private async void OnCutClick(object? sender, RoutedEventArgs e) => await CopySelectionAsync(true);

    private async void OnPasteClick(object? sender, RoutedEventArgs e) => await PasteAsync();
    private void OnAddNodeClick(object? sender, RoutedEventArgs e) => ShowNodeSearch(_pastePosition);
    private void OnBreadcrumbClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int depth } && DataContext is GraphWorkspaceViewModel graph)
            graph.NavigateToGraph(depth);
    }

    private void OnOpenGroupClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GraphWorkspaceViewModel { SelectedNode: { } node } graph && !graph.EnterGraph(node))
            graph.ReportStatus("Select a Graph Group to open it.");
    }

    private async void OnGraphKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not GraphWorkspaceViewModel graph)
        {
            return;
        }

        var shortcuts = ShortcutService.Shared;
        if (shortcuts.Matches("graph.copy", e))
        {
            e.Handled = true;
            await CopySelectionAsync(false);
        }
        else if (shortcuts.Matches("graph.cut", e))
        {
            e.Handled = true;
            await CopySelectionAsync(true);
        }
        else if (shortcuts.Matches("graph.paste", e))
        {
            e.Handled = true;
            await PasteAsync();
        }
        else if (shortcuts.Matches("graph.all", e))
        {
            graph.SetSelection(graph.Nodes);
            e.Handled = true;
        }
        else if (shortcuts.Matches("graph.delete", e))
        {
            graph.DeleteNodes(graph.SelectedNodes.ToArray());
            e.Handled = true;
        }
        else if (shortcuts.Matches("graph.search", e))
        {
            var center = new Point(
                (GraphScrollViewer.Offset.X + GraphScrollViewer.Viewport.Width / 2) / _zoom,
                (GraphScrollViewer.Offset.Y + GraphScrollViewer.Viewport.Height / 2) / _zoom);
            ShowNodeSearch(_lastGraphPointer ?? center);
            e.Handled = true;
        }
        else if (shortcuts.Matches("graph.frame", e))
        {
            FrameSelection();
            e.Handled = true;
        }
        else if (shortcuts.Matches("graph.group", e))
        {
            graph.GroupSelection();
            e.Handled = true;
        }
        else if (shortcuts.Matches("graph.zoomIn", e) || shortcuts.Matches("graph.zoomOut", e))
        {
            ZoomBy(shortcuts.Matches("graph.zoomIn", e) ? 1.25 : 1 / 1.25);
            e.Handled = true;
        }
    }

    private void ShowNodeSearch(Point position)
    {
        _nodeSearchPosition = new Point(Math.Max(0, position.X), Math.Max(0, position.Y));
        NodeSearchBox.Text = string.Empty;
        RefreshNodeSearch();
        NodeSearchPopup.IsOpen = true;
        Dispatcher.UIThread.Post(() => NodeSearchBox.Focus());
    }

    private void OnNodeSearchTextChanged(object? sender, TextChangedEventArgs e) => RefreshNodeSearch();

    private void RefreshNodeSearch()
    {
        if (NodeSearchResults is null || NodeSearchBox is null) return;
        var search = NodeSearchBox.Text?.Trim() ?? string.Empty;
        var entries = GraphNodeDefinition.All
            .Where(definition => definition.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                 definition.QualifiedName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                 definition.Group.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(definition => new NodeCatalogEntryViewModel(definition)).ToArray();
        NodeSearchResults.ItemsSource = entries;
        NodeSearchResults.SelectedIndex = entries.Length > 0 ? 0 : -1;
    }

    private void OnNodeSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            NodeSearchPopup.IsOpen = false;
            Focus();
        }
        else if (e.Key == Key.Enter) AddSearchSelection();
        else if (e.Key is Key.Down or Key.Up && NodeSearchResults.ItemCount > 0)
        {
            var delta = e.Key == Key.Down ? 1 : -1;
            NodeSearchResults.SelectedIndex = Math.Clamp(NodeSearchResults.SelectedIndex + delta, 0, NodeSearchResults.ItemCount - 1);
            NodeSearchResults.ScrollIntoView(NodeSearchResults.SelectedIndex);
        }
        else return;
        e.Handled = true;
    }

    private void OnNodeSearchDoubleTapped(object? sender, TappedEventArgs e) => AddSearchSelection();

    private void AddSearchSelection()
    {
        if (DataContext is not GraphWorkspaceViewModel graph ||
            NodeSearchResults.SelectedItem is not NodeCatalogEntryViewModel entry ||
            !GraphNodeDefinition.TryFind(entry.KindName, out var definition)) return;
        try { graph.CreateNode(definition!.Kind, _nodeSearchPosition.X, _nodeSearchPosition.Y); }
        catch (InvalidOperationException ex) { graph.ReportStatus(ex.Message); }
        NodeSearchPopup.IsOpen = false;
        Focus();
    }

    private async Task CopySelectionAsync(bool cut)
    {
        if (DataContext is not GraphWorkspaceViewModel graph || graph.SelectionCount == 0)
        {
            return;
        }

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard
                ?? throw new InvalidOperationException("The clipboard is unavailable.");
            var nodes = graph.SelectedNodes.ToArray();
            var revision = graph.History.CurrentNodeId;
            var text = ClipboardPrefix + JsonSerializer.Serialize(graph.CreateSelectionSnapshot());
            await clipboard.SetTextAsync(text);
            if (cut)
            {
                if (graph.History.CurrentNodeId != revision || nodes.Any(node => !graph.Nodes.Contains(node)))
                {
                    graph.ReportStatus("The graph changed while copying; cut was cancelled.");
                    return;
                }

                graph.DeleteNodes(nodes);
            }
            graph.ReportStatus(cut ? "Nodes cut." : "Nodes copied.");
        }
        catch (Exception ex)
        {
            graph.ReportStatus($"Clipboard operation failed: {ex.Message}");
        }
    }

    private async Task PasteAsync()
    {
        if (DataContext is not GraphWorkspaceViewModel graph)
        {
            return;
        }

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard
                ?? throw new InvalidOperationException("The clipboard is unavailable.");
            var text = await clipboard.TryGetTextAsync();
            if (text is null || text.Length > 1_000_000 ||
                !text.StartsWith(ClipboardPrefix, StringComparison.Ordinal))
            {
                graph.ReportStatus("The clipboard does not contain LIBERA FX nodes.");
                return;
            }

            var snapshot = JsonSerializer.Deserialize<GraphSnapshot>(text[ClipboardPrefix.Length..])
                ?? throw new InvalidDataException("The clipboard graph is empty.");
            graph.PasteSnapshot(snapshot, Math.Max(0, _pastePosition.X), Math.Max(0, _pastePosition.Y));
            _pastePosition = new Point(_pastePosition.X + 24, _pastePosition.Y + 24);
        }
        catch (Exception ex)
        {
            graph.ReportStatus($"Could not paste nodes: {ex.Message}");
        }
    }

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(GraphScrollViewer).Properties.IsMiddleButtonPressed)
        {
            return;
        }

        _isPanning = true;
        _panStart = e.GetPosition(GraphScrollViewer);
        _panOffset = GraphScrollViewer.Offset;
        e.Pointer.Capture(GraphScrollViewer);
        e.Handled = true;
    }

    private void OnViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        var graphPoint = e.GetPosition(GraphCanvas);
        if (graphPoint.X >= 0 && graphPoint.Y >= 0 &&
            graphPoint.X <= GraphCanvas.Width && graphPoint.Y <= GraphCanvas.Height)
            _lastGraphPointer = graphPoint;
        if (!_isPanning)
        {
            return;
        }

        var position = e.GetPosition(GraphScrollViewer);
        GraphScrollViewer.Offset = new Vector(
            _panOffset.X + _panStart.X - position.X,
            _panOffset.Y + _panStart.Y - position.Y);
        e.Handled = true;
    }

    private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void OnViewportPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _isPanning = false;
    }

    private void OnViewportPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0)
        {
            return;
        }

        SetZoom(_zoom * Math.Pow(1.15, e.Delta.Y), e.GetPosition(GraphScrollViewer));
        e.Handled = true;
    }

    private void OnResetZoomClick(object? sender, RoutedEventArgs e)
    {
        SetZoom(1, new Point(GraphScrollViewer.Viewport.Width / 2, GraphScrollViewer.Viewport.Height / 2));
    }

    private void OnZoomInClick(object? sender, RoutedEventArgs e) => ZoomBy(1.25);
    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => ZoomBy(1 / 1.25);
    private void OnFrameSelectionClick(object? sender, RoutedEventArgs e) => FrameSelection();
    private void OnGroupSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GraphWorkspaceViewModel graph) graph.GroupSelection();
    }

    private void ZoomBy(double factor) =>
        SetZoom(_zoom * factor, new Point(GraphScrollViewer.Viewport.Width / 2, GraphScrollViewer.Viewport.Height / 2));

    private void FrameSelection()
    {
        if (DataContext is not GraphWorkspaceViewModel graph || graph.SelectionCount == 0) return;
        var selected = graph.SelectedNodes.ToHashSet();
        var views = GraphCanvas.GetVisualDescendants().OfType<GraphNodeView>()
            .Where(view => view.DataContext is GraphNodeViewModel node && selected.Contains(node)).ToArray();
        var left = selected.Min(node => node.DisplayX);
        var top = selected.Min(node => node.DisplayY);
        var right = selected.Max(node => node.DisplayX + GraphNodeViewModel.NodeWidth);
        var bottom = selected.Max(node => node.DisplayY +
            (views.FirstOrDefault(view => ReferenceEquals(view.DataContext, node))?.Bounds.Height ?? 80));
        var viewport = GraphScrollViewer.Viewport;
        if (viewport.Width <= 0 || viewport.Height <= 0) return;
        var zoom = Math.Min(Math.Max(1, viewport.Width - 80) / Math.Max(1, right - left),
                            Math.Max(1, viewport.Height - 80) / Math.Max(1, bottom - top));
        SetZoom(zoom, new Point(viewport.Width / 2, viewport.Height / 2));
        GraphScrollViewer.Offset = new Vector((left + right) / 2 * _zoom - viewport.Width / 2,
                                               (top + bottom) / 2 * _zoom - viewport.Height / 2);
    }

    private void SetZoom(double zoom, Point anchor)
    {
        zoom = Math.Clamp(zoom, 0.05, 4);
        var oldOffset = GraphScrollViewer.Offset;
        var graphX = (oldOffset.X + anchor.X) / _zoom;
        var graphY = (oldOffset.Y + anchor.Y) / _zoom;
        _zoom = zoom;
        ZoomHost.LayoutTransform = new ScaleTransform(zoom, zoom);
        ZoomHost.UpdateLayout();
        GraphScrollViewer.UpdateLayout();
        GraphScrollViewer.Offset = new Vector(graphX * zoom - anchor.X, graphY * zoom - anchor.Y);
        ZoomButton.Content = $"{zoom:P0}";
    }
}
