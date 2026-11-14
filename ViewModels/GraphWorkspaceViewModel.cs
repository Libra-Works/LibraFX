using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed class GraphWorkspaceViewModel : ObservableObject
{
    private const int MaxGraphDepth = 12;
    private readonly UndoRedoManager _history;
    public GroupAssetCatalog? GroupAssets { get; }
    public NodeDefinitionCatalog? NodeDefinitions { get; }
    public IReadOnlyList<GraphNodeDefinition> AvailableNodeDefinitions =>
        NodeDefinitions?.AvailableDefinitions ?? GraphNodeDefinition.Core;
    private int _nextNodeIndex;
    private GraphNodeViewModel? _selectedNode;
    private readonly List<GraphNodeViewModel> _selectedNodes = [];
    private GraphNodeViewModel? _pendingOutputNode;
    private Point? _connectionPreviewPoint;
    private string _connectionStatus = LocalizationService.Shared.Get("Messages.ChooseConnection");
    private string? _connectionStatusKey = "Messages.ChooseConnection";
    private object?[] _connectionStatusArgs = [];
    private GraphSnapshot _rootSnapshot = new([], []);
    private readonly List<Guid> _graphPath = [];

    public ObservableCollection<GraphNodeViewModel> Nodes { get; } = [];
    public ObservableCollection<GraphNodeViewModel> CommentNodes { get; } = [];
    public ObservableCollection<GraphNodeViewModel> ForegroundNodes { get; } = [];
    public ObservableCollection<GraphBreadcrumb> Breadcrumbs { get; } = [new(LocalizationService.Shared.Get("Messages.Top"), 0)];

    public ObservableCollection<GraphConnectionViewModel> Connections { get; } = [];

    public ObservableCollection<GraphConnectionViewModel> SelectedConnections { get; } = [];

    public IReadOnlyList<GraphNodeViewModel> SelectedNodes => _selectedNodes;

    public int SelectionCount => _selectedNodes.Count;

    public UndoRedoManager History => _history;

    public GraphNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        private set
        {
            if (!SetProperty(ref _selectedNode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(HasNoSelection));
            OnPropertyChanged(nameof(SelectedNodeName));
            OnPropertyChanged(nameof(SelectedNodeType));
            RefreshSelectedConnections();
            DeleteSelectedNodeCommand.NotifyCanExecuteChanged();
        }
    }

    public bool HasSelection => SelectedNode is not null;

    public bool HasNoSelection => SelectedNode is null;

    public string SelectedNodeName
    {
        get => SelectedNode?.Name ?? string.Empty;
        set
        {
            if (SelectedNode is not null)
            {
                SelectedNode.Name = value;
            }
        }
    }

    public string SelectedNodeType => SelectedNode is { } node
        ? LocalizationService.Shared.GetOrDefault(node.Definition.DisplayNameKey, node.Definition.Name) : string.Empty;

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set
        {
            _connectionStatusKey = null;
            _connectionStatusArgs = [];
            SetProperty(ref _connectionStatus, value);
        }
    }

    internal bool IsRefreshingLocalizedText { get; private set; }

    internal void SetConnectionStatus(string key, params object?[] args)
    {
        ConnectionStatus = LocalizationService.Shared.Get(key, args);
        _connectionStatusKey = key;
        _connectionStatusArgs = args;
    }

    private void RefreshLocalizedText()
    {
        RefreshBreadcrumbs();
        OnPropertyChanged(nameof(SelectedNodeType));
        if (_connectionStatusKey is not { } key) return;
        IsRefreshingLocalizedText = true;
        try
        {
            SetProperty(ref _connectionStatus, LocalizationService.Shared.Get(key, _connectionStatusArgs), nameof(ConnectionStatus));
        }
        finally
        {
            IsRefreshingLocalizedText = false;
        }
    }

    public GraphNodeViewModel? PendingOutputNode => _pendingOutputNode;

    public Point? ConnectionPreviewPoint
    {
        get => _connectionPreviewPoint;
        private set => SetProperty(ref _connectionPreviewPoint, value);
    }

    public IRelayCommand DeleteSelectedNodeCommand { get; }

    public GraphWorkspaceViewModel(UndoRedoManager history, GroupAssetCatalog? groupAssets = null,
        NodeDefinitionCatalog? nodeDefinitions = null)
    {
        _history = history;
        GroupAssets = groupAssets;
        NodeDefinitions = nodeDefinitions;
        Nodes.CollectionChanged += OnNodesChanged;
        DeleteSelectedNodeCommand = new RelayCommand(DeleteSelectedNode, () => HasSelection);
        Connections.CollectionChanged += (_, _) => RefreshSelectedConnections();
        _history.Configure(CreateSnapshot, RestoreFromHistory, ValidateSnapshot);
        MessageLocalization.Observe(this, static graph => graph.RefreshLocalizedText());
    }

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Reset or NotifyCollectionChangedAction.Move)
        {
            CommentNodes.Clear();
            ForegroundNodes.Clear();
            foreach (var node in Nodes)
                (node.IsComment ? CommentNodes : ForegroundNodes).Add(node);
            return;
        }
        if (e.OldItems is not null)
            foreach (GraphNodeViewModel node in e.OldItems)
                (node.IsComment ? CommentNodes : ForegroundNodes).Remove(node);
        if (e.NewItems is not null)
            foreach (GraphNodeViewModel node in e.NewItems)
            {
                var target = node.IsComment ? CommentNodes : ForegroundNodes;
                var index = Nodes.TakeWhile(item => item != node).Count(item => item.IsComment == node.IsComment);
                target.Insert(index, node);
            }
    }

    public void SelectNode(GraphNodeViewModel? node)
    {
        SetSelection(node is null ? [] : [node]);
    }

    public void ToggleSelection(GraphNodeViewModel node)
    {
        if (!Nodes.Contains(node))
        {
            return;
        }

        SetSelection(_selectedNodes.Contains(node)
            ? _selectedNodes.Where(selected => selected != node).ToArray()
            : _selectedNodes.Append(node).ToArray());
    }

    public void SetSelection(IEnumerable<GraphNodeViewModel> nodes)
    {
        var selection = nodes.Where(Nodes.Contains).Distinct().ToArray();
        if (SelectedNode is not null)
        {
            SelectedNode.PropertyChanged -= OnSelectedNodePropertyChanged;
        }

        foreach (var old in _selectedNodes)
        {
            old.SetSelected(false);
        }

        _selectedNodes.Clear();
        _selectedNodes.AddRange(selection);
        foreach (var selected in _selectedNodes)
        {
            selected.SetSelected(true);
        }

        SelectedNode = _selectedNodes.LastOrDefault();
        if (SelectedNode is not null)
        {
            SelectedNode.PropertyChanged += OnSelectedNodePropertyChanged;
        }

        OnPropertyChanged(nameof(SelectionCount));
    }

    public GraphSnapshot CreateSelectionSnapshot()
    {
        var ids = _selectedNodes.Select(node => node.Id).ToHashSet();
        var snapshot = CreateCurrentSnapshot();
        return new GraphSnapshot(
            snapshot.Nodes.Where(node => ids.Contains(node.Id)).ToArray(),
            snapshot.Connections.Where(connection =>
                ids.Contains(connection.SourceNodeId) && ids.Contains(connection.TargetNodeId)).ToArray());
    }

    public void PasteSnapshot(GraphSnapshot snapshot, double x, double y)
    {
        ValidateSnapshot(snapshot, new HashSet<Guid>(), _graphPath.Count);
        if (snapshot.Nodes.Count == 0 || !double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentException("Pasted nodes and a finite paste position are required.");
        }

        var minX = snapshot.Nodes.Min(node => node.X);
        var minY = snapshot.Nodes.Min(node => node.Y);
        var nodesByOldId = new Dictionary<Guid, GraphNodeViewModel>();
        var commands = new List<IUndoableCommand>();
        foreach (var specification in snapshot.Nodes)
        {
            var newX = x + (specification.X - minX);
            var newY = y + (specification.Y - minY);
            if (!double.IsFinite(newX) || !double.IsFinite(newY))
            {
                throw new ArgumentException("Pasted node coordinates are invalid.");
            }

            var definition = GraphNodeDefinition.All.Single(item => item.Kind == specification.Kind);
            var node = new GraphNodeViewModel(this, definition, specification.Name, newX, newY,
                curve: specification.Curve, childGraph: specification.ChildGraph is null ? null : CloneGraph(specification.ChildGraph),
                parameters: specification.Parameters, groupAsset: specification.GroupAsset);
            nodesByOldId.Add(specification.Id, node);
            commands.Add(new AddGraphNodeCommand(this, node, Nodes.Count + commands.Count));
        }

        foreach (var connection in snapshot.Connections)
        {
            commands.Add(new AddGraphConnectionCommand(
                this, nodesByOldId[connection.SourceNodeId], nodesByOldId[connection.TargetNodeId],
                targetPortId: connection.TargetPortId));
        }

        _history.Execute(new CompositeUndoableCommand(LocalizationService.Shared.Get("Messages.History.PasteNodes"), commands));
        _nextNodeIndex += snapshot.Nodes.Count;
        SetSelection(snapshot.Nodes.Select(node => nodesByOldId[node.Id]));
        SetConnectionStatus("Messages.NodesPasted", snapshot.Nodes.Count);
    }

    public void DeleteNodes(IEnumerable<GraphNodeViewModel> nodes)
    {
        var selected = nodes.Where(Nodes.Contains).Distinct().ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        _history.Execute(new CompositeUndoableCommand(
            LocalizationService.Shared.Get("Messages.History.DeleteNodes"),
            selected.Select(node => new DeleteGraphNodeCommand(this, node))));
    }

    public void ReportStatus(string status)
    {
        if (MessageLocalization.ErrorKey(status) is { } key)
            SetConnectionStatus(key);
        else
            ConnectionStatus = status;
    }

    public GraphSnapshot CreateSnapshot()
    {
        var current = CreateCurrentSnapshot();
        _rootSnapshot = ReplaceGraph(_rootSnapshot, _graphPath, 0, current);
        return _rootSnapshot;
    }

    public GraphSnapshot CreateCurrentSnapshot()
    {
        var nodes = Nodes
            .Select(node => new GraphNodeSnapshot(
                node.Id,
                node.Definition.Kind,
                node.Name,
                node.X,
                node.Y,
                node.Definition.PortType,
                node.Definition.PortCategory,
                node.Curve,
                node.ChildGraph,
                node.Parameters,
                node.GroupAsset))
            .ToArray();
        var connections = Connections
            .Select(connection => new GraphConnectionSnapshot(
                connection.Source.Id,
                connection.Target.Id,
                connection.TargetPortId))
            .ToArray();

        return new GraphSnapshot(nodes, connections);
    }

    private static GraphSnapshot ReplaceGraph(GraphSnapshot root, IReadOnlyList<Guid> path, int depth, GraphSnapshot replacement)
    {
        if (depth == path.Count) return replacement;
        var found = false;
        var nodes = root.Nodes.Select(node =>
        {
            if (node.Id != path[depth] || node.ChildGraph is null) return node;
            found = true;
            return node with { ChildGraph = ReplaceGraph(node.ChildGraph, path, depth + 1, replacement) };
        }).ToArray();
        if (!found) throw new InvalidOperationException("The graph hierarchy no longer exists.");
        return root with { Nodes = nodes };
    }

    private static GraphSnapshot CloneGraph(GraphSnapshot source)
    {
        var ids = source.Nodes.ToDictionary(node => node.Id, _ => Guid.NewGuid());
        return new GraphSnapshot(
            source.Nodes.Select(node => node with
            {
                Id = ids[node.Id],
                ChildGraph = node.ChildGraph is null ? null : CloneGraph(node.ChildGraph)
            }).ToArray(),
            source.Connections.Select(connection => new GraphConnectionSnapshot(
                ids[connection.SourceNodeId], ids[connection.TargetNodeId], connection.TargetPortId)).ToArray());
    }

    public bool EnterGraph(GraphNodeViewModel node)
    {
        if (!Nodes.Contains(node) || node.ChildGraph is null || _graphPath.Count >= MaxGraphDepth) return false;
        if (node.GroupAsset is { } reference)
        {
            try
            {
                var asset = GroupAssets?.Library().Load(reference.Id)
                    ?? throw new InvalidOperationException("The group library is unavailable.");
                if (asset.Locked)
                {
                    SetConnectionStatus("Messages.GroupLocked");
                    return false;
                }

            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                       InvalidOperationException or ArgumentException)
            {
                SetConnectionStatus("Messages.EditLinkedGroupFailed", ex.Message);
                return false;
            }
        }
        CreateSnapshot();
        _graphPath.Add(node.Id);
        RestoreCurrentGraph(node.ChildGraph);
        RefreshBreadcrumbs();
        SetConnectionStatus("Messages.EditingNestedGraph");
        return true;
    }

    public GraphNodeViewModel CreateGroupAssetNode(Guid assetId, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentException("A finite position is required.");
        var catalog = GroupAssets ?? throw new InvalidOperationException("The group library is unavailable.");
        var asset = catalog.Library().Load(assetId);
        ValidateSnapshot(asset.Graph, new HashSet<Guid>(), _graphPath.Count + 1);
        var definition = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.GraphGroup);
        var reference = new GroupAssetReference(asset.Id, asset.Revision, asset.Locked);
        var node = new GraphNodeViewModel(this, definition, asset.Name, x, y,
            childGraph: CloneGraph(asset.Graph), groupAsset: reference);
        _history.Execute(new AddGraphNodeCommand(this, node, Nodes.Count));
        SelectNode(node);
        SetConnectionStatus("Messages.GroupAdded", asset.Name, asset.Revision);
        return node;
    }

    public void SaveSelectedGroupAsset()
    {
        if (SelectedNode is not { IsGraphGroup: true } node || node.ChildGraph is null)
            throw new InvalidOperationException("Select one Graph Group to save.");
        var catalog = GroupAssets ?? throw new InvalidOperationException("The group library is unavailable.");
        var library = catalog.Library();
        var asset = node.GroupAsset is { } reference
            ? library.Save(reference.Id, reference.Revision, node.Name, node.ChildGraph)
            : library.Create(node.Name, node.ChildGraph);
        var current = new GroupAssetReference(asset.Id, asset.Revision, asset.Locked);
        _history.Execute(new SetGroupAssetCommand(node, node.GroupAsset, current));
        catalog.Refresh();
        SetConnectionStatus("Messages.GroupSaved", asset.Name, asset.Revision);
    }

    public void ToggleSelectedGroupAssetLock()
    {
        if (SelectedNode?.GroupAsset is not { } reference)
            throw new InvalidOperationException("Select a saved group asset.");
        var catalog = GroupAssets ?? throw new InvalidOperationException("The group library is unavailable.");
        var library = catalog.Library();
        var asset = library.Load(reference.Id);
        asset = library.SetLocked(reference.Id, !asset.Locked);
        catalog.Refresh();
        SetConnectionStatus(asset.Locked ? "Messages.GroupNowLocked" : "Messages.GroupNowUnlocked", asset.Name);
    }

    public IReadOnlyList<GroupUpdateCandidate> FindGroupUpdates()
    {
        var catalog = GroupAssets;
        if (catalog is null || catalog.Assets.Count == 0) return [];
        var library = catalog.Library();
        var latest = catalog.Assets.ToDictionary(asset => asset.Id);
        var results = new List<GroupUpdateCandidate>();
        bool Visit(GraphSnapshot graph)
        {
            var anyConflict = false;
            foreach (var node in graph.Nodes)
            {
                var childConflict = node.ChildGraph is not null && Visit(node.ChildGraph);
                if (node.GroupAsset is { } reference && node.ChildGraph is { } child &&
                    latest.TryGetValue(reference.Id, out var asset) && asset.Revision > reference.Revision)
                {
                    var changed = true;
                    try
                    {
                        var baseline = library.LoadRevision(reference.Id, reference.Revision);
                        changed = !GraphSnapshot.EquivalentIgnoringIds(baseline.Graph, child);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
                    {
                    }
                    changed |= childConflict;
                    results.Add(new GroupUpdateCandidate(node.Id, reference.Id, node.Name,
                        reference.Revision, asset.Revision, changed));
                    anyConflict |= changed;
                }
                anyConflict |= childConflict;
            }
            return anyConflict;
        }
        Visit(CreateSnapshot());
        return results;
    }

    public PreparedGroupUpdates? PrepareGroupUpdates(IReadOnlyList<GroupUpdateCandidate> candidates)
    {
        var catalog = GroupAssets ?? throw new InvalidOperationException("The group library is unavailable.");
        var library = catalog.Library();
        var requested = candidates.Where(candidate => !candidate.HasLocalChanges).ToDictionary(candidate => candidate.NodeId);
        if (requested.Count == 0) return null;
        var currentCandidates = FindGroupUpdates().ToDictionary(candidate => candidate.NodeId);
        if (requested.Any(item => !currentCandidates.TryGetValue(item.Key, out var current) || current != item.Value))
            throw new InvalidOperationException("The graph or group library changed; check for updates again.");
        var approved = requested.Values.Select(item => (item.AssetId, item.CurrentRevision, item.LatestRevision)).ToHashSet();
        var before = CreateSnapshot();
        var count = 0;
        GraphSnapshot RewriteNewChildren(GraphSnapshot graph, HashSet<Guid> ancestors)
        {
            var nodes = graph.Nodes.Select(node =>
            {
                if (node.ChildGraph is not { } child) return node;
                if (node.GroupAsset is not { } reference)
                    return node with { ChildGraph = RewriteNewChildren(child, ancestors) };
                if (!ancestors.Add(reference.Id)) return node;
                try
                {
                    var approvedUpdate = approved.FirstOrDefault(item =>
                        item.AssetId == reference.Id && item.CurrentRevision == reference.Revision);
                    if (approvedUpdate != default)
                    {
                        var baseline = library.LoadRevision(reference.Id, reference.Revision);
                        var current = library.Load(reference.Id);
                        if (current.Revision != approvedUpdate.LatestRevision)
                            throw new InvalidOperationException($"Group '{node.Name}' changed again; check for updates again.");
                        if (GraphSnapshot.EquivalentIgnoringIds(baseline.Graph, child))
                        {
                            child = CloneGraph(current.Graph);
                            reference = new GroupAssetReference(current.Id, current.Revision, current.Locked);
                            count++;
                        }
                    }
                    return node with { ChildGraph = RewriteNewChildren(child, ancestors), GroupAsset = reference };
                }
                finally { ancestors.Remove(reference.Id); }
            }).ToArray();
            return graph with { Nodes = nodes };
        }
        GraphSnapshot Rewrite(GraphSnapshot graph)
        {
            var nodes = graph.Nodes.Select(node =>
            {
                if (node.GroupAsset is { } reference && node.ChildGraph is { } child &&
                    requested.TryGetValue(node.Id, out var candidate) &&
                    reference.Id == candidate.AssetId && reference.Revision == candidate.CurrentRevision)
                {
                    var baseline = library.LoadRevision(reference.Id, reference.Revision);
                    var current = library.Load(reference.Id);
                    if (current.Revision != candidate.LatestRevision)
                        throw new InvalidOperationException($"Group '{node.Name}' changed again; check for updates again.");
                    if (!GraphSnapshot.EquivalentIgnoringIds(baseline.Graph, child))
                        throw new InvalidOperationException($"Group '{node.Name}' has local changes.");
                    count++;
                    return node with
                    {
                        ChildGraph = RewriteNewChildren(CloneGraph(current.Graph), [current.Id]),
                        GroupAsset = new GroupAssetReference(current.Id, current.Revision, current.Locked)
                    };
                }
                return node.ChildGraph is null ? node : node with { ChildGraph = Rewrite(node.ChildGraph) };
            }).ToArray();
            return graph with { Nodes = nodes };
        }
        var after = Rewrite(before);
        if (count == 0) return null;
        ValidateSnapshot(after);
        return new PreparedGroupUpdates(before, after, count);
    }

    public int CommitGroupUpdates(PreparedGroupUpdates prepared)
    {
        if (!GraphSnapshot.ContentEquals(prepared.Before, CreateSnapshot()))
            throw new InvalidOperationException("The graph changed; check for updates again.");
        _history.Execute(new ReplaceGraphSnapshotCommand(this, prepared.Before, prepared.After));
        SetConnectionStatus("Messages.LinkedGroupsUpdated", prepared.Count);
        return prepared.Count;
    }

    public int ApplyGroupUpdates(IReadOnlyList<GroupUpdateCandidate> candidates)
    {
        var prepared = PrepareGroupUpdates(candidates);
        return prepared is null ? 0 : CommitGroupUpdates(prepared);
    }

    public void NavigateToGraph(int depth)
    {
        if (depth < 0 || depth > _graphPath.Count || depth == _graphPath.Count) return;
        CreateSnapshot();
        _graphPath.RemoveRange(depth, _graphPath.Count - depth);
        RestoreCurrentGraph(GetCurrentGraph());
        RefreshBreadcrumbs();
        SetConnectionStatus("Messages.GraphLevelChanged");
    }

    public void NavigateToNode(IReadOnlyList<Guid> parentPath, Guid nodeId)
    {
        if (!_graphPath.SequenceEqual(parentPath))
        {
            NavigateToGraph(0);
            foreach (var id in parentPath)
            {
                var group = Nodes.FirstOrDefault(node => node.Id == id);
                if (group is null || !EnterGraph(group)) return;
            }
        }
        var target = Nodes.FirstOrDefault(node => node.Id == nodeId);
        if (target is not null) SelectNode(target);
    }

    public bool GroupSelection()
    {
        if (_selectedNodes.Count == 0) return false;
        if (_graphPath.Count >= MaxGraphDepth)
        {
            SetConnectionStatus("Messages.MaxGraphDepth");
            return false;
        }
        var selected = _selectedNodes.ToArray();
        var ids = selected.Select(node => node.Id).ToHashSet();
        if (Connections.Any(connection => ids.Contains(connection.Source.Id) != ids.Contains(connection.Target.Id)))
        {
            SetConnectionStatus("Messages.CannotGroupExternalConnections");
            return false;
        }
        var selection = CreateSelectionSnapshot();
        var minX = selection.Nodes.Min(node => node.X);
        var minY = selection.Nodes.Min(node => node.Y);
        var child = selection with
        {
            Nodes = selection.Nodes.Select(node => node with { X = node.X - minX + 80, Y = node.Y - minY + 80 }).ToArray()
        };
        try { ValidateSnapshot(child, new HashSet<Guid>(), _graphPath.Count + 1); }
        catch (InvalidDataException ex)
        {
            ReportStatus(ex.Message);
            return false;
        }
        var definition = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.GraphGroup);
        var group = new GraphNodeViewModel(this, definition, definition.Name, minX, minY, childGraph: child);
        var commands = selected.Select(node => (IUndoableCommand)new DeleteGraphNodeCommand(this, node)).ToList();
        commands.Add(new AddGraphNodeCommand(this, group, Nodes.Count));
        _history.Execute(new CompositeUndoableCommand(LocalizationService.Shared.Get("Messages.History.GroupNodes"), commands));
        SetConnectionStatus("Messages.NodesGrouped", selected.Length);
        return true;
    }

    private GraphSnapshot GetCurrentGraph()
    {
        var graph = _rootSnapshot;
        foreach (var id in _graphPath)
            graph = graph.Nodes.First(node => node.Id == id).ChildGraph!;
        return graph;
    }

    private void RefreshBreadcrumbs()
    {
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new GraphBreadcrumb(LocalizationService.Shared.Get("Messages.Top"), 0));
        var graph = _rootSnapshot;
        foreach (var id in _graphPath)
        {
            var node = graph.Nodes.First(item => item.Id == id);
            Breadcrumbs.Add(new GraphBreadcrumb(node.Name, Breadcrumbs.Count));
            graph = node.ChildGraph!;
        }
    }

    public void InitializeFromSnapshot(GraphSnapshot snapshot)
    {
        if (Nodes.Count != 0 || Connections.Count != 0 || _history.CanUndo || _history.CanRedo)
        {
            throw new InvalidOperationException("A graph can only be initialized once while empty.");
        }

        ValidateSnapshot(snapshot);
        _rootSnapshot = snapshot;
        _graphPath.Clear();
        RestoreCurrentGraph(snapshot);
        RefreshBreadcrumbs();
        _history.Clear();
    }

    public static void ValidateSnapshot(GraphSnapshot snapshot) => GraphValidator.Validate(snapshot);

    private static void ValidateSnapshot(GraphSnapshot snapshot, HashSet<Guid> allIds, int depth)
    {
        GraphValidator.Validate(snapshot, allIds, depth);
    }

    private void RestoreFromHistory(GraphSnapshot snapshot)
    {
        ValidateSnapshot(snapshot);
        _rootSnapshot = snapshot;
        var graph = snapshot;
        var validDepth = 0;
        foreach (var id in _graphPath)
        {
            var child = graph.Nodes.FirstOrDefault(node => node.Id == id)?.ChildGraph;
            if (child is null) break;
            graph = child;
            validDepth++;
        }

        if (validDepth < _graphPath.Count) _graphPath.RemoveRange(validDepth, _graphPath.Count - validDepth);
        RestoreCurrentGraph(graph);
        RefreshBreadcrumbs();
        SetConnectionStatus("Messages.HistoryRestored");
    }

    internal void RestoreSnapshotForCommand(GraphSnapshot snapshot) => RestoreFromHistory(snapshot);

    private void RestoreCurrentGraph(GraphSnapshot snapshot)
    {
        var selectedIds = SelectedNodes.Select(node => node.Id).ToArray();
        var expandedNodes = Nodes.Where(node => node.IsDetailsExpanded).Select(node => node.Id).ToHashSet();
        var definitions = GraphNodeDefinition.All.ToDictionary(definition => definition.Kind);
        var nodesById = new Dictionary<Guid, GraphNodeViewModel>();
        foreach (var specification in snapshot.Nodes)
        {
            var definition = definitions[specification.Kind];
            var node = new GraphNodeViewModel(
                this,
                definition,
                specification.Name,
                specification.X,
                specification.Y,
                specification.Id,
                specification.Curve,
                specification.ChildGraph,
                specification.Parameters,
                specification.GroupAsset)
            {
                IsDetailsExpanded = expandedNodes.Contains(specification.Id)
            };
            nodesById.Add(node.Id, node);
        }

        var connections = new List<GraphConnectionViewModel>(snapshot.Connections.Count);
        foreach (var specification in snapshot.Connections)
        {
            var source = nodesById[specification.SourceNodeId];
            var target = nodesById[specification.TargetNodeId];
            connections.Add(new GraphConnectionViewModel(source, target, specification.TargetPortId));
        }

        SelectNode(null);
        Connections.Clear();
        Nodes.Clear();
        foreach (var specification in snapshot.Nodes)
        {
            Nodes.Add(nodesById[specification.Id]);
        }

        foreach (var connection in connections)
        {
            Connections.Add(connection);
        }

        SetSelection(selectedIds.Where(nodesById.ContainsKey).Select(id => nodesById[id]));
        _nextNodeIndex = Nodes.Count;
        _pendingOutputNode = null;
        ConnectionPreviewPoint = null;
        OnPropertyChanged(nameof(PendingOutputNode));
    }

    public GraphNodeViewModel GetNode(Guid id)
    {
        return Nodes.FirstOrDefault(node => node.Id == id)
            ?? throw new KeyNotFoundException($"Graph node '{id}' was not found.");
    }

    public GraphSnapshot ApplyPlan(LiberaGraphEditPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plan.Nodes);
        ArgumentNullException.ThrowIfNull(plan.Connections);
        if (plan.Nodes.Count == 0)
        {
            throw new ArgumentException("A graph plan must contain at least one node.", nameof(plan));
        }

        var commands = new List<IUndoableCommand>();
        var nodesByKey = new Dictionary<string, GraphNodeViewModel>(StringComparer.Ordinal);
        for (var index = 0; index < plan.Nodes.Count; index++)
        {
            var specification = plan.Nodes[index];
            if (specification is null || string.IsNullOrWhiteSpace(specification.Key))
            {
                throw new ArgumentException("Node keys must not be empty.", nameof(plan));
            }

            var node = CreatePlannedNode(specification);
            if (!nodesByKey.TryAdd(specification.Key, node))
            {
                throw new ArgumentException("Node keys must be unique.", nameof(plan));
            }

            commands.Add(new AddGraphNodeCommand(this, node, Nodes.Count + index));
        }

        var connectionKeys = Connections
            .Select(connection => (Source: connection.Source.Id, Target: connection.Target.Id, Port: connection.TargetPortId))
            .ToHashSet();
        for (var index = 0; index < plan.Connections.Count; index++)
        {
            var specification = plan.Connections[index];
            if (specification is null ||
                string.IsNullOrWhiteSpace(specification.SourceKey) ||
                string.IsNullOrWhiteSpace(specification.TargetKey))
            {
                throw new ArgumentException("Connection node keys must not be empty.", nameof(plan));
            }

            if (!nodesByKey.TryGetValue(specification.SourceKey, out var source) ||
                !nodesByKey.TryGetValue(specification.TargetKey, out var target))
            {
                throw new ArgumentException(
                    "Each connection must reference node keys in the same graph plan.",
                    nameof(plan));
            }

            if (source == target)
            {
                throw new ArgumentException("A node cannot connect to itself.", nameof(plan));
            }

            if (source.IsGraphGroup || target.IsGraphGroup)
                throw new ArgumentException("Group boundary ports are not implemented yet.", nameof(plan));
            if (source.IsComment || target.IsComment)
                throw new ArgumentException("Comment nodes do not have ports.", nameof(plan));

            var targetPortId = specification.TargetPortId;
            if (target.Definition.InputPorts is { } ports)
            {
                targetPortId ??= ports.FirstOrDefault()?.Id;
                if (targetPortId is null || !ports.Any(port => port.Id == targetPortId))
                    throw new ArgumentException("The target input port does not exist.", nameof(plan));
                if (connectionKeys.Any(key => key.Target == target.Id && key.Port == targetPortId))
                    throw new ArgumentException("The target input is already connected.", nameof(plan));
            }
            else if (targetPortId is not null)
                throw new ArgumentException("The target input port does not exist.", nameof(plan));

            if (source.Definition.PortCategory != target.Definition.PortCategory ||
                source.Definition.PortType != target.Definition.GetInputPortType(targetPortId))
            {
                throw new ArgumentException(
                    "Port categories and data types must match.",
                    nameof(plan));
            }

            if (!connectionKeys.Add((source.Id, target.Id, targetPortId)))
            {
                throw new ArgumentException("The graph plan contains a duplicate connection.", nameof(plan));
            }

            if (WouldCreateCycle(source.Id, target.Id,
                    connectionKeys.Where(key => key != (source.Id, target.Id, targetPortId))
                        .Select(key => (key.Source, key.Target))))
            {
                throw new ArgumentException("Connections cannot create a cycle.", nameof(plan));
            }

            commands.Add(new AddGraphConnectionCommand(
                this,
                source,
                target,
                Connections.Count + index,
                targetPortId));
        }

        _history.Execute(new CompositeUndoableCommand("Apply graph plan", commands));
        _nextNodeIndex += plan.Nodes.Count;
        return CreateCurrentSnapshot();
    }

    public void BeginConnection(GraphNodeViewModel source)
    {
        SelectNode(source);
        _pendingOutputNode = source;
        OnPropertyChanged(nameof(PendingOutputNode));
        SetConnectionStatus("Messages.ChooseInput", source.Name, source.DisplayPortType);
    }

    public void PreviewConnection(Point point)
    {
        if (_pendingOutputNode is not null)
        {
            ConnectionPreviewPoint = point;
        }
    }

    public void CancelConnection()
    {
        _pendingOutputNode = null;
        ConnectionPreviewPoint = null;
        OnPropertyChanged(nameof(PendingOutputNode));
        SetConnectionStatus("Messages.ChooseConnection");
    }

    public void CompleteConnection(GraphNodeViewModel target, string? targetPortId = null)
    {
        SelectNode(target);

        if (_pendingOutputNode is null)
        {
            SetConnectionStatus("Messages.ChooseOutputFirst");
            return;
        }

        var source = _pendingOutputNode;
        _pendingOutputNode = null;
        ConnectionPreviewPoint = null;
        OnPropertyChanged(nameof(PendingOutputNode));
        if (TryConnectNodes(source, target, out var error, targetPortId))
        {
            SetConnectionStatus("Messages.NodesConnected", source.Name, target.Name);
        }
        else
        {
            if (error is null)
                SetConnectionStatus("Messages.ConnectionFailed");
            else if (MessageLocalization.ErrorKey(error) is { } key)
                SetConnectionStatus(key);
            else
                ConnectionStatus = error;
        }
    }

    public bool TryConnectNodes(
        GraphNodeViewModel source,
        GraphNodeViewModel target,
        out string? error,
        string? targetPortId = null)
    {
        if (!Nodes.Contains(source) || !Nodes.Contains(target))
        {
            error = "Both nodes must belong to this graph.";
            return false;
        }

        if (source == target)
        {
            error = "A node cannot connect to itself.";
            return false;
        }

        if (source.IsGraphGroup || target.IsGraphGroup)
        {
            error = "Group boundary ports are not implemented yet.";
            return false;
        }
        if (source.IsComment || target.IsComment)
        {
            error = "Comment nodes do not have ports.";
            return false;
        }
        if (target.Definition.InputPorts is { } ports)
        {
            targetPortId ??= ports.FirstOrDefault()?.Id;
            if (targetPortId is null || !ports.Any(port => port.Id == targetPortId))
            {
                error = "The target input port does not exist.";
                return false;
            }
            if (Connections.Any(connection => connection.Target == target && connection.TargetPortId == targetPortId))
            {
                error = "The target input is already connected.";
                return false;
            }
        }
        else if (targetPortId is not null)
        {
            error = "The target input port does not exist.";
            return false;
        }

        if (source.Definition.PortCategory != target.Definition.PortCategory ||
            source.Definition.PortType != target.Definition.GetInputPortType(targetPortId))
        {
            error = "Port categories and data types must match.";
            return false;
        }

        if (Connections.Any(connection =>
                connection.Source == source && connection.Target == target && connection.TargetPortId == targetPortId))
        {
            error = "These ports are already connected.";
            return false;
        }

        if (WouldCreateCycle(source.Id, target.Id,
                Connections.Select(connection => (connection.Source.Id, connection.Target.Id))))
        {
            error = "Connections cannot create a cycle.";
            return false;
        }

        _history.Execute(new AddGraphConnectionCommand(this, source, target, targetPortId: targetPortId));
        error = null;
        return true;
    }

    public void DisconnectConnection(GraphConnectionViewModel connection)
    {
        if (Connections.Contains(connection))
        {
            _history.Execute(new RemoveGraphConnectionCommand(this, connection));
            SetConnectionStatus("Messages.ConnectionRemoved");
        }
    }

    private void RefreshSelectedConnections()
    {
        SelectedConnections.Clear();
        if (SelectedNode is null)
        {
            return;
        }

        foreach (var connection in Connections.Where(connection =>
                     connection.Source == SelectedNode || connection.Target == SelectedNode))
        {
            SelectedConnections.Add(connection);
        }
    }

    private static bool WouldCreateCycle(
        Guid sourceId,
        Guid targetId,
        IEnumerable<(Guid SourceId, Guid TargetId)> connections)
    {
        var outgoing = connections.ToLookup(connection => connection.SourceId, connection => connection.TargetId);
        var visited = new HashSet<Guid>();
        var pending = new Stack<Guid>();
        pending.Push(targetId);
        while (pending.TryPop(out var node))
        {
            if (node == sourceId)
            {
                return true;
            }

            if (visited.Add(node))
            {
                foreach (var next in outgoing[node])
                {
                    pending.Push(next);
                }
            }
        }

        return false;
    }

    public void MoveNode(GraphNodeViewModel node, double x, double y)
    {
        if (node.X == x && node.Y == y)
        {
            return;
        }

        if (node.IsComment)
        {
            var dx = x - node.X;
            var dy = y - node.Y;
            MoveNodes(GetMoveTargets([node]).Select(item => (item, item.X + dx, item.Y + dy)));
            return;
        }
        _history.Execute(new MoveGraphNodeCommand(node, node.X, node.Y, x, y));
    }

    public IReadOnlyList<GraphNodeViewModel> GetMoveTargets(IEnumerable<GraphNodeViewModel> selection)
    {
        var selected = selection.Where(Nodes.Contains).Distinct().ToArray();
        var result = selected.ToHashSet();
        foreach (var comment in selected.Where(node => node.IsComment))
        {
            var right = comment.X + comment.CommentWidth;
            var bottom = comment.Y + comment.CommentHeight;
            foreach (var node in Nodes)
            {
                if (ReferenceEquals(node, comment)) continue;
                var height = node.IsComment ? node.CommentHeight : node.RenderedHeight;
                if (node.X >= comment.X && node.Y >= comment.Y &&
                    node.X + node.RenderedWidth <= right && node.Y + height <= bottom)
                    result.Add(node);
            }
        }
        return Nodes.Where(result.Contains).ToArray();
    }

    public void MoveNodes(IEnumerable<(GraphNodeViewModel Node, double X, double Y)> positions)
    {
        var commands = positions
            .Where(item => Nodes.Contains(item.Node) &&
                           double.IsFinite(item.X) && double.IsFinite(item.Y) &&
                           (item.Node.X != item.X || item.Node.Y != item.Y))
            .Select(item => new MoveGraphNodeCommand(item.Node, item.Node.X, item.Node.Y, item.X, item.Y))
            .ToArray();
        if (commands.Length > 0)
        {
            _history.Execute(new CompositeUndoableCommand(LocalizationService.Shared.Get("Messages.History.MoveNodes"), commands));
        }
    }

    public void RenameNode(GraphNodeViewModel node, string name)
    {
        if (node.Name == name)
        {
            return;
        }

        _history.Execute(new RenameGraphNodeCommand(node, node.Name, name));
    }

    public void SetCurve(GraphNodeViewModel node, FCurveSnapshot curve, string description)
    {
        if (!Nodes.Contains(node) || node.Definition.Kind != GraphNodeKind.FloatFCurve || node.Curve is null)
        {
            throw new InvalidOperationException("Select an F-Curve node in this graph.");
        }

        FCurve.Validate(curve);
        if (node.Curve.Keys.SequenceEqual(curve.Keys))
        {
            return;
        }

        _history.Execute(new SetFCurveCommand(node, node.Curve, curve, description));
    }

    public void SetParameter(GraphNodeViewModel node, string id, JsonElement value)
    {
        if (!Nodes.Contains(node)) throw new InvalidOperationException("The node is not in this graph.");
        var definition = node.Definition.Parameters.FirstOrDefault(parameter => parameter.Id == id)
            ?? throw new InvalidOperationException("The node parameter no longer exists.");
        if (!NodeParameter.IsValidValue(definition.Type, value))
            throw new ArgumentException("The value is invalid for this parameter.", nameof(value));
        if (NodeParameter.ValuesEqual(node.GetParameterValue(definition), value)) return;
        var parameters = node.Parameters?.ToList() ?? [];
        var index = parameters.FindIndex(parameter => parameter.Id == id);
        var updated = new NodeParameterSnapshot(id, definition.Type, value.Clone());
        if (index < 0) parameters.Add(updated);
        else parameters[index] = updated;
        _history.Execute(new SetNodeParametersCommand(node, node.Parameters, parameters.AsReadOnly(), definition.Name));
    }

    internal void SetCommentSize(GraphNodeViewModel node, double width, double height)
    {
        if (!Nodes.Contains(node) || !node.IsComment || !double.IsFinite(width) || !double.IsFinite(height))
            throw new InvalidOperationException("The comment size is invalid.");
        width = Math.Clamp(width, 180, 4000);
        height = Math.Clamp(height, 80, 4000);
        var values = node.Parameters?.ToList() ?? [];
        foreach (var (id, value) in new[] { ("width", width), ("height", height) })
        {
            var item = new NodeParameterSnapshot(id, "float", JsonSerializer.SerializeToElement(value));
            var index = values.FindIndex(existing => existing.Id == id);
            if (index < 0) values.Add(item);
            else values[index] = item;
        }
        var oldWidth = node.GetParameterValue(node.Definition.Parameters.Single(item => item.Id == "width")).GetDouble();
        var oldHeight = node.GetParameterValue(node.Definition.Parameters.Single(item => item.Id == "height")).GetDouble();
        if (oldWidth == width && oldHeight == height) return;
        _history.Execute(new SetNodeParametersCommand(node, node.Parameters, values.AsReadOnly(), LocalizationService.Shared.Get("Messages.History.CommentSize")));
    }

    public GraphNodeViewModel CreateNode(GraphNodeKind kind, double? x = null, double? y = null)
    {
        if (kind == GraphNodeKind.GraphGroup && _graphPath.Count >= MaxGraphDepth)
            throw new InvalidOperationException("The graph hierarchy has reached its maximum depth.");
        var definition = AvailableNodeDefinitions.FirstOrDefault(item => item.Kind == kind)
            ?? throw new ArgumentException(LocalizationService.Shared.Get("Editor.NodeNotInProject"));
        var index = _nextNodeIndex++;
        var node = new GraphNodeViewModel(
            this,
            definition,
            definition.Name,
            x ?? 80 + (index % 4) * 280,
            y ?? 80 + (index / 4) * 160);
        _history.Execute(new AddGraphNodeCommand(this, node, Nodes.Count));
        return node;
    }

    public void DeleteNode(GraphNodeViewModel node)
    {
        _history.Execute(new DeleteGraphNodeCommand(this, node));
    }

    internal void AddNode(GraphNodeViewModel node, int index)
    {
        Nodes.Insert(Math.Clamp(index, 0, Nodes.Count), node);
        SelectNode(node);
    }

    internal int RemoveNode(GraphNodeViewModel node)
    {
        var index = Nodes.IndexOf(node);
        if (index >= 0)
        {
            Nodes.RemoveAt(index);
        }

        if (_selectedNodes.Contains(node))
        {
            SetSelection(_selectedNodes.Where(selected => selected != node).ToArray());
        }

        if (_pendingOutputNode == node)
        {
            CancelConnection();
            SetConnectionStatus("Messages.PendingConnectionDeleted");
        }

        return index;
    }

    internal int AddConnection(GraphConnectionViewModel connection, int index)
    {
        var insertIndex = Math.Clamp(index, 0, Connections.Count);
        Connections.Insert(insertIndex, connection);
        return insertIndex;
    }

    internal int RemoveConnection(GraphConnectionViewModel connection)
    {
        var index = Connections.IndexOf(connection);
        if (index >= 0)
        {
            Connections.RemoveAt(index);
        }

        return index;
    }

    private GraphNodeViewModel CreatePlannedNode(LiberaGraphNodeSpec specification)
    {
        if (specification.Kind == GraphNodeKind.GraphGroup && _graphPath.Count >= MaxGraphDepth)
            throw new ArgumentException("The graph hierarchy has reached its maximum depth.");
        if (!AvailableNodeDefinitions.Any(item => item.Kind == specification.Kind))
        {
            throw new ArgumentException($"Unsupported node kind '{specification.Kind}'.");
        }

        if (string.IsNullOrWhiteSpace(specification.Name))
        {
            throw new ArgumentException("Node names must not be empty.");
        }

        if (!double.IsFinite(specification.X) || !double.IsFinite(specification.Y))
        {
            throw new ArgumentException("Node positions must be finite numbers.");
        }

        var definition = GraphNodeDefinition.All.Single(item => item.Kind == specification.Kind);
        return new GraphNodeViewModel(
            this,
            definition,
            specification.Name,
            specification.X,
            specification.Y);
    }

    private void DeleteSelectedNode()
    {
        DeleteNodes(_selectedNodes.ToArray());
    }

    private void OnSelectedNodePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphNodeViewModel.Name))
        {
            OnPropertyChanged(nameof(SelectedNodeName));
        }
    }

}

public sealed record GraphBreadcrumb(string Name, int Depth)
{
    public bool HasSeparator => Depth > 0;
}
