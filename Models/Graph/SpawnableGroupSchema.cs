namespace LiberaFx.Models.Graph;

public static class SpawnableGroupSchema
{
    public const int MaxInputs = 4;
    public static bool IsInput(GraphNodeKind kind) => kind.Value is
        "SpawnInputFloat" or "SpawnInputVector2" or "SpawnInputVector3" or "SpawnInputVector4" or "SpawnInputColor";
    public static string PortId(Guid id) => "p_" + id.ToString("N");
    public static bool IsOutput(GraphNodeKind kind) => kind.Value == "SpawnOutputVector3";
    public static string OutputPortId(Guid id) => "o_" + id.ToString("N");

    public static string? RemapPort(GraphNodeSnapshot original, GraphSnapshot? clone, string? port)
    {
        if (original.Kind == GraphNodeKind.GraphGroup)
            return GroupBoundarySchema.RemapPort(original, clone, port);
        if (original.Kind != GraphNodeKind.SpawnableGroup || original.ChildGraph is null || clone is null || port is null)
            return port;
        if (original.ChildGraph.Nodes.Count != clone.Nodes.Count)
            throw new InvalidDataException("A cloned group must preserve its boundary node order.");
        for (var index = 0; index < original.ChildGraph.Nodes.Count; index++)
        {
            var oldNode = original.ChildGraph.Nodes[index];
            var newNode = clone.Nodes[index];
            if (IsInput(oldNode.Kind) && PortId(oldNode.Id) == port)
                return PortId(newNode.Id);
            if (IsOutput(oldNode.Kind) && OutputPortId(oldNode.Id) == port)
                return OutputPortId(newNode.Id);
        }
        return port;
    }

    public static GraphNodeDefinition Effective(GraphNodeDefinition definition, GraphSnapshot? child)
    {
        if (definition.Kind != GraphNodeKind.SpawnableGroup || child is null) return definition;
        var inputs = new List<GraphInputPortDefinition>(definition.InputPorts ?? []);
        foreach (var node in child.Nodes.Where(node => IsInput(node.Kind)))
        {
            var inputDefinition = GraphNodeDefinition.All.Single(item => item.Kind == node.Kind);
            var value = node.Parameters?.FirstOrDefault(item => item.Id == "value")?.Value
                ?? inputDefinition.Parameters.Single(item => item.Id == "value").DefaultValue;
            inputs.Add(new GraphInputPortDefinition(PortId(node.Id), node.Name, node.PortType,
                GraphPortCategory.Parameter, value));
        }
        var outputs = new List<GraphOutputPortDefinition>(definition.OutputPorts ?? []);
        foreach (var node in child.Nodes.Where(node => IsOutput(node.Kind)))
            outputs.Add(new GraphOutputPortDefinition(OutputPortId(node.Id), node.Name,
                node.PortType, GraphPortCategory.Parameter));
        return definition with { InputPorts = inputs.AsReadOnly(), OutputPorts = outputs.AsReadOnly() };
    }
}
