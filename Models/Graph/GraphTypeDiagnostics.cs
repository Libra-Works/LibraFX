namespace LiberaFx.Models.Graph;

public sealed record GraphTypeDiagnostic(Guid NodeId, string? InputPortId, Guid SourceNodeId,
    string SourceType, string ExpectedType)
{
    public GraphConversionResolution Conversion { get; init; } = new([]);
}

public static class GraphTypeDiagnostics
{
    public static IReadOnlyList<GraphTypeDiagnostic> Analyze(GraphSnapshot graph,
        IEnumerable<GraphNodeDefinition>? availableDefinitions = null, bool includeChildren = true,
        IReadOnlyDictionary<Guid, GraphNodeDefinition>? resolvedDefinitions = null)
    {
        var result = new List<GraphTypeDiagnostic>();
        var conversions = new GraphConversionRegistry(availableDefinitions ?? GraphNodeDefinition.All);
        void Visit(GraphSnapshot current)
        {
            var nodes = current.Nodes.ToDictionary(item => item.Id);
            var effective = ReferenceEquals(current, graph) && resolvedDefinitions is not null
                ? resolvedDefinitions : GraphPolymorphism.Resolve(current);
            foreach (var connection in current.Connections)
            {
                var source = effective[connection.SourceNodeId].GetOutputPortType(connection.SourcePortId);
                var target = effective[connection.TargetNodeId].GetInputPortType(connection.TargetPortId);
                if (source is not null && target is not null && source != target &&
                    !GraphPolymorphism.CanBroadcast(nodes[connection.TargetNodeId], connection.TargetPortId, source, target))
                    result.Add(new(connection.TargetNodeId, connection.TargetPortId,
                        connection.SourceNodeId, source, target)
                    { Conversion = conversions.Resolve(source, target) });
            }
            foreach (var node in includeChildren ? nodes.Values : Enumerable.Empty<GraphNodeSnapshot>())
                if (node.ChildGraph is not null) Visit(node.ChildGraph);
        }
        Visit(graph);
        return result;
    }

    public static void Validate(GraphSnapshot graph)
    {
        var diagnostics = Analyze(graph);
        if (diagnostics.Count == 0) return;
        throw new InvalidDataException(string.Join(Environment.NewLine, diagnostics.Select(item =>
            $"Node {item.NodeId}, input '{item.InputPortId ?? "(default)"}': " +
            $"source {item.SourceNodeId} has type {item.SourceType}; expected {item.ExpectedType}. " +
            "Disconnect the input or connect a compatible source. " + item.Conversion.Describe())));
    }
}
