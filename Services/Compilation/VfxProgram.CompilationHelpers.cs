using System.Text.Json;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Compilation;

public sealed partial class VfxProgram
{
    private static void ValidateCurveBudget(IReadOnlyCollection<VfxCurveInstruction> curves)
    {
        if (curves.Sum(curve => curve.Keys.Length) > MaxCurveKeysTotal)
            throw new InvalidDataException("The graph contains too many F-Curve keyframes.");
    }

    private static double[] ReadColor(GraphNodeSnapshot node, GraphNodeDefinition definition)
    {
        var parameter = definition.Parameters.FirstOrDefault(item => item.Id == "value" && item.Type == "color")
            ?? throw new InvalidDataException($"Node '{node.Name}' is missing color parameter 'value'.");
        var value = node.Parameters?.FirstOrDefault(item => item.Id == "value" && item.Type == "color")?.Value
            ?? parameter.DefaultValue;
        if (!NodeParameter.IsValidValue("color", value))
            throw new InvalidDataException($"Node '{node.Name}' has an invalid color parameter.");
        return value.EnumerateArray().Select(item => item.GetDouble()).ToArray();
    }

    private static (int OutputWidth, int InputWidth, double[] Parameters) Conversion(
        VfxOpcode opcode, GraphNodeSnapshot node, GraphNodeDefinition definition, bool connected)
    {
        var sourceWidth = opcode switch
        {
            VfxOpcode.FloatToInt32 or VfxOpcode.Int32ToFloat => 1,
            VfxOpcode.ScalarToVector2 or VfxOpcode.ScalarToVector3 or VfxOpcode.ScalarToVector4 => 1,
            VfxOpcode.Vector2ToVector3 or VfxOpcode.Vector2ToVector4 => 2,
            VfxOpcode.Vector3ToVector2 or VfxOpcode.Vector3ToVector4 => 3,
            VfxOpcode.Vector4ToVector2 or VfxOpcode.Vector4ToVector3 or
                VfxOpcode.MaskVector2 or VfxOpcode.MaskVector3 or VfxOpcode.MaskVector4 or
                VfxOpcode.ColorToVector4 or VfxOpcode.Vector4ToColor => 4,
            _ => throw new InvalidDataException($"Node '{node.Name}' is not a supported conversion.")
        };
        if (!connected) throw new InvalidDataException($"Node '{node.Name}' requires an input connection.");
        var outputWidth = Width(node.PortType);
        var parameters = new double[4];
        if (opcode is VfxOpcode.Vector2ToVector3 or VfxOpcode.Vector2ToVector4 or
                 VfxOpcode.Vector3ToVector4)
        {
            parameters[0] = FloatParameter(node, definition, "fill");
        }
        else if (opcode is VfxOpcode.MaskVector2 or VfxOpcode.MaskVector3 or VfxOpcode.MaskVector4)
        {
            var parameter = definition.Parameters.FirstOrDefault(item => item.Id == "channels" && item.Type == "string")
                ?? throw new InvalidDataException($"Node '{node.Name}' is missing string parameter 'channels'.");
            var channels = node.Parameters?.FirstOrDefault(item => item.Id == "channels" && item.Type == "string")?.Value
                ?? parameter.DefaultValue;
            if (channels.ValueKind != JsonValueKind.String) throw new InvalidDataException($"Node '{node.Name}' has invalid channels.");
            var text = channels.GetString()!;
            if (text.Length != outputWidth || text.Any(channel => channel is not ('x' or 'y' or 'z' or 'w')))
                throw new InvalidDataException($"Node '{node.Name}' channels must contain exactly {outputWidth} of x, y, z, or w.");
            for (var index = 0; index < text.Length; index++) parameters[index] = "xyzw".IndexOf(text[index]);
        }
        return (outputWidth, sourceWidth, parameters);
    }

    private static double FloatParameter(GraphNodeSnapshot node, GraphNodeDefinition definition, string id)
    {
        var parameter = definition.Parameters.FirstOrDefault(item => item.Id == id && item.Type == "float")
            ?? throw new InvalidDataException($"Node '{node.Name}' is missing float parameter '{id}'.");
        var value = node.Parameters?.FirstOrDefault(item => item.Id == id && item.Type == "float")?.Value
            ?? parameter.DefaultValue;
        if (!value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new InvalidDataException($"Node '{node.Name}' has invalid parameter '{id}'.");
        return number;
    }

    private static IEnumerable<GraphNodeSnapshot> Flatten(GraphSnapshot graph)
    {
        foreach (var node in graph.Nodes)
        {
            yield return node;
            if (node.ChildGraph is not null && !GpuParticleEditorService.IsGpu(node.Kind) &&
                node.Kind != GraphNodeKind.SpawnableGroup)
                foreach (var child in Flatten(node.ChildGraph))
                    yield return child;
        }
    }

    internal static void ValidateForCompilation(GraphSnapshot graph)
    {
        // Validate shape before any schema/pin lookup can enumerate a malformed
        // Spawnable child. Do not turn null/missing collections into an empty,
        // successfully compiled effect.
        static void Shape(GraphSnapshot? current, int depth)
        {
            if (current is null || current.Nodes is null || current.Connections is null)
                throw new InvalidDataException("A graph requires non-null nodes and connections collections.");
            if (depth > 12) throw new InvalidDataException("The graph hierarchy is too deep.");
            foreach (var node in current.Nodes)
            {
                if (node is null) throw new InvalidDataException("The graph contains a null node.");
                if (node.ChildGraph is { } child) Shape(child, depth + 1);
            }
        }
        Shape(graph, 0);
        GraphValidator.Validate(graph);
    }

    private static GraphSnapshot LowerLifecycle(GraphSnapshot graph, bool hasLife)
    {
        var starts = graph.Nodes.Where(node => node.Kind.Value is "OnStart" or "OwnerDestroy" or "OnDestroy").Select(node => node.Id).ToHashSet();
        var lives = graph.Nodes.Where(node => node.Kind.Value == "LifeTime").Select(node => node.Id).ToHashSet();
        var outputs = graph.Nodes.Where(node => SpawnableGroupSchema.IsOutput(node.Kind)).Select(node => node.Id).ToHashSet();
        var signalBoundaries = graph.Nodes.Where(node =>
            SpawnableGroupSchema.IsSignalInput(node.Kind) || SpawnableGroupSchema.IsSignalOutput(node.Kind))
            .Select(node => node.Id).ToHashSet();
        var lifetimeReads = graph.Nodes.Where(node => node.Kind.Value is "InstanceRemaining" or "InstanceNormalizedAge")
            .Select(node => node.Id).ToHashSet();
        if (!hasLife && graph.Nodes.Any(node => node.Kind.Value == "InstanceRemaining"))
            throw new InvalidDataException("Instance Remaining requires a finite Life Time; infinite remaining cannot be represented by runtime numeric nodes.");
        return graph with
        {
            Nodes = graph.Nodes.Select(node => node.Kind.Value switch
            {
                "OnStart" => node with { Kind = GraphNodeKind.Signal },
                "OwnerDestroy" => node with { Kind = GraphNodeKind.Signal },
                "OnDestroy" => node with { Kind = GraphNodeKind.Signal },
                "InstanceAge" => node with { Kind = GraphNodeKind.Time },
                "InstanceRemaining" => node with { Kind = new GraphNodeKind("LifecycleRuntimeRemaining") },
                "InstanceNormalizedAge" when hasLife => node with { Kind = new GraphNodeKind("LifecycleRuntimeNormalizedAge") },
                "InstanceNormalizedAge" => node with { Kind = GraphNodeKind.Float, Parameters =
                    [new NodeParameterSnapshot("value", "float", JsonSerializer.SerializeToElement(0d))] },
                "LifeTime" => node with { Kind = GraphNodeKind.Float, Parameters =
                    [new NodeParameterSnapshot("value", "float", node.Parameters?.SingleOrDefault(p => p.Id == "seconds")?.Value
                        ?? JsonSerializer.SerializeToElement(2d))] },
                "SpawnOutputFloat" => node with { Kind = GraphNodeKind.Float },
                "SpawnOutputVector2" => node with { Kind = GraphNodeKind.FloatX2 },
                "SpawnOutputVector3" => node with { Kind = GraphNodeKind.FloatX3 },
                "SpawnOutputVector4" => node with { Kind = GraphNodeKind.FloatX4 },
                "SpawnOutputColor" => node with { Kind = GraphNodeDefinition.All.Single(item =>
                    item.TypeName == "Color" && item.Namespace == "LiberaFx").Kind },
                "SpawnInputSignal" or "SpawnOutputSignal" => node with { Kind = GraphNodeKind.Signal },
                _ => node.ChildGraph is { } child && node.Kind != GraphNodeKind.SpawnableGroup &&
                    !GpuParticleEditorService.IsGpu(node.Kind) ? node with { ChildGraph = LowerLifecycle(child, hasLife) } : node
            }).ToArray(),
            Connections = graph.Connections.Select(edge => edge with
            {
                SourcePortId = starts.Contains(edge.SourceNodeId) || signalBoundaries.Contains(edge.SourceNodeId) ? null :
                    hasLife && lifetimeReads.Contains(edge.SourceNodeId) ? "value" : edge.SourcePortId,
                TargetPortId = lives.Contains(edge.TargetNodeId) || outputs.Contains(edge.TargetNodeId) ? null : edge.TargetPortId
            }).ToArray()
        };
    }

    private static bool ContainsSpawnable(GraphSnapshot graph) => graph.Nodes.Any(node =>
        node.Kind == GraphNodeKind.SpawnableGroup ||
        node.ChildGraph is { } child && !GpuParticleEditorService.IsGpu(node.Kind) && ContainsSpawnable(child));

    private static IEnumerable<GraphConnectionSnapshot> graphConnections(GraphSnapshot graph, IReadOnlyList<GraphNodeSnapshot> nodes)
    {
        var ids = nodes.Select(node => node.Id).ToHashSet();
        return graph.Connections.Concat(graph.Nodes.Where(node => node.ChildGraph is not null && node.Kind != GraphNodeKind.SpawnableGroup)
            .SelectMany(node => graphConnections(node.ChildGraph!, nodes)))
            .Where(connection => ids.Contains(connection.SourceNodeId) && ids.Contains(connection.TargetNodeId));
    }

    private static int Width(string portType) => portType switch
    {
        "Float" or "Int32" => 1, "FloatX2" => 2, "FloatX3" => 3, "FloatX4" or "Color" => 4,
        _ when GraphEnumBranchSchema.IsEnumPort(portType) => 1,
        _ => throw new InvalidDataException($"Port type '{portType}' is not supported by the runtime.")
    };

    private static bool IsArithmetic(VfxOpcode opcode) =>
        (byte)opcode >= (byte)VfxOpcode.Add && (byte)opcode <= (byte)VfxOpcode.Divide ||
        (byte)opcode >= (byte)VfxOpcode.AddVector2 && (byte)opcode <= (byte)VfxOpcode.DivideVector4 ||
        IsInt32Arithmetic(opcode);

    private static bool IsInt32Arithmetic(VfxOpcode opcode) =>
        opcode is VfxOpcode.Int32Add or VfxOpcode.Int32Subtract or
            VfxOpcode.Int32Multiply or VfxOpcode.Int32Divide;

    private static bool IsSingleInput(VfxOpcode opcode) =>
        (byte)opcode >= (byte)VfxOpcode.ScalarToVector2 && (byte)opcode <= (byte)VfxOpcode.Vector4ToColor ||
        opcode is VfxOpcode.FloatToInt32 or VfxOpcode.Int32ToFloat;

    private static VfxNodeValueTypes CreateValueTypes(
        VfxOpcode opcode, GraphNodeSnapshot node, GraphNodeDefinition definition)
    {
        GraphValueType PortType(string? type, string description)
        {
            if (!GraphValueTypeSchema.TryGetValueType(type, out var valueType))
                throw new InvalidDataException($"Node '{node.Name}' has unsupported {description} type '{type}'.");
            return valueType;
        }

        var parameterInputs = (definition.InputPorts ?? [])
            .Where(port => definition.GetInputPortCategory(port.Id) == GraphPortCategory.Parameter)
            .ToArray();
        var parameterOutputs = (definition.OutputPorts ?? [])
            .Where(port => definition.GetOutputPortCategory(port.Id) == GraphPortCategory.Parameter)
            .ToArray();
        var outputType = definition.Runtime?.Execution == "signal" ||
            parameterOutputs.Length == 0 && definition.OutputPorts is not null
                ? GraphValueType.None
                : PortType(parameterOutputs.FirstOrDefault()?.PortType ?? node.PortType, "output");
        outputType = opcode switch
        {
            VfxOpcode.BreakVector2 => GraphValueType.Vector2,
            VfxOpcode.BreakVector3 => GraphValueType.Vector3,
            VfxOpcode.BreakVector4 => GraphValueType.Vector4,
            _ => outputType
        };

        string? InputPort(int index) => definition.InputPorts is null ? null :
            index < parameterInputs.Length ? parameterInputs[index].Id : null;
        string?[] inputPortIds = opcode switch
        {
            VfxOpcode.Time or VfxOpcode.FCurve or VfxOpcode.BranchTime => [],
            VfxOpcode.EnumValue => ["value"],
            VfxOpcode.EnumSelect => ["selector"],
            VfxOpcode.Int32 => [InputPort(0)],
            VfxOpcode.Int32Add or VfxOpcode.Int32Subtract or VfxOpcode.Int32Multiply or
                VfxOpcode.Int32Divide => ["a", "b"],
            VfxOpcode.FloatToInt32 or VfxOpcode.Int32ToFloat => ["a"],
            _ when opcode is VfxOpcode.LogicalEventSignal => parameterInputs.Select(port => (string?)port.Id).ToArray(),
            _ when (byte)opcode == 40 || (byte)opcode == 41 =>
                parameterInputs.Select(port => (string?)port.Id).ToArray(),
            _ when IsArithmetic(opcode) => ["a", "b"],
            _ when opcode == VfxOpcode.Scalar ||
                   opcode is VfxOpcode.Vector2 or VfxOpcode.Vector3 or VfxOpcode.Vector4 or VfxOpcode.Color =>
                [InputPort(0)],
            _ when IsSingleInput(opcode) ||
                   opcode is VfxOpcode.BreakVector2 or VfxOpcode.BreakVector3 or VfxOpcode.BreakVector4 =>
                ["a"],
            _ => []
        };
        var inputTypes = inputPortIds.Select(portId =>
        {
            var type = portId is null
                ? definition.GetInputPortType(null) ?? node.PortType
                : definition.GetInputPortType(portId);
            return PortType(type, "input");
        }).ToArray();
        if (inputTypes.Length > 4)
            throw new InvalidDataException($"Node '{node.Name}' has too many numeric inputs.");
        return new(outputType, inputTypes);
    }

    private static bool TryOpcode(string kind, out VfxOpcode opcode)
    {
        opcode = kind switch
        {
            "Float" or "SpawnInputFloat" => VfxOpcode.Scalar, "Time" => VfxOpcode.Time,
            "Int32" => VfxOpcode.Int32,
            "Int32Add" => VfxOpcode.Int32Add, "Int32Subtract" => VfxOpcode.Int32Subtract,
            "Int32Multiply" => VfxOpcode.Int32Multiply, "Int32Divide" => VfxOpcode.Int32Divide,
            "FloatToInt32" => VfxOpcode.FloatToInt32, "Int32ToFloat" => VfxOpcode.Int32ToFloat,
            "Add" => VfxOpcode.Add, "Subtract" => VfxOpcode.Subtract,
            "Multiply" => VfxOpcode.Multiply, "Divide" => VfxOpcode.Divide,
            "FloatX2" or "SpawnInputVector2" => VfxOpcode.Vector2, "FloatX3" or "SpawnInputVector3" => VfxOpcode.Vector3,
            "FloatX4" or "SpawnInputVector4" => VfxOpcode.Vector4,
            "Color" or "SpawnInputColor" or "554aaa8a-74a0-4620-9e28-d1e7bd20ffda" => VfxOpcode.Color,
            "AddVector2" => VfxOpcode.AddVector2, "SubtractVector2" => VfxOpcode.SubtractVector2,
            "MultiplyVector2" => VfxOpcode.MultiplyVector2, "DivideVector2" => VfxOpcode.DivideVector2,
            "AddVector3" => VfxOpcode.AddVector3, "SubtractVector3" => VfxOpcode.SubtractVector3,
            "MultiplyVector3" => VfxOpcode.MultiplyVector3, "DivideVector3" => VfxOpcode.DivideVector3,
            "AddVector4" => VfxOpcode.AddVector4, "SubtractVector4" => VfxOpcode.SubtractVector4,
            "MultiplyVector4" => VfxOpcode.MultiplyVector4, "DivideVector4" => VfxOpcode.DivideVector4,
            "ScalarToVector2" => VfxOpcode.ScalarToVector2, "ScalarToVector3" => VfxOpcode.ScalarToVector3,
            "ScalarToVector4" => VfxOpcode.ScalarToVector4,
            "BreakVector2" => VfxOpcode.BreakVector2, "BreakVector3" => VfxOpcode.BreakVector3,
            "BreakVector4" => VfxOpcode.BreakVector4,
            "Vector2ToVector3" => VfxOpcode.Vector2ToVector3, "Vector2ToVector4" => VfxOpcode.Vector2ToVector4,
            "Vector3ToVector2" => VfxOpcode.Vector3ToVector2, "Vector3ToVector4" => VfxOpcode.Vector3ToVector4,
            "Vector4ToVector2" => VfxOpcode.Vector4ToVector2, "Vector4ToVector3" => VfxOpcode.Vector4ToVector3,
            "MaskVector2" => VfxOpcode.MaskVector2, "MaskVector3" => VfxOpcode.MaskVector3,
            "MaskVector4" => VfxOpcode.MaskVector4, "ColorToVector4" => VfxOpcode.ColorToVector4,
            "Vector4ToColor" => VfxOpcode.Vector4ToColor,
            "FloatFCurve" => VfxOpcode.FCurve,
            _ => 0
        };
        return opcode != 0;
    }
}
