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
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class GraphNodeView : UserControl
{
    private GraphEditorView? _dragEditor;
    private GraphNodeViewModel? _dragNode;
    private (GraphNodeViewModel Node, double X, double Y)[] _dragNodes = [];
    private Point _pointerStart;
    private GraphNodeViewModel? _connectionSource;
    private string? _connectionSourcePortId;
    private Grid? _connectionCanvas;
    private Point _connectionStart;
    private bool _connectionMoved;
    private readonly PortSnapFeedback _connectionFeedback = new();
    private (Button Pin, GraphNodeViewModel Node, string? PortId)[] _connectionTargets = [];
    private GraphNodeViewModel? _resizeNode;
    private GraphEditorView? _resizeEditor;
    private Point _resizeStart;
    private double _resizeWidth;
    private double _resizeHeight;
    private Guid _resizeRevision;
    private GraphNodeViewModel? _imageNode;
    private Bitmap? _commentBitmap;
    private string? _imageStatusKey;
    private bool _editingComment;
    private GraphNodeViewModel? _lifecycleNode;
    private GraphNodeViewModel? _anchorNode;
    private bool _anchorsDirty = true;

    public GraphNodeView()
    {
        InitializeComponent();
        LayoutUpdated += (_, _) =>
        {
            if (_anchorsDirty) UpdatePortAnchors();
        };
        SizeChanged += (_, _) =>
        {
            _anchorsDirty = true;
            if (DataContext is not GraphNodeViewModel node) return;
            node.RenderedHeight = Bounds.Height;
            var editor = this.FindAncestorOfType<GraphEditorView>();
            if (editor is not null) Dispatcher.UIThread.Post(editor.UpdateCanvasExtent);
        };
    }

    private void UpdatePortAnchors()
    {
        _anchorsDirty = false;
        if (DataContext is not GraphNodeViewModel node) return;
        var inputs = new Dictionary<string, Point>();
        var outputs = new Dictionary<string, Point>();
        foreach (var port in this.GetVisualDescendants().OfType<Control>().Where(control =>
            control.IsEffectivelyVisible && ReferenceEquals(control.FindAncestorOfType<GraphNodeView>(), this)))
        {
            var input = port.Tag is GraphInputPortDefinition || port.Tag is "InputPort";
            var output = port.Tag is GraphOutputPortDefinition || port.Tag is "OutputPort";
            if (!input && !output) continue;
            if (port.TranslatePoint(new Point(port.Bounds.Width / 2, port.Bounds.Height / 2), this) is not { } center) continue;
            var id = port.Tag switch
            {
                GraphInputPortDefinition definition => definition.Id,
                GraphOutputPortDefinition definition => definition.Id,
                _ => ""
            };
            (input ? inputs : outputs)[id] = center;
        }
        node.SetPortAnchors(inputs, outputs);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LocalizationService.Shared.LanguageChanged += OnLanguageChanged;
        AttachImageNode();
        AttachLifecycleNode();
        AttachAnchorNode();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LocalizationService.Shared.LanguageChanged -= OnLanguageChanged;
        DetachImageNode();
        DetachLifecycleNode();
        DetachAnchorNode();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        DetachImageNode();
        DetachLifecycleNode();
        DetachAnchorNode();
        if (this.IsAttachedToVisualTree())
        {
            AttachImageNode();
            AttachLifecycleNode();
            AttachAnchorNode();
        }
    }

    private void AttachAnchorNode()
    {
        _anchorsDirty = true;
        if (DataContext is not GraphNodeViewModel node || _anchorNode == node) return;
        _anchorNode = node;
        node.PropertyChanged += OnAnchorNodePropertyChanged;
    }

    private void DetachAnchorNode()
    {
        if (_anchorNode is not null)
            _anchorNode.PropertyChanged -= OnAnchorNodePropertyChanged;
        _anchorNode = null;
    }

    private void OnAnchorNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GraphNodeViewModel.InputPorts) or
            nameof(GraphNodeViewModel.OutputPorts) or
            nameof(GraphNodeViewModel.HasPorts) or
            nameof(GraphNodeViewModel.HasCustomInputs) or
            nameof(GraphNodeViewModel.HasCustomOutputs) or
            nameof(GraphNodeViewModel.IsDetailsExpanded) or
            nameof(GraphNodeViewModel.IsInlineExpanded) or
            nameof(GraphNodeViewModel.IsLifecycleNode) or
            nameof(GraphNodeViewModel.RenderedWidth))
        {
            _anchorsDirty = true;
        }
    }

    private void AttachLifecycleNode()
    {
        if (DataContext is not GraphNodeViewModel { IsLifecycleNode: true } node || _lifecycleNode == node) return;
        _lifecycleNode = node;
        node.Workspace.Connections.CollectionChanged += OnLifecycleConnectionsChanged;
    }

    private void DetachLifecycleNode()
    {
        if (_lifecycleNode is not null)
            _lifecycleNode.Workspace.Connections.CollectionChanged -= OnLifecycleConnectionsChanged;
        _lifecycleNode = null;
    }

    private void OnLifecycleConnectionsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        _lifecycleNode?.RefreshLifecyclePresentation();

    private void AttachImageNode()
    {
        if (DataContext is not GraphNodeViewModel { IsComment: true } node || _imageNode == node) return;
        _imageNode = node;
        node.PropertyChanged += OnImageNodeChanged;
        _editingComment = false;
        UpdateCommentBody();
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
        if (e.PropertyName == nameof(GraphNodeViewModel.Parameters))
        {
            UpdateCommentImage();
            UpdateCommentBody();
        }
    }

    private void UpdateCommentBody()
    {
        var formatted = _imageNode?.HasFormattedComment == true;
        CommentSource.IsVisible = !formatted || _editingComment;
        CommentPreview.IsVisible = formatted && !_editingComment;
        EditCommentButton.Icon = _editingComment ? Models.Appearance.EditorIcon.Check : Models.Appearance.EditorIcon.Settings;
    }

    internal bool IsBlankCommentArea(PointerEventArgs e)
    {
        if (DataContext is not GraphNodeViewModel { IsComment: true } ||
            !new Rect(CommentBody.Bounds.Size).Contains(e.GetPosition(CommentBody)) ||
            e.Source is not Visual source ||
            source.GetSelfAndVisualAncestors().Any(visual => visual is ScrollBar or CheckBox))
        {
            return false;
        }

        if (CommentSource.IsVisible && CommentSource.FindDescendantOfType<Avalonia.Controls.Presenters.TextPresenter>() is { } text &&
            !string.IsNullOrWhiteSpace(CommentSource.Text))
        {
            return !text.TextLayout.HitTestPoint(e.GetPosition(text)).IsInside;
        }

        return !CommentPreview.IsVisible ||
            !source.GetSelfAndVisualAncestors().TakeWhile(visual => visual != CommentPreview)
                .OfType<TextBlock>().Any(label => label.TextLayout.HitTestPoint(e.GetPosition(label)).IsInside);
    }

    internal bool ShouldFocusGraphOnBlankComment(PointerPressedEventArgs e) =>
        !_editingComment && CommentSource.IsVisible && !string.IsNullOrWhiteSpace(CommentSource.Text) &&
        IsBlankCommentArea(e);

    internal bool TryScrollComment(PointerWheelEventArgs e)
    {
        if (DataContext is not GraphNodeViewModel { IsComment: true } ||
            !new Rect(CommentBody.Bounds.Size).Contains(e.GetPosition(CommentBody)))
        {
            return false;
        }

        var viewer = CommentSource.IsVisible ? CommentSource.FindDescendantOfType<ScrollViewer>()
            : CommentPreview.Content as ScrollViewer;
        if (viewer is null || viewer.Extent.Height <= viewer.Viewport.Height)
        {
            return false;
        }

        viewer.Offset = new Vector(viewer.Offset.X,
            Math.Clamp(viewer.Offset.Y - e.Delta.Y * 48, 0, viewer.Extent.Height - viewer.Viewport.Height));
        return true;
    }

    private void OnEditCommentClick(object? sender, RoutedEventArgs e)
    {
        if (_editingComment && _imageNode is { } node) node.CommentText = CommentSource.Text ?? "";
        _editingComment = !_editingComment;
        UpdateCommentBody();
        if (_editingComment) CommentSource.Focus();
        e.Handled = true;
    }

    private void OnCommentTaskChanged(object? sender, CommentTaskChangedEventArgs e)
    {
        if (_imageNode is not { CommentStyle: CommentFormat.Markdown } node ||
            !node.Workspace.Nodes.Contains(node) || node.CommentText != e.Source || e.Offset < 0) return;
        node.CommentText = CommentMarkup.ToggleTask(e.Source, e.Offset, e.Value);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (DataContext is GraphNodeViewModel node) node.RefreshLocalizedText();
        if (_imageStatusKey is { } key)
            ImageStatus.Text = LocalizationService.Shared.Get(key);
    }

    private void SetImageStatus(string? key, string? externalMessage = null)
    {
        _imageStatusKey = key;
        ImageStatus.Text = key is null
            ? externalMessage ?? ""
            : LocalizationService.Shared.Get(key);
    }

    private void SetImageError(Exception exception)
    {
        var key = exception.Message switch
        {
            "Image exceeds 16 MiB." => "Panels.CommentImageSourceTooLarge",
            "Choose a PNG or JPEG image no larger than 8192 x 8192 pixels." => "Panels.CommentImageInvalid",
            "Image could not be prepared for embedding." => "Panels.CommentImagePrepareFailed",
            "Image could not be reduced to the embedding limit." => "Panels.CommentImageOptimizeFailed",
            _ => null
        };
        SetImageStatus(key, exception.Message);
    }

    private void UpdateCommentImage()
    {
        EmbeddedImage.Source = null;
        _commentBitmap?.Dispose();
        _commentBitmap = null;
        SetImageStatus(null);
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
                SetImageStatus("Panels.CommentImageDisplayTooLarge");
                return;
            }
            _commentBitmap = bitmap;
            EmbeddedImage.Source = bitmap;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IOException or InvalidDataException or
                                   InvalidOperationException or NotSupportedException)
        {
            SetImageStatus("Panels.CommentImageDecodeFailed");
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
                Title = LocalizationService.Shared.Get("Panels.ChooseCommentImage"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(LocalizationService.Shared.Get("Panels.PngOrJpeg")) { Patterns = ["*.png", "*.jpg", "*.jpeg"] }]
            });
            if (files.Count == 0) return;
            using var file = files[0];
            await AddCommentImageAsync(node, file);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                                   ArgumentException or InvalidOperationException or NotSupportedException)
        {
            if (ReferenceEquals(DataContext, node)) SetImageError(ex);
        }
    }

    private async Task AddCommentImageAsync(GraphNodeViewModel node, IStorageFile file)
    {
        try
        {
            await using var input = await file.OpenReadAsync();
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int count;
            while ((count = await input.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + count > CommentImage.MaximumSourceBytes)
                    throw new InvalidDataException("Image exceeds 16 MiB.");
                buffer.Write(chunk, 0, count);
            }
            var data = buffer.ToArray();
            if (!CommentImage.IsValidSource(data, out var width, out var height))
                throw new InvalidDataException("Choose a PNG or JPEG image no larger than 8192 x 8192 pixels.");
            var optimized = data.Length > CommentImage.MaximumBytes || width > 4096 || height > 4096;
            if (optimized) data = OptimizeCommentImage(data, width, height);
            else
            {
                buffer.Position = 0;
                using var bitmap = new Bitmap(buffer);
            }
            var value = JsonSerializer.SerializeToElement(Convert.ToBase64String(data));
            if (!NodeParameter.IsValidValue("commentImage", value))
                throw new InvalidDataException("Image could not be prepared for embedding.");
            if (ReferenceEquals(DataContext, node) && node.Workspace.Nodes.Contains(node))
            {
                node.Workspace.SetParameter(node, "image", value);
                if (optimized) SetImageStatus("Panels.CommentImageOptimized");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                                   ArgumentException or InvalidOperationException or NotSupportedException)
        {
            if (ReferenceEquals(DataContext, node)) SetImageError(ex);
        }
    }

    private static byte[] OptimizeCommentImage(byte[] source, int width, int height)
    {
        var png = source.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        foreach (var size in new[] { 1024, 768, 512, 256 })
        {
            using var input = new MemoryStream(source, false);
            using var bitmap = width >= height
                ? Bitmap.DecodeToWidth(input, Math.Min(size, width), BitmapInterpolationMode.HighQuality)
                : Bitmap.DecodeToHeight(input, Math.Min(size, height), BitmapInterpolationMode.HighQuality);
            using var output = new MemoryStream();
            bitmap.Save(output, png ? PngBitmapEncoderOptions.Default : JpegBitmapEncoderOptions.Default);
            if (output.Length <= CommentImage.MaximumBytes) return output.ToArray();
        }
        throw new InvalidDataException("Image could not be reduced to the embedding limit.");
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
        if (DataContext is not GraphNodeViewModel node ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
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
        _dragEditor = null;
        _dragNode = null;
        _dragNodes = [];
    }

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not GraphNodeViewModel node || (!node.IsComment && !node.IsInlineExpanded) ||
            sender is not Control handle || !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed ||
            this.FindAncestorOfType<GraphEditorView>() is not { } editor) return;
        node.SelectCommand.Execute(null);
        _resizeNode = node;
        _resizeEditor = editor;
        _resizeStart = e.GetPosition(editor);
        _resizeWidth = node.IsComment ? node.CommentWidth : node.InlineWidth;
        _resizeHeight = node.IsComment ? node.CommentHeight : node.InlineHeight;
        _resizeRevision = node.Workspace.History.CurrentNodeId;
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnResizeMoved(object? sender, PointerEventArgs e)
    {
        if (_resizeNode is not { } node || _resizeEditor is not { } editor) return;
        var delta = (e.GetPosition(editor) - _resizeStart) / editor.DebugZoom;
        if (node.IsComment) node.PreviewCommentSize(_resizeWidth + delta.X, _resizeHeight + delta.Y);
        else node.PreviewInlineSize(_resizeWidth + delta.X, _resizeHeight + delta.Y);
        e.Handled = true;
    }

    private void OnResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_resizeNode is not { } node) return;
        var width = node.IsComment ? node.CommentWidth : node.InlineWidth;
        var height = node.IsComment ? node.CommentHeight : node.InlineHeight;
        _resizeNode = null;
        _resizeEditor = null;
        if (node.IsComment) node.CancelCommentSize();
        else node.CancelInlineSize();
        e.Pointer.Capture(null);
        if (node.Workspace.History.CurrentNodeId == _resizeRevision)
        {
            node.Workspace.RunAuthoringAction(() =>
            {
                if (node.IsComment) node.Workspace.SetCommentSize(node, width, height);
                else node.Workspace.SetInlinePresentation(node, true, width, height);
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

    private void OnOutputPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not GraphNodeViewModel node ||
            sender is not Control port ||
            !e.GetCurrentPoint(port).Properties.IsLeftButtonPressed ||
            this.FindAncestorOfType<GraphEditorView>()?.FindControl<Grid>("GraphCanvas") is not { } canvas)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            var outputPortId = port.Tag is GraphOutputPortDefinition op ? op.Id : null;
            var matches = node.Workspace.Connections
                .Where(c => c.Source == node && (outputPortId is null || c.SourcePortId == outputPortId))
                .ToArray();
            if (matches.Length > 0)
            {
                node.Workspace.RunAuthoringAction(() =>
                {
                    foreach (var connection in matches)
                        node.Workspace.DisconnectConnection(connection);
                });
            }
            e.Handled = true;
            return;
        }

        _connectionSource = node;
        _connectionCanvas = canvas;
        _connectionStart = e.GetPosition(canvas);
        _connectionMoved = false;
        _connectionSourcePortId = port.Tag is GraphOutputPortDefinition outputPort ? outputPort.Id : null;
        _connectionTargets = canvas.GetVisualDescendants().OfType<Button>()
            .Where(button => button.IsEffectivelyVisible &&
                button.FindAncestorOfType<GraphEditorView>()?.DataContext == node.Workspace)
            .Select(button => (Pin: button, Node: button.FindAncestorOfType<GraphNodeView>()?.DataContext as GraphNodeViewModel,
                PortId: (button.Tag as GraphInputPortDefinition)?.Id))
            .Where(item => item.Node is not null && (item.Pin.Tag is GraphInputPortDefinition or "InputPort") &&
                node.Workspace.CanConnectNodes(node, item.Node, out _, item.PortId, _connectionSourcePortId))
            .Select(item => (item.Pin, item.Node!, item.PortId)).ToArray();
        if (_connectionSourcePortId is null) node.SelectOutputPortCommand.Execute(null);
        else node.Workspace.BeginConnection(node, _connectionSourcePortId);
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
            var target = FindInputAt(canvas, position);
            _connectionFeedback.Show(target?.Pin);
            if (target is { } snap && snap.Pin.TranslatePoint(new Point(snap.Pin.Bounds.Width / 2, snap.Pin.Bounds.Height / 2), canvas) is { } anchor)
                position = anchor;
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
        _connectionFeedback.Dispose();
        _connectionTargets = [];
        _connectionSource = null;
        var sourcePortId = _connectionSourcePortId;
        _connectionSourcePortId = null;
        _connectionCanvas = null;
        e.Pointer.Capture(null);
        if (target is { } input)
        {
            input.Node.Workspace.RunAuthoringAction(() =>
                input.Node.Workspace.CompleteConnection(input.Node, input.PortId, sourcePortId));
        }
        else if (moved)
        {
            source.Workspace.CancelConnection();
        }

        e.Handled = true;
    }

    private void OnCustomInputClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GraphNodeViewModel node &&
            sender is Button { Tag: GraphInputPortDefinition port })
            node.Workspace.RunAuthoringAction(() => node.Workspace.CompleteConnection(node, port.Id));
    }

    private (Button Pin, GraphNodeViewModel Node, string? PortId)? FindInputAt(Grid canvas, Point position)
    {
        var root = TopLevel.GetTopLevel(canvas);
        if (root is null || canvas.TranslatePoint(position, root) is not { } pointer) return null;
        (Button Pin, GraphNodeViewModel Node, string? PortId)? best = null;
        var distance = PortSnapFeedback.AcquireRadius;
        foreach (var candidate in _connectionTargets)
        {
            if (!candidate.Pin.IsEffectivelyVisible || candidate.Pin.TranslatePoint(
                    new Point(candidate.Pin.Bounds.Width / 2, candidate.Pin.Bounds.Height / 2), root) is not { } center) continue;
            var next = ((Vector)(center - pointer)).Length;
            var current = ReferenceEquals(candidate.Pin, _connectionFeedback.Pin);
            if (next <= distance || best is null && current && next <= PortSnapFeedback.ReleaseRadius)
            {
                best = candidate;
                distance = next;
            }
        }
        return best;
    }

    private void OnToggleInlineClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GraphNodeViewModel node)
            node.Workspace.RunAuthoringAction(() =>
                node.Workspace.SetInlinePresentation(node, !node.IsInlineExpanded, node.InlineWidth, node.InlineHeight));
        e.Handled = true;
    }

    private void OnMaximizeInlineClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GraphNodeViewModel node) node.Workspace.MaximizeInlineGraph(node);
        e.Handled = true;
    }

    private void OnOutputPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _connectionFeedback.Dispose();
        _connectionTargets = [];
        _connectionSource?.Workspace.CancelConnection();
        _connectionSource = null;
        _connectionSourcePortId = null;
        _connectionCanvas = null;
    }
}
