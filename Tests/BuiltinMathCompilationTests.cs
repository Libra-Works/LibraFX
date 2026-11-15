using System.Buffers.Binary;
using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;

namespace LiberaFx.Tests;

public static class BuiltinMathCompilationTests
{
    public static void Run()
    {
        var math = GraphNodeDefinition.All.Where(item =>
            item.TypeId.ToString("D").StartsWith("74305ac1-91b2-4c33-8d44-", StringComparison.Ordinal)).ToArray();
        Require(math.Length == 47, "All native math definitions must load.");
        var typeId = new byte[16];
        foreach (var definition in math)
        {
            var program = VfxProgram.Compile(new GraphSnapshot([Node(definition)], []));
            var instruction = program.CustomInstructions.Single();
            Require(program.BinaryVersion == 3 && instruction.TypeId == definition.TypeId &&
                    instruction.Inputs.Length == definition.InputPorts!.Count,
                $"{definition.Kind} must compile to a matching v3 instruction.");
            var bytes = program.ToBinary();
            definition.TypeId.TryWriteBytes(typeId, bigEndian: true, out _);
            Require(bytes.AsSpan(16 + 24, 16).SequenceEqual(typeId),
                $"{definition.Kind} UUID must use Guid byte layout.");
        }

        var clamp = math.Single(item => item.Kind.Value == "Clamp");
        var edited = Node(clamp,
            new NodeParameterSnapshot("a", "float", JsonSerializer.SerializeToElement(4.5)),
            new NodeParameterSnapshot("b", "float", JsonSerializer.SerializeToElement(-2.0)));
        var compiled = VfxProgram.Compile(new GraphSnapshot([edited], [])).CustomInstructions.Single();
        Require(compiled.Inputs[0].Defaults[0] == 4.5 && compiled.Inputs[1].Defaults[0] == -2 &&
                compiled.Inputs[2].Defaults[0] == 1, "Edited values and declared defaults must be lowered.");
        var binary = VfxProgram.Compile(new GraphSnapshot([edited], [])).ToBinary();
        Require(BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(binary.AsSpan(16 + 80))) == 4.5,
            "The edited default must be serialized.");

        var normalize = math.Single(item => item.Kind.Value == "NormalizeVector3");
        var vector = Node(normalize, new NodeParameterSnapshot("a", "vector3",
            JsonSerializer.SerializeToElement(new[] { 3.0, 4.0, 5.0 })));
        var inputs = VfxProgram.Compile(new GraphSnapshot([vector], [])).CustomInstructions.Single().Inputs;
        Require(inputs[0].Defaults.Take(3).SequenceEqual(new[] { 3.0, 4.0, 5.0 }),
            "Edited vector defaults must be lowered.");
        var compose = math.Single(item => item.Kind.Value == "ComposeVector4");
        var composeInputs = VfxProgram.Compile(new GraphSnapshot([Node(compose)], []))
            .CustomInstructions.Single().Inputs;
        Require(composeInputs.Length == 4 && composeInputs[3].Defaults[0] == 1,
            "ComposeVector4 must default alpha to one.");
        var time = Node(GraphNodeDefinition.All.Single(item => item.Kind.Value == "Time"));
        var sin = Node(math.Single(item => item.Kind.Value == "Sin"));
        var channels = Node(compose, new NodeParameterSnapshot("g", "float",
            JsonSerializer.SerializeToElement(0.25)));
        var connected = VfxProgram.Compile(new GraphSnapshot([time, sin, channels],
            [new(time.Id, sin.Id, "a"), new(sin.Id, channels.Id, "r", "value")]));
        var composed = connected.CustomInstructions.Single(item => item.NodeId == channels.Id);
        Require(composed.Inputs[0].SourceIndex >= 0 && composed.Inputs[1].Defaults[0] == 0.25 &&
                composed.Inputs[3].Defaults[0] == 1,
            "A connected scalar must drive one composed channel while other defaults persist.");
        var examples = Path.Combine(AppContext.BaseDirectory, "Runtime", "examples", "CustomNodes");
        var customDefinitions = GraphNodeDefinition.LoadDirectory(examples, out var warnings);
        Require(warnings.Count == 0, "Example custom node definitions must load.");
        var custom = customDefinitions.Single(item => item.TypeName == "CustomScale");
        var customNode = Node(custom, new NodeParameterSnapshot("a", "float",
            JsonSerializer.SerializeToElement(9.0)));
        var customInputs = VfxProgram.Compile(new GraphSnapshot([customNode], []))
            .CustomInstructions.Single().Inputs;
        Require(customInputs[0].Defaults[0] == 1 && customInputs[1].Defaults[0] == 1,
            "Custom inputs without a matching parameter must retain port defaults.");
        var invalid = Node(normalize, new NodeParameterSnapshot("a", "vector3",
            JsonSerializer.SerializeToElement(new[] { 1.0, 2.0 })));
        try { VfxProgram.Compile(new GraphSnapshot([invalid], [])); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid edited defaults must be rejected.");
    }

    private static GraphNodeSnapshot Node(GraphNodeDefinition definition, params NodeParameterSnapshot[] parameters) =>
        new(Guid.NewGuid(), definition.Kind, definition.Name, 0, 0, definition.PortType,
            definition.PortCategory, Parameters: parameters);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
