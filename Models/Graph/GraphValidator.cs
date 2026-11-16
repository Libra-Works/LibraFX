namespace LiberaFx.Models.Graph;

public static class GraphValidator
{
    public static void Validate(GraphSnapshot snapshot) => Validate(snapshot, new HashSet<Guid>(), 0);

    public static void ValidateDocument(GraphSnapshot snapshot) =>
        Validate(snapshot, new HashSet<Guid>(), 0, false, null, true);

    public static void Validate(GraphSnapshot snapshot, HashSet<Guid> allIds, int depth) =>
        Validate(snapshot, allIds, depth, false, null, false);

    private static void Validate(GraphSnapshot snapshot, HashSet<Guid> allIds, int depth, bool insideSpawnable,
        GraphNodeKind? parentKind, bool isDocumentRoot)
    {
        if (depth > 12) throw new InvalidDataException("The graph hierarchy is too deep.");
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Nodes);
        ArgumentNullException.ThrowIfNull(snapshot.Connections);
        GroupExposedItems.Validate(snapshot.ExposedItems);
        if (isDocumentRoot && snapshot.ExposedItems is { Count: > 0 })
            throw new InvalidDataException("Exposed items must belong to a group.");
        var definitions = GraphNodeDefinition.All.ToDictionary(definition => definition.Kind);
        var nodes = new Dictionary<Guid, GraphNodeSnapshot>();
        var effectiveDefinitions = new Dictionary<Guid, GraphNodeDefinition>();
        foreach (var node in snapshot.Nodes)
        {
            if (node is not null && GameIoSchema.IsNode(node.Kind) && depth != 0)
                throw new InvalidDataException("Game I/O nodes must be directly in the root graph.");
            if (insideSpawnable && node is not null && node.Kind == GraphNodeKind.SpawnableGroup)
                throw new InvalidDataException("Nested Spawnable Groups are not supported.");
            if (node is not null && (isDocumentRoot || parentKind is not null && parentKind != GraphNodeKind.GraphGroup) &&
                (GroupBoundarySchema.IsInput(node.Kind) || GroupBoundarySchema.IsOutput(node.Kind)))
                throw new InvalidDataException("Group boundary nodes must be directly inside a Graph Group.");
            if (node is null || node.Id == Guid.Empty || !allIds.Add(node.Id) ||
                string.IsNullOrWhiteSpace(node.Name) || !double.IsFinite(node.X) || !double.IsFinite(node.Y) ||
                !definitions.TryGetValue(node.Kind, out var definition) ||
                (node.IsPolymorphic && !GraphPolymorphism.TryFamily(node.Kind, out _)) ||
                node.PortType != definition.PortType || node.PortCategory != definition.PortCategory ||
                (node.Kind == GraphNodeKind.FloatFCurve) != (node.Curve is not null) ||
                (node.Kind == GraphNodeKind.GraphGroup || node.Kind == GraphNodeKind.SpawnableGroup) != (node.ChildGraph is not null) ||
                (node.GroupAsset is not null &&
                 (node.Kind != GraphNodeKind.GraphGroup && node.Kind != GraphNodeKind.SpawnableGroup || node.GroupAsset.Id == Guid.Empty || node.GroupAsset.Revision < 1)) ||
                !nodes.TryAdd(node.Id, node))
                throw new InvalidDataException("The project contains an invalid graph node.");
            if (node.Curve is not null) FCurve.Validate(node.Curve);
            if (node.GroupOverrides is not null && node.ChildGraph is null)
                throw new InvalidDataException("Only a group can have instance values.");
            GroupExposedItems.ValidateOverrides(node.GroupOverrides);
            if (node.KindDomain == "GpuParticle" && node.Parameters?.Any(p =>
                    p is not null && p.Id == Services.GpuParticleEditorService.ParameterId &&
                    (p.Type != "gpuState" || !Services.GpuParticleEditorService.IsStateValue(p.Value))) == true)
                throw new InvalidDataException("The GPU workspace parameter has an invalid type or authoring shape.");
            if (node.Parameters is { } parameters)
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                if (parameters.Count > 256 || parameters.Any(parameter =>
                        parameter is null || string.IsNullOrWhiteSpace(parameter.Id) || parameter.Id.Length > 128 ||
                        !ids.Add(parameter.Id) || string.IsNullOrWhiteSpace(parameter.Type) ||
                        parameter.Type.Length > 128 || !RenderNodeSchema.IsValidParameterValue(definition, parameter)))
                    throw new InvalidDataException("The project contains invalid node parameters.");
            }
            RenderNodeSchema.ValidateNodeParameters(definition, node.Parameters);
            GameIoSchema.ValidateNodeParameters(definition, node.Parameters);
            if (node.ChildGraph is not null)
                Validate(node.ChildGraph, allIds, depth + 1,
                    insideSpawnable || node.Kind == GraphNodeKind.SpawnableGroup, node.Kind, false);
            if (node.Kind == GraphNodeKind.SpawnableGroup &&
                node.ChildGraph!.Nodes.Count(child => SpawnableGroupSchema.IsInput(child.Kind)) > SpawnableGroupSchema.MaxInputs)
                throw new InvalidDataException("A Spawnable Group supports at most four input ports.");
            effectiveDefinitions.Add(node.Id, GroupBoundarySchema.Effective(SpawnableGroupSchema.Effective(
                GameIoSchema.Effective(RenderNodeSchema.Effective(definition, node.Parameters), node.Parameters),
                node.ChildGraph), node.ChildGraph));
        }

        var connections = new HashSet<(Guid SourceId, string? SourcePortId, Guid TargetId, string? TargetPortId)>();
        var occupiedInputs = new HashSet<(Guid TargetId, string PortId)>();
        foreach (var connection in snapshot.Connections)
        {
            if (connection is null ||
                !nodes.TryGetValue(connection.SourceNodeId, out var source) ||
                !nodes.TryGetValue(connection.TargetNodeId, out var target) ||
                source.Id == target.Id ||
                source.Kind == GraphNodeKind.Comment || target.Kind == GraphNodeKind.Comment ||
                effectiveDefinitions[source.Id].GetOutputPortType(connection.SourcePortId) is null ||
                effectiveDefinitions[target.Id].GetInputPortType(connection.TargetPortId) is null ||
                effectiveDefinitions[source.Id].GetOutputPortCategory(connection.SourcePortId) !=
                effectiveDefinitions[target.Id].GetInputPortCategory(connection.TargetPortId) ||
                (effectiveDefinitions[target.Id].InputPorts is null
                    ? connection.TargetPortId is not null
                    : connection.TargetPortId is not { } port ||
                      !effectiveDefinitions[target.Id].InputPorts!.Any(item => item.Id == port) ||
                      !occupiedInputs.Add((target.Id, port))) ||
                !connections.Add((source.Id, connection.SourcePortId, target.Id, connection.TargetPortId)))
                throw new InvalidDataException("The project contains an invalid graph connection.");
        }
    }
}
