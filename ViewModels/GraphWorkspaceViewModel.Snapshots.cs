using LiberaFx.Models.Graph;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed partial class GraphWorkspaceViewModel
{
    public GraphSnapshot CreateSnapshot()
    {
        if (!ReferenceEquals(_document, this))
        {
            return _document.CreateSnapshot();
        }

        var current = CreateCurrentSnapshot() with
        {
            AutoDestroyWhenIdle = GetCurrentGraph().AutoDestroyWhenIdle
        };
        _rootSnapshot = ReplaceGraph(_rootSnapshot, _graphPath, 0, current) with
        {
            ResourceIdentities = _rootSnapshot.ResourceIdentities
        };
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
                node.GroupOverrides) { SignalKeys = node.SignalKeys })
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
        if (depth == path.Count)
        {
            return replacement;
        }

        var found = false;
        var nodes = root.Nodes.Select(node =>
        {
            if (node.Id != path[depth] || node.ChildGraph is null)
            {
                return node;
            }

            found = true;
            return node with { ChildGraph = ReplaceGraph(node.ChildGraph, path, depth + 1, replacement) };
        }).ToArray();
        if (!found)
        {
            throw new InvalidOperationException("The graph hierarchy no longer exists.");
        }

        var changed = nodes.Single(node => node.Id == path[depth]);
        var connections = root.Connections;
        if (CpuParticleSchema.IsContainer(changed.Kind))
        {
            var definition = CpuParticleSchema.Effective(GraphNodeDefinition.All.Single(item => item.Kind == changed.Kind), changed.ChildGraph);
            var inputs = definition.InputPorts!.Select(port => port.Id).ToHashSet();
            var outputs = definition.OutputPorts!.Select(port => port.Id).ToHashSet();
            connections = connections.Where(edge =>
                (edge.TargetNodeId != changed.Id || edge.TargetPortId is { } input && inputs.Contains(input)) &&
                (edge.SourceNodeId != changed.Id || edge.SourcePortId is { } output && outputs.Contains(output))).ToArray();
        }
        else if (changed.Kind == GraphNodeKind.SpawnableGroup)
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
        var ids = new Dictionary<Guid, Guid>();
        void Collect(GraphSnapshot graph)
        {
            foreach (var node in graph.Nodes)
            {
                ids.Add(node.Id, Guid.NewGuid());
                if (node.ChildGraph is { } child) Collect(child);
            }
        }
        Collect(source);
        GraphSnapshot Clone(GraphSnapshot graph)
        {
            var nodes = graph.Nodes.Select(node => node with
            {
                Id = ids[node.Id], Parameters = GpuParticleEditorService.RemapReferences(node, ids),
                ChildGraph = node.ChildGraph is null ? null : Clone(node.ChildGraph)
            }).ToArray();
            return graph with
            {
                Nodes = nodes, ExposedItems = GroupExposedItems.Remap(graph.ExposedItems, ids),
                Connections = graph.Connections.Select(connection => new GraphConnectionSnapshot(
                    ids[connection.SourceNodeId], ids[connection.TargetNodeId],
                    SpawnableGroupSchema.RemapPort(graph.Nodes.Single(node => node.Id == connection.TargetNodeId),
                        nodes.Single(node => node.Id == ids[connection.TargetNodeId]).ChildGraph, connection.TargetPortId),
                    SpawnableGroupSchema.RemapPort(graph.Nodes.Single(node => node.Id == connection.SourceNodeId),
                        nodes.Single(node => node.Id == ids[connection.SourceNodeId]).ChildGraph, connection.SourcePortId))).ToArray()
            };
        }
        return Clone(source);
    }

    private GraphSnapshot GetCurrentGraph()
    {
        var graph = _rootSnapshot;
        foreach (var id in _graphPath)
        {
            graph = graph.Nodes.First(node => node.Id == id).ChildGraph!;
        }

        return graph;
    }

    private void RefreshBreadcrumbs()
    {
        OnPropertyChanged(nameof(IsRootGraph));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(IsCpuParticleOverview));
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
        if (owner?.Kind != GraphNodeKind.SpawnableGroup)
        {
            owner = null;
        }

        if (owner is null && _containingSpawnable is null)
        {
            return;
        }

        if (owner is not null && _containingSpawnable is { } current &&
            current.Id == owner.Id && current.Name == owner.Name && current.GroupAsset == owner.GroupAsset &&
            GroupExposedItems.OverridesEqual(current.GroupOverrides, owner.GroupOverrides) &&
            GraphSnapshot.ContentEquals(current.ChildGraph!, owner.ChildGraph!))
        {
            return;
        }

        _containingSpawnable = owner is null ? null : new GraphNodeViewModel(this,
            GraphNodeDefinition.All.Single(definition => definition.Kind == owner.Kind),
            owner.Name, owner.X, owner.Y, owner.Id, owner.Curve, owner.ChildGraph,
            owner.Parameters, owner.GroupAsset, owner.IsPolymorphic, owner.GroupOverrides);
        OnPropertyChanged(nameof(ContainingSpawnable));
        OnPropertyChanged(nameof(InspectorNode));
    }

    public void InitializeFromSnapshot(GraphSnapshot snapshot)
    {
        if (IsInlineWorkspace)
        {
            throw new InvalidOperationException("Initialize the document, not an inline view.");
        }

        if (Nodes.Count != 0 || Connections.Count != 0 || _history.CanUndo || _history.CanRedo)
        {
            throw new InvalidOperationException("A graph can only be initialized once while empty.");
        }

        ValidateSnapshot(snapshot);
        _hierarchyCache.Clear();
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
        foreach (var id in changed)
        {
            NotifyGpuProgramChanged(id);
        }
    }

    private static void ValidateSnapshot(GraphSnapshot snapshot, HashSet<Guid> allIds, int depth)
    {
        GraphValidator.Validate(snapshot, allIds, depth);
    }

    private void RestoreFromHistory(GraphSnapshot snapshot)
    {
        ValidateSnapshot(snapshot);
        _hierarchyCache.Clear();
        _rootSnapshot = snapshot;
        OnPropertyChanged(nameof(AutoDestroyWhenIdle));
        var graph = snapshot;
        var validDepth = 0;
        foreach (var id in _graphPath)
        {
            var child = graph.Nodes.FirstOrDefault(node => node.Id == id)?.ChildGraph;
            if (child is null)
            {
                break;
            }

            graph = child;
            validDepth++;
        }

        if (validDepth < _graphPath.Count)
        {
            _graphPath.RemoveRange(validDepth, _graphPath.Count - validDepth);
        }

        RestoreCurrentGraph(graph);
        RefreshBreadcrumbs();
        SetConnectionStatus("Messages.HistoryRestored");
        SynchronizeInlineWorkspaces(snapshot);
    }

    internal void RestoreSnapshotForCommand(GraphSnapshot snapshot) => _document.RestoreFromHistory(snapshot);

    private bool IsCurrentGraphSpawnable
    {
        get
        {
            if (_graphPath.Count == 0)
            {
                return false;
            }

            var graph = _rootSnapshot;
            GraphNodeSnapshot? owner = null;
            foreach (var id in _graphPath)
            {
                owner = graph.Nodes.FirstOrDefault(node => node.Id == id);
                if (owner?.ChildGraph is not { } child)
                {
                    return false;
                }

                graph = child;
            }
            return owner?.Kind == GraphNodeKind.SpawnableGroup;
        }
    }

    private void CacheCurrentHierarchy(string key) =>
        _hierarchyCache[key] = new CachedHierarchyState(Nodes.ToArray(), Connections.ToArray());

    private void RestoreHierarchy(GraphSnapshot snapshot)
    {
        if (_hierarchyCache.TryGetValue(CurrentHierarchyKey, out var cached) &&
            cached.Nodes.Length == snapshot.Nodes.Count &&
            cached.Connections.Length == snapshot.Connections.Count)
        {
            RestoreCachedHierarchy(cached, snapshot);
            return;
        }
        RestoreCurrentGraph(snapshot);
    }

    private void RestoreCachedHierarchy(CachedHierarchyState cached, GraphSnapshot snapshot)
    {
        var selectedIds = SelectedNodes.Select(node => node.Id).ToArray();
        var snapshotById = snapshot.Nodes.ToDictionary(n => n.Id);
        foreach (var node in cached.Nodes)
        {
            if (snapshotById.TryGetValue(node.Id, out var spec) && spec.ChildGraph is not null)
            {
                node.SetChildGraphValue(spec.ChildGraph);
            }
        }
        _restoringGraph = true;
        try
        {
            SelectNode(null);
            Connections.Clear();
            Nodes.ReplaceAll(cached.Nodes);
            Connections.ReplaceAll(cached.Connections);
            var nodesById = cached.Nodes.ToDictionary(n => n.Id);
            SetSelection(selectedIds.Where(nodesById.ContainsKey).Select(id => nodesById[id]));
            _nextNodeIndex = Nodes.Count;
            _pendingOutputNode = null;
            _pendingOutputPortId = null;
            ConnectionPreviewPoint = null;
            OnPropertyChanged(nameof(PendingOutputNode));
            OnPropertyChanged(nameof(PendingOutputPortId));
        }
        finally { _restoringGraph = false; }
        RefreshSelectedConnections();
        RefreshSignalOrder();
        RefreshTypeDiagnostics();
    }

    private void RestoreCurrentGraph(GraphSnapshot snapshot)
    {
        var selectedIds = SelectedNodes.Select(node => node.Id).ToArray();
        var expandedNodes = Nodes.Where(node => node.IsDetailsExpanded).Select(node => node.Id).ToHashSet();
        var nodesById = new Dictionary<Guid, GraphNodeViewModel>(snapshot.Nodes.Count);
        foreach (var specification in snapshot.Nodes)
        {
            var definition = GraphNodeDefinition.Find(specification.Kind)
                ?? throw new KeyNotFoundException($"Graph node definition '{specification.Kind}' was not found.");
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
                specification.GroupOverrides, specification.SignalKeys)
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

        _restoringGraph = true;
        try
        {
        SelectNode(null);
        Connections.Clear();
        Nodes.ReplaceAll(snapshot.Nodes.Select(specification => nodesById[specification.Id]));
        Connections.ReplaceAll(connections);

        SetSelection(selectedIds.Where(nodesById.ContainsKey).Select(id => nodesById[id]));
        _nextNodeIndex = Nodes.Count;
        _pendingOutputNode = null;
        _pendingOutputPortId = null;
        ConnectionPreviewPoint = null;
        OnPropertyChanged(nameof(PendingOutputNode));
        OnPropertyChanged(nameof(PendingOutputPortId));
        }
        finally { _restoringGraph = false; }
        RefreshSelectedConnections();
        RefreshSignalOrder();
        RefreshTypeDiagnostics();
    }

    public GraphNodeViewModel GetNode(Guid id)
    {
        return Nodes.FirstOrDefault(node => node.Id == id)
            ?? throw new KeyNotFoundException($"Graph node '{id}' was not found.");
    }
}
