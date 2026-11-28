using System.Text.Json;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Project;
using LiberaFx.Services.Samples;
using LiberaFx.ViewModels.Graph;
using LiberaFx.Services.Compilation;

internal static class ModelDrawTests
{
    public static void Run(string directory, bool materialAnimation = false)
    {
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("Supply an absolute, isolated fixture directory.");
        Directory.CreateDirectory(directory);
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager()) { AutoDestroyWhenIdle = false };
        // Explicit sample lookup keys, resolved by each demo's bindings, not portable asset IDs.
        var material = new EngineMaterial(0, materialAnimation
                ? "/Game/LiberaFxMaterialSample/PulseMaterial.PulseMaterial" : "builtin:unlit-color",
            "Cube material", [new("Color", "vector", JsonSerializer.SerializeToElement(new[] { 1d, 1, 1, 1 }))]);
        var asset = new EngineAsset(materialAnimation ? "/Engine/BasicShapes/Cube.Cube" :
            "/Game/LiberaFxSamples/Cube.Cube", "Cube", "model", "Sample",
            Materials: materialAnimation ? [] : [material]);
        var name = materialAnimation ? "MaterialPulse" : "Cube";
        var model = RenderNodeAuthoring.CreateModel(graph, asset, materialAnimation ? 1980 : 360, 100);
        var modelId = model.Id;
        graph.History.Undo();
        Require(graph.Nodes.Count == 0, "Asset creation must be one undo step.");
        Require(graph.History.Redo(), "Model creation must support redo.");
        model = graph.GetNode(modelId);
        if (materialAnimation)
            graph.AssignRenderMaterial(model, new(material.Path, "PulseMaterial", "material", "Sample",
                Materials: [material]), 0);
        graph.SetParameter(model, "rotation", JsonSerializer.SerializeToElement(new[] { 15d, 30, 0 }));
        graph.SetParameter(model, "scale", JsonSerializer.SerializeToElement(new[] { 1d, 1.5, 0.75 }));
        GraphNodeViewModel tint;
        if (materialAnimation)
        {
            GraphNodeKind Kind(string type) => graph.AvailableNodeDefinitions.Single(item => item.TypeName == type).Kind;
            static NodeParameterSnapshot Value(string id, object value) => new(id, value is double[] ? "vector4" : "float",
                JsonSerializer.SerializeToElement(value));
            graph.ApplyPlan(new(
                [new("time", Kind("Time"), "Effect time", 0, 100),
                 new("speed", Kind("Multiply"), "4-second period (pi / 2)", 280, 100, [Value("b", Math.PI / 2)]),
                 new("wave", Kind("Cos"), "Cosine", 560, 100),
                 new("amplitude", Kind("Multiply"), "Amplitude", 840, 100, [Value("b", -0.5)]),
                 new("bias", Kind("Add"), "White - Blue - White", 1120, 100, [Value("b", 0.5)]),
                 new("color", Kind("LerpVector4"), "White to Blue", 1480, 100,
                     [Value("a", new[] { 1d, 1, 1, 1 }), Value("b", new[] { 0d, 0, 1, 1 })])],
                [new("time", "speed", "a"), new("speed", "wave", "a"),
                 new("wave", "amplitude", "a", "value"), new("amplitude", "bias", "a"),
                 new("bias", "color", "c")]));
            tint = graph.Nodes.Single(node => node.Definition.TypeName == "LerpVector4");
        }
        else
        {
            tint = graph.CreateNode(GraphNodeKind.FloatX4, 60, 100);
            graph.SetParameter(tint, "value", JsonSerializer.SerializeToElement(new[] { 0.15, 0.65, 1, 1 }));
        }
        var colorPort = model.InputPorts.Single(port => port.Name == "Color").Id;
        Require(graph.TryConnectNodes(tint, model, out var error, targetPortId: colorPort,
            sourcePortId: materialAnimation ? "value" : null), error ?? "Color connection failed.");
        var snapshot = graph.CreateSnapshot();
        using (var stream = new MemoryStream())
        {
            new ProjectFileService().SaveAsync(stream, graph).GetAwaiter().GetResult();
            File.WriteAllBytes(Path.Combine(directory, name + ".lfxproj"), stream.ToArray());
        }
        using (var stream = new MemoryStream(File.ReadAllBytes(Path.Combine(directory, name + ".lfxproj"))))
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
            Require(draw.GetProperty("materials")[0].GetProperty("path").GetString() == material.Path,
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
        File.WriteAllBytes(Path.Combine(directory, name + ".lfxp"), SpawnableGroupPackageBinary.Serialize(package));
        Console.WriteLine("Authored ModelDraw " + name + " project/package: " + directory);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
