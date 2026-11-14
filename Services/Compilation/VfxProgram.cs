using System.Buffers.Binary;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Compilation;

public enum VfxOpcode : byte
{
    Scalar = 1, Time = 2, Add = 3, Subtract = 4, Multiply = 5, Divide = 6
}

public readonly record struct VfxInstruction(
    Guid NodeId, VfxOpcode Opcode, int InputA, int InputB, double DefaultA, double DefaultB);

public sealed class VfxProgram
{
    private const int HeaderSize = 12;
    private const int InstructionSize = 44;
    private const int MaxInstructions = 10000;
    private readonly VfxInstruction[] _instructions;

    public IReadOnlyList<VfxInstruction> Instructions { get; }
    public int WorkSizeBytes => checked(_instructions.Length * sizeof(double));

    private VfxProgram(VfxInstruction[] instructions)
    {
        _instructions = instructions;
        Instructions = Array.AsReadOnly(instructions);
    }

    public static VfxProgram Compile(GraphSnapshot graph)
    {
        GraphValidator.Validate(graph);
        var nodes = graph.Nodes.Where(node => node.Kind != GraphNodeKind.Comment).ToArray();
        if (nodes.Length > MaxInstructions) throw new InvalidDataException("Too many runtime nodes.");
        var byId = nodes.ToDictionary(node => node.Id);
        foreach (var node in nodes)
            if (!TryOpcode(node.Kind, out _))
                throw new InvalidDataException($"Node '{node.Name}' ({node.Kind}) is not supported by the runtime.");
        var incoming = graph.Connections.ToLookup(connection => connection.TargetNodeId);
        var outgoing = graph.Connections.ToLookup(connection => connection.SourceNodeId);
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
        var code = new VfxInstruction[order.Count];
        for (var index = 0; index < order.Count; index++)
        {
            var node = order[index];
            TryOpcode(node.Kind, out var opcode);
            var definition = GraphNodeDefinition.All.Single(item => item.Kind == node.Kind);
            if (node.PortType != "Float" || node.PortCategory != GraphPortCategory.Parameter ||
                (opcode == VfxOpcode.Time && incoming[node.Id].Any()) ||
                (opcode == VfxOpcode.Scalar && incoming[node.Id].Any(connection => connection.TargetPortId is not null)) ||
                (opcode is VfxOpcode.Add or VfxOpcode.Subtract or VfxOpcode.Multiply or VfxOpcode.Divide &&
                 incoming[node.Id].Any(connection => connection.TargetPortId is not ("a" or "b"))))
                throw new InvalidDataException($"Node '{node.Name}' has unsupported runtime ports.");
            double Parameter(string id)
            {
                var parameter = definition.Parameters.FirstOrDefault(item => item.Id == id && item.Type == "float")
                    ?? throw new InvalidDataException($"Node '{node.Name}' is missing float parameter '{id}'.");
                var value = node.Parameters?.FirstOrDefault(item => item.Id == id && item.Type == "float")?.Value
                    ?? parameter.DefaultValue;
                return value.GetDouble();
            }
            int Input(string? port)
            {
                var matches = incoming[node.Id].Where(connection => connection.TargetPortId == port).ToArray();
                if (matches.Length > 1)
                    throw new InvalidDataException($"Node '{node.Name}' has multiple connections to one input.");
                return matches.Length == 0 ? -1 : indices[matches[0].SourceNodeId];
            }
            code[index] = opcode switch
            {
                VfxOpcode.Scalar => new(node.Id, opcode, Input(null), -1, Parameter("value"), 0),
                VfxOpcode.Time => new(node.Id, opcode, -1, -1, 0, 0),
                _ => new(node.Id, opcode, Input("a"), Input("b"), Parameter("a"), Parameter("b"))
            };
        }
        return new VfxProgram(code);
    }

    public byte[] ToBinary()
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

    private static bool TryOpcode(GraphNodeKind kind, out VfxOpcode opcode)
    {
        opcode = kind.Value switch
        {
            "Float" => VfxOpcode.Scalar, "Time" => VfxOpcode.Time,
            "Add" => VfxOpcode.Add, "Subtract" => VfxOpcode.Subtract,
            "Multiply" => VfxOpcode.Multiply, "Divide" => VfxOpcode.Divide,
            _ => 0
        };
        return opcode != 0;
    }
}
