using Avalonia;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed partial class GraphWorkspaceViewModel
{
    public GraphSnapshot ApplyPlan(LiberaGraphEditPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plan.Nodes);
        ArgumentNullException.ThrowIfNull(plan.Connections);
        if (!Enum.IsDefined(plan.Layout))
        {
            throw new ArgumentException("Unknown graph layout mode.");
        }

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
            {
                throw new ArgumentException("Comment nodes do not have ports.", nameof(plan));
            }

            var targetPortId = specification.TargetPortId;
            if (target.Definition.InputPorts is { } ports)
            {
                targetPortId ??= ports.FirstOrDefault()?.Id;
                if (targetPortId is null || !ports.Any(port => port.Id == targetPortId))
                {
                    throw new ArgumentException("The target input port does not exist.", nameof(plan));
                }

                if (target.Definition.GetInputPortCategory(targetPortId) != GraphPortCategory.Signal &&
                    connectionKeys.Any(key => key.Target == target.Id && key.TargetPort == targetPortId))
                {
                    throw new ArgumentException("The target input is already connected.", nameof(plan));
                }
            }
            else if (targetPortId is not null)
            {
                throw new ArgumentException("The target input port does not exist.", nameof(plan));
            }

            var sourcePortId = specification.SourcePortId;
            if (source.Definition.OutputPorts is { } outputPorts)
            {
                if (sourcePortId is null || !outputPorts.Any(port => port.Id == sourcePortId))
                {
                    throw new ArgumentException("The source output port does not exist.", nameof(plan));
                }
            }
            else if (sourcePortId is not null)
            {
                throw new ArgumentException("The source output port does not exist.", nameof(plan));
            }

            if (source.Definition.GetOutputPortCategory(sourcePortId) == GraphPortCategory.Signal &&
                connectionKeys.Any(key => key.Source == source.Id && key.SourcePort == sourcePortId))
            {
                throw new ArgumentException("A Signal output can have only one destination. Use a Sequence node to branch.", nameof(plan));
            }

            if (source.Definition.GetOutputPortCategory(sourcePortId) != target.Definition.GetInputPortCategory(targetPortId) ||
                (!source.IsPolymorphic && !target.IsPolymorphic &&
                 source.Definition.GetOutputPortType(sourcePortId) != target.Definition.GetInputPortType(targetPortId)))
            {
                throw new ArgumentException(
                    "Port categories and data types must match. " +
                    new GraphConversionRegistry(AvailableNodeDefinitions).Resolve(
                        source.Definition.GetOutputPortType(sourcePortId)!,
                        target.Definition.GetInputPortType(targetPortId)!).Describe(),
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

        if (plan.Layout != GraphLayoutMode.Manual)
        {
            var additions = nodesByKey.Values.ToArray();
            var pinned = plan.Nodes.Where(spec => spec.IsPositionFixed).Select(spec => nodesByKey[spec.Key].Id).ToHashSet();
            var top = Nodes.Count == 0 ? 80 : Nodes.Max(node => GraphFlowLayout.Bounds(node).Bottom) + 80;
            var moves = GraphFlowLayout.Arrange(additions, plannedConnections, Nodes.ToArray(), pinned,
                plan.Layout, new Point(80, top));
            foreach (var move in moves)
            {
                nodesByKey.Values.First(node => node.Id == move.NodeId).SetPosition(move.X, move.Y);
            }
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
        ExecuteEdit(new CompositeUndoableCommand("Apply graph plan", commands));
        _nextNodeIndex += plan.Nodes.Count;
        return CreateCurrentSnapshot();
    }

    private GraphNodeViewModel CreatePlannedNode(LiberaGraphNodeSpec specification)
    {
        if ((specification.Kind == GraphNodeKind.GraphGroup || specification.Kind == GraphNodeKind.SpawnableGroup) && _graphPath.Count >= MaxGraphDepth)
        {
            throw new ArgumentException("The graph hierarchy has reached its maximum depth.");
        }

        var definition = AvailableNodeDefinitions.FirstOrDefault(item => item.Kind == specification.Kind)
            ?? throw new ArgumentException($"Unsupported node kind '{specification.Kind}'.");
        if (specification.IsPolymorphic && !GraphPolymorphism.TryFamily(specification.Kind, out _))
        {
            throw new ArgumentException("The node has no polymorphic family.");
        }

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
}
