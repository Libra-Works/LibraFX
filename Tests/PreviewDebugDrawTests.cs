using System.Text;
using System.Text.Json;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Resources;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;

internal static class PreviewDebugDrawTests
{
    public static void Run(string? fixtureDirectory = null)
    {
        LiveGameDebugDisconnectTests.Run();
        var transform = Node("Transform", ("position", "vector3", new[] { 17d, 23d, 31d }));
        var value = Node("Float");
        var text = Node("DebugText", ("text", "string", "PRIVATE_DEBUG_ONLY_日本語"));
        var model = Node("DebugModel", ("assetPath", "string", "/Game/DebugOnly/Probe.Probe"));
        var graph = new GraphSnapshot([transform, value, text, model],
            [new(transform.Id, text.Id, "matrix", "value"), new(transform.Id, model.Id, "matrix", "value")])
            { AutoDestroyWhenIdle = false };
        var original = JsonSerializer.Serialize(graph);
        var stripped = PreviewDebugDraw.Strip(graph);
        var distribution = VfxProgram.Compile(graph, out var distributionScene);
        var preview = VfxProgram.Compile(graph, out var previewScene, options: VfxCompileOptions.LiveTransfer);
        Require(distribution.ToBinary().SequenceEqual(VfxProgram.Compile(stripped).ToBinary()),
            "Stripping observers must retain every ordinary numeric producer.");
        Require(distribution.ToBinary().SequenceEqual(preview.ToBinary()),
            "Debug observers must not change ordinary bytecode or its version.");
        using (var scene = JsonDocument.Parse(previewScene))
        {
            var draws = scene.RootElement.GetProperty("nodes").EnumerateArray().ToArray();
            Require(draws.Length == 2 && draws.All(draw => draw.GetProperty("previewOnly").GetBoolean()),
                "Only explicit live-transfer options may emit debug draw descriptors.");
            foreach (var draw in draws)
                Require(draw.GetProperty("inputs").EnumerateArray().Count(input =>
                    input.GetProperty("width").GetInt32() == 4 &&
                    input.GetProperty("sourceNodeId").ValueKind == JsonValueKind.String) == 4,
                    "World Matrix must reach adapters as four bound rows, not a truncated Vector4.");
        }
        Require(!Encoding.UTF8.GetString(distributionScene).Contains("DebugOnly", StringComparison.Ordinal) &&
            !Encoding.UTF8.GetString(distributionScene).Contains("PRIVATE_DEBUG", StringComparison.Ordinal),
            "Distribution scenes must not contain debug strings or assets.");
        using (var scene = JsonDocument.Parse(distributionScene))
            Require(scene.RootElement.GetProperty("nodes").GetArrayLength() == 0, "Default OFF.");
        var disabled = text with { Parameters = [.. text.Parameters!,
            new("enabled", "bool", JsonSerializer.SerializeToElement(false))] };
        using (var scene = JsonDocument.Parse(RenderProgramBindings.CreateScene(new([disabled], []), 1,
            options: VfxCompileOptions.LiveTransfer)))
            Require(scene.RootElement.GetProperty("nodes").GetArrayLength() == 0, "Disabled observer has no draw.");

        var life = Node("LifeTime");
        var signal = Node("Signal");
        var child = new GraphSnapshot([life, signal, text], []);
        var container = Node("SpawnableGroup") with { ChildGraph = child };
        var packageGraph = new GraphSnapshot([container], []);
        var package = SpawnableGroupPackage.Compile(packageGraph);
        var cleanPackage = SpawnableGroupPackage.Compile(PreviewDebugDraw.Strip(packageGraph));
        Require(SpawnableGroupPackageBinary.Serialize(package).SequenceEqual(SpawnableGroupPackageBinary.Serialize(cleanPackage)),
            "Distribution stripping must preserve Signal, scope lifetime, template identity and bytecode.");
        Require(package.Templates[0].Bytecode.SequenceEqual(
            SpawnableGroupPackage.Compile(packageGraph, options: VfxCompileOptions.LiveTransfer).Templates[0].Bytecode),
            "Preview-only observers cannot modify lifetime/Signal side effects.");
        Require(JsonSerializer.Serialize(graph) == original, "Compilation cannot edit source/history.");
        VerifyResourceManifest(graph, model);

        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        workspace.InitializeFromSnapshot(graph);
        var debug = workspace.GetNode(text.Id);
        workspace.SetParameter(debug, "enabled", JsonSerializer.SerializeToElement(false));
        Require(debug.PreviewDebugDrawStatus.Contains(
            LocalizationService.Shared.Get("DebugDraw.Disabled"), StringComparison.Ordinal), "Visible disabled badge.");
        Require(workspace.History.Undo() && workspace.History.Redo(), "Enabled edits use ordinary Undo/Redo.");
        using var project = new MemoryStream();
        new ProjectFileService().SaveAsync(project, workspace).GetAwaiter().GetResult();
        project.Position = 0;
        var restored = ProjectDocumentReader.LoadAsync(project).GetAwaiter().GetResult().Graph;
        Require(GraphSnapshot.ContentEquals(restored, workspace.CreateSnapshot()), "Stable IDs, text and enabled state persist.");
        workspace.SetSelection([workspace.GetNode(text.Id), workspace.GetNode(model.Id), workspace.GetNode(transform.Id)]);
        workspace.PasteSnapshot(workspace.CreateSelectionSnapshot(), 500, 300);
        Require(workspace.Nodes.Count(node => node.Definition.Kind.Value == "DebugText") == 2,
            "Copy/paste preserves dedicated debug nodes and their remapped IDs.");
        Require(workspace.Nodes.Select(node => node.Id).Distinct().Count() == workspace.Nodes.Count, "Paste IDs are unique.");
        var playback = new EditorPlayback();
        playback.Play(); playback.Pause();
        Require(!playback.Snapshot.DebugDrawStopped, "Pause retains debug observations.");
        playback.StopCommand.Execute(null);
        Require(playback.Snapshot.DebugDrawStopped, "Stop explicitly clears debug scope.");
        playback.Dispose();

        if (fixtureDirectory is null) return;
        Directory.CreateDirectory(fixtureDirectory);
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "preview.lfxp"),
            SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(graph, options: VfxCompileOptions.LiveTransfer)));
        var textGraph = graph with
        {
            Nodes = graph.Nodes.Where(node => node.Id != model.Id).ToArray(),
            Connections = graph.Connections.Where(edge => edge.TargetNodeId != model.Id).ToArray()
        };
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "text-preview.lfxp"),
            SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(textGraph, options: VfxCompileOptions.LiveTransfer)));
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "distribution.lfxp"),
            SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(graph)));
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "render-config.json"), previewScene);
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "distribution-render-config.json"), distributionScene);
    }

    private static GraphNodeSnapshot Node(string kind, params (string Id, string Type, object Value)[] parameters)
    {
        var definition = GraphNodeDefinition.All.Single(item => item.Kind.Value == kind);
        return new(Guid.NewGuid(), definition.Kind, definition.Name, 0, 0, definition.PortType,
            definition.PortCategory, Parameters: parameters.Select(item => new NodeParameterSnapshot(item.Id,
                item.Type, JsonSerializer.SerializeToElement(item.Value))).ToArray());
    }
    private static void VerifyResourceManifest(GraphSnapshot graph, GraphNodeSnapshot debugModel)
    {
        var ordinary = Node("ModelDraw", ("assetPath", "string", "/Game/Ordinary/Shared.Shared"));
        var sharedDebug = Node("DebugModel", ("assetPath", "string", "/Game/Ordinary/Shared.Shared"));
        graph = graph with { Nodes = [.. graph.Nodes, ordinary, sharedDebug] };
        var project = new EngineProjectIdentity("Unreal", Guid.NewGuid());
        var shared = Guid.NewGuid();
        var exclusive = Guid.NewGuid();
        var sharedBinding = new OpaqueBinding("unreal.object-path", "/Game/Ordinary/Shared.Shared");
        var exclusiveBinding = new OpaqueBinding("unreal.object-path", "/Game/DebugOnly/Probe.Probe");
        var identities = new ResourceIdentityDocument(1,
            [new(shared, "model", "Ordinary shared model"), new(exclusive, "model", "PRIVATE_DEBUG_RESOURCE")],
            [new(project, shared, "model", sharedBinding), new(project, exclusive, "model", exclusiveBinding)],
            [], [new(ordinary.Id, "assetPath", shared), new(sharedDebug.Id, "assetPath", shared),
                new(debugModel.Id, "assetPath", exclusive)], []);
        var probe = new ResourceIdentitySnapshotProbe(new(1, project, true,
            [new(sharedBinding, ResourceResolutionStatus.Resolved, "model")], []));
        var bytes = ResourceIdentityExport.CreateManifest(graph, identities, project, probe);
        using var manifest = JsonDocument.Parse(bytes);
        Require(manifest.RootElement.GetProperty("resources").GetArrayLength() == 1 &&
            manifest.RootElement.GetProperty("resources")[0].GetProperty("id").GetGuid() == shared &&
            manifest.RootElement.GetProperty("uses").GetArrayLength() == 1,
            "Export must strip exclusive debug resource references without losing shared ordinary resources.");
        Require(!Encoding.UTF8.GetString(bytes).Contains("PRIVATE_DEBUG_RESOURCE", StringComparison.Ordinal) &&
            !Encoding.UTF8.GetString(bytes).Contains("DebugOnly", StringComparison.Ordinal),
            "Exclusive debug assets cannot leak through the resource manifest.");
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
