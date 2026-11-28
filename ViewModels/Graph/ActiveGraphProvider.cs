using CommunityToolkit.Mvvm.ComponentModel;

namespace LiberaFx.ViewModels.Graph;

public sealed class ActiveGraphProvider : ObservableObject
{
    private GraphWorkspaceViewModel _graph;
    private object? _activeGpuInspector;

    public GraphWorkspaceViewModel Graph
    {
        get => _graph;
        private set => SetProperty(ref _graph, value);
    }

    public object? ActiveGpuInspector
    {
        get => _activeGpuInspector;
        set => SetProperty(ref _activeGpuInspector, value);
    }

    public ActiveGraphProvider(GraphWorkspaceViewModel graph)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
    }

    public void SetGraph(GraphWorkspaceViewModel graph)
    {
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
    }
}
