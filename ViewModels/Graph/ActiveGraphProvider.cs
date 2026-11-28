using CommunityToolkit.Mvvm.ComponentModel;

namespace LiberaFx.ViewModels;

public sealed class ActiveGraphProvider : ObservableObject
{
    private GraphWorkspaceViewModel _graph;

    public GraphWorkspaceViewModel Graph
    {
        get => _graph;
        private set => SetProperty(ref _graph, value);
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
