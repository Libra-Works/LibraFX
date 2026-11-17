namespace LiberaFx.Models.Graph;

public static class GroupBoundarySchema
{
    public static bool IsInput(GraphNodeKind kind) => kind.Value is
        "GroupInputFloat" or "GroupInputVector2" or "GroupInputVector3" or
        "GroupInputVector4" or "GroupInputColor" or "GroupInputSignal" or "GroupInputMatrix" ||
        GraphEnumBranchSchema.TryGetBoundaryDefinition(kind, out _, out var isInput) && isInput;
    public static bool IsOutput(GraphNodeKind kind) => kind.Value is
        "GroupOutputFloat" or "GroupOutputVector2" or "GroupOutputVector3" or
        "GroupOutputVector4" or "GroupOutputColor" or "GroupOutputSignal" or "GroupOutputMatrix" ||
        GraphEnumBranchSchema.TryGetBoundaryDefinition(kind, out _, out var isInput) && !isInput;
    public static string InputPortId(Guid id) => "p_" + id.ToString("N");
    public static string OutputPortId(Guid id) => "o_" + id.ToString("N");

    public static GraphNodeDefinition Effective(GraphNodeDefinition definition, GraphSnapshot? child)
    {
        if (definition.Kind != GraphNodeKind.GraphGroup) return definition;
        var inputs = new List<GraphInputPortDefinition>();
        var outputs = new List<GraphOutputPortDefinition>();
        var defaultLabels = new Dictionary<(bool Input, string Type), int>();
        foreach (var node in child?.Nodes ?? [])
        {
            if (!IsInput(node.Kind) && !IsOutput(node.Kind)) continue;
            var boundary = GraphNodeDefinition.All.SingleOrDefault(item => item.Kind == node.Kind);
            var label = node.Name;
            if (boundary is not null && node.Name == boundary.Name)
            {
                var key = (IsInput(node.Kind), node.PortType);
                var number = defaultLabels.GetValueOrDefault(key) + 1;
                defaultLabels[key] = number;
                label = GraphNodeDefinition.DisplayPortType(node.PortType);
                if (number > 1) label += $" {number}";
            }
            if (IsInput(node.Kind))
            {
                var input = boundary!;
                var value = node.Parameters?.FirstOrDefault(item => item.Id == "value")?.Value
                    ?? input.Parameters.FirstOrDefault(item => item.Id == "value")?.DefaultValue;
                inputs.Add(new(InputPortId(node.Id), label, node.PortType, node.PortCategory, value));
            }
            if (IsOutput(node.Kind))
                outputs.Add(new(OutputPortId(node.Id), label, node.PortType, node.PortCategory));
        }
        return definition with { InputPorts = inputs.AsReadOnly(), OutputPorts = outputs.AsReadOnly() };
    }

    public static string? RemapPort(GraphNodeSnapshot original, GraphSnapshot? clone, string? port)
    {
        if (original.Kind != GraphNodeKind.GraphGroup || original.ChildGraph is null || clone is null || port is null)
            return port;
        if (original.ChildGraph.Nodes.Count != clone.Nodes.Count)
            throw new InvalidDataException("A cloned group must preserve its boundary node order.");
        for (var index = 0; index < original.ChildGraph.Nodes.Count; index++)
        {
            var oldNode = original.ChildGraph.Nodes[index];
            var newNode = clone.Nodes[index];
            if (IsInput(oldNode.Kind) && InputPortId(oldNode.Id) == port) return InputPortId(newNode.Id);
            if (IsOutput(oldNode.Kind) && OutputPortId(oldNode.Id) == port) return OutputPortId(newNode.Id);
        }
        return port;
    }
}
