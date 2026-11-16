namespace LiberaFx.Models.Graph;

public static class GroupBoundaryLowering
{
    public static GraphSnapshot Lower(GraphSnapshot graph) => Expand(graph, false);
    public static GraphSnapshot LowerGroupContents(GraphSnapshot graph) => Expand(graph, true);

    private static GraphSnapshot Expand(GraphSnapshot graph, bool insideGroup)
    {
        var groups = graph.Nodes.Where(node => node.Kind == GraphNodeKind.GraphGroup)
            .ToDictionary(node => node.Id);
        var nodes = new List<GraphNodeSnapshot>();
        var connections = new List<GraphConnectionSnapshot>();
        foreach (var node in graph.Nodes)
        {
            if (node.Kind == GraphNodeKind.GraphGroup)
            {
                var child = Expand(node.ChildGraph!, true);
                nodes.AddRange(child.Nodes);
                connections.AddRange(child.Connections);
            }
            else if (GroupBoundarySchema.IsInput(node.Kind) || GroupBoundarySchema.IsOutput(node.Kind))
            {
                if (!insideGroup)
                    throw new InvalidDataException("Group boundary nodes must be directly inside a Graph Group.");
                var kind = node.PortType switch
                {
                    "Float" => GraphNodeKind.Float,
                    "FloatX2" => GraphNodeKind.FloatX2,
                    "FloatX3" => GraphNodeKind.FloatX3,
                    "FloatX4" => GraphNodeKind.FloatX4,
                    "Color" => GraphNodeDefinition.All.Single(item => item.TypeName == "Color" &&
                        item.Namespace == "LiberaFx").Kind,
                    "Signal" => GraphNodeKind.Signal,
                    "Matrix" => new GraphNodeKind("MatrixRuntimeBoundary"),
                    _ => throw new InvalidDataException("Unsupported group boundary type.")
                };
                var parameters = node.Parameters;
                if (node.PortCategory == GraphPortCategory.Parameter && node.PortType != "Matrix" &&
                    parameters?.Any(item => item.Id == "value") != true)
                {
                    var definition = GraphNodeDefinition.All.Single(item => item.Kind == node.Kind);
                    var value = definition.Parameters.Single(item => item.Id == "value");
                    parameters = [.. parameters ?? [], new NodeParameterSnapshot(value.Id, value.Type, value.DefaultValue.Clone())];
                }
                nodes.Add(node with { Kind = kind, Parameters = parameters });
            }
            else nodes.Add(node);
        }

        var expandedNodes = nodes.ToDictionary(node => node.Id);
        foreach (var edge in graph.Connections)
        {
            var sourceId = edge.SourceNodeId;
            var targetId = edge.TargetNodeId;
            var sourcePort = edge.SourcePortId;
            var targetPort = edge.TargetPortId;
            if (groups.TryGetValue(sourceId, out var sourceGroup))
            {
                var output = sourceGroup.ChildGraph!.Nodes.SingleOrDefault(node =>
                    GroupBoundarySchema.IsOutput(node.Kind) &&
                    GroupBoundarySchema.OutputPortId(node.Id) == sourcePort)
                    ?? throw new InvalidDataException("Invalid group output boundary.");
                sourceId = output.Id;
                sourcePort = null;
            }
            if (groups.TryGetValue(targetId, out var targetGroup))
            {
                var input = targetGroup.ChildGraph!.Nodes.SingleOrDefault(node =>
                    GroupBoundarySchema.IsInput(node.Kind) &&
                    GroupBoundarySchema.InputPortId(node.Id) == targetPort)
                    ?? throw new InvalidDataException("Invalid group input boundary.");
                targetId = input.Id;
                targetPort = null;
            }
            if (expandedNodes[sourceId].Kind.Value == "MatrixRuntimeBoundary") sourcePort = "value";
            if (expandedNodes[targetId].Kind.Value == "MatrixRuntimeBoundary") targetPort = "matrix";
            connections.Add(new(sourceId, targetId, targetPort, sourcePort));
        }
        return new(nodes, connections);
    }
}
