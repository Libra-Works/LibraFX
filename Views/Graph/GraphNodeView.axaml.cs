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
using LiberaFx.Services.Compilation;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;

namespace LiberaFx.Views.Graph;

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
    private bool _resizeStartExpanded;
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

}
