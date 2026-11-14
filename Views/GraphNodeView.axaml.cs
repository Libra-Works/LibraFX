using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class GraphNodeView : UserControl
{
    private GraphEditorView? _dragEditor;
    private GraphNodeViewModel? _dragNode;
    private (GraphNodeViewModel Node, double X, double Y)[] _dragNodes = [];
    private Point _pointerStart;
    private GraphNodeViewModel? _connectionSource;
    private Grid? _connectionCanvas;
    private Point _connectionStart;
    private bool _connectionMoved;

    public GraphNodeView()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            if (DataContext is not GraphNodeViewModel node) return;
            node.RenderedHeight = Bounds.Height;
            var editor = this.FindAncestorOfType<GraphEditorView>();
            if (editor is not null) Dispatcher.UIThread.Post(editor.UpdateCanvasExtent);
        };
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source &&
            (ReferenceEquals(source, DetailsToggle) || source.GetVisualAncestors().Contains(DetailsToggle)))
            return;

        if (DataContext is not GraphNodeViewModel node ||
            sender is not Control header ||
            !e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount >= 2 && node.IsGraphGroup)
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

        if (!node.IsSelected)
        {
            node.SelectCommand.Execute(null);
        }

        _dragNode = node;
        _dragNodes = node.Workspace.SelectedNodes.Select(selected => (selected, selected.X, selected.Y)).ToArray();
        _pointerStart = e.GetPosition(_dragEditor);
        e.Pointer.Capture(header);
        e.Handled = true;
    }

    private void OnNodePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not GraphNodeViewModel node ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        this.FindAncestorOfType<GraphEditorView>()?.Focus();
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
        e.Handled = true;
    }

    private void OnHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragNode is null)
        {
            return;
        }

        var graph = _dragNode.Workspace;
        var positions = _dragNodes.Select(item => (item.Node, item.Node.DisplayX, item.Node.DisplayY)).ToArray();
        var nodes = _dragNodes.Select(item => item.Node).ToArray();
        ResetDrag();
        e.Pointer.Capture(null);
        try
        {
            graph.MoveNodes(positions);
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
        _dragEditor = null;
        _dragNode = null;
        _dragNodes = [];
    }

    private void OnOutputPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not GraphNodeViewModel node ||
            sender is not Control port ||
            !e.GetCurrentPoint(port).Properties.IsLeftButtonPressed ||
            this.FindAncestorOfType<GraphEditorView>()?.FindControl<Grid>("GraphCanvas") is not { } canvas)
        {
            return;
        }

        _connectionSource = node;
        _connectionCanvas = canvas;
        _connectionStart = e.GetPosition(canvas);
        _connectionMoved = false;
        node.SelectOutputPortCommand.Execute(null);
        e.Pointer.Capture(port);
        e.Handled = true;
    }

    private void OnOutputPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_connectionSource is not { } source || _connectionCanvas is not { } canvas)
        {
            return;
        }

        var position = e.GetPosition(canvas);
        if (!_connectionMoved &&
            (Math.Abs(position.X - _connectionStart.X) >= 5 ||
             Math.Abs(position.Y - _connectionStart.Y) >= 5))
        {
            _connectionMoved = true;
        }

        if (_connectionMoved)
        {
            var editor = this.FindAncestorOfType<GraphEditorView>();
            source.Workspace.PreviewConnection(editor?.ToGraphPoint(position) ?? position);
        }

        e.Handled = true;
    }

    private void OnOutputPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_connectionSource is not { } source || _connectionCanvas is not { } canvas)
        {
            return;
        }

        var moved = _connectionMoved;
        var position = e.GetPosition(canvas);
        var target = moved ? FindInputAt(canvas, position) : null;
        _connectionSource = null;
        _connectionCanvas = null;
        e.Pointer.Capture(null);
        if (target is not null)
        {
            target.SelectInputPortCommand.Execute(null);
        }
        else if (moved)
        {
            source.Workspace.CancelConnection();
        }

        e.Handled = true;
    }

    private static GraphNodeViewModel? FindInputAt(Grid canvas, Point position)
    {
        foreach (var button in canvas.GetVisualDescendants().OfType<Button>().Reverse())
        {
            if (button.Tag is not string tag || tag != "InputPort" ||
                !button.IsEffectivelyVisible ||
                button.DataContext is not GraphNodeViewModel node ||
                button.TranslatePoint(default, canvas) is not { } origin)
            {
                continue;
            }

            if (new Rect(origin, button.Bounds.Size).Contains(position))
            {
                return node;
            }
        }

        return null;
    }

    private void OnOutputPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _connectionSource?.Workspace.CancelConnection();
        _connectionSource = null;
        _connectionCanvas = null;
    }
}
