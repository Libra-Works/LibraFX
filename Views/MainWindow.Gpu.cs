using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class MainWindow
{
    private ActiveGraphProvider? _gpuActive;
    private GraphWorkspaceViewModel? _gpuGraph;
    private void OnGpuDataContextChanged(object? sender, EventArgs e)
    {
        DetachGpuWorkspace();
        if (DataContext is not MainViewModel model) return;
        _gpuActive = model.ActiveGraphProvider;
        _gpuActive.PropertyChanged += OnGpuActiveChanged;
        AttachGpuGraph();
    }
    private void OnGpuActiveChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActiveGraphProvider.Graph)) return;
        GpuWorkspace.CloseWorkspace();
        AttachGpuGraph();
    }
    private void AttachGpuGraph()
    {
        if (_gpuGraph is not null) _gpuGraph.GpuEditorRequested -= OnGpuRequested;
        _gpuGraph = _gpuActive?.Graph.Document;
        if (_gpuGraph is not null) _gpuGraph.GpuEditorRequested += OnGpuRequested;
    }
    private void OnGpuRequested(object? sender, Guid container)
    {
        if (sender is GraphWorkspaceViewModel graph) GpuWorkspace.Open(graph, container);
    }
    private void DetachGpuWorkspace()
    {
        if (_gpuActive is not null) _gpuActive.PropertyChanged -= OnGpuActiveChanged;
        if (_gpuGraph is not null) _gpuGraph.GpuEditorRequested -= OnGpuRequested;
        _gpuActive = null; _gpuGraph = null;
        GpuWorkspace.CloseWorkspace();
    }
}
