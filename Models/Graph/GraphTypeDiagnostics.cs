namespace LiberaFx.Models.Graph;

public sealed record GraphTypeDiagnostic(Guid NodeId, string? InputPortId, Guid SourceNodeId,
    string SourceType, string ExpectedType);

public static class GraphTypeDiagnostics
{
    public static IReadOnlyList<GraphTypeDiagnostic> Analyze(GraphSnapshot graph)
    {
        var result = new List<GraphTypeDiagnostic>();
        void Visit(GraphSnapshot current)
        {
            var nodes = current.Nodes.ToDictionary(item => item.Id);
            var effective = GraphPolymorphism.Resolve(current);
            foreach (var connection in current.Connections)
            {
                var source = effective[connection.SourceNodeId].GetOutputPortType(connection.SourcePortId);
                var target = effective[connection.TargetNodeId].GetInputPortType(connection.TargetPortId);
                if (source is not null && target is not null && source != target &&
                    !GraphPolymorphism.CanBroadcast(nodes[connection.TargetNodeId], connection.TargetPortId, source, target))
                    result.Add(new(connection.TargetNodeId, connection.TargetPortId,
                        connection.SourceNodeId, source, target));
            }
            foreach (var node in nodes.Values)
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
            "Disconnect the input or connect a compatible source.")));
    }
}
