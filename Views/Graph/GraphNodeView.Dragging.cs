using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using System.ComponentModel;
using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.ViewModels.Graph;
using LiberaFx.Views.Controls;

namespace LiberaFx.Views.Graph;

public partial class GraphNodeView : UserControl
{
    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source &&
            (ReferenceEquals(source, DetailsToggle) || source.GetVisualAncestors().Contains(DetailsToggle) ||
             source.GetSelfAndVisualAncestors().OfType<Button>().Any()))
            return;

        if (DataContext is not GraphNodeViewModel node ||
            sender is not Control header ||
            !e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount >= 2 && node.IsGpuParticle)
        {
            node.Workspace.OpenGpuEditor(node);
            e.Handled = true;
            return;
        }
        if (e.ClickCount >= 2 && ProceduralResourceSchema.IsMetadata(node.Definition.Kind))
        {
            node.Workspace.OpenProceduralEditor(node);
            e.Handled = true;
            return;
        }
        if (e.ClickCount >= 2 && node.IsGraphGroup && e.KeyModifiers == KeyModifiers.None)
        {
            node.Workspace.EnterGraph(node);
            e.Handled = true;
            return;
        }

        _dragEditor = this.FindAncestorOfType<GraphEditorView>();
        if (_dragEditor is null)
        {
            return;
        }

        this.FindAncestorOfType<GraphEditorView>()?.Focus();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            node.Workspace.ToggleSelection(node);
            e.Handled = true;
            return;
        }

        _dragNode = node;
        _dragNodes = node.IsSelected
            ? node.Workspace.GetMoveTargets(node.Workspace.SelectedNodes)
                .Select(selected => (selected, selected.X, selected.Y)).ToArray()
            : [(node, node.X, node.Y)];
        _pointerStart = e.GetPosition(_dragEditor);
        e.Pointer.Capture(header);
        e.Handled = true;
        if (!node.IsSelected)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!node.IsSelected && node.Workspace.Nodes.Contains(node))
                    node.SelectCommand.Execute(null);
            }, DispatcherPriority.Background);
        }
    }

    private void OnNodePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled) return;
        if (e.Source is Visual input && input.GetSelfAndVisualAncestors().OfType<ParameterEditorHost>().Any())
            return;
        if (e.Source is Visual source &&
            source.GetSelfAndVisualAncestors().OfType<GraphEditorView>().FirstOrDefault() is { } sourceEditor &&
            !ReferenceEquals(sourceEditor, this.FindAncestorOfType<GraphEditorView>())) return;
        if (e.Source is Visual origin &&
            origin.GetSelfAndVisualAncestors().OfType<GraphNodeView>().FirstOrDefault() is { } nearest &&
            !ReferenceEquals(nearest, this)) return;
        if (DataContext is not GraphNodeViewModel node)
        {
            return;
        }
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            if (!node.IsSelected)
                node.Workspace.SelectNode(node);
            return;
        }
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        this.FindAncestorOfType<GraphEditorView>()?.Focus();
        if (e.ClickCount >= 2 && node.IsGpuParticle)
        {
            node.Workspace.OpenGpuEditor(node);
            e.Handled = true;
            return;
        }
        if (e.ClickCount >= 2 && node.IsGraphGroup && e.KeyModifiers == KeyModifiers.None &&
            !IsInteractiveSource(e.Source))
        {
            node.Workspace.EnterGraph(node);
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            node.Workspace.ToggleSelection(node);
        }
        else
        {
            node.Workspace.SelectNode(node);
        }
        e.Handled = true;
    }

    private static bool IsInteractiveSource(object? source)
    {
        if (source is not Visual visual) return false;
        return visual.GetSelfAndVisualAncestors().OfType<Control>().Any(control =>
            control is Button or ToggleButton or TextBox or NumericUpDown or ComboBox or Slider ||
            control.Tag is GraphInputPortDefinition or GraphOutputPortDefinition ||
            string.Equals(control.Tag?.ToString(), "OutputPort", StringComparison.Ordinal));
    }

    private void OnHeaderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragNode is null || _dragEditor is null)
        {
            return;
        }

        var current = e.GetPosition(_dragEditor);
        var dx = (current.X - _pointerStart.X) / _dragEditor.DebugZoom;
        var dy = (current.Y - _pointerStart.Y) / _dragEditor.DebugZoom;
        foreach (var item in _dragNodes)
        {
            item.Node.PreviewMoveTo(item.X + dx, item.Y + dy);
        }
        var dragged = _dragNodes.Select(item => item.Node).ToHashSet();
        _dragEditor.ShowInteractionWires(_dragNode.Workspace,
            _dragNode.Workspace.Connections.Where(edge => dragged.Contains(edge.Source) || dragged.Contains(edge.Target)));
        e.Handled = true;
    }

    private void OnHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragNode is null)
        {
            return;
        }

        var dragged = _dragNode;
        var graph = dragged.Workspace;
        var positions = _dragNodes.Select(item => (item.Node, item.Node.DisplayX, item.Node.DisplayY)).ToArray();
        var nodes = _dragNodes.Select(item => item.Node).ToArray();
        var destination = _dragEditor is not null &&
            (Math.Abs(e.GetPosition(_dragEditor).X - _pointerStart.X) +
             Math.Abs(e.GetPosition(_dragEditor).Y - _pointerStart.Y)) > 4
            ? _dragEditor.FindDropContainer(e.GetPosition(_dragEditor), nodes) : null;
        ResetDrag();
        e.Pointer.Capture(null);
        if (!dragged.IsSelected && graph.Nodes.Contains(dragged))
            dragged.SelectCommand.Execute(null);
        try
        {
            graph.RunAuthoringAction(() =>
            {
                if (destination is null || !graph.MoveNodesIntoGraph(nodes, destination)) graph.MoveNodes(positions);
            });
        }
        finally
        {
            foreach (var node in nodes)
            {
                node.CancelPreviewMove();
            }
        }
        e.Handled = true;
    }

    private void OnHeaderPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        foreach (var item in _dragNodes)
        {
            item.Node.CancelPreviewMove();
        }
        ResetDrag();
    }

    private void ResetDrag()
    {
        _dragEditor?.ClearInteractionWires();
        _dragEditor = null;
        _dragNode = null;
        _dragNodes = [];
    }

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not GraphNodeViewModel node || (!node.IsComment && !node.HasInlineWorkspace) ||
            sender is not Control handle || !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed ||
            this.FindAncestorOfType<GraphEditorView>() is not { } editor) return;
        node.SelectCommand.Execute(null);
        _resizeNode = node;
        _resizeEditor = editor;
        _resizeStart = e.GetPosition(editor);
        _resizeStartExpanded = node.IsInlineExpanded;
        _resizeWidth = node.IsComment ? node.CommentWidth : node.IsInlineExpanded ? node.InlineWidth : node.RenderedWidth;
        _resizeHeight = node.IsComment ? node.CommentHeight : node.IsInlineExpanded ? node.InlineHeight : 0;
        _resizeRevision = node.Workspace.History.CurrentNodeId;
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnResizeMoved(object? sender, PointerEventArgs e)
    {
        if (_resizeNode is not { } node || _resizeEditor is not { } editor) return;
        var delta = (e.GetPosition(editor) - _resizeStart) / editor.DebugZoom;
        if (node.IsComment) node.PreviewCommentSize(_resizeWidth + delta.X, _resizeHeight + delta.Y);
        else if (_resizeStartExpanded)
        {
            var nextHeight = _resizeHeight + delta.Y;
            if (nextHeight < 80)
                node.PreviewInlineSize(_resizeWidth + delta.X, 160, expanded: false);
            else
                node.PreviewInlineSize(_resizeWidth + delta.X, nextHeight, expanded: true);
        }
        else
        {
            if (delta.Y >= 32)
                node.PreviewInlineSize(Math.Max(_resizeWidth, _resizeWidth + delta.X), Math.Max(160, delta.Y), expanded: true);
            else
                node.PreviewInlineSize(_resizeWidth + delta.X, 160, expanded: false);
        }
        e.Handled = true;
    }

    private void OnResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_resizeNode is not { } node) return;
        var width = node.IsComment ? node.CommentWidth : node.InlineWidth;
        var height = node.IsComment ? node.CommentHeight : node.InlineHeight;
        var expanded = node.IsInlineExpanded;
        var wasExpanded = _resizeStartExpanded;
        _resizeNode = null;
        _resizeEditor = null;
        if (node.IsComment) node.CancelCommentSize();
        else node.CancelInlineSize();
        e.Pointer.Capture(null);
        if (node.Workspace.History.CurrentNodeId == _resizeRevision &&
            (node.IsComment || expanded || wasExpanded))
        {
            node.Workspace.RunAuthoringAction(() =>
            {
                if (node.IsComment) node.Workspace.SetCommentSize(node, width, height);
                else node.Workspace.SetInlinePresentation(node, expanded, width, height);
            });
        }
        e.Handled = true;
    }

    private void OnResizeCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_resizeNode is { IsComment: true } comment) comment.CancelCommentSize();
        else _resizeNode?.CancelInlineSize();
        _resizeNode = null;
        _resizeEditor = null;
    }

}
