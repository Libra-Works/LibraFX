using System.Collections.ObjectModel;
using System.ComponentModel;
using Dock.Model.Mvvm.Controls;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed class NodePropertiesPaneViewModel : Tool
{
    private readonly ActiveGraphProvider _activeGraph;
    private GraphWorkspaceViewModel _graph;
    private GraphNodeViewModel? _node;

    public GraphWorkspaceViewModel Graph => _graph;
    public ObservableCollection<NodeParameterEditorViewModel> Parameters { get; } = [];
    public string PluginStatus => Environment.GetEnvironmentVariable("LIBERAFX_INSPECTOR_PLUGINS") == "1"
        ? string.Join(Environment.NewLine, InspectorEditors.Warnings)
        : "Custom DLL inspectors are disabled. Set LIBERAFX_INSPECTOR_PLUGINS=1 to load trusted editors.";

    public NodePropertiesPaneViewModel(ActiveGraphProvider activeGraph)
    {
        _activeGraph = activeGraph;
        _graph = activeGraph.Graph;
        _activeGraph.PropertyChanged += OnActiveGraphChanged;
        _graph.PropertyChanged += OnGraphChanged;
        RefreshNode();
    }

    private void OnActiveGraphChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActiveGraphProvider.Graph)) return;
        _graph.PropertyChanged -= OnGraphChanged;
        _graph = _activeGraph.Graph;
        _graph.PropertyChanged += OnGraphChanged;
        OnPropertyChanged(nameof(Graph));
        RefreshNode();
    }

    private void OnGraphChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphWorkspaceViewModel.SelectedNode)) RefreshNode();
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphNodeViewModel.Parameters))
            foreach (var parameter in Parameters) parameter.Refresh();
    }

    private void RefreshNode()
    {
        if (_node is not null) _node.PropertyChanged -= OnNodeChanged;
        _node = _graph.SelectedNode;
        Parameters.Clear();
        if (_node is null) return;
        _node.PropertyChanged += OnNodeChanged;
        foreach (var definition in _node.Definition.Parameters)
            Parameters.Add(new NodeParameterEditorViewModel(_graph, _node, definition));
    }
}
