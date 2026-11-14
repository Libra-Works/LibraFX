using Dock.Model.Mvvm.Controls;

namespace LiberaFx.ViewModels;

public sealed class NodePropertiesPaneViewModel : Tool
{
    private readonly ActiveGraphProvider _activeGraph;

    public GraphWorkspaceViewModel Graph => _activeGraph.Graph;

    public NodePropertiesPaneViewModel(ActiveGraphProvider activeGraph)
    {
        _activeGraph = activeGraph;
        _activeGraph.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActiveGraphProvider.Graph))
            {
                OnPropertyChanged(nameof(Graph));
            }
        };
    }
}
