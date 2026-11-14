using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Dock.Model.Mvvm.Controls;

namespace LiberaFx.ViewModels;

public sealed class OutlinerViewModel : Tool
{
    private readonly ActiveGraphProvider _activeGraph;
    private readonly HashSet<GraphNodeViewModel> _observedNodes = [];
    private GraphWorkspaceViewModel _graph;
    private string _searchText = string.Empty;

    public ObservableCollection<GraphNodeViewModel> Nodes { get; } = [];

    public GraphWorkspaceViewModel Graph => _graph;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                Refresh();
            }
        }
    }

    public OutlinerViewModel(ActiveGraphProvider activeGraph)
    {
        _activeGraph = activeGraph;
        _graph = activeGraph.Graph;
        _activeGraph.PropertyChanged += OnActiveGraphChanged;
        _graph.Nodes.CollectionChanged += OnNodesChanged;
        Refresh();
    }

    private void OnActiveGraphChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActiveGraphProvider.Graph))
        {
            return;
        }

        _graph.Nodes.CollectionChanged -= OnNodesChanged;
        _graph = _activeGraph.Graph;
        _graph.Nodes.CollectionChanged += OnNodesChanged;
        OnPropertyChanged(nameof(Graph));
        Refresh();
    }

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphNodeViewModel.Name))
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        var current = _graph.Nodes.ToHashSet();
        foreach (var node in _observedNodes.Except(current).ToArray())
        {
            node.PropertyChanged -= OnNodeChanged;
            _observedNodes.Remove(node);
        }

        foreach (var node in current.Except(_observedNodes))
        {
            node.PropertyChanged += OnNodeChanged;
            _observedNodes.Add(node);
        }

        var search = SearchText.Trim();
        var visible = _graph.Nodes
            .Where(node => node.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                           node.Definition.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        for (var index = Nodes.Count - 1; index >= 0; index--)
        {
            if (!visible.Contains(Nodes[index]))
            {
                Nodes.RemoveAt(index);
            }
        }

        for (var index = 0; index < visible.Length; index++)
        {
            var existing = Nodes.IndexOf(visible[index]);
            if (existing < 0)
            {
                Nodes.Insert(index, visible[index]);
            }
            else if (existing != index)
            {
                Nodes.Move(existing, index);
            }
        }

        OnPropertyChanged(nameof(Graph));
    }
}
