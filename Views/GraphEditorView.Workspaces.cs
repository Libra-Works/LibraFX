using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class GraphEditorView
{
    private GraphWorkspaceViewModel? _specializedDocument;
    private Guid? _specializedNodeId;
    private GpuWorkspaceView? _gpuWorkspace;
    private ProceduralWorkspaceView? _proceduralWorkspace;

    private void ObserveSpecializedWorkspaces(bool subscribe)
    {
        if (_specializedDocument is { } previous)
        {
            previous.GpuEditorRequested -= OpenGpuWorkspace;
            previous.ProceduralEditorRequested -= OpenProceduralWorkspace;
        }
        _specializedDocument = null;
        if (!subscribe)
        {
            CloseSpecializedWorkspace();
            return;
        }
        if (DataContext is not GraphWorkspaceViewModel { IsInlineWorkspace: false } graph) return;
        _specializedDocument = graph.Document;
        _specializedDocument.GpuEditorRequested += OpenGpuWorkspace;
        _specializedDocument.ProceduralEditorRequested += OpenProceduralWorkspace;
    }

    private bool PrepareSpecializedWorkspace(object? sender, Guid nodeId)
    {
        if (sender is not GraphWorkspaceViewModel source || _specializedDocument is not { } document ||
            !ReferenceEquals(source.Document, document)) return false;
        CloseSpecializedWorkspace();
        // Maximize an inline editor into its document's graph area, just like CPU Particle.
        document.NavigateToNode(source.GraphPath, nodeId, frameSelection: false);
        if (!document.Nodes.Any(node => node.Id == nodeId)) return false;
        _specializedNodeId = nodeId;
        document.ActivateEditingWorkspace();
        return true;
    }

    private void OpenGpuWorkspace(object? sender, Guid nodeId)
    {
        if (!PrepareSpecializedWorkspace(sender, nodeId)) return;
        _gpuWorkspace ??= new GpuWorkspaceView();
        _gpuWorkspace.UseDocumentNavigation();
        _gpuWorkspace.PropertyChanged -= OnSpecializedVisibilityChanged;
        _gpuWorkspace.PropertyChanged += OnSpecializedVisibilityChanged;
        SpecializedWorkspace.Content = _gpuWorkspace;
        SpecializedWorkspace.IsVisible = true;
        _gpuWorkspace.Open(_specializedDocument!, nodeId);
        _gpuWorkspace.Workspace!.Refreshed += RefreshSpecializedBreadcrumbs;
        RefreshSpecializedBreadcrumbs(this, EventArgs.Empty);
    }

    private void OpenProceduralWorkspace(object? sender, Guid nodeId)
    {
        if (!PrepareSpecializedWorkspace(sender, nodeId)) return;
        _proceduralWorkspace ??= new ProceduralWorkspaceView { UseDocumentNavigation = true };
        _proceduralWorkspace.PropertyChanged -= OnSpecializedVisibilityChanged;
        _proceduralWorkspace.PropertyChanged += OnSpecializedVisibilityChanged;
        SpecializedWorkspace.Content = _proceduralWorkspace;
        SpecializedWorkspace.IsVisible = true;
        // Open closes an earlier session before making the workspace visible.
        _proceduralWorkspace.Open(_specializedDocument!, nodeId);
        RefreshSpecializedBreadcrumbs(this, EventArgs.Empty);
    }

    private void OnSpecializedVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty && sender is Control { IsVisible: false } &&
            SpecializedWorkspace.IsVisible && ReferenceEquals(SpecializedWorkspace.Content, sender))
            CloseSpecializedWorkspace();
    }

    private void CloseSpecializedWorkspace()
    {
        if (SpecializedWorkspace is null) return;
        _specializedNodeId = null;
        SpecializedWorkspace.IsVisible = false;
        SpecializedWorkspace.Content = null;
        SpecializedBreadcrumbs.Children.Clear();
        if (_gpuWorkspace?.Workspace is { } gpu) gpu.Refreshed -= RefreshSpecializedBreadcrumbs;
        _gpuWorkspace?.CloseWorkspace();
        _proceduralWorkspace?.CloseWorkspace();
    }

    private void RefreshSpecializedBreadcrumbs(object? sender, EventArgs e)
    {
        SpecializedBreadcrumbs.Children.Clear();
        if (_specializedNodeId is not { } id || _specializedDocument?.Nodes.FirstOrDefault(node => node.Id == id) is not { } node)
        {
            CloseSpecializedWorkspace();
            return;
        }
        void Add(string label, Action action)
        {
            var separator = new TextBlock { Text = "›", VerticalAlignment = VerticalAlignment.Center };
            separator.Classes.Add("editor-heading");
            SpecializedBreadcrumbs.Children.Add(separator);
            var button = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { new EditorIconView { Icon = Models.Appearance.EditorIcon.Group },
                    new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } } }, Padding = new Thickness(8, 2) };
            button.Classes.Add("editor-tool");
            Avalonia.Automation.AutomationProperties.SetName(button, label);
            button.Click += (_, _) => action();
            SpecializedBreadcrumbs.Children.Add(button);
        }
        Add(node.DisplayName, () => _gpuWorkspace?.Workspace?.Back());
        if (ReferenceEquals(SpecializedWorkspace.Content, _gpuWorkspace) && _gpuWorkspace?.Workspace?.Kernel is { } kernel)
            Add(kernel.Id, () => { });
    }
}
