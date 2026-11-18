using System.Text.Json;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;

internal static class ModelDrawTests
{
    public static void Run(string directory)
    {
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("Supply an absolute, isolated fixture directory.");
        Directory.CreateDirectory(directory);
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager()) { AutoDestroyWhenIdle = false };
        // Explicit sample lookup keys, resolved by each demo's bindings, not portable asset IDs.
        var asset = new EngineAsset("/Game/LiberaFxSamples/Cube.Cube", "Cube", "model", "Sample",
            Materials: [new(0, "builtin:unlit-color", "Cube material",
                [new("Color", "vector", JsonSerializer.SerializeToElement(new[] { 1d, 1, 1, 1 }))])]);
        var model = RenderNodeAuthoring.CreateModel(graph, asset, 360, 100);
        var modelId = model.Id;
        graph.History.Undo();
        Require(graph.Nodes.Count == 0, "Asset creation must be one undo step.");
        Require(graph.History.Redo(), "Model creation must support redo.");
        model = graph.GetNode(modelId);
        graph.SetParameter(model, "rotation", JsonSerializer.SerializeToElement(new[] { 15d, 30, 0 }));
        graph.SetParameter(model, "scale", JsonSerializer.SerializeToElement(new[] { 1d, 1.5, 0.75 }));
        var tint = graph.CreateNode(GraphNodeKind.FloatX4, 60, 100);
        graph.SetParameter(tint, "value", JsonSerializer.SerializeToElement(new[] { 0.15, 0.65, 1, 1 }));
        var colorPort = model.InputPorts.Single(port => port.Name == "Color").Id;
        Require(graph.TryConnectNodes(tint, model, out var error, targetPortId: colorPort), error ?? "Color connection failed.");
        var snapshot = graph.CreateSnapshot();
        using (var stream = new MemoryStream())
        {
            new ProjectFileService().SaveAsync(stream, graph).GetAwaiter().GetResult();
            File.WriteAllBytes(Path.Combine(directory, "Cube.lfxproj"), stream.ToArray());
        }
        using (var stream = new MemoryStream(File.ReadAllBytes(Path.Combine(directory, "Cube.lfxproj"))))
        {
            var loaded = new ProjectFileService().LoadAsync(stream).GetAwaiter().GetResult();
            Require(GraphSnapshot.ContentEquals(snapshot, loaded.Graph), "Model graph changed during project persistence.");
            var restored = new GraphWorkspaceViewModel(new UndoRedoManager());
            restored.InitializeFromSnapshot(loaded.Graph);
            snapshot = restored.CreateSnapshot();
        }
        var package = SpawnableGroupPackage.Compile(snapshot);
        using (var json = JsonDocument.Parse(package.RootRenderConfiguration))
        {
            var draw = json.RootElement.GetProperty("nodes").EnumerateArray().Single();
            Require(draw.GetProperty("nodeId").GetGuid() == modelId &&
                draw.GetProperty("kind").GetString() == "model" &&
                draw.GetProperty("assetPath").GetString() == asset.Path, "Compilation lost the authored model.");
            Require(draw.GetProperty("materials")[0].GetProperty("path").GetString() == "builtin:unlit-color",
                "Compilation lost the declared material binding.");
            var inputs = draw.GetProperty("inputs").EnumerateArray()
                .ToDictionary(input => input.GetProperty("id").GetString()!);
            Require(inputs[colorPort].GetProperty("sourceNodeId").GetGuid() == tint.Id &&
                inputs[colorPort].GetProperty("materialParameter").GetProperty("name").GetString() == "Color",
                "The material color must come from the compiled graph, not an engine-side hardcoded color.");
            Require(inputs["rotation"].GetProperty("defaults").EnumerateArray().Select(value => value.GetDouble())
                .SequenceEqual(new[] { 15d, 30, 0 }) &&
                inputs["scale"].GetProperty("defaults").EnumerateArray().Select(value => value.GetDouble())
                .SequenceEqual(new[] { 1d, 1.5, 0.75 }), "Compilation lost the authored transform defaults.");
        }
        File.WriteAllBytes(Path.Combine(directory, "Cube.lfxp"), SpawnableGroupPackageBinary.Serialize(package));
        Console.WriteLine("Authored ModelDraw Cube project/package: " + directory);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
