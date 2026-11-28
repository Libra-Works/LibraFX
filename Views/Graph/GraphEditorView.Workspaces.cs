using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using LiberaFx.ViewModels.Graph;
using LiberaFx.Views.Gpu;
using LiberaFx.Views.Procedural;

namespace LiberaFx.Views.Graph;

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
            previous.SpecializedNavigationRequested -= RestoreSpecializedNavigation;
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
        _specializedDocument.SpecializedNavigationRequested += RestoreSpecializedNavigation;
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
        {
            CloseSpecializedWorkspace();
            _specializedDocument?.RecordGraphNavigation();
        }
    }

    private void RestoreSpecializedNavigation(object? sender, (Guid? Node, string? Kernel) entry)
    {
        if (sender is not GraphWorkspaceViewModel graph || entry.Node is not { } id)
        {
            CloseSpecializedWorkspace();
            return;
        }
        var node = graph.Nodes.FirstOrDefault(item => item.Id == id);
        if (node is null)
        {
            CloseSpecializedWorkspace();
            return;
        }
        if (node.IsGpuParticle)
        {
            OpenGpuWorkspace(graph, id);
            if (entry.Kernel is { } kernel && _gpuWorkspace?.Workspace is { } workspace &&
                workspace.State.Program.Kernels.Any(item => item.Id == kernel))
            {
                workspace.EnterKernel(kernel);
            }
        }
        else if (node.IsProcedural)
        {
            OpenProceduralWorkspace(graph, id);
        }
    }

    private void CloseSpecializedWorkspace()
    {
        if (SpecializedWorkspace is null) return;
        _specializedNodeId = null;
        SpecializedWorkspace.IsVisible = false;
        SpecializedWorkspace.Content = null;
        if (_gpuWorkspace?.Workspace is { } gpu) gpu.Refreshed -= RefreshSpecializedBreadcrumbs;
        _gpuWorkspace?.CloseWorkspace();
        _proceduralWorkspace?.CloseWorkspace();
    }

    private void RefreshSpecializedBreadcrumbs(object? sender, EventArgs e)
    {
        if (_specializedNodeId is not { } id || _specializedDocument?.Nodes.FirstOrDefault(node => node.Id == id) is null)
        {
            CloseSpecializedWorkspace();
            return;
        }
        _specializedDocument.RecordSpecializedNavigation(id,
            ReferenceEquals(SpecializedWorkspace.Content, _gpuWorkspace) ? _gpuWorkspace?.Workspace?.KernelId : null);
    }
}
