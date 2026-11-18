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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachNode();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachNode();
        Content = null;
        if (_editor is not null) _editor.DataContext = null;
        _editor = null;
        base.OnDetachedFromVisualTree(e);
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
        if (_node is not { IsInlineExpanded: true } node) { Content = null; return; }
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
}
