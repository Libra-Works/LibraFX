using System.Buffers.Binary;
using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;

namespace LiberaFx.Tests;

public static class QuaternionRandomCompilationTests
{
    public static void Run()
    {
        var definitions = QuaternionRandomNodeCatalog.BuiltInIds.Select(pair =>
            GraphNodeDefinition.Core.Single(item => item.TypeId == pair.Key && item.Kind.Value == pair.Value)).ToArray();
        Require(definitions.Length == 16, "All quaternion/random built-ins must retain symbolic kinds.");
        foreach (var definition in definitions)
        {
            Require(definition.Group is "Quaternion" or "Random" && definition.DescriptionKey is not null,
                $"{definition.Kind}: group/help metadata is required.");
            var program = VfxProgram.Compile(new GraphSnapshot([Node(definition.Kind.Value)], []));
            var instruction = program.CustomInstructions.Single();
            Require(program.BinaryVersion == 3 && instruction.TypeId == definition.TypeId &&
                    instruction.Inputs.Length == definition.InputPorts!.Count,
                $"{definition.Kind}: expected the existing custom v3 ABI.");
            var bytes = program.ToBinary();
            var uuid = new byte[16];
            definition.TypeId.TryWriteBytes(uuid, bigEndian: true, out _);
            Require(bytes.AsSpan(40, 16).SequenceEqual(uuid), $"{definition.Kind}: UUID byte order changed.");
            Require(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(56, 4)) == 1,
                $"{definition.Kind}: callback ABI must stay at 1.");
        }

        var compose = Node("QuaternionCompose", Parameter("x", 0.25), Parameter("w", 2));
        var composeInstruction = VfxProgram.Compile(new GraphSnapshot([compose], []))
            .CustomInstructions.Single();
        Require(composeInstruction.Inputs.Select(input => input.Defaults[0]).SequenceEqual(new[] { 0.25, 0, 0, 2 }),
            "XYZW construction must preserve four independent scalar inputs.");

        var rotationGraph = RotationGraph();
        var serialized = JsonSerializer.Serialize(rotationGraph);
        var restored = JsonSerializer.Deserialize<GraphSnapshot>(serialized)!;
        var originalProgram = VfxProgram.Compile(rotationGraph);
        var restoredProgram = VfxProgram.Compile(restored);
        Require(originalProgram.ToBinary().SequenceEqual(restoredProgram.ToBinary()),
            "Graph JSON roundtrip must preserve quaternion IDs, parameters and connections.");
        var rotated = restoredProgram.CustomInstructions.Single(item =>
            item.TypeId == Definition("QuaternionRotateVector").TypeId);
        Require(rotated.Inputs[0].SourceIndex >= 0 &&
                rotated.Inputs[1].Defaults.Take(3).SequenceEqual(new[] { 1.0, 2.0, -3.0 }),
            "An authored Euler rotation must connect to explicit vector rotation with edited vector defaults.");
        VerifySerializedVectorDefault(originalProgram);
        var logicalProgram = VfxProgram.Compile(rotationGraph with
        {
            Nodes = [.. rotationGraph.Nodes, Node("SignalToNumber")]
        });
        Require(logicalProgram.BinaryVersion == 11,
            "Custom inputs mixed with logical/enum metadata require the non-overlapping v11 guard layout.");
        VerifySerializedVectorDefault(logicalProgram);

        var randomGraph = RandomGraph();
        var randomProgram = VfxProgram.Compile(randomGraph);
        var random = randomProgram.CustomInstructions.Single();
        Require(random.Inputs[1].SourceIndex >= 0 && random.Inputs[0].Defaults[0] == 12345 &&
                random.Inputs[2].Defaults.Take(3).SequenceEqual(new[] { -2.0, 10.0, 0.25 }) &&
                random.Inputs[3].Defaults.Take(3).SequenceEqual(new[] { 3.0, -10.0, 0.25 }),
            "SampleIndex wiring and component-specific (including reversed/equal) ranges must survive lowering.");
        var package = SpawnableGroupPackageBinary.Deserialize(
            SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(randomGraph)));
        Require(package.ParentBytecode.SequenceEqual(randomProgram.ToBinary()),
            "Effect packaging must retain deterministic random instructions.");

        // Existing real named outputs work on the XYZW representation, without another runtime ABI.
        var fromEuler = Node("QuaternionFromEuler", Vector("euler", 20, -35, 70));
        var split = Node("BreakVector4");
        var rebuilt = Node("QuaternionCompose");
        var edges = new List<GraphConnectionSnapshot> { new(fromEuler.Id, split.Id, "a", "value") };
        foreach (var component in new[] { "x", "y", "z", "w" })
        {
            edges.Add(new(split.Id, rebuilt.Id, component, component));
        }
        var namedProgram = VfxProgram.Compile(new GraphSnapshot([fromEuler, split, rebuilt], edges));
        var rebuiltInputs = namedProgram.CustomInstructions.Single(item => item.NodeId == rebuilt.Id).Inputs;
        Require(rebuiltInputs.Select(input => input.SourceComponent).SequenceEqual(new[] { 0, 1, 2, 3 }),
            "Named BreakVector4 XYZW ports must compile to distinct quaternion component lanes.");

        // RandomInt intentionally uses numeric Float pins; it is not an implicit semantic Int32 cast.
        var integer = Definition("RandomInt");
        Require(integer.PortType == "Float" && integer.InputPorts!.All(port =>
                integer.GetInputPortType(port.Id) == "Float"),
            "Do not smuggle semantic Int32 ports into callback ABI v1.");
        var malformed = Node("QuaternionSlerp", new NodeParameterSnapshot("a", "vector4",
            JsonSerializer.SerializeToElement(new[] { 0, 0, 1 })));
        try
        {
            VfxProgram.Compile(new GraphSnapshot([malformed], []));
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException("Malformed quaternion defaults must fail conversion.");
    }

    private static void VerifySerializedVectorDefault(VfxProgram program)
    {
        var rotated = program.CustomInstructions.Single(item =>
            item.TypeId == Definition("QuaternionRotateVector").TypeId);
        var index = Array.FindIndex(program.TypedInstructions.ToArray(), item => item.NodeId == rotated.NodeId);
        var bytes = program.ToBinary();
        var recordSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12));
        Require(BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(16 + index * recordSize + 112)) == 1,
            "Custom input defaults must survive serialization without branch-guard overlap.");
    }

    // Optional INTERNAL compiler-to-native raw program fixtures, produced by VfxProgram.Compile.
    // These are not unified EffectBinary files and do not exercise the standalone converter.
    // Caller supplies a NEW isolated temporary directory. Pass the two .lfxb paths to
    // QuaternionRandomTests in this order; that test reads raw bytecode using Program::load.
    public static void WriteNativeFixtures(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || Directory.Exists(directory))
        {
            throw new ArgumentException("Supply an absolute, new temporary directory.", nameof(directory));
        }
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "quaternion.lfxb"), VfxProgram.Compile(RotationGraph()).ToBinary());
        File.WriteAllBytes(Path.Combine(directory, "random.lfxb"), VfxProgram.Compile(RandomGraph()).ToBinary());
    }

    private static GraphSnapshot RotationGraph()
    {
        var euler = Node("QuaternionFromEuler", Vector("euler", 20, -35, 70));
        var rotate = Node("QuaternionRotateVector", Vector("vector", 1, 2, -3));
        return new([euler, rotate], [new(euler.Id, rotate.Id, "q", "value")]);
    }

    private static GraphSnapshot RandomGraph()
    {
        var time = Node("Time");
        var random = Node("RandomVector3", Parameter("seed", 12345),
            Vector("min", -2, 10, 0.25), Vector("max", 3, -10, 0.25));
        return new([time, random], [new(time.Id, random.Id, "sampleIndex")]);
    }

    private static GraphNodeDefinition Definition(string kind) =>
        GraphNodeDefinition.Core.Single(item => item.TypeName == kind);

    private static GraphNodeSnapshot Node(string kind, params NodeParameterSnapshot[] parameters)
    {
        var definition = Definition(kind);
        return new(Guid.NewGuid(), definition.Kind, definition.Name, 0, 0, definition.PortType,
            definition.PortCategory, Parameters: parameters);
    }

    private static NodeParameterSnapshot Parameter(string id, double value) =>
        new(id, "float", JsonSerializer.SerializeToElement(value));
    private static NodeParameterSnapshot Vector(string id, params double[] value) =>
        new(id, value.Length == 3 ? "vector3" : "vector4", JsonSerializer.SerializeToElement(value));
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
