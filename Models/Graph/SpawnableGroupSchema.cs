namespace LiberaFx.Models.Graph;

public static class SpawnableGroupSchema
{
    public const int MaxInputLanes = 256;
    public static int InputWidth(string portType) => portType switch
    {
        "Float" => 1,
        "FloatX2" => 2,
        "FloatX3" => 3,
        "FloatX4" or "Color" => 4,
        _ => throw new InvalidDataException($"Spawn input type '{portType}' is not supported.")
    };
    public static bool IsInput(GraphNodeKind kind) => kind.Value is
        "SpawnInputFloat" or "SpawnInputVector2" or "SpawnInputVector3" or "SpawnInputVector4" or "SpawnInputColor";
    public static bool IsSignalInput(GraphNodeKind kind) => kind.Value == "SpawnInputSignal";
    public static bool IsSignalOutput(GraphNodeKind kind) => kind.Value == "SpawnOutputSignal";
    public static bool IsLive(GraphNodeSnapshot input)
    {
        var mode = input.Parameters?.SingleOrDefault(parameter => parameter.Id == "bindingMode")?.Value;
        if (mode is null) return false;
        if (mode.Value.ValueKind != System.Text.Json.JsonValueKind.Number ||
            !mode.Value.TryGetInt32(out var value) || value is not (0 or 1))
            throw new InvalidDataException("Spawn input binding mode must be CaptureAtSpawn or Live.");
        return value == 1;
    }
    public static string PortId(Guid id) => "p_" + id.ToString("N");
    public static bool IsOutput(GraphNodeKind kind) => kind.Value is
        "SpawnOutputFloat" or "SpawnOutputVector2" or "SpawnOutputVector3" or
        "SpawnOutputVector4" or "SpawnOutputColor";
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
            if ((IsInput(oldNode.Kind) || IsSignalInput(oldNode.Kind)) && PortId(oldNode.Id) == port)
                return PortId(newNode.Id);
            if ((IsOutput(oldNode.Kind) || IsSignalOutput(oldNode.Kind)) && OutputPortId(oldNode.Id) == port)
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
        foreach (var node in child.Nodes.Where(node => IsSignalInput(node.Kind)))
            inputs.Add(new GraphInputPortDefinition(PortId(node.Id), node.Name, "Signal", GraphPortCategory.Signal));
        var outputs = new List<GraphOutputPortDefinition>(definition.OutputPorts ?? []);
        foreach (var node in child.Nodes.Where(node => IsOutput(node.Kind)))
            outputs.Add(new GraphOutputPortDefinition(OutputPortId(node.Id), node.Name,
                node.PortType, GraphPortCategory.Parameter));
        foreach (var node in child.Nodes.Where(node => IsSignalOutput(node.Kind)))
            outputs.Add(new GraphOutputPortDefinition(OutputPortId(node.Id), node.Name, "Signal", GraphPortCategory.Signal));
        return definition with { InputPorts = inputs.AsReadOnly(), OutputPorts = outputs.AsReadOnly() };
    }
}
