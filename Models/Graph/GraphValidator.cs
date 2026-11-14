namespace LiberaFx.Models.Graph;

public static class GraphValidator
{
    public static void Validate(GraphSnapshot snapshot) => Validate(snapshot, new HashSet<Guid>(), 0);

    public static void Validate(GraphSnapshot snapshot, HashSet<Guid> allIds, int depth)
    {
        if (depth > 12) throw new InvalidDataException("The graph hierarchy is too deep.");
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Nodes);
        ArgumentNullException.ThrowIfNull(snapshot.Connections);
        var definitions = GraphNodeDefinition.All.ToDictionary(definition => definition.Kind);
        var nodes = new Dictionary<Guid, GraphNodeSnapshot>();
        foreach (var node in snapshot.Nodes)
        {
            if (node is null || node.Id == Guid.Empty || !allIds.Add(node.Id) ||
                string.IsNullOrWhiteSpace(node.Name) || !double.IsFinite(node.X) || !double.IsFinite(node.Y) ||
                !definitions.TryGetValue(node.Kind, out var definition) ||
                node.PortType != definition.PortType || node.PortCategory != definition.PortCategory ||
                (node.Kind == GraphNodeKind.FloatFCurve) != (node.Curve is not null) ||
                (node.Kind == GraphNodeKind.GraphGroup) != (node.ChildGraph is not null) ||
                !nodes.TryAdd(node.Id, node))
                throw new InvalidDataException("The project contains an invalid graph node.");
            if (node.Curve is not null) FCurve.Validate(node.Curve);
            if (node.Parameters is { } parameters)
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                if (parameters.Count > 256 || parameters.Any(parameter =>
                        parameter is null || string.IsNullOrWhiteSpace(parameter.Id) || parameter.Id.Length > 128 ||
                        !ids.Add(parameter.Id) || string.IsNullOrWhiteSpace(parameter.Type) ||
                        parameter.Type.Length > 128 || !NodeParameter.IsValidValue(parameter.Type, parameter.Value)))
                    throw new InvalidDataException("The project contains invalid node parameters.");
            }
            if (node.ChildGraph is not null) Validate(node.ChildGraph, allIds, depth + 1);
        }

        var connections = new HashSet<(Guid SourceId, Guid TargetId, string? PortId)>();
        var occupiedInputs = new HashSet<(Guid TargetId, string PortId)>();
        foreach (var connection in snapshot.Connections)
        {
            if (connection is null ||
                !nodes.TryGetValue(connection.SourceNodeId, out var source) ||
                !nodes.TryGetValue(connection.TargetNodeId, out var target) ||
                source.Id == target.Id ||
                source.Kind == GraphNodeKind.GraphGroup || target.Kind == GraphNodeKind.GraphGroup ||
                source.Kind == GraphNodeKind.Comment || target.Kind == GraphNodeKind.Comment ||
                source.PortType != definitions[target.Kind].GetInputPortType(connection.TargetPortId) ||
                source.PortCategory != target.PortCategory ||
                (definitions[target.Kind].InputPorts is null
                    ? connection.TargetPortId is not null
                    : connection.TargetPortId is not { } port ||
                      !definitions[target.Kind].InputPorts!.Any(item => item.Id == port) ||
                      !occupiedInputs.Add((target.Id, port))) ||
                !connections.Add((source.Id, target.Id, connection.TargetPortId)))
                throw new InvalidDataException("The project contains an invalid graph connection.");
        }
    }
}
