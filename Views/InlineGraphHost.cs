using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

// Lazy creation is essential: a collapsed group must not recursively construct its descendants.
public sealed class InlineGraphHost : ContentControl
{
    private GraphNodeViewModel? _node;
    private GraphEditorView? _editor;
    private GpuWorkspaceView? _gpu;
    private ProceduralWorkspaceView? _procedural;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachNode();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachNode();
        ClearHostedWorkspace();
        base.OnDetachedFromVisualTree(e);
    }

    private void ClearHostedWorkspace()
    {
        Content = null;
        if (_editor is not null) _editor.DataContext = null;
        _editor = null;
        if (_gpu is not null)
        {
            _gpu.PropertyChanged -= OnEmbeddedWorkspacePropertyChanged;
            _gpu.CloseWorkspace();
            _gpu = null;
        }
        if (_procedural is not null)
        {
            _procedural.PropertyChanged -= OnEmbeddedWorkspacePropertyChanged;
            _procedural.CloseWorkspace();
            _procedural = null;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        DetachNode();
        if (this.IsAttachedToVisualTree()) AttachNode();
    }

    private void AttachNode()
    {
        _node = DataContext as GraphNodeViewModel;
        if (_node is not null) _node.PropertyChanged += OnNodeChanged;
        UpdateEditor();
    }

    private void DetachNode()
    {
        if (_node is not null) _node.PropertyChanged -= OnNodeChanged;
        _node = null;
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphNodeViewModel.IsInlineExpanded)) UpdateEditor();
    }

    private void UpdateEditor()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_node is not { IsInlineExpanded: true } node)
        {
            // Retain the child's editor and viewport while collapsed. Detach still disposes it.
            if (_editor is not null) Content = null;
            else ClearHostedWorkspace();
            return;
        }
        if (node.IsGpuParticle)
        {
            if (_gpu is null)
            {
                _gpu = new GpuWorkspaceView();
                _gpu.PropertyChanged += OnEmbeddedWorkspacePropertyChanged;
            }
            if (!ReferenceEquals(Content, _gpu))
            {
                _gpu.Open(node.Workspace, node.Id);
                Content = _gpu;
            }
            return;
        }
        if (node.IsProcedural)
        {
            if (_procedural is null)
            {
                _procedural = new ProceduralWorkspaceView();
                _procedural.PropertyChanged += OnEmbeddedWorkspacePropertyChanged;
            }
            if (!ReferenceEquals(Content, _procedural))
            {
                _procedural.Open(node.Workspace, node.Id);
                Content = _procedural;
            }
            return;
        }
        if (_editor?.DataContext is GraphWorkspaceViewModel current &&
            ReferenceEquals(current.Document, node.Workspace.Document) &&
            current.GraphPath.SequenceEqual(node.Workspace.GraphPath.Append(node.Id)))
        {
            Content = _editor;
            return;
        }
        var workspace = node.Workspace.OpenInlineGraph(node);
        if (_editor is null || !ReferenceEquals(_editor.DataContext, workspace))
        {
            _editor = new GraphEditorView { DataContext = workspace, MinWidth = 0 };
        }
        Content = _editor;
    }

    private void OnEmbeddedWorkspacePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty && sender is Control { IsVisible: false } &&
            _node is { IsInlineExpanded: true } node && ReferenceEquals(Content, sender))
        {
            node.Workspace.RunAuthoringAction(() =>
                node.Workspace.SetInlinePresentation(node, false, node.InlineWidth, node.InlineHeight));
        }
    }
}
