using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;
using LiberaFx.Services.Samples;

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
            "Standalone Game I/O metadata uses the legacy layout until the raw path is removed.");
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
                BinaryPrimitives.ReadUInt16LittleEndian(package.ParentBytecode.AsSpan(4)) == 11,
            "Spawnable package output must preserve root Game I/O metadata.");
        if (Environment.GetEnvironmentVariable("LIBERAFX_INSTANCE_GAMEIO_FIXTURES") is { Length: > 0 } fixtureDirectory)
        {
            Directory.CreateDirectory(fixtureDirectory);
            var persistentPackage = package with { AutoDestroyWhenIdle = false };
            File.WriteAllBytes(Path.Combine(fixtureDirectory, "RootGameIo.lfxp"),
                SpawnableGroupPackageBinary.Serialize(persistentPackage));
            WriteScriptActionFixture(fixtureDirectory);
        }
        var spawnableDefinition = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.SpawnableGroup);
        var templateWithGameIo = new GraphNodeSnapshot(Guid.NewGuid(), spawnableDefinition.Kind,
            spawnableDefinition.Name, 0, 0, spawnableDefinition.PortType, spawnableDefinition.PortCategory,
            ChildGraph: new GraphSnapshot([inputProperty], []));
        RequireInvalidMessage(() => SpawnableGroupPackage.Compile(new GraphSnapshot([templateWithGameIo], [])),
            "Spawn Input/Output", "Named Game I/O inside a Spawnable template must be rejected with guidance.");
        VerifyMatrixPorts();
        VerifyInstancePorts();
        VerifyCurrentCamera();

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

    private static void WriteScriptActionFixture(string fixtureDirectory)
    {
        var trigger = Node(GraphNodeKind.InputSignal, ("name", "trigger"));
        var action = Node(GraphNodeKind.OutputSignal, ("name", "action.sample"));
        var reply = Node(GraphNodeKind.InputProperty, ("name", "reply"), ("valueType", 0));
        var observed = Node(GraphNodeKind.OutputProperty, ("name", "observed"), ("valueType", 0));
        var graph = new GraphSnapshot([trigger, action, reply, observed],
        [
            new(trigger.Id, action.Id, "signal", "then"),
            new(reply.Id, observed.Id, "value", "value")
        ]) { AutoDestroyWhenIdle = false };
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "ScriptAction.lfxp"),
            SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(graph)));
    }

    private static void VerifyCurrentCamera()
    {
        var camera = Node(new GraphNodeKind("CurrentCamera"));
        var position = Node(GraphNodeKind.OutputProperty, ("name", "cameraPosition"), ("valueType", 2));
        var valid = Node(GraphNodeKind.OutputProperty, ("name", "cameraValid"), ("valueType", 0));
        var graph = new GraphSnapshot([camera, position, valid],
        [
            new(camera.Id, position.Id, "value", "position"),
            new(camera.Id, valid.Id, "value", "valid")
        ]) { AutoDestroyWhenIdle = false };
        var package = SpawnableGroupPackage.Compile(graph);
        var input = package.GameIoPorts.Single(port => port.Name == GameIoSchema.CurrentCameraProperty);
        Require(input.NodeId == camera.Id && input.Kind == GameIoPortKind.InputProperty && input.Width == 4 &&
                input.RuntimeNodeIds?.Single() == camera.Id,
            "Current Camera must bind a single host sample for Position and Valid.");
        var lowered = GameIoSchema.Lower(graph).Graph;
        var positionEdge = lowered.Connections.Single(edge => edge.TargetNodeId == position.Id);
        var validEdge = lowered.Connections.Single(edge => edge.TargetNodeId == valid.Id);
        Require(lowered.Nodes.Single(node => node.Id == positionEdge.SourceNodeId).Kind.Value == "Vector4ToVector3" &&
                lowered.Nodes.Single(node => node.Id == validEdge.SourceNodeId).Kind.Value == "BreakVector4" &&
                validEdge.SourcePortId == "w", "Position must not consume the validity lane.");
        RequireInvalid(() => SpawnableGroupPackage.Compile(graph with { Nodes = [.. graph.Nodes, Node(camera.Kind)] }),
            "Current Camera cannot register two samplers.");
        var fixtureDirectory = Environment.GetEnvironmentVariable("LIBERAFX_INSTANCE_GAMEIO_FIXTURES");
        if (!string.IsNullOrEmpty(fixtureDirectory))
        {
            Directory.CreateDirectory(fixtureDirectory);
            File.WriteAllBytes(Path.Combine(fixtureDirectory, "CurrentCamera.lfxp"), SpawnableGroupPackageBinary.Serialize(package));
        }
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
        var fixtureDirectory = Environment.GetEnvironmentVariable("LIBERAFX_INSTANCE_GAMEIO_FIXTURES");
        if (!string.IsNullOrWhiteSpace(fixtureDirectory))
        {
            Directory.CreateDirectory(fixtureDirectory);
            File.WriteAllBytes(Path.Combine(fixtureDirectory, "RootMatrixGameIo.lfxp"),
                SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(
                    graph with { AutoDestroyWhenIdle = false })));
        }
    }

    private static string PortShape(GameIoPort port) =>
        $"{port.NodeId:D}:{port.Name}:{port.Kind}:{port.Width}:{port.IsColor}:" +
        string.Join(",", port.RuntimeNodeIds ?? []);

    private static void VerifyInstancePorts()
    {
        var input = Node(new GraphNodeKind("InstanceInputProperty"), ("name", "speed"));
        var output = Node(new GraphNodeKind("InstanceOutputProperty"), ("name", "result"));
        var signal = Node(new GraphNodeKind("InstanceInputSignal"), ("name", "ping"));
        var response = Node(new GraphNodeKind("InstanceOutputSignal"), ("name", "pong"));
        var matrix = Node(new GraphNodeKind("InstanceInputProperty"), ("name", "matrix"), ("valueType", 5));
        var matrixOutput = Node(new GraphNodeKind("InstanceOutputProperty"), ("name", "matrixResult"), ("valueType", 5));
        var child = new GraphSnapshot([input, output, signal, response, matrix, matrixOutput],
            [new(input.Id, output.Id, "value", "value"), new(signal.Id, response.Id, "signal", "then"),
             new(matrix.Id, matrixOutput.Id, "value", "value")]);
        var spawnable = Node(GraphNodeKind.SpawnableGroup) with { ChildGraph = child };
        var spawn = Node(GraphNodeKind.InputSignal, ("name", "spawn"));
        var graph = new GraphSnapshot([spawn, spawnable], [new(spawn.Id, spawnable.Id, "spawn", "then")])
            { AutoDestroyWhenIdle = false };
        GraphValidator.ValidateDocument(graph);
        var package = SpawnableGroupPackage.Compile(graph);
        Require(package.GameIoPorts.Count == 1 && package.GameIoPorts[0].Name == "spawn" &&
            package.Templates.Count == 1 && package.Templates[0].Bytecode.Length > 0,
            "Dedicated instance ports must compile inside a template without leaking into shared root I/O.");
        RequireInvalid(() => GraphValidator.ValidateDocument(new GraphSnapshot([input], [])),
            "Instance ports must not be accepted as shared document-root ports.");
        RequireInvalid(() => VfxProgram.Compile(new GraphSnapshot([input], [])),
            "The public standalone compiler must not bypass instance-only placement.");
        var nested = Node(GraphNodeKind.GraphGroup) with { ChildGraph = child };
        RequireInvalid(() => GraphValidator.ValidateDocument(new GraphSnapshot(
            [spawnable with { ChildGraph = new GraphSnapshot([nested], []) }], [])),
            "Instance I/O must remain directly inside its Spawnable scope.");
        var folder = Environment.GetEnvironmentVariable("LIBERAFX_INSTANCE_GAMEIO_FIXTURES");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "InstanceGameIo.lfxp"), SpawnableGroupPackageBinary.Serialize(package));
        File.WriteAllBytes(Path.Combine(folder, "InstanceGameIo.container"), spawnable.Id.ToByteArray(bigEndian: true));
    }

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
