using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Commands;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;

namespace LiberaFx.Tests;

public static class CustomNodeCompilationTests
{
    public static void Run()
    {
        var exampleDirectory = FindExampleDirectory();
        var definitions = GraphNodeDefinition.LoadDirectory(exampleDirectory, out var warnings);
        Require(warnings.Count == 0 && definitions.Count == 2, "Custom node example definitions must validate.");

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var scaleGraph = JsonSerializer.Deserialize<GraphSnapshot>(
            File.ReadAllText(Path.Combine(exampleDirectory, "..", "Graphs", "CustomScaleGraph.json")), options)!;
        var scaleProgram = VfxProgram.Compile(scaleGraph);
        var scaleBytes = scaleProgram.ToBinary();
        Require(scaleProgram.BinaryVersion == 3 &&
                BinaryPrimitives.ReadUInt16LittleEndian(scaleBytes.AsSpan(4)) == 3,
            "A project custom node must select bytecode v3.");
        Require(scaleProgram.CustomInstructions.Count == 1 &&
                scaleProgram.CustomInstructions[0].Inputs.Length == 2 &&
                scaleProgram.CustomInstructions[0].Inputs.All(input => input.SourceIndex >= 0),
            "Typed custom inputs must compile to indexed dependencies.");

        var signalGraph = JsonSerializer.Deserialize<GraphSnapshot>(
            File.ReadAllText(Path.Combine(exampleDirectory, "..", "Graphs", "CustomSignalGraph.json")), options)!;
        var signalProgram = VfxProgram.Compile(signalGraph);
        var route = signalProgram.CustomInstructions.First(item =>
            item.TypeId == Guid.Parse("c3de3377-59ae-4a2d-8991-7f38e5d82112"));
        Require(signalProgram.BinaryVersion == 3 && route.BranchCount == 2 &&
                route.Inputs.Length == 1 && route.Inputs[0].SourceIndex >= 0 &&
                route.BranchTargets.Any(target => target >= 0),
            "Signal nodes must preserve parameter inputs and indexed branches.");
        var profiles = new TeamProfilesService(Path.Combine(AppDataDirectory.Root, "custom-node-compiler-profiles.json"));
        Require(profiles.Save(profiles.Active.Id, "Custom compiler fixtures", "", exampleDirectory), profiles.Status);
        using var catalog = new NodeDefinitionCatalog(profiles);
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager(), nodeDefinitions: catalog);
        var scalar = workspace.CreateNode(GraphNodeKind.Float);
        var routeDefinition = definitions.Single(definition => definition.Runtime?.Execution == "signal");
        var firstRoute = workspace.CreateNode(routeDefinition.Kind);
        var secondRoute = workspace.CreateNode(routeDefinition.Kind);
        var sequence = workspace.CreateNode(GraphNodeKind.Sequence);
        Require(workspace.TryConnectNodes(scalar, firstRoute, out _, "amount"),
            "The editor must accept parameter input on a signal node.");
        Require(workspace.TryConnectNodes(firstRoute, sequence, out _, "signal", "next") &&
                workspace.TryConnectNodes(sequence, secondRoute, out _, "enter", "then_0"),
            "The editor must connect actual signal pins, not the node's other input categories.");
        Require(!workspace.TryConnectNodes(firstRoute, secondRoute, out _, "amount", "done"),
            "The editor must reject signal-to-parameter pin connections.");
        var thirdRoute = workspace.CreateNode(routeDefinition.Kind);
        var fourthRoute = workspace.CreateNode(routeDefinition.Kind);
        Require(!workspace.TryConnectNodes(firstRoute, thirdRoute, out _, "enter", "next") &&
                workspace.TryConnectNodes(sequence, thirdRoute, out _, "enter", "then_1") &&
                workspace.TryConnectNodes(firstRoute, fourthRoute, out _, "enter", "done"),
            "Each custom Signal output must accept its own ordered targets.");
        var fanout = VfxProgram.Compile(workspace.CreateSnapshot());
        var fanoutRoute = fanout.CustomInstructions.Single(item => item.NodeId == firstRoute.Id);
        var sequenceRoute = fanout.CustomInstructions.Single(item => item.NodeId == sequence.Id);
        var next = routeDefinition.OutputPorts!.Where(port => port.Category == GraphPortCategory.Signal)
            .ToList().FindIndex(port => port.Id == "next");
        var done = routeDefinition.OutputPorts!.Where(port => port.Category == GraphPortCategory.Signal)
            .ToList().FindIndex(port => port.Id == "done");
        Require(fanout.BinaryVersion == 5 && next >= 0 && done >= 0 &&
                fanout.TypedInstructions[fanoutRoute.BranchTargets[next]].NodeId == sequence.Id &&
                fanoutRoute.BranchAdditionalTargets[next].Length == 0 &&
                fanout.TypedInstructions[sequenceRoute.BranchTargets[0]].NodeId == secondRoute.Id &&
                fanout.TypedInstructions[sequenceRoute.BranchAdditionalTargets[0].Single()].NodeId == thirdRoute.Id &&
                fanout.TypedInstructions[fanoutRoute.BranchTargets[done]].NodeId == fourthRoute.Id &&
                fanoutRoute.BranchAdditionalTargets[done].Length == 0,
            "Custom Signal fan-out must retain target order separately for each output.");
    }

    internal static string FindExampleDirectory()
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "Runtime", "examples", "CustomNodes");
        if (File.Exists(Path.Combine(packaged, "CustomScale.json"))) return packaged;
        for (DirectoryInfo? directory = new(Environment.CurrentDirectory);
             directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "Runtime", "examples", "CustomNodes");
            if (File.Exists(Path.Combine(candidate, "CustomScale.json"))) return candidate;
        }
        throw new DirectoryNotFoundException("Runtime/examples/CustomNodes was not found from the working directory.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
