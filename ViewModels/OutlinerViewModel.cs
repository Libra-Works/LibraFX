using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using LiberaFx.Models.Graph;

namespace LiberaFx.ViewModels;

public sealed class OutlinerViewModel : Tool
{
    private readonly ActiveGraphProvider _activeGraph;
    private GraphWorkspaceViewModel _graph;
    private string _searchText = string.Empty;
    private bool _refreshPending;
    private bool _refreshing;
    private OutlinerNode? _selectedNode;
    private Dictionary<Guid, OutlinerNode> _nodesById = [];

    public ObservableCollection<OutlinerNode> Nodes { get; } = [];
    public GraphWorkspaceViewModel Graph => _graph;
    public OutlinerNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (SetProperty(ref _selectedNode, value) && !_refreshing && value is not null)
                Select(value);
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
                Refresh();
        }
    }

    public OutlinerViewModel(ActiveGraphProvider activeGraph)
    {
        _activeGraph = activeGraph;
        _graph = activeGraph.Graph;
        _activeGraph.PropertyChanged += OnActiveGraphChanged;
        Subscribe();
        Refresh();
    }

    public void Select(OutlinerNode node) => _graph.NavigateToNode(node.ParentPath, node.Id);

    private void OnActiveGraphChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActiveGraphProvider.Graph)) return;
        Unsubscribe();
        _graph = _activeGraph.Graph;
        Subscribe();
        OnPropertyChanged(nameof(Graph));
        ScheduleRefresh();
    }

    private void Subscribe()
    {
        _graph.History.StateChanged += OnHistoryChanged;
        _graph.Breadcrumbs.CollectionChanged += OnBreadcrumbsChanged;
        _graph.PropertyChanged += OnGraphChanged;
    }

    private void Unsubscribe()
    {
        _graph.History.StateChanged -= OnHistoryChanged;
        _graph.Breadcrumbs.CollectionChanged -= OnBreadcrumbsChanged;
        _graph.PropertyChanged -= OnGraphChanged;
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => ScheduleRefresh();
    private void OnGraphChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphWorkspaceViewModel.SelectedNode) && !_refreshing)
            SyncSelection();
    }

    private void OnBreadcrumbsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleRefresh();

    private void ScheduleRefresh()
    {
        if (_refreshPending) return;
        _refreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshPending = false;
            Refresh();
        }, DispatcherPriority.Background);
    }

    private void Refresh()
    {
        var search = SearchText.Trim();
        var root = _graph.CreateSnapshot();
        var current = new Dictionary<Guid, OutlinerNode>();
        _refreshing = true;
        try
        {
            var nodes = BuildNodes(root, [], search, current);
            Reconcile(Nodes, nodes);
            _nodesById = current;
        }
        finally
        {
            _refreshing = false;
        }
        SyncSelection();
    }

    private void SyncSelection()
    {
        var id = _graph.SelectedNode?.Id;
        var node = id is { } value && _nodesById.TryGetValue(value, out var item) ? item : null;
        _refreshing = true;
        try { SelectedNode = node; }
        finally { _refreshing = false; }
    }

    private IReadOnlyList<OutlinerNode> BuildNodes(GraphSnapshot graph, Guid[] path, string search,
        Dictionary<Guid, OutlinerNode> current)
    {
        var result = new List<OutlinerNode>();
        foreach (var node in graph.Nodes)
        {
            var children = node.ChildGraph is null
                ? []
                : BuildNodes(node.ChildGraph, [.. path, node.Id], search, current);
            var matches = search.Length == 0 ||
                node.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                children.Count > 0 ||
                GraphNodeDefinition.TryFind(node.Kind.Value, out var definition) &&
                definition!.Name.Contains(search, StringComparison.OrdinalIgnoreCase);
            if (matches)
            {
                var item = _nodesById.GetValueOrDefault(node.Id) ?? new OutlinerNode(node.Id);
                item.Update(node.Name, GraphNodeDefinition.DisplayPortType(node.PortType), path);
                Reconcile(item.Children, children);
                current[node.Id] = item;
                result.Add(item);
            }
        }
        return result;
    }

    private static void Reconcile(ObservableCollection<OutlinerNode> target, IReadOnlyList<OutlinerNode> desired)
    {
        for (var index = target.Count - 1; index >= 0; index--)
            if (!desired.Contains(target[index])) target.RemoveAt(index);
        for (var index = 0; index < desired.Count; index++)
        {
            var oldIndex = target.IndexOf(desired[index]);
            if (oldIndex < 0) target.Insert(index, desired[index]);
            else if (oldIndex != index) target.Move(oldIndex, index);
        }
    }
}

public sealed class OutlinerNode(Guid id) : ObservableObject
{
    private string _name = string.Empty;
    private string _portType = string.Empty;
    public Guid Id { get; } = id;
    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public string PortType { get => _portType; private set => SetProperty(ref _portType, value); }
    public Guid[] ParentPath { get; private set; } = [];
    public ObservableCollection<OutlinerNode> Children { get; } = [];

    internal void Update(string name, string portType, Guid[] path)
    {
        Name = name;
        PortType = portType;
        ParentPath = path;
    }
}
