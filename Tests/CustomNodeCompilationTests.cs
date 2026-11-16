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
        Require(workspace.TryConnectNodes(scalar, firstRoute, out _, "amount"),
            "The editor must accept parameter input on a signal node.");
        Require(workspace.TryConnectNodes(firstRoute, secondRoute, out _, "enter", "next"),
            "The editor must connect actual signal pins, not the node's other input categories.");
        Require(!workspace.TryConnectNodes(firstRoute, secondRoute, out _, "amount", "done"),
            "The editor must reject signal-to-parameter pin connections.");
    }

    private static string FindExampleDirectory()
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
