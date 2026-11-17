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
    public event EventHandler<Guid>? GpuEditorRequested;
    public event EventHandler<Guid>? GpuProgramChanged;
    private Dictionary<Guid, string> _publishedGpuStates = [];
    public void OpenGpuEditor(GraphNodeViewModel node)
    {
        if (Nodes.Contains(node) && node.IsGpuParticle) GpuEditorRequested?.Invoke(this, node.Id);
    }
    internal void NotifyGpuProgramChanged(Guid nodeId) => GpuProgramChanged?.Invoke(this, nodeId);
    private const int MaxGraphDepth = 12;
    private readonly UndoRedoManager _history;
    public GroupAssetCatalog? GroupAssets { get; }
    public NodeDefinitionCatalog? NodeDefinitions { get; }
    public IReadOnlyList<GraphNodeDefinition> AvailableNodeDefinitions =>
        NodeDefinitions?.AvailableDefinitions ?? GraphNodeDefinition.AuthoringCore;
    private int _nextNodeIndex;
    private GraphNodeViewModel? _selectedNode;
    private readonly List<GraphNodeViewModel> _selectedNodes = [];
    private GraphNodeViewModel? _pendingOutputNode;
    private string? _pendingOutputPortId;
    private Point? _connectionPreviewPoint;
    private string _connectionStatus = LocalizationService.Shared.Get("Messages.ChooseConnection");
    private string? _connectionStatusKey = "Messages.ChooseConnection";
    private object?[] _connectionStatusArgs = [];
    private GraphSnapshot _rootSnapshot = new([], []);
    private readonly List<Guid> _graphPath = [];
    private GraphNodeViewModel? _containingSpawnable;

    public ObservableCollection<GraphNodeViewModel> Nodes { get; } = [];
    public ObservableCollection<GraphNodeViewModel> CommentNodes { get; } = [];
    public ObservableCollection<GraphNodeViewModel> ForegroundNodes { get; } = [];
    public ObservableCollection<GraphBreadcrumb> Breadcrumbs { get; } = [new(LocalizationService.Shared.Get("Messages.Top"), 0)];

    public ObservableCollection<GraphConnectionViewModel> Connections { get; } = [];

    public ObservableCollection<GraphConnectionViewModel> SelectedConnections { get; } = [];
    public ObservableCollection<GraphProblemViewModel> TypeProblems { get; } = [];

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
            OnPropertyChanged(nameof(InspectorNode));
            RefreshSelectedConnections();
            DeleteSelectedNodeCommand.NotifyCanExecuteChanged();
        }
    }

    public bool HasSelection => SelectedNode is not null;

    public bool HasNoSelection => SelectedNode is null;

    public GraphNodeViewModel? ContainingSpawnable => _containingSpawnable;
    public GraphNodeViewModel? InspectorNode => SelectedNode ?? ContainingSpawnable;

    internal bool IsCurrentInspectorTarget(GraphNodeViewModel node) =>
        ReferenceEquals(InspectorNode, node) &&
        (Nodes.Contains(node) || ReferenceEquals(ContainingSpawnable, node));

    public bool IsRootGraph => _graphPath.Count == 0;

    public bool AutoDestroyWhenIdle
    {
        get => _rootSnapshot.AutoDestroyWhenIdle;
        set
        {
            var before = CreateSnapshot();
            if (before.AutoDestroyWhenIdle == value) return;
            _history.Execute(new ReplaceGraphSnapshotCommand(this, before,
                before with { AutoDestroyWhenIdle = value },
                LocalizationService.Shared.Get("Messages.History.AutoDestroyWhenIdle")));
        }
    }

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
        RefreshTypeDiagnostics();
        foreach (var node in Nodes) node.RefreshLocalizedText();
        foreach (var connection in Connections) connection.RefreshLocalizedText();
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
    public string? PendingOutputPortId => _pendingOutputPortId;

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
        _history.StateChanged += (_, _) =>
        {
            PublishGpuChanges();
            RefreshContainingSpawnable();
        };
        GroupAssets = groupAssets;
        NodeDefinitions = nodeDefinitions;
        Nodes.CollectionChanged += OnNodesChanged;
        DeleteSelectedNodeCommand = new RelayCommand(DeleteSelectedNode, () => HasSelection);
        Connections.CollectionChanged += (_, _) =>
        {
            RefreshSelectedConnections();
            RefreshTypeDiagnostics();
            RefreshSignalOrder();
        };
        _history.Configure(CreateSnapshot, RestoreFromHistory, ValidateSnapshot);
        MessageLocalization.Observe(this, static graph => graph.RefreshLocalizedText());
    }

    private void RefreshSignalOrder()
    {
        foreach (var group in Connections.GroupBy(connection => (connection.Source.Id, connection.SourcePortId)))
        {
            var connections = group.ToArray();
            var fanout = connections.Length > 1 &&
                connections[0].Source.Definition.GetOutputPortCategory(connections[0].SourcePortId) == GraphPortCategory.Signal &&
                !(connections[0].Source.Definition.Kind == GraphNodeKind.SpawnableGroup &&
                    connections[0].SourcePortId == "destroy");
            for (var index = 0; index < connections.Length; index++)
                connections[index].SetSignalOrder(fanout ? index + 1 : 0, fanout ? connections.Length : 0);
        }
    }

    internal void RefreshTypeDiagnostics()
    {
        var snapshot = CreateCurrentSnapshot();
        var effective = GraphPolymorphism.Resolve(snapshot);
        foreach (var node in Nodes)
            node.ApplyResolvedDefinition(effective[node.Id]);
        var byId = Nodes.ToDictionary(node => node.Id);
        var problems = new List<GraphProblemViewModel>();
        foreach (var diagnostic in GraphTypeDiagnostics.Analyze(snapshot))
        {
            if (!byId.TryGetValue(diagnostic.NodeId, out var target) ||
                !byId.TryGetValue(diagnostic.SourceNodeId, out var source)) continue;
            var text = LocalizationService.Shared.Get("Messages.GraphTypeMismatch",
                target.Name, target.Id, diagnostic.InputPortId ?? "input",
                source.Name, source.Id, GraphNodeDefinition.DisplayPortType(diagnostic.SourceType),
                GraphNodeDefinition.DisplayPortType(diagnostic.ExpectedType));
            problems.Add(new GraphProblemViewModel(target, text));
        }
        if (!IsCurrentGraphSpawnable)
            foreach (var node in Nodes.Where(node => node.Definition.Kind.Value == "LifeTime"))
                problems.Add(new GraphProblemViewModel(node,
                    LocalizationService.Shared.Get("Messages.LifeTimeSpawnableOnly")));
        var byTarget = problems.ToLookup(problem => problem.Node);
        foreach (var node in Nodes.Where(n => GpuParticleEditorService.IsGpu(n.Definition.Kind)))
            foreach (var diagnostic in GpuParticleEditorService.Diagnostics(GpuParticleEditorService.ReadState(node)))
                problems.Add(new GraphProblemViewModel(node,
                    $"GPU {node.Id} / {diagnostic.KernelId} / {diagnostic.NodeId} / {diagnostic.PinId}: " +
                    $"{diagnostic.Code}: {diagnostic.Message}" +
                    (diagnostic.Span is { } span ? $" [text {span.Start}:{span.Length}]" : "")));
        byTarget = problems.ToLookup(problem => problem.Node);
        foreach (var node in Nodes)
            node.SetDiagnostics(string.Join(Environment.NewLine,
                byTarget[node].Select(problem => problem.Message)));
        TypeProblems.Clear();
        foreach (var problem in problems) TypeProblems.Add(problem);
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
        ValidateSpawnPlacement(snapshot.Nodes.Select(node => node.Kind));
        if (InsideSpawnable() && ContainsSpawnable(snapshot))
            throw new InvalidOperationException("Nested Spawnable Groups are not supported.");
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
                parameters: specification.Parameters, groupAsset: specification.GroupAsset,
                isPolymorphic: specification.IsPolymorphic, groupOverrides: specification.GroupOverrides);
            nodesByOldId.Add(specification.Id, node);
            commands.Add(new AddGraphNodeCommand(this, node, Nodes.Count + commands.Count));
        }

        var connectionIndex = Connections.Count;
        foreach (var connection in snapshot.Connections)
        {
            var oldTarget = snapshot.Nodes.Single(node => node.Id == connection.TargetNodeId);
            var newTarget = nodesByOldId[connection.TargetNodeId];
            commands.Add(new AddGraphConnectionCommand(
                this, nodesByOldId[connection.SourceNodeId], newTarget, connectionIndex++,
                targetPortId: SpawnableGroupSchema.RemapPort(oldTarget, newTarget.ChildGraph, connection.TargetPortId),
                sourcePortId: SpawnableGroupSchema.RemapPort(snapshot.Nodes.Single(node => node.Id == connection.SourceNodeId),
                    nodesByOldId[connection.SourceNodeId].ChildGraph, connection.SourcePortId)));
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
        var current = CreateCurrentSnapshot() with { AutoDestroyWhenIdle = GetCurrentGraph().AutoDestroyWhenIdle };
        _rootSnapshot = ReplaceGraph(_rootSnapshot, _graphPath, 0, current);
        return _rootSnapshot;
    }

    public GraphSnapshot CreateCurrentSnapshot()
    {
        var nodes = Nodes
            .Select(node => new GraphNodeSnapshot(
                node.Id,
                node.BaseDefinition.Kind,
                node.Name,
                node.X,
                node.Y,
                node.BaseDefinition.PortType,
                node.BaseDefinition.PortCategory,
                node.Curve,
                node.ChildGraph,
                node.Parameters,
                node.GroupAsset,
                node.IsPolymorphic,
                node.GroupOverrides))
            .ToArray();
        var connections = Connections
            .Select(connection => new GraphConnectionSnapshot(
                connection.Source.Id,
                connection.Target.Id,
                connection.TargetPortId,
                connection.SourcePortId))
            .ToArray();

        return new GraphSnapshot(nodes, connections, GetCurrentGraph().ExposedItems);
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
        var changed = nodes.Single(node => node.Id == path[depth]);
        var connections = root.Connections;
        if (changed.Kind == GraphNodeKind.SpawnableGroup)
        {
            var valid = changed.ChildGraph!.Nodes.Where(node =>
                    SpawnableGroupSchema.IsInput(node.Kind) || SpawnableGroupSchema.IsSignalInput(node.Kind))
                .Select(node => SpawnableGroupSchema.PortId(node.Id)).Append("spawn").ToHashSet(StringComparer.Ordinal);
            connections = root.Connections.Where(edge => edge.TargetNodeId != changed.Id ||
                edge.TargetPortId is not null && valid.Contains(edge.TargetPortId)).ToArray();
            var validOutputs = changed.ChildGraph.Nodes.Where(node =>
                    SpawnableGroupSchema.IsOutput(node.Kind) || SpawnableGroupSchema.IsSignalOutput(node.Kind))
                .Select(node => SpawnableGroupSchema.OutputPortId(node.Id)).Concat(["then", "destroy"]).ToHashSet();
            connections = connections.Where(edge => edge.SourceNodeId != changed.Id ||
                edge.SourcePortId is not null && validOutputs.Contains(edge.SourcePortId)).ToArray();
        }
        else if (changed.Kind == GraphNodeKind.GraphGroup)
        {
            var validInputs = changed.ChildGraph!.Nodes.Where(node => GroupBoundarySchema.IsInput(node.Kind))
                .Select(node => GroupBoundarySchema.InputPortId(node.Id)).ToHashSet(StringComparer.Ordinal);
            var validOutputs = changed.ChildGraph.Nodes.Where(node => GroupBoundarySchema.IsOutput(node.Kind))
                .Select(node => GroupBoundarySchema.OutputPortId(node.Id)).ToHashSet(StringComparer.Ordinal);
            connections = connections.Where(edge =>
                (edge.TargetNodeId != changed.Id || edge.TargetPortId is not null && validInputs.Contains(edge.TargetPortId)) &&
                (edge.SourceNodeId != changed.Id || edge.SourcePortId is not null && validOutputs.Contains(edge.SourcePortId))).ToArray();
        }
        return root with { Nodes = nodes, Connections = connections };
    }

    private static GraphSnapshot CloneGraph(GraphSnapshot source)
    {
        var ids = source.Nodes.ToDictionary(node => node.Id, _ => Guid.NewGuid());
        var nodes = source.Nodes.Select(node => node with
            {
                Id = ids[node.Id],
                ChildGraph = node.ChildGraph is null ? null : CloneGraph(node.ChildGraph)
            }).ToArray();
        return source with
        {
            Nodes = nodes,
            ExposedItems = GroupExposedItems.Remap(source.ExposedItems, ids),
            Connections = source.Connections.Select(connection => new GraphConnectionSnapshot(
                ids[connection.SourceNodeId], ids[connection.TargetNodeId],
                SpawnableGroupSchema.RemapPort(source.Nodes.Single(node => node.Id == connection.TargetNodeId),
                    nodes.Single(node => node.Id == ids[connection.TargetNodeId]).ChildGraph, connection.TargetPortId),
                SpawnableGroupSchema.RemapPort(source.Nodes.Single(node => node.Id == connection.SourceNodeId),
                    nodes.Single(node => node.Id == ids[connection.SourceNodeId]).ChildGraph, connection.SourcePortId))).ToArray()
        };
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
                    changed |= childConflict || node.GroupOverrides is { Count: > 0 };
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
        static Dictionary<string, string?> PortMap(GraphSnapshot baseline, GraphSnapshot oldChild,
            GraphSnapshot latest, GraphSnapshot newChild)
        {
            var map = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var i = 0; i < baseline.Nodes.Count; ++i)
            {
                var old = oldChild.Nodes[i];
                var groupOutput = GroupBoundarySchema.IsOutput(old.Kind);
                var groupInput = GroupBoundarySchema.IsInput(old.Kind);
                var isOutput = SpawnableGroupSchema.IsOutput(old.Kind);
                if (!SpawnableGroupSchema.IsInput(old.Kind) && !isOutput && !groupInput && !groupOutput) continue;
                var latestIndex = latest.Nodes.ToList().FindIndex(node => node.Id == baseline.Nodes[i].Id &&
                    node.Kind == old.Kind);
                string Port(Guid id) => groupOutput ? GroupBoundarySchema.OutputPortId(id) :
                    groupInput ? GroupBoundarySchema.InputPortId(id) :
                    isOutput ? SpawnableGroupSchema.OutputPortId(id) : SpawnableGroupSchema.PortId(id);
                map[Port(old.Id)] = latestIndex < 0 ? null : Port(newChild.Nodes[latestIndex].Id);
            }
            return map;
        }
        static GraphSnapshot WithMappedConnections(GraphSnapshot graph, GraphNodeSnapshot[] nodes,
            Dictionary<Guid, Dictionary<string, string?>> maps)
        {
            var edges = new List<GraphConnectionSnapshot>();
            foreach (var edge in graph.Connections)
            {
                var mapped = edge;
                if (edge.TargetPortId is { } port && maps.TryGetValue(edge.TargetNodeId, out var map) &&
                    map.TryGetValue(port, out var newPort))
                {
                    if (newPort is null) continue;
                    mapped = mapped with { TargetPortId = newPort };
                }
                if (edge.SourcePortId is { } sourcePort && maps.TryGetValue(edge.SourceNodeId, out var sourceMap) &&
                    sourceMap.TryGetValue(sourcePort, out var newSource))
                {
                    if (newSource is null) continue;
                    mapped = mapped with { SourcePortId = newSource };
                }
                edges.Add(mapped);
            }
            return graph with { Nodes = nodes, Connections = edges };
        }
        GraphSnapshot RewriteNewChildren(GraphSnapshot graph, HashSet<Guid> ancestors)
        {
            var maps = new Dictionary<Guid, Dictionary<string, string?>>();
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
                        if (node.GroupOverrides is not { Count: > 0 } &&
                            GraphSnapshot.EquivalentIgnoringIds(baseline.Graph, child))
                        {
                            var replacement = CloneGraph(current.Graph);
                            if (node.Kind == GraphNodeKind.SpawnableGroup || node.Kind == GraphNodeKind.GraphGroup)
                                maps[node.Id] = PortMap(baseline.Graph, child, current.Graph, replacement);
                            child = replacement;
                            reference = new GroupAssetReference(current.Id, current.Revision, current.Locked);
                            count++;
                        }
                    }
                    return node with { ChildGraph = RewriteNewChildren(child, ancestors), GroupAsset = reference };
                }
                finally { ancestors.Remove(reference.Id); }
            }).ToArray();
            return WithMappedConnections(graph, nodes, maps);
        }
        GraphSnapshot Rewrite(GraphSnapshot graph)
        {
            var maps = new Dictionary<Guid, Dictionary<string, string?>>();
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
                    var replacement = RewriteNewChildren(CloneGraph(current.Graph), [current.Id]);
                    if (node.Kind == GraphNodeKind.SpawnableGroup || node.Kind == GraphNodeKind.GraphGroup)
                        maps[node.Id] = PortMap(baseline.Graph, child, current.Graph, replacement);
                    return node with
                    {
                        ChildGraph = replacement,
                        GroupAsset = new GroupAssetReference(current.Id, current.Revision, current.Locked)
                    };
                }
                return node.ChildGraph is null ? node : node with { ChildGraph = Rewrite(node.ChildGraph) };
            }).ToArray();
            return WithMappedConnections(graph, nodes, maps);
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
        OnPropertyChanged(nameof(IsRootGraph));
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new GraphBreadcrumb(LocalizationService.Shared.Get("Messages.Top"), 0));
        var graph = _rootSnapshot;
        foreach (var id in _graphPath)
        {
            var node = graph.Nodes.First(item => item.Id == id);
            Breadcrumbs.Add(new GraphBreadcrumb(node.Name, Breadcrumbs.Count));
            graph = node.ChildGraph!;
        }
        RefreshContainingSpawnable();
    }

    private void RefreshContainingSpawnable()
    {
        var graph = _rootSnapshot;
        GraphNodeSnapshot? owner = null;
        foreach (var id in _graphPath)
        {
            owner = graph.Nodes.FirstOrDefault(node => node.Id == id);
            if (owner?.ChildGraph is not { } child) { owner = null; break; }
            graph = child;
        }
        if (owner?.Kind != GraphNodeKind.SpawnableGroup) owner = null;
        if (owner is null && _containingSpawnable is null) return;
        if (owner is not null && _containingSpawnable is { } current &&
            current.Id == owner.Id && current.Name == owner.Name && current.GroupAsset == owner.GroupAsset &&
            GroupExposedItems.OverridesEqual(current.GroupOverrides, owner.GroupOverrides) &&
            GraphSnapshot.ContentEquals(current.ChildGraph!, owner.ChildGraph!)) return;
        _containingSpawnable = owner is null ? null : new GraphNodeViewModel(this,
            GraphNodeDefinition.All.Single(definition => definition.Kind == owner.Kind),
            owner.Name, owner.X, owner.Y, owner.Id, owner.Curve, owner.ChildGraph,
            owner.Parameters, owner.GroupAsset, owner.IsPolymorphic, owner.GroupOverrides);
        OnPropertyChanged(nameof(ContainingSpawnable));
        OnPropertyChanged(nameof(InspectorNode));
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
        OnPropertyChanged(nameof(AutoDestroyWhenIdle));
        _history.Clear();
    }

    public static void ValidateSnapshot(GraphSnapshot snapshot) => GraphValidator.Validate(snapshot);

    private void PublishGpuChanges()
    {
        var current = GpuProgramDeliveryService.Containers(CreateSnapshot()).ToDictionary(n => n.Id,
            n => n.Parameters?.FirstOrDefault(p => p.Id == GpuParticleEditorService.ParameterId)?.Value.GetRawText() ?? "{}");
        var changed = _publishedGpuStates.Keys.Concat(current.Keys).Distinct().Where(id =>
            _publishedGpuStates.GetValueOrDefault(id) != current.GetValueOrDefault(id)).ToArray();
        _publishedGpuStates = current;
        foreach (var id in changed) NotifyGpuProgramChanged(id);
    }

    private static void ValidateSnapshot(GraphSnapshot snapshot, HashSet<Guid> allIds, int depth)
    {
        GraphValidator.Validate(snapshot, allIds, depth);
    }

    private void RestoreFromHistory(GraphSnapshot snapshot)
    {
        ValidateSnapshot(snapshot);
        _rootSnapshot = snapshot;
        OnPropertyChanged(nameof(AutoDestroyWhenIdle));
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

    private bool IsCurrentGraphSpawnable
    {
        get
        {
            if (_graphPath.Count == 0) return false;
            var graph = _rootSnapshot;
            GraphNodeSnapshot? owner = null;
            foreach (var id in _graphPath)
            {
                owner = graph.Nodes.FirstOrDefault(node => node.Id == id);
                if (owner?.ChildGraph is not { } child) return false;
                graph = child;
            }
            return owner?.Kind == GraphNodeKind.SpawnableGroup;
        }
    }

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
                specification.GroupAsset,
                specification.IsPolymorphic,
                specification.GroupOverrides)
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
            connections.Add(new GraphConnectionViewModel(source, target,
                specification.TargetPortId, specification.SourcePortId));
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
        _pendingOutputPortId = null;
        ConnectionPreviewPoint = null;
        OnPropertyChanged(nameof(PendingOutputNode));
        OnPropertyChanged(nameof(PendingOutputPortId));
        RefreshTypeDiagnostics();
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
        ValidateSpawnPlacement(plan.Nodes.Where(node => node is not null).Select(node => node.Kind));

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
            .Select(connection => (Source: connection.Source.Id, Target: connection.Target.Id,
                TargetPort: connection.TargetPortId, SourcePort: connection.SourcePortId))
            .ToHashSet();
        var plannedConnections = new List<GraphConnectionSnapshot>(plan.Connections.Count);
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

            if (source.IsComment || target.IsComment)
                throw new ArgumentException("Comment nodes do not have ports.", nameof(plan));

            var targetPortId = specification.TargetPortId;
            if (target.Definition.InputPorts is { } ports)
            {
                targetPortId ??= ports.FirstOrDefault()?.Id;
                if (targetPortId is null || !ports.Any(port => port.Id == targetPortId))
                    throw new ArgumentException("The target input port does not exist.", nameof(plan));
                if (connectionKeys.Any(key => key.Target == target.Id && key.TargetPort == targetPortId))
                    throw new ArgumentException("The target input is already connected.", nameof(plan));
            }
            else if (targetPortId is not null)
                throw new ArgumentException("The target input port does not exist.", nameof(plan));

            var sourcePortId = specification.SourcePortId;
            if (source.Definition.OutputPorts is { } outputPorts)
            {
                if (sourcePortId is null || !outputPorts.Any(port => port.Id == sourcePortId))
                    throw new ArgumentException("The source output port does not exist.", nameof(plan));
            }
            else if (sourcePortId is not null)
                throw new ArgumentException("The source output port does not exist.", nameof(plan));

            if (source.Definition.GetOutputPortCategory(sourcePortId) != target.Definition.GetInputPortCategory(targetPortId) ||
                (!source.IsPolymorphic && !target.IsPolymorphic &&
                 source.Definition.GetOutputPortType(sourcePortId) != target.Definition.GetInputPortType(targetPortId)))
            {
                throw new ArgumentException(
                    "Port categories and data types must match.",
                    nameof(plan));
            }

            if (!connectionKeys.Add((source.Id, target.Id, targetPortId, sourcePortId)))
            {
                throw new ArgumentException("The graph plan contains a duplicate connection.", nameof(plan));
            }

            if (WouldCreateCycle(source.Id, target.Id,
                    connectionKeys.Where(key => key != (source.Id, target.Id, targetPortId, sourcePortId))
                        .Select(key => (key.Source, key.Target))))
            {
                throw new ArgumentException("Connections cannot create a cycle.", nameof(plan));
            }

            commands.Add(new AddGraphConnectionCommand(
                this,
                source,
                target,
                Connections.Count + index,
                targetPortId,
                sourcePortId));
            plannedConnections.Add(new GraphConnectionSnapshot(
                source.Id, target.Id, targetPortId, sourcePortId));
        }

        var current = CreateCurrentSnapshot();
        var plannedNodes = nodesByKey.Values.Select(node => new GraphNodeSnapshot(
            node.Id,
            node.BaseDefinition.Kind,
            node.Name,
            node.X,
            node.Y,
            node.BaseDefinition.PortType,
            node.BaseDefinition.PortCategory,
            node.Curve,
            node.ChildGraph,
            node.Parameters,
            node.GroupAsset,
            node.IsPolymorphic,
            node.GroupOverrides));
        GraphValidator.Validate(new GraphSnapshot(
            current.Nodes.Concat(plannedNodes).ToArray(),
            current.Connections.Concat(plannedConnections).ToArray()));
        _history.Execute(new CompositeUndoableCommand("Apply graph plan", commands));
        _nextNodeIndex += plan.Nodes.Count;
        return CreateCurrentSnapshot();
    }

    public void BeginConnection(GraphNodeViewModel source, string? sourcePortId = null)
    {
        SelectNode(source);
        _pendingOutputPortId = sourcePortId;
        _pendingOutputNode = source;
        OnPropertyChanged(nameof(PendingOutputNode));
        OnPropertyChanged(nameof(PendingOutputPortId));
        SetConnectionStatus("Messages.ChooseInput", source.Name,
            GraphNodeDefinition.DisplayPortType(source.Definition.GetOutputPortType(sourcePortId) ?? source.PortType));
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
        _pendingOutputPortId = null;
        ConnectionPreviewPoint = null;
        OnPropertyChanged(nameof(PendingOutputNode));
        OnPropertyChanged(nameof(PendingOutputPortId));
        SetConnectionStatus("Messages.ChooseConnection");
    }

    public void CompleteConnection(GraphNodeViewModel target, string? targetPortId = null, string? sourcePortId = null)
    {
        SelectNode(target);

        if (_pendingOutputNode is null)
        {
            SetConnectionStatus("Messages.ChooseOutputFirst");
            return;
        }

        var source = _pendingOutputNode;
        sourcePortId ??= _pendingOutputPortId;
        _pendingOutputNode = null;
        _pendingOutputPortId = null;
        ConnectionPreviewPoint = null;
        OnPropertyChanged(nameof(PendingOutputNode));
        OnPropertyChanged(nameof(PendingOutputPortId));
        if (TryConnectNodes(source, target, out var error, targetPortId, sourcePortId))
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
        string? targetPortId = null,
        string? sourcePortId = null)
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

        if (source.Definition.OutputPorts is { } outputPorts)
        {
            if (sourcePortId is null || !outputPorts.Any(port => port.Id == sourcePortId))
            {
                error = "The source output port does not exist.";
                return false;
            }
        }
        else if (sourcePortId is not null)
        {
            error = "The source output port does not exist.";
            return false;
        }

        if (source.Definition.GetOutputPortCategory(sourcePortId) != target.Definition.GetInputPortCategory(targetPortId) ||
            (!source.IsPolymorphic && !target.IsPolymorphic &&
             source.Definition.GetOutputPortType(sourcePortId) != target.Definition.GetInputPortType(targetPortId)))
        {
            error = "Port categories and data types must match.";
            return false;
        }

        if (Connections.Any(connection =>
                connection.Source == source && connection.Target == target &&
                connection.TargetPortId == targetPortId && connection.SourcePortId == sourcePortId))
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

        _history.Execute(new AddGraphConnectionCommand(this, source, target,
            targetPortId: targetPortId, sourcePortId: sourcePortId));
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

    public bool CanPublishGroupParameter(GraphNodeViewModel node, string parameterId)
    {
        if (_graphPath.Count == 0 || !Nodes.Contains(node)) return false;
        var definition = node.Definition.Parameters.FirstOrDefault(parameter => parameter.Id == parameterId);
        if (definition is null || !GroupExposedItems.SupportsParameter(node.Definition, definition))
            return false;
        return GetCurrentGraph().ExposedItems?.Any(item => item.Editor == "parameter" &&
            item.Targets[0].NodeId == node.Id && item.Targets[0].ParameterId == parameterId) != true;
    }

    public Guid PublishGroupParameter(GraphNodeViewModel node, string parameterId)
    {
        if (!CanPublishGroupParameter(node, parameterId))
            throw new InvalidOperationException("The selected parameter cannot be exposed here.");
        var definition = node.Definition.Parameters.Single(parameter => parameter.Id == parameterId);
        var item = new GroupExposedItem(Guid.NewGuid(), $"{node.Name}: {definition.Name}", "", "",
            GetCurrentGraph().ExposedItems?.Count ?? 0, "parameter", [new(node.Id, parameterId)]);
        return PublishGroupItem(item);
    }

    public bool CanPublishGroupCurves() => CanPublishGroupCurves(_selectedNodes);

    public bool CanPublishGroupCurves(IReadOnlyList<GraphNodeViewModel> nodes)
    {
        if (_graphPath.Count == 0 || nodes.Count is not (1 or 3) || nodes.Distinct().Count() != nodes.Count ||
            nodes.Any(node => !Nodes.Contains(node) || node.Curve is null)) return false;
        var existing = (GetCurrentGraph().ExposedItems ?? []).Where(item => item.Editor is "curve" or "vector3Curve")
            .SelectMany(item => item.Targets).Select(target => target.NodeId).ToHashSet();
        return nodes.All(node => !existing.Contains(node.Id));
    }

    public Guid PublishGroupCurves() => PublishGroupCurves(_selectedNodes);

    public Guid PublishGroupCurves(IReadOnlyList<GraphNodeViewModel> nodes)
    {
        if (!CanPublishGroupCurves(nodes))
            throw new InvalidOperationException("Select one or three unexposed F-Curve nodes inside a group.");
        var item = new GroupExposedItem(Guid.NewGuid(),
            nodes.Count == 1 ? nodes[0].Name : "Vector3 Curve", "", "",
            GetCurrentGraph().ExposedItems?.Count ?? 0,
            nodes.Count == 1 ? "curve" : "vector3Curve",
            nodes.Select(node => new GroupExposedTarget(node.Id, null)).ToArray());
        return PublishGroupItem(item);
    }

    private Guid PublishGroupItem(GroupExposedItem item)
    {
        var before = CreateSnapshot();
        var parent = before;
        foreach (var id in _graphPath)
        {
            var owner = parent.Nodes.Single(candidate => candidate.Id == id);
            if (owner.GroupAsset is { } reference)
            {
                var asset = GroupAssets?.Library().Load(reference.Id)
                    ?? throw new InvalidOperationException("The group library is unavailable.");
                if (asset.Locked) throw new InvalidOperationException("The group asset is locked.");
            }
            parent = owner.ChildGraph!;
        }
        var items = (parent.ExposedItems ?? []).Append(item).ToArray();
        GroupExposedItems.Validate(items);
        var after = ReplaceGraph(before, _graphPath, 0, parent with { ExposedItems = items });
        ValidateSnapshot(after);
        _history.Execute(new ReplaceGraphSnapshotCommand(this, before, after,
            LocalizationService.Shared.Get("Messages.History.PublishGroupParameter")));
        return item.Id;
    }

    public void UpdateGroupExposedItem(GraphNodeViewModel group, GroupExposedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        GroupExposedItems.Validate([item]);
        if (!Nodes.Contains(group) || group.ChildGraph is null)
            throw new InvalidOperationException("Select a group in this graph.");
        var before = CreateSnapshot();
        var current = before;
        foreach (var id in _graphPath)
        {
            var owner = current.Nodes.Single(candidate => candidate.Id == id);
            EnsureAssetUnlocked(owner.GroupAsset);
            current = owner.ChildGraph!;
        }
        var original = current.Nodes.Single(candidate => candidate.Id == group.Id);
        EnsureAssetUnlocked(original.GroupAsset);
        var child = original.ChildGraph!;
        var items = child.ExposedItems?.ToArray() ?? [];
        var index = Array.FindIndex(items, candidate => candidate.Id == item.Id);
        if (index < 0) throw new InvalidOperationException("The exposed item no longer exists.");
        var previous = items[index];
        if (previous.Editor != item.Editor || !previous.Targets.SequenceEqual(item.Targets))
            throw new InvalidOperationException("An exposed item's targets cannot be changed here.");
        if (item.Presentation == "slider" || item.Minimum is not null || item.Maximum is not null || item.Step is not null)
        {
            var target = item.Targets.SingleOrDefault();
            var node = target is null ? null : child.Nodes.FirstOrDefault(candidate => candidate.Id == target.NodeId);
            var definition = node is null ? null : GraphNodeDefinition.All.FirstOrDefault(candidate => candidate.Kind == node.Kind);
            var parameter = definition is null ? null :
                GameIoSchema.Effective(RenderNodeSchema.Effective(definition, node!.Parameters), node.Parameters)
                    .Parameters.FirstOrDefault(candidate => candidate.Id == target!.ParameterId);
            if (item.Editor != "parameter" || parameter?.Type is not ("float" or "int"))
                throw new InvalidOperationException("Ranges and sliders require a numeric parameter.");
            var initial = node!.Parameters?.FirstOrDefault(candidate =>
                candidate.Id == parameter.Id && candidate.Type == parameter.Type)?.Value ?? parameter.DefaultValue;
            if (item.Minimum is { } minimum && initial.GetDouble() < minimum ||
                item.Maximum is { } maximum && initial.GetDouble() > maximum)
                throw new InvalidOperationException("The group default is outside the requested range.");
        }
        items[index] = item;
        GroupExposedItems.Validate(items);
        var changedGroup = original with { ChildGraph = child with { ExposedItems = items } };
        var candidateGraph = current with
        {
            Nodes = current.Nodes.Select(node => node.Id == group.Id ? changedGroup : node).ToArray()
        };
        var materialized = GroupExposedItems.Materialize(candidateGraph);
        GraphValidator.Validate(materialized);
        var after = ReplaceGraph(before, _graphPath, 0, candidateGraph);
        ValidateSnapshot(after);
        if (GraphSnapshot.ContentEquals(before, after)) return;
        _history.Execute(new ReplaceGraphSnapshotCommand(this, before, after,
            LocalizationService.Shared.Get("Messages.History.ConfigureGroupParameter")));
    }

    private void EnsureAssetUnlocked(GroupAssetReference? reference)
    {
        if (reference is null) return;
        var asset = GroupAssets?.Library().Load(reference.Id)
            ?? throw new InvalidOperationException("The group library is unavailable.");
        if (asset.Locked) throw new InvalidOperationException("The group asset is locked.");
    }

    public void SetGroupInstanceValue(GraphNodeViewModel node, GroupInstanceValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if ((!Nodes.Contains(node) && !ReferenceEquals(ContainingSpawnable, node)) || node.ChildGraph is null)
            throw new InvalidOperationException("Select a group in this graph.");
        GroupExposedItems.ValidateOverrides([value]);
        var values = node.GroupOverrides?.ToList() ?? [];
        var item = new GroupInstanceValue(value.ItemId, value.Components.Select(component =>
            new GroupInstanceComponent(component.Value?.Clone(),
                component.Curve is { } curve ? new FCurveSnapshot(curve.Keys.ToArray()) : null)).ToArray());
        var index = values.FindIndex(existing => existing.ItemId == item.ItemId);
        if (index < 0) values.Add(item);
        else values[index] = item;
        var result = values.AsReadOnly();
        GroupExposedItems.ValidateOverrides(result);
        if (GroupExposedItems.OverridesEqual(node.GroupOverrides, result)) return;
        ApplyGroupOverrides(node, result);
    }

    private void ApplyGroupOverrides(GraphNodeViewModel node, IReadOnlyList<GroupInstanceValue>? values)
    {
        var before = CreateSnapshot();
        var path = ReferenceEquals(ContainingSpawnable, node)
            ? _graphPath.Take(_graphPath.Count - 1).ToArray() : _graphPath.ToArray();
        var parent = before;
        foreach (var id in path) parent = parent.Nodes.Single(candidate => candidate.Id == id).ChildGraph!;
        var snapshot = parent.Nodes.Single(candidate => candidate.Id == node.Id) with
        {
            GroupOverrides = values
        };
        var materialized = GroupExposedItems.Materialize(new GraphSnapshot([snapshot], []));
        GraphValidator.Validate(materialized);
        if (!ReferenceEquals(ContainingSpawnable, node))
        {
            _history.Execute(new SetGroupOverridesCommand(node, node.GroupOverrides, values));
            return;
        }
        var updated = parent with
        {
            Nodes = parent.Nodes.Select(candidate => candidate.Id == node.Id ? snapshot : candidate).ToArray()
        };
        var after = ReplaceGraph(before, path, 0, updated);
        ValidateSnapshot(after);
        _history.Execute(new ReplaceGraphSnapshotCommand(this, before, after,
            LocalizationService.Shared.Get("Messages.History.GroupInstanceValue")));
    }

    public void ResetGroupInstanceValue(GraphNodeViewModel node, Guid itemId)
    {
        if ((!Nodes.Contains(node) && !ReferenceEquals(ContainingSpawnable, node)) || node.ChildGraph is null)
            throw new InvalidOperationException("Select a group in this graph.");
        if (node.GroupOverrides?.Any(value => value.ItemId == itemId) != true) return;
        var remaining = node.GroupOverrides.Where(value => value.ItemId != itemId).ToArray();
        ApplyGroupOverrides(node, remaining.Length == 0 ? null : remaining);
    }

    public void SetParameter(GraphNodeViewModel node, string id, JsonElement value)
    {
        if (!Nodes.Contains(node)) throw new InvalidOperationException("The node is not in this graph.");
        if (RenderNodeSchema.IsRenderNode(node.Definition) && id == "materialSchema")
            throw new InvalidOperationException("Assign an engine asset to update the material schema.");
        var definition = node.Definition.Parameters.FirstOrDefault(parameter => parameter.Id == id)
            ?? throw new InvalidOperationException("The node parameter no longer exists.");
        if (!NodeParameter.IsValidValue(definition.Type, value))
            throw new ArgumentException("The value is invalid for this parameter.", nameof(value));
        if (RenderNodeSchema.IsRenderNode(node.Definition) && id == "assetPath" &&
            value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } assetPath &&
            !RenderNodeSchema.IsSafeAssetPath(assetPath))
            throw new ArgumentException("The engine asset path is invalid.", nameof(value));
        if (RenderNodeSchema.IsRenderNode(node.Definition) &&
            RenderNodeSchema.Read(GraphNodeDefinition.All.Single(item => item.Kind == node.Definition.Kind), node.Parameters)
                .FirstOrDefault(parameter => parameter.Id == id && parameter.Kind == "texture") is not null &&
            value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: > 0 } texturePath &&
            !RenderNodeSchema.IsSafeAssetPath(texturePath))
            throw new ArgumentException("The texture parameter must use a safe engine asset path.", nameof(value));
        if (NodeParameter.ValuesEqual(node.GetParameterValue(definition), value)) return;
        var parameters = node.Parameters?.ToList() ?? [];
        var index = parameters.FindIndex(parameter => parameter.Id == id);
        var updated = new NodeParameterSnapshot(id, definition.Type, value.Clone());
        if (index < 0) parameters.Add(updated);
        else parameters[index] = updated;
        _history.Execute(new SetNodeParametersCommand(node, node.Parameters, parameters.AsReadOnly(), definition.Name));
    }

    public void AssignRenderAsset(GraphNodeViewModel node, EngineAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!Nodes.Contains(node) || !RenderNodeSchema.IsRenderNode(node.Definition))
            throw new InvalidOperationException("Select a model or sprite render node.");
        var assetPath = RenderNodeSchema.IsSafeAssetPath(asset.Path)
            ? asset.Path
            : throw new ArgumentException("The engine asset path is invalid.", nameof(asset));
        var schema = RenderNodeSchema.Encode(asset);
        var pathDefinition = node.Definition.Parameters.FirstOrDefault(parameter => parameter.Id == "assetPath")
            ?? throw new InvalidOperationException("The render node does not define an asset path.");
        var schemaDefinition = node.Definition.Parameters.FirstOrDefault(parameter => parameter.Id == "materialSchema")
            ?? throw new InvalidOperationException("The render node does not define a material schema.");

        var values = node.Parameters?.ToList() ?? [];
        var oldSchema = node.Parameters?.FirstOrDefault(parameter =>
            parameter.Id == schemaDefinition.Id && parameter.Type == schemaDefinition.Type)?.Value
            ?? schemaDefinition.DefaultValue;
        var oldIds = RenderNodeSchema.Describe(oldSchema.ValueKind == JsonValueKind.String
            ? oldSchema.GetString() : "[]").Select(parameter => parameter.Id).ToHashSet(StringComparer.Ordinal);
        var newIds = RenderNodeSchema.Describe(schema).Select(parameter => parameter.Id).ToHashSet(StringComparer.Ordinal);
        values.RemoveAll(parameter => oldIds.Contains(parameter.Id) && !newIds.Contains(parameter.Id));
        SetValue(values, pathDefinition.Id, pathDefinition.Type, JsonSerializer.SerializeToElement(assetPath));
        SetValue(values, schemaDefinition.Id, schemaDefinition.Type, JsonSerializer.SerializeToElement(schema));

        var updated = values.AsReadOnly();
        var newDefinition = RenderNodeSchema.Effective(
            GraphNodeDefinition.All.Single(definition => definition.Kind == node.Definition.Kind), updated);
        var commands = new List<IUndoableCommand>();
        if (!ParameterSnapshotsEqual(node.Parameters, updated))
            commands.Add(new SetNodeParametersCommand(node, node.Parameters, updated, "engine asset"));
        foreach (var connection in Connections.Where(connection => connection.Target == node).ToArray())
        {
            var sourceCategory = connection.Source.Definition.GetOutputPortCategory(connection.SourcePortId);
            var targetCategory = newDefinition.GetInputPortCategory(connection.TargetPortId);
            var validPort = newDefinition.InputPorts?.Any(port => port.Id == connection.TargetPortId) == true;
            if (!validPort || sourceCategory != targetCategory)
                commands.Add(new RemoveGraphConnectionCommand(this, connection));
        }
        if (commands.Count != 0)
            _history.Execute(new CompositeUndoableCommand("Assign engine asset", commands));

        static void SetValue(List<NodeParameterSnapshot> parameters, string id, string type, JsonElement value)
        {
            var index = parameters.FindIndex(parameter => parameter.Id == id);
            var item = new NodeParameterSnapshot(id, type, value.Clone());
            if (index < 0) parameters.Add(item);
            else parameters[index] = item;
        }

        static bool ParameterSnapshotsEqual(
            IReadOnlyList<NodeParameterSnapshot>? left,
            IReadOnlyList<NodeParameterSnapshot> right) =>
            left is not null && left.Count == right.Count &&
            left.Zip(right).All(pair => pair.First.Id == pair.Second.Id && pair.First.Type == pair.Second.Type &&
                NodeParameter.ValuesEqual(pair.First.Value, pair.Second.Value)) ||
            left is null && right.Count == 0;
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

    public GraphNodeViewModel CreateNode(GraphNodeKind kind, double? x = null, double? y = null, bool isPolymorphic = false)
    {
        if ((kind == GraphNodeKind.GraphGroup || kind == GraphNodeKind.SpawnableGroup) && _graphPath.Count >= MaxGraphDepth)
            throw new InvalidOperationException("The graph hierarchy has reached its maximum depth.");
        ValidateSpawnPlacement([kind]);
        var definition = AvailableNodeDefinitions.FirstOrDefault(item => item.Kind == kind)
            ?? throw new ArgumentException(LocalizationService.Shared.Get("Editor.NodeNotInProject"));
        var family = GraphPolymorphism.TryFamily(kind, out var selectedFamily) ? selectedFamily : null;
        if (isPolymorphic && family is null)
            throw new ArgumentException("The node has no polymorphic family.", nameof(kind));
        var index = _nextNodeIndex++;
        var nodeId = Guid.NewGuid();
        var node = new GraphNodeViewModel(
            this,
            definition,
            isPolymorphic ? LocalizationService.Shared.GetOrDefault(family?.DisplayNameKey ?? definition.DisplayNameKey, definition.Name) : definition.Name,
            x ?? 80 + (index % 4) * 280,
            y ?? 80 + (index / 4) * 160,
            id: nodeId,
            parameters: GpuParticleEditorService.IsGpu(kind) ? GpuParticleEditorService.InitialParameters(nodeId) : null,
            isPolymorphic: isPolymorphic);
        _history.Execute(new AddGraphNodeCommand(this, node, Nodes.Count));
        return node;
    }

    private void ValidateSpawnPlacement(IEnumerable<GraphNodeKind> kinds)
    {
        var additions = kinds.ToArray();
        var parent = _rootSnapshot;
        GraphNodeSnapshot? owner = null;
        var insideSpawnable = false;
        foreach (var id in _graphPath)
        {
            owner = parent.Nodes.First(node => node.Id == id);
            insideSpawnable |= owner.Kind == GraphNodeKind.SpawnableGroup;
            parent = owner.ChildGraph!;
        }

        if (insideSpawnable && additions.Any(kind => kind == GraphNodeKind.SpawnableGroup))
            throw new InvalidOperationException("Nested Spawnable Groups are not supported.");
        var ownerDestroyCount = additions.Count(kind => kind.Value == "OwnerDestroy");
        if (ownerDestroyCount != 0 && (owner?.Kind != GraphNodeKind.SpawnableGroup ||
            Nodes.Count(node => node.Definition.Kind.Value == "OwnerDestroy") + ownerDestroyCount > 1))
            throw new InvalidOperationException(LocalizationService.Shared.Get("Messages.OwnerDestroyPlacement"));
        var rootDestroyCount = additions.Count(kind => kind.Value == "OnDestroy");
        if (rootDestroyCount != 0 && (owner is not null ||
            Nodes.Count(node => node.Definition.Kind.Value == "OnDestroy") + rootDestroyCount > 1))
            throw new InvalidOperationException(LocalizationService.Shared.Get("Messages.OnDestroyPlacement"));
        if (additions.Any(GameIoSchema.IsNode) && owner is not null)
            throw new InvalidOperationException("Game I/O nodes must be placed directly in the root graph.");
        if (additions.Any(kind => GroupBoundarySchema.IsInput(kind) || GroupBoundarySchema.IsOutput(kind)) &&
            owner?.Kind != GraphNodeKind.GraphGroup)
            throw new InvalidOperationException("Group boundary nodes must be placed directly inside a Graph Group.");
        var newInputLanes = additions.Where(SpawnableGroupSchema.IsInput)
            .Sum(kind => SpawnableGroupSchema.InputWidth(GraphNodeDefinition.All.Single(item => item.Kind == kind).PortType));
        if (newInputLanes != 0)
        {
            if (owner?.Kind != GraphNodeKind.SpawnableGroup)
                throw new InvalidOperationException("Spawn inputs must be placed directly inside a Spawnable Group.");
            var existingInputLanes = Nodes.Where(node => SpawnableGroupSchema.IsInput(node.Definition.Kind))
                .Sum(node => SpawnableGroupSchema.InputWidth(node.Definition.PortType));
            if (existingInputLanes + newInputLanes > SpawnableGroupSchema.MaxInputLanes)
                throw new InvalidOperationException($"A Spawnable Group supports at most {SpawnableGroupSchema.MaxInputLanes} input value lanes.");
        }
    }

    private bool InsideSpawnable()
    {
        var graph = _rootSnapshot;
        foreach (var id in _graphPath)
        {
            var node = graph.Nodes.First(item => item.Id == id);
            if (node.Kind == GraphNodeKind.SpawnableGroup) return true;
            graph = node.ChildGraph!;
        }
        return false;
    }

    private static bool ContainsSpawnable(GraphSnapshot graph) => graph.Nodes.Any(node =>
        node.Kind == GraphNodeKind.SpawnableGroup ||
        node.ChildGraph is { } child && ContainsSpawnable(child));

    public void DeleteNode(GraphNodeViewModel node)
    {
        _history.Execute(new DeleteGraphNodeCommand(this, node));
    }

    internal void AddNode(GraphNodeViewModel node, int index)
    {
        Nodes.Insert(Math.Clamp(index, 0, Nodes.Count), node);
        SelectNode(node);
        RefreshTypeDiagnostics();
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

        RefreshTypeDiagnostics();
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
        if ((specification.Kind == GraphNodeKind.GraphGroup || specification.Kind == GraphNodeKind.SpawnableGroup) && _graphPath.Count >= MaxGraphDepth)
            throw new ArgumentException("The graph hierarchy has reached its maximum depth.");
        var definition = AvailableNodeDefinitions.FirstOrDefault(item => item.Kind == specification.Kind)
            ?? throw new ArgumentException($"Unsupported node kind '{specification.Kind}'.");
        if (specification.IsPolymorphic && !GraphPolymorphism.TryFamily(specification.Kind, out _))
            throw new ArgumentException("The node has no polymorphic family.");

        if (string.IsNullOrWhiteSpace(specification.Name))
        {
            throw new ArgumentException("Node names must not be empty.");
        }

        if (!double.IsFinite(specification.X) || !double.IsFinite(specification.Y))
        {
            throw new ArgumentException("Node positions must be finite numbers.");
        }

        var plannedId = Guid.NewGuid();
        return new GraphNodeViewModel(
            this,
            definition,
            specification.Name,
            specification.X,
            specification.Y,
            id: plannedId,
            parameters: GpuParticleEditorService.IsGpu(specification.Kind) &&
                specification.Parameters?.Any(p => p.Id == GpuParticleEditorService.ParameterId) != true
                ? [.. specification.Parameters ?? [], .. GpuParticleEditorService.InitialParameters(plannedId)]
                : specification.Parameters,
            isPolymorphic: specification.IsPolymorphic);
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
