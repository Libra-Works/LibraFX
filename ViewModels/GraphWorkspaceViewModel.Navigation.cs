using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed partial class GraphWorkspaceViewModel
{
    public void ReportStatus(string status)
    {
        if (MessageLocalization.ErrorKey(status) is { } key)
        {
            SetConnectionStatus(key);
        }
        else
        {
            ConnectionStatus = status;
        }
    }

    public bool EnterGraph(GraphNodeViewModel node)
    {
        if (!ReferenceEquals(_document, this))
        {
            return MaximizeInlineGraph(node);
        }

        return EnterGraphCore(node, inspectLocked: false);
    }

    private bool EnterGraphCore(GraphNodeViewModel node, bool inspectLocked)
    {
        if (!Nodes.Contains(node) || node.ChildGraph is null || _graphPath.Count >= MaxGraphDepth)
        {
            return false;
        }

        if (node.GroupAsset is { } reference)
        {
            try
            {
                var asset = GroupAssets?.Load(reference.Id)
                    ?? throw new InvalidOperationException("The group library is unavailable.");
                if (asset.Locked && !inspectLocked)
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

    public void NavigateToGraph(int depth)
    {
        if (depth < 0 || depth > _graphPath.Count || depth == _graphPath.Count)
        {
            return;
        }

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
            CreateSnapshot();
            var current = _rootSnapshot;
            foreach (var id in parentPath)
            {
                var group = current.Nodes.FirstOrDefault(node => node.Id == id);
                if (group?.ChildGraph is null) return;
                if (group.GroupAsset is { } reference)
                {
                    var asset = GroupAssets?.Assets.FirstOrDefault(item => item.Id == reference.Id);
                    if (asset is null)
                    {
                        try { asset = GroupAssets?.Load(reference.Id); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                                   InvalidOperationException or ArgumentException)
                        {
                            SetConnectionStatus("Messages.EditLinkedGroupFailed", ex.Message);
                            return;
                        }
                    }
                    if (asset is null)
                    {
                        SetConnectionStatus("Messages.EditLinkedGroupFailed", "The group library is unavailable.");
                        return;
                    }
                    if (asset.Locked)
                    {
                        SetConnectionStatus("Messages.GroupLocked");
                        return;
                    }
                }
                current = group.ChildGraph;
            }
            _graphPath.Clear();
            _graphPath.AddRange(parentPath);
            RestoreCurrentGraph(current);
            RefreshBreadcrumbs();
            SetConnectionStatus("Messages.GraphLevelChanged");
        }
        var target = Nodes.FirstOrDefault(node => node.Id == nodeId);
        if (target is not null)
        {
            SelectNode(target);
            FrameSelectionRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool GroupSelection()
    {
        if (_selectedNodes.Count == 0)
        {
            return false;
        }

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
        ExecuteEdit(new CompositeUndoableCommand(LocalizationService.Shared.Get("Messages.History.GroupNodes"), commands));
        SetConnectionStatus("Messages.NodesGrouped", selected.Length);
        return true;
    }

    public bool CanCreateNode(GraphNodeKind kind, out string? reason)
    {
        if (IsReadOnly)
        {
            reason = LocalizationService.Shared.Get("Inline.Locked");
            return false;
        }
        if ((kind == GraphNodeKind.GraphGroup || kind == GraphNodeKind.SpawnableGroup ||
             CpuParticleSchema.HasChild(kind)) && _graphPath.Count >= MaxGraphDepth)
        {
            reason = "The graph hierarchy has reached its maximum depth.";
            return false;
        }
        try
        {
            ValidateSpawnPlacement([kind]);
            reason = null;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private void ValidateSpawnPlacement(IEnumerable<GraphNodeKind> kinds)
    {
        var additions = kinds.ToArray();
        var parent = _rootSnapshot;
        GraphNodeSnapshot? owner = null;
        var insideCpuStage = false;
        foreach (var id in _graphPath)
        {
            owner = parent.Nodes.First(node => node.Id == id);
            insideCpuStage |= CpuParticleSchema.IsStage(owner.Kind);
            parent = owner.ChildGraph!;
        }

        if (additions.Any(CpuParticleSchema.IsStageOnly) &&
            !insideCpuStage)
        {
            throw new InvalidOperationException("Particle attributes and boundaries belong inside a CPU Spawn/Update stage.");
        }
        if (additions.Any(kind => CpuParticleSchema.IsBoundary(kind) ||
                CpuParticleSchema.Name(kind) == "CpuParticleStageEvent") &&
            (owner is null || !CpuParticleSchema.IsStage(owner.Kind)))
        {
            throw new InvalidOperationException("CPU boundary nodes and Stage Event must be directly in Spawn/Update.");
        }
        if (additions.Any(kind => CpuParticleSchema.IsStage(kind) ||
                CpuParticleSchema.Name(kind) == "CpuParticleSpriteDraw") &&
            (owner is null || !CpuParticleSchema.IsContainer(owner.Kind)))
        {
            throw new InvalidOperationException("CPU Spawn/Update and Sprite Draw belong directly inside CpuParticle.");
        }

        var addsSimulationContainer = additions.Any(kind =>
            kind == GraphNodeKind.SpawnableGroup || CpuParticleSchema.IsContainer(kind) ||
            GpuParticleEditorService.IsGpu(kind));
        if (insideCpuStage && addsSimulationContainer)
        {
            throw new InvalidOperationException("Particle kernels cannot contain Spawnable, CPU Particle, or GPU simulation containers.");
        }
        if (owner is not null && CpuParticleSchema.IsContainer(owner.Kind) &&
            additions.Any(kind => kind == GraphNodeKind.SpawnableGroup ||
                CpuParticleSchema.IsContainer(kind) || GpuParticleEditorService.IsGpu(kind)))
        {
            throw new InvalidOperationException("A CPU Particle container can contain only its Spawn/Update stages and Sprite Draw.");
        }

        var ownerDestroyCount = additions.Count(kind => kind.Value == "OwnerDestroy");
        if (ownerDestroyCount != 0 && (owner?.Kind != GraphNodeKind.SpawnableGroup ||
            Nodes.Count(node => node.Definition.Kind.Value == "OwnerDestroy") + ownerDestroyCount > 1))
        {
            throw new InvalidOperationException(LocalizationService.Shared.Get("Messages.OwnerDestroyPlacement"));
        }

        var rootDestroyCount = additions.Count(kind => kind.Value == "OnDestroy");
        if (rootDestroyCount != 0 && (owner is not null ||
            Nodes.Count(node => node.Definition.Kind.Value == "OnDestroy") + rootDestroyCount > 1))
        {
            throw new InvalidOperationException(LocalizationService.Shared.Get("Messages.OnDestroyPlacement"));
        }

        if (additions.Any(kind => GameIoSchema.IsNode(kind) && !GameIoSchema.IsInstanceNode(kind)) && owner is not null)
        {
            throw new InvalidOperationException("Game I/O nodes must be placed directly in the root graph.");
        }

        if (additions.Any(GameIoSchema.IsInstanceNode) && owner?.Kind != GraphNodeKind.SpawnableGroup)
        {
            throw new InvalidOperationException("Instance Game I/O nodes must be placed directly inside a Spawnable Group.");
        }

        if (additions.Any(kind => GroupBoundarySchema.IsInput(kind) || GroupBoundarySchema.IsOutput(kind)) &&
            owner?.Kind != GraphNodeKind.GraphGroup)
        {
            throw new InvalidOperationException("Group boundary nodes must be placed directly inside a Graph Group.");
        }

        var newInputLanes = additions.Where(SpawnableGroupSchema.IsInput)
            .Sum(kind => SpawnableGroupSchema.InputWidth(GraphNodeDefinition.All.Single(item => item.Kind == kind).PortType));
        if (newInputLanes != 0)
        {
            if (owner?.Kind != GraphNodeKind.SpawnableGroup)
            {
                throw new InvalidOperationException("Spawn inputs must be placed directly inside a Spawnable Group.");
            }

            var existingInputLanes = Nodes.Where(node => SpawnableGroupSchema.IsInput(node.Definition.Kind))
                .Sum(node => SpawnableGroupSchema.InputWidth(node.Definition.PortType));
            if (existingInputLanes + newInputLanes > SpawnableGroupSchema.MaxInputLanes)
            {
                throw new InvalidOperationException($"A Spawnable Group supports at most {SpawnableGroupSchema.MaxInputLanes} input value lanes.");
            }
        }
    }

    private bool InsideSpawnable()
    {
        var graph = _rootSnapshot;
        foreach (var id in _graphPath)
        {
            var node = graph.Nodes.First(item => item.Id == id);
            if (node.Kind == GraphNodeKind.SpawnableGroup)
            {
                return true;
            }

            graph = node.ChildGraph!;
        }
        return false;
    }

}
