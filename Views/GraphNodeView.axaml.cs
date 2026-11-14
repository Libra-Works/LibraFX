using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using System.ComponentModel;
using System.Text.Json;
using LiberaFx.Models.Graph;
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
    private GraphNodeViewModel? _resizeNode;
    private GraphEditorView? _resizeEditor;
    private Point _resizeStart;
    private double _resizeWidth;
    private double _resizeHeight;
    private Guid _resizeRevision;
    private GraphNodeViewModel? _imageNode;
    private Bitmap? _commentBitmap;

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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachImageNode();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachImageNode();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        DetachImageNode();
        if (this.IsAttachedToVisualTree()) AttachImageNode();
    }

    private void AttachImageNode()
    {
        if (DataContext is not GraphNodeViewModel { IsComment: true } node || _imageNode == node) return;
        _imageNode = node;
        node.PropertyChanged += OnImageNodeChanged;
        UpdateCommentImage();
    }

    private void DetachImageNode()
    {
        if (_imageNode is not null) _imageNode.PropertyChanged -= OnImageNodeChanged;
        _imageNode = null;
        EmbeddedImage.Source = null;
        _commentBitmap?.Dispose();
        _commentBitmap = null;
    }

    private void OnImageNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphNodeViewModel.Parameters)) UpdateCommentImage();
    }

    private void UpdateCommentImage()
    {
        EmbeddedImage.Source = null;
        _commentBitmap?.Dispose();
        _commentBitmap = null;
        ImageStatus.Text = "";
        var definition = _imageNode?.Definition.Parameters.FirstOrDefault(item => item.Id == "image");
        var value = definition is null ? null : _imageNode!.GetParameterValue(definition).GetString();
        if (string.IsNullOrEmpty(value)) return;
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(value), false);
            var bitmap = new Bitmap(stream);
            if (bitmap.PixelSize.Width > 4096 || bitmap.PixelSize.Height > 4096)
            {
                bitmap.Dispose();
                ImageStatus.Text = "Image exceeds 4096 pixels.";
                return;
            }
            _commentBitmap = bitmap;
            EmbeddedImage.Source = bitmap;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IOException or InvalidOperationException)
        {
            ImageStatus.Text = "Image could not be decoded.";
        }
    }

    private async void OnAddImageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GraphNodeViewModel { IsComment: true } node ||
            TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose a comment image",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("PNG or JPEG") { Patterns = ["*.png", "*.jpg", "*.jpeg"] }]
            });
            if (files.Count == 0) return;
            await using var input = await files[0].OpenReadAsync();
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int count;
            while ((count = await input.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + count > CommentImage.MaximumBytes) throw new InvalidDataException("Image exceeds 512 KiB.");
                buffer.Write(chunk, 0, count);
            }
            var value = JsonSerializer.SerializeToElement(Convert.ToBase64String(buffer.ToArray()));
            if (!NodeParameter.IsValidValue("commentImage", value)) throw new InvalidDataException("Choose a PNG or JPEG image.");
            buffer.Position = 0;
            using var bitmap = new Bitmap(buffer);
            if (bitmap.PixelSize.Width > 4096 || bitmap.PixelSize.Height > 4096)
                throw new InvalidDataException("Image exceeds 4096 pixels.");
            if (ReferenceEquals(DataContext, node) && node.Workspace.Nodes.Contains(node))
                node.Workspace.SetParameter(node, "image", value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            ImageStatus.Text = ex.Message;
        }
    }

    private void OnRemoveImageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GraphNodeViewModel { IsComment: true } node)
            node.Workspace.SetParameter(node, "image", JsonSerializer.SerializeToElement(""));
    }

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
        _dragNodes = node.Workspace.GetMoveTargets(node.Workspace.SelectedNodes)
            .Select(selected => (selected, selected.X, selected.Y)).ToArray();
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

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not GraphNodeViewModel { IsComment: true } node ||
            sender is not Control handle || !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed ||
            this.FindAncestorOfType<GraphEditorView>() is not { } editor) return;
        node.SelectCommand.Execute(null);
        _resizeNode = node;
        _resizeEditor = editor;
        _resizeStart = e.GetPosition(editor);
        _resizeWidth = node.CommentWidth;
        _resizeHeight = node.CommentHeight;
        _resizeRevision = node.Workspace.History.CurrentNodeId;
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnResizeMoved(object? sender, PointerEventArgs e)
    {
        if (_resizeNode is not { } node || _resizeEditor is not { } editor) return;
        var delta = (e.GetPosition(editor) - _resizeStart) / editor.DebugZoom;
        node.PreviewCommentSize(_resizeWidth + delta.X, _resizeHeight + delta.Y);
        e.Handled = true;
    }

    private void OnResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_resizeNode is not { } node) return;
        var width = node.CommentWidth;
        var height = node.CommentHeight;
        _resizeNode = null;
        _resizeEditor = null;
        node.CancelCommentSize();
        e.Pointer.Capture(null);
        if (node.Workspace.History.CurrentNodeId == _resizeRevision)
            node.Workspace.SetCommentSize(node, width, height);
        e.Handled = true;
    }

    private void OnResizeCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _resizeNode?.CancelCommentSize();
        _resizeNode = null;
        _resizeEditor = null;
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
