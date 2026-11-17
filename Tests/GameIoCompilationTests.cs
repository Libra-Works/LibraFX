using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;

namespace LiberaFx.Tests;

public static class GameIoCompilationTests
{
    public static void Run()
    {
        var authoringKinds = GraphNodeDefinition.AuthoringCore.Select(item => item.Kind).ToHashSet();
        Require(authoringKinds.IsSupersetOf([
                    GraphNodeKind.InputProperty, GraphNodeKind.InputSignal,
                    GraphNodeKind.OutputProperty, GraphNodeKind.OutputSignal,
                    new GraphNodeKind("OwnerMatrix")]) &&
                GraphNodeDefinition.AuthoringCore.All(item => !item.IsInternalRuntime),
            "The public Game I/O nodes must be discoverable without exposing runtime lowering nodes.");
        var inputProperty = Node(GraphNodeKind.InputProperty, ("name", "position"), ("valueType", 2));
        var inputSignal = Node(GraphNodeKind.InputSignal, ("name", "trigger"));
        var color = Node(GraphNodeDefinition.All.Single(item => item.TypeName == "Color"));
        var outputProperty = Node(GraphNodeKind.OutputProperty, ("name", "tint"), ("valueType", 4));
        var outputSignal = Node(GraphNodeKind.OutputSignal, ("name", "finished"));
        var graph = new GraphSnapshot(
            [inputProperty, inputSignal, color, outputProperty, outputSignal],
            [
                new(color.Id, outputProperty.Id, "value"),
                new(inputSignal.Id, outputSignal.Id, "signal", "then")
            ]);

        GraphValidator.Validate(graph);
        var effectiveOutput = GameIoSchema.Effective(
            GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.OutputProperty),
            outputProperty.Parameters);
        Require(effectiveOutput.GetInputPortType("value") == "Color",
            "The selected property type must update the visible input port.");

        var program = VfxProgram.Compile(graph);
        Require(program.BinaryVersion == 7 &&
                program.GameIoPorts.Select(port => port.Name).SequenceEqual(
                    ["position", "trigger", "tint", "finished"]) &&
                program.GameIoPorts[0].Width == 3 &&
                program.GameIoPorts[2].Width == 4 && program.GameIoPorts[2].IsColor,
            "Root Game I/O descriptors must preserve names, direction, width, and Color identity.");
        var binary = program.ToBinary();
        var metadataOffset = 16 + program.TypedInstructions.Count * 256;
        Require(BinaryPrimitives.ReadUInt16LittleEndian(binary.AsSpan(4)) == 7 &&
                binary.AsSpan(metadataOffset, 4).SequenceEqual("LFIO"u8),
            "Game I/O metadata must use the v7 extension after instruction data.");
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };
        var compileMetadata = JsonSerializer.Serialize(new { gameIoPorts = program.GameIoPorts }, jsonOptions);
        Require(compileMetadata.Contains("\"kind\":\"OutputProperty\"", StringComparison.Ordinal) &&
                compileMetadata.Contains("\"name\":\"finished\"", StringComparison.Ordinal),
            "MCP compile metadata must expose named Game I/O descriptors.");
        if (Environment.GetEnvironmentVariable("LIBERAFX_GAMEIO_BYTECODE") is { Length: > 0 } bytecodePath)
            File.WriteAllBytes(bytecodePath, binary);

        var package = SpawnableGroupPackage.Compile(graph);
        Require(package.GameIoPorts.Select(PortShape).SequenceEqual(program.GameIoPorts.Select(PortShape)) &&
                BinaryPrimitives.ReadUInt16LittleEndian(package.ParentBytecode.AsSpan(4)) == 7,
            "Spawnable package output must preserve root Game I/O metadata.");
        var spawnableDefinition = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.SpawnableGroup);
        var templateWithGameIo = new GraphNodeSnapshot(Guid.NewGuid(), spawnableDefinition.Kind,
            spawnableDefinition.Name, 0, 0, spawnableDefinition.PortType, spawnableDefinition.PortCategory,
            ChildGraph: new GraphSnapshot([inputProperty], []));
        RequireInvalidMessage(() => SpawnableGroupPackage.Compile(new GraphSnapshot([templateWithGameIo], [])),
            "Spawn Input/Output", "Named Game I/O inside a Spawnable template must be rejected with guidance.");
        VerifyMatrixPorts();

        var duplicate = graph with
        {
            Nodes = graph.Nodes.Select(node => node.Id == outputSignal.Id
                ? node with
                {
                    Parameters = node.Parameters!.Select(parameter => parameter.Id == "name"
                        ? parameter with { Value = JsonSerializer.SerializeToElement("trigger") }
                        : parameter).ToArray()
                }
                : node).ToArray()
        };
        RequireInvalid(() => _ = VfxProgram.Compile(duplicate),
            "Duplicate Game I/O port names must be rejected.");
        var groupDefinition = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.GraphGroup);
        var nestedGroup = new GraphNodeSnapshot(Guid.NewGuid(), GraphNodeKind.GraphGroup, "Group", 0, 0,
            groupDefinition.PortType, groupDefinition.PortCategory,
            ChildGraph: new GraphSnapshot([inputProperty], []));
        RequireInvalid(() => GraphValidator.Validate(new GraphSnapshot([nestedGroup], [])),
            "Game I/O nodes must be rejected below the root graph.");
    }

    private static void VerifyMatrixPorts()
    {
        var owner = Node(new GraphNodeKind("OwnerMatrix"));
        var local = Node(GraphNodeKind.InputProperty, ("name", "local"), ("valueType", 5));
        var multiply = Node(new GraphNodeKind("MatrixMultiply"));
        var world = Node(GraphNodeKind.OutputProperty, ("name", "world"), ("valueType", 5));
        var graph = new GraphSnapshot([owner, local, multiply, world],
        [
            new(owner.Id, multiply.Id, "parent", "value"),
            new(local.Id, multiply.Id, "local", "value"),
            new(multiply.Id, world.Id, "value", "value")
        ]);
        GraphValidator.Validate(graph);
        var program = VfxProgram.Compile(graph);
        Require(program.GameIoPorts.Count == 3 &&
                program.GameIoPorts.All(port => port.Width == 16 &&
                    port.RuntimeNodeIds?.Count == 4) &&
                program.GameIoPorts.Select(port => port.Kind).SequenceEqual([
                    GameIoPortKind.OwnerMatrix, GameIoPortKind.InputProperty, GameIoPortKind.OutputProperty]),
            "Owner Matrix and Matrix Properties must each bind all four runtime rows.");
        var bytecodePath = Environment.GetEnvironmentVariable("LIBERAFX_GAMEIO_MATRIX_BYTECODE");
        if (!string.IsNullOrWhiteSpace(bytecodePath))
            File.WriteAllBytes(bytecodePath, program.ToBinary());
    }

    private static string PortShape(GameIoPort port) =>
        $"{port.NodeId:D}:{port.Name}:{port.Kind}:{port.Width}:{port.IsColor}:" +
        string.Join(",", port.RuntimeNodeIds ?? []);

    private static GraphNodeSnapshot Node(GraphNodeKind kind, params (string Id, object Value)[] values)
    {
        var definition = GraphNodeDefinition.All.FirstOrDefault(item => item.Kind == kind)
            ?? throw new InvalidOperationException(
                $"Missing node definition '{kind}'. Loaded: {string.Join(", ", GraphNodeDefinition.All.Select(item => item.Kind.Value))}");
        return Node(definition, values);
    }

    private static GraphNodeSnapshot Node(GraphNodeDefinition definition, params (string Id, object Value)[] values)
    {
        var kind = definition.Kind;
        var parameters = values.Select(item =>
        {
            var parameter = definition.Parameters.FirstOrDefault(candidate => candidate.Id == item.Id)
                ?? throw new InvalidOperationException(
                    $"{kind} has no parameter '{item.Id}'. Found: {string.Join(", ", definition.Parameters.Select(candidate => candidate.Id))}");
            return new NodeParameterSnapshot(parameter.Id, parameter.Type,
                JsonSerializer.SerializeToElement(item.Value));
        }).ToArray();
        return new(Guid.NewGuid(), kind, definition.Name, 0, 0, definition.PortType,
            definition.PortCategory, Parameters: parameters);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RequireInvalid(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void RequireInvalidMessage(Action action, string expectedMessage, string failureMessage)
    {
        try { action(); }
        catch (InvalidDataException ex) when (ex.Message.Contains(expectedMessage, StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException(failureMessage);
    }
}
