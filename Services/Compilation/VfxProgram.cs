using System.Buffers.Binary;
using System.Text.Json;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Compilation;

public enum VfxOpcode : byte
{
    Scalar = 1, Time = 2, Add = 3, Subtract = 4, Multiply = 5, Divide = 6,
    Vector2 = 7, Vector3 = 8, Vector4 = 9, Color = 10,
    AddVector2 = 11, SubtractVector2 = 12, MultiplyVector2 = 13, DivideVector2 = 14,
    AddVector3 = 15, SubtractVector3 = 16, MultiplyVector3 = 17, DivideVector3 = 18,
    AddVector4 = 19, SubtractVector4 = 20, MultiplyVector4 = 21, DivideVector4 = 22,
    ScalarToVector2 = 23, ScalarToVector3 = 24, ScalarToVector4 = 25,
    BreakVector2 = 26, BreakVector3 = 27, BreakVector4 = 28,
    Vector2ToVector3 = 29, Vector2ToVector4 = 30, Vector3ToVector2 = 31,
    Vector3ToVector4 = 32, Vector4ToVector2 = 33, Vector4ToVector3 = 34,
    MaskVector2 = 35, MaskVector3 = 36, MaskVector4 = 37,
    ColorToVector4 = 38, Vector4ToColor = 39
}

public readonly record struct VfxInstruction(
    Guid NodeId, VfxOpcode Opcode, int InputA, int InputB, double DefaultA, double DefaultB);

public readonly record struct VfxTypedInstruction(
    Guid NodeId, VfxOpcode Opcode, int OutputWidth, int InputAWidth, int InputBWidth, byte TypeFlags,
    int InputASourceComponent, int InputBSourceComponent, int InputA, int InputB,
    double[] DefaultA, double[] DefaultB);

public sealed class VfxProgram
{
    private const int HeaderSize = 12;
    private const int InstructionSize = 44;
    private const int TypedHeaderSize = 16;
    private const int TypedInstructionSize = 96;
    private const int MaxInstructions = 10000;
    private readonly VfxInstruction[] _instructions;
    private readonly VfxTypedInstruction[] _typedInstructions;
    private readonly ushort _version;

    public IReadOnlyList<VfxInstruction> Instructions { get; }
    public IReadOnlyList<VfxTypedInstruction> TypedInstructions { get; }
    public ushort BinaryVersion => _version;
    public int WorkSizeBytes => checked(_instructions.Length * (_version == 1 ? sizeof(double) : 4 * sizeof(double)));

    private VfxProgram(VfxInstruction[] instructions, VfxTypedInstruction[] typedInstructions, ushort version)
    {
        _instructions = instructions;
        _typedInstructions = typedInstructions;
        _version = version;
        Instructions = Array.AsReadOnly(instructions);
        TypedInstructions = Array.AsReadOnly(typedInstructions);
    }

    public static VfxProgram Compile(GraphSnapshot graph)
    {
        GraphValidator.Validate(graph);
        var nodes = Flatten(graph)
            .Where(node => node.Kind != GraphNodeKind.Comment && node.Kind != GraphNodeKind.GraphGroup)
            .ToArray();
        if (nodes.Length > MaxInstructions) throw new InvalidDataException("Too many runtime nodes.");
        var byId = nodes.ToDictionary(node => node.Id);
        var definitions = GraphNodeDefinition.All.ToDictionary(item => item.Kind);
        foreach (var node in nodes)
            if (!TryOpcode(node.Kind.Value, out _))
                throw new InvalidDataException($"Node '{node.Name}' ({node.Kind}) is not supported by the runtime.");
        var incoming = graphConnections(graph, nodes).ToLookup(connection => connection.TargetNodeId);
        var outgoing = graphConnections(graph, nodes).ToLookup(connection => connection.SourceNodeId);
        var degree = nodes.ToDictionary(node => node.Id, node => incoming[node.Id].Count());
        var queue = new Queue<GraphNodeSnapshot>(nodes.Where(node => degree[node.Id] == 0));
        var order = new List<GraphNodeSnapshot>(nodes.Length);
        while (queue.TryDequeue(out var node))
        {
            order.Add(node);
            foreach (var connection in outgoing[node.Id])
                if (--degree[connection.TargetNodeId] == 0) queue.Enqueue(byId[connection.TargetNodeId]);
        }
        if (order.Count != nodes.Length) throw new InvalidDataException("The graph contains a cycle.");
        var indices = order.Select((node, index) => (node.Id, index)).ToDictionary(item => item.Id, item => item.index);
        var typedCode = new VfxTypedInstruction[order.Count];
        var needsTyped = false;
        for (var index = 0; index < order.Count; index++)
        {
            var node = order[index];
            TryOpcode(node.Kind.Value, out var opcode);
            var definition = definitions[node.Kind];
            var typeWidth = Width(node.PortType);
            needsTyped |= typeWidth != 1 || node.PortType == "Color" ||
                          (byte)opcode > (byte)VfxOpcode.Divide;
            if (node.PortCategory != GraphPortCategory.Parameter ||
                (opcode == VfxOpcode.Time && incoming[node.Id].Any()) ||
                (opcode == VfxOpcode.Scalar && incoming[node.Id].Any(connection => connection.TargetPortId is not null)) ||
                (IsArithmetic(opcode) && incoming[node.Id].Any(connection => connection.TargetPortId is not ("a" or "b"))) ||
                (IsSingleInput(opcode) && incoming[node.Id].Any(connection => connection.TargetPortId != "a")))
                throw new InvalidDataException($"Node '{node.Name}' has unsupported runtime ports.");

            var outputWidth = typeWidth;
            var defaultA = new double[4];
            var defaultB = new double[4];
            var inputAWidth = 0;
            var inputBWidth = 0;
            var inputA = -1;
            var inputB = -1;
            var inputASourceComponent = 0;
            var inputBSourceComponent = 0;
            var inputAIsColor = false;
            var inputBIsColor = false;

            double Parameter(string id)
            {
                var parameter = definition.Parameters.FirstOrDefault(item => item.Id == id && item.Type == "float")
                    ?? throw new InvalidDataException($"Node '{node.Name}' is missing float parameter '{id}'.");
                var value = node.Parameters?.FirstOrDefault(item => item.Id == id && item.Type == "float")?.Value
                    ?? parameter.DefaultValue;
                if (!value.TryGetDouble(out var number) || !double.IsFinite(number))
                    throw new InvalidDataException($"Node '{node.Name}' has an invalid float parameter '{id}'.");
                return number;
            }

            double[] VectorParameter(string id, int width)
            {
                var parameterType = $"vector{width}";
                var parameter = definition.Parameters.FirstOrDefault(item => item.Id == id && item.Type == parameterType)
                    ?? throw new InvalidDataException($"Node '{node.Name}' is missing {parameterType} parameter '{id}'.");
                var value = node.Parameters?.FirstOrDefault(item => item.Id == id && item.Type == parameterType)?.Value
                    ?? parameter.DefaultValue;
                if (!NodeParameter.IsValidValue(parameterType, value))
                    throw new InvalidDataException($"Node '{node.Name}' has an invalid vector parameter '{id}'.");
                return value.EnumerateArray().Select(item => item.GetDouble()).ToArray();
            }

            int Input(string? port, int expectedWidth, out int sourceComponent, out bool sourceIsColor)
            {
                sourceComponent = 0;
                sourceIsColor = false;
                var matches = incoming[node.Id].Where(connection => connection.TargetPortId == port).ToArray();
                if (matches.Length > 1)
                    throw new InvalidDataException($"Node '{node.Name}' has multiple connections to one input.");
                if (matches.Length == 0) return -1;
                var source = byId[matches[0].SourceNodeId];
                var sourceDefinition = definitions[source.Kind];
                var sourceType = sourceDefinition.GetOutputPortType(matches[0].SourcePortId);
                if (sourceType is null)
                    throw new InvalidDataException($"Node '{node.Name}' is connected to an invalid source output.");
                var sourceWidth = Width(sourceType);
                if (sourceWidth != expectedWidth)
                    throw new InvalidDataException($"Node '{node.Name}' input '{port}' expects {expectedWidth} components.");
                sourceIsColor = sourceType == "Color";
                if (sourceDefinition.OutputPorts is { } sourcePorts)
                {
                    var portIndex = -1;
                    for (var index = 0; index < sourcePorts.Count; index++)
                        if (sourcePorts[index].Id == matches[0].SourcePortId) portIndex = index;
                    if (portIndex < 0 || portIndex > 3)
                        throw new InvalidDataException($"Node '{node.Name}' is connected to an invalid source output.");
                    sourceComponent = portIndex;
                }
                return indices[source.Id];
            }

            if (opcode is VfxOpcode.Scalar or VfxOpcode.Vector2 or VfxOpcode.Vector3 or VfxOpcode.Vector4 or VfxOpcode.Color)
            {
                var width = outputWidth;
                double[] values = node.PortType == "Float" ? [Parameter("value")] :
                    node.PortType == "Color" ? ReadColor(node, definition) : VectorParameter("value", width);
                Array.Copy(values, defaultA, values.Length);
                var inputPort = definition.InputPorts is null ? null : "a";
                inputA = Input(inputPort, width,
                    out inputASourceComponent, out inputAIsColor);
                inputAWidth = inputA < 0 ? 0 : width;
                if (node.PortType == "Float" && incoming[node.Id].Any(connection => connection.TargetPortId is not null))
                    throw new InvalidDataException($"Node '{node.Name}' has an unsupported runtime input.");
            }
            else if (opcode == VfxOpcode.Time)
            {
                outputWidth = 1;
            }
            else if (IsArithmetic(opcode))
            {
                var width = outputWidth;
                var parameterType = width == 1 ? null : $"vector{width}";
                double[] defaultsA = parameterType is null ? [Parameter("a")] : VectorParameter("a", width);
                double[] defaultsB = parameterType is null ? [Parameter("b")] : VectorParameter("b", width);
                Array.Copy(defaultsA, defaultA, defaultsA.Length);
                Array.Copy(defaultsB, defaultB, defaultsB.Length);
                inputA = Input("a", width, out inputASourceComponent, out inputAIsColor);
                inputB = Input("b", width, out inputBSourceComponent, out inputBIsColor);
                inputAWidth = inputA < 0 ? 0 : width;
                inputBWidth = inputB < 0 ? 0 : width;
            }
            else if (opcode is VfxOpcode.BreakVector2 or VfxOpcode.BreakVector3 or VfxOpcode.BreakVector4)
            {
                outputWidth = typeWidth;
                inputA = Input("a", outputWidth, out inputASourceComponent, out inputAIsColor);
                if (inputA < 0) throw new InvalidDataException($"Node '{node.Name}' requires an input connection.");
                inputAWidth = outputWidth;
            }
            else
            {
                var setup = Conversion(opcode, node, definition, incoming[node.Id].Any());
                outputWidth = setup.OutputWidth;
                inputA = Input("a", setup.InputWidth, out inputASourceComponent, out inputAIsColor);
                inputAWidth = setup.InputWidth;
                Array.Copy(setup.Parameters, defaultA, setup.Parameters.Length);
            }

            if (outputWidth != typeWidth)
                throw new InvalidDataException($"Node '{node.Name}' declares an incompatible output type.");
            var typeFlags = (byte)(node.PortType == "Color" ? 1 : 0);
            if (inputAIsColor) typeFlags |= 2;
            if (inputBIsColor) typeFlags |= 4;
            typedCode[index] = new(node.Id, opcode, outputWidth, inputAWidth, inputBWidth,
                typeFlags, inputASourceComponent, inputBSourceComponent,
                inputA, inputB, defaultA, defaultB);
        }

        var version = needsTyped ? (ushort)2 : (ushort)1;
        var legacy = typedCode.Select(item => new VfxInstruction(item.NodeId, item.Opcode, item.InputA, item.InputB,
            item.DefaultA[0], item.DefaultB[0])).ToArray();
        return new VfxProgram(legacy, typedCode, version);
    }

    public byte[] ToBinary()
    {
        if (_version == 1)
        {
            var bytes = new byte[checked(HeaderSize + _instructions.Length * InstructionSize)];
            "LFXB"u8.CopyTo(bytes);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
            bytes[6] = 1;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), _instructions.Length);
            for (var index = 0; index < _instructions.Length; index++)
            {
                var span = bytes.AsSpan(HeaderSize + index * InstructionSize, InstructionSize);
                var instruction = _instructions[index];
                instruction.NodeId.TryWriteBytes(span, bigEndian: true, out _);
                span[16] = (byte)instruction.Opcode;
                BinaryPrimitives.WriteInt32LittleEndian(span[20..], instruction.InputA);
                BinaryPrimitives.WriteInt32LittleEndian(span[24..], instruction.InputB);
                BinaryPrimitives.WriteDoubleLittleEndian(span[28..], instruction.DefaultA);
                BinaryPrimitives.WriteDoubleLittleEndian(span[36..], instruction.DefaultB);
            }
            return bytes;
        }

        var typedBytes = new byte[checked(TypedHeaderSize + _typedInstructions.Length * TypedInstructionSize)];
        "LFXB"u8.CopyTo(typedBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(typedBytes.AsSpan(4), 2);
        typedBytes[6] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(typedBytes.AsSpan(8), _typedInstructions.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(typedBytes.AsSpan(12), TypedInstructionSize);
        for (var index = 0; index < _typedInstructions.Length; index++)
        {
            var span = typedBytes.AsSpan(TypedHeaderSize + index * TypedInstructionSize, TypedInstructionSize);
            var instruction = _typedInstructions[index];
            instruction.NodeId.TryWriteBytes(span, bigEndian: true, out _);
            span[16] = (byte)instruction.Opcode;
            span[17] = (byte)instruction.OutputWidth;
            span[18] = (byte)instruction.InputAWidth;
            span[19] = (byte)instruction.InputBWidth;
            span[20] = instruction.TypeFlags;
            span[21] = (byte)instruction.InputASourceComponent;
            span[22] = (byte)instruction.InputBSourceComponent;
            BinaryPrimitives.WriteInt32LittleEndian(span[24..], instruction.InputA);
            BinaryPrimitives.WriteInt32LittleEndian(span[28..], instruction.InputB);
            for (var component = 0; component < 4; component++)
            {
                BinaryPrimitives.WriteDoubleLittleEndian(span[(32 + component * 8)..], instruction.DefaultA[component]);
                BinaryPrimitives.WriteDoubleLittleEndian(span[(64 + component * 8)..], instruction.DefaultB[component]);
            }
        }
        return typedBytes;
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
            if (node.ChildGraph is not null)
                foreach (var child in Flatten(node.ChildGraph))
                    yield return child;
        }
    }

    private static IEnumerable<GraphConnectionSnapshot> graphConnections(GraphSnapshot graph, IReadOnlyList<GraphNodeSnapshot> nodes)
    {
        var ids = nodes.Select(node => node.Id).ToHashSet();
        return graph.Connections.Concat(graph.Nodes.Where(node => node.ChildGraph is not null)
            .SelectMany(node => graphConnections(node.ChildGraph!, nodes)))
            .Where(connection => ids.Contains(connection.SourceNodeId) && ids.Contains(connection.TargetNodeId));
    }

    private static int Width(string portType) => portType switch
    {
        "Float" => 1, "FloatX2" => 2, "FloatX3" => 3, "FloatX4" or "Color" => 4,
        _ => throw new InvalidDataException($"Port type '{portType}' is not supported by the runtime.")
    };

    private static bool IsArithmetic(VfxOpcode opcode) =>
        (byte)opcode >= (byte)VfxOpcode.Add && (byte)opcode <= (byte)VfxOpcode.Divide ||
        (byte)opcode >= (byte)VfxOpcode.AddVector2 && (byte)opcode <= (byte)VfxOpcode.DivideVector4;

    private static bool IsSingleInput(VfxOpcode opcode) =>
        (byte)opcode >= (byte)VfxOpcode.ScalarToVector2 && (byte)opcode <= (byte)VfxOpcode.Vector4ToColor;

    private static bool TryOpcode(string kind, out VfxOpcode opcode)
    {
        opcode = kind switch
        {
            "Float" => VfxOpcode.Scalar, "Time" => VfxOpcode.Time,
            "Add" => VfxOpcode.Add, "Subtract" => VfxOpcode.Subtract,
            "Multiply" => VfxOpcode.Multiply, "Divide" => VfxOpcode.Divide,
            "FloatX2" => VfxOpcode.Vector2, "FloatX3" => VfxOpcode.Vector3,
            "FloatX4" => VfxOpcode.Vector4, "Color" => VfxOpcode.Color,
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
            _ => 0
        };
        return opcode != 0;
    }
}
