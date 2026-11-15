using System.Text.Json;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;

internal static class AssetAuthoringTests
{
    public static void Run()
    {
        VerifyMaterialSchemaAndHistory();
        VerifyMaterialAssociationDelivery();
        VerifySpriteModes();
        VerifyLocalMetadata();
    }

    private static void VerifyMaterialAssociationDelivery()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var asset = new EngineAsset("/Engine/BasicShapes/Cube.Cube", "Cube", "model", "Unreal",
            Materials: [new(0, "/Engine/BasicShapes/BasicShapeMaterial.BasicShapeMaterial", "Basic", [
                new("LegacyGlobal", "vector", JsonSerializer.SerializeToElement(new[] { 1d, 0d, 0d, 1d }), 0, -1),
                new("Layer", "scalar", JsonSerializer.SerializeToElement(0.5), 0, 3)
            ])]);
        var node = RenderNodeAuthoring.CreateModel(graph, asset);
        var legacyId = node.InputPorts.Single(port => port.Name == "LegacyGlobal").Id;
        using var scene = JsonDocument.Parse(LiberaFx.Services.Compilation.RenderProgramBindings.CreateScene(graph.CreateSnapshot(), 1));
        if (scene.RootElement.GetProperty("nodes")[0].GetProperty("materials")[0].GetProperty("path").GetString()
            != asset.Materials![0].Path)
            throw new InvalidOperationException("Declared material overrides did not reach the renderer.");
        var inputs = scene.RootElement.GetProperty("nodes")[0].GetProperty("inputs").EnumerateArray()
            .Where(input => input.TryGetProperty("materialParameter", out var parameter) &&
                parameter.ValueKind == JsonValueKind.Object).ToArray();
        var legacy = inputs.Single(input => input.GetProperty("id").GetString() == legacyId).GetProperty("materialParameter");
        var layer = inputs.Single(input => input.GetProperty("materialParameter").GetProperty("name").GetString() == "Layer")
            .GetProperty("materialParameter");
        if (legacy.GetProperty("association").GetInt32() != 2 || layer.GetProperty("association").GetInt32() != 0 ||
            layer.GetProperty("index").GetInt32() != 3)
            throw new InvalidOperationException("Global material migration lost pin identity or changed valid layer bindings.");
    }

    private static void VerifyMaterialSchemaAndHistory()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var definition = GraphNodeDefinition.All.Single(item => item.TypeName == "ModelDraw");
        var asset = Asset(
            ("Opacity", "scalar", JsonSerializer.SerializeToElement(0.6)),
            ("GlowTint", "vector", JsonSerializer.SerializeToElement(new[] { 1d, 0.2, 0.1, 1d })),
            ("Noise", "texture", JsonSerializer.SerializeToElement("/Game/Textures/T_Noise")));
        var schema = RenderNodeSchema.Encode(asset);
        var described = RenderNodeSchema.Read(definition, [
            new("materialSchema", "string", JsonSerializer.SerializeToElement(schema))
        ]);
        var sourceParameters = asset.Materials!.SelectMany(material =>
            material.Parameters.Select(parameter => (material.Slot, parameter.Name, parameter.Kind))).ToArray();
        var sourceSlots = sourceParameters.Select(parameter => parameter.Slot).Distinct().ToHashSet();
        if (described.Count != sourceParameters.Length ||
            described.Select(item => item.Slot).Distinct().ToHashSet().SetEquals(sourceSlots) is false ||
            described.Any(item => !sourceParameters.Any(source =>
                source.Slot == item.Slot && source.Name == item.Name &&
                string.Equals(source.Kind, item.Kind, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("The assigned material schema did not retain both slots.");
        var effective = RenderNodeSchema.Effective(definition, [
            new("materialSchema", "string", JsonSerializer.SerializeToElement(schema))
        ]);
        var numeric = effective.InputPorts!.Where(port => port.Id.StartsWith("material_", StringComparison.Ordinal)).ToArray();
        var numericDescriptors = described.Where(parameter => parameter.Kind is "scalar" or "vector").ToArray();
        if (numeric.Length != numericDescriptors.Length ||
            numeric.Single(port => port.Name == "Opacity").PortType != "Float" ||
            numeric.Single(port => port.Name == "GlowTint").PortType != "FloatX4" ||
            numeric.Single(port => port.Name == "Pulse").PortType != "Float" ||
            !effective.Parameters.Any(parameter => parameter.Name == "Noise" && parameter.Type == "string") ||
            RenderNodeSchema.DescribePin(schema, described.Single(item => item.Kind == "texture").Id) is not null)
            throw new InvalidOperationException("Scalar/vector pins or texture property descriptors were not generated correctly.");

        var node = RenderNodeAuthoring.CreateModel(graph, asset);
        var opacity = node.InputPorts.Single(port => port.Name == "Opacity");
        var tint = node.InputPorts.Single(port => port.Name == "GlowTint");
        var scalarSource = graph.CreateNode(GraphNodeKind.Float);
        var vectorSource = graph.CreateNode(GraphNodeKind.FloatX4);
        if (!graph.TryConnectNodes(scalarSource, node, out _, opacity.Id) ||
            !graph.TryConnectNodes(vectorSource, node, out _, tint.Id))
            throw new InvalidOperationException("Material input pins did not accept matching numeric connections.");
        graph.SetParameter(node, opacity.Id, JsonSerializer.SerializeToElement(0.85));
        var saved = graph.CreateSnapshot();
        var restoredGraph = new GraphWorkspaceViewModel(new UndoRedoManager());
        restoredGraph.InitializeFromSnapshot(saved);
        var restored = restoredGraph.Nodes.Single(item => item.Definition.TypeName == "ModelDraw");
        if (restored.InputPorts.All(port => port.Id != opacity.Id) ||
            restored.GetParameterValue(restored.Definition.Parameters.Single(parameter => parameter.Id == opacity.Id))
                .GetDouble() != 0.85 ||
            restoredGraph.Connections.Count(connection => connection.Target == restored) != 2)
            throw new InvalidOperationException("Save/load did not restore material pins, their defaults, and connections.");

        var replacement = Asset(
            ("Opacity", "scalar", JsonSerializer.SerializeToElement(0.25)),
            ("Rim", "vector", JsonSerializer.SerializeToElement(new[] { 0d, 1d, 0d, 1d })));
        RenderNodeAuthoring.AssignAsset(restoredGraph, restored, replacement);
        if (restoredGraph.Connections.Count(connection => connection.Target == restored) != 1 ||
            restored.InputPorts.Any(port => port.Id == tint.Id) ||
            restored.InputPorts.All(port => port.Id == opacity.Id))
            throw new InvalidOperationException("Changing material schemas did not retain compatible links and remove stale links.");
        restoredGraph.History.Undo();
        restored = restoredGraph.GetNode(restored.Id);
        if (restoredGraph.Connections.Count(connection => connection.Target == restored) != 2 ||
            restored.InputPorts.All(port => port.Id == tint.Id) ||
            restored.GetParameterValue(restored.Definition.Parameters.Single(parameter => parameter.Id == opacity.Id))
                .GetDouble() != 0.85)
            throw new InvalidOperationException("Undo did not restore material pins, links, and parameter values.");

        var planGraph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var modelDefinition = GraphNodeDefinition.All.Single(item => item.TypeName == "ModelDraw");
        var planAsset = Asset(("Opacity", "scalar", JsonSerializer.SerializeToElement(0.5)));
        var planSchema = RenderNodeSchema.Encode(planAsset);
        var planPin = RenderNodeSchema.Describe(planSchema).Single(item => item.Name == "Opacity");
        var plan = new LiberaGraphEditPlan(
        [
            new("driver", GraphNodeKind.Float, "Driver", 0, 0),
            new("model", modelDefinition.Kind, "Model", 200, 0,
            [
                new("assetPath", "string", JsonSerializer.SerializeToElement(planAsset.Path)),
                new("materialSchema", "string", JsonSerializer.SerializeToElement(planSchema))
            ])
        ],
        [new("driver", "model", planPin.Id)]);
        planGraph.ApplyPlan(plan);
        var plannedModel = planGraph.Nodes.Single(item => item.Definition.TypeName == "ModelDraw");
        if (planGraph.Connections.Count != 1 || plannedModel.InputPorts.All(port => port.Id != planPin.Id))
            throw new InvalidOperationException("Parameterized render pins were not available during atomic graph-plan validation.");
        planGraph.History.Undo();
        if (planGraph.Nodes.Count != 0 || planGraph.Connections.Count != 0)
            throw new InvalidOperationException("An asset-initialized render graph plan was not one undoable change.");

        var invalid = restoredGraph.CreateSnapshot();
        var target = invalid.Nodes.Single(item => item.Id == restored.Id);
        var values = (target.Parameters ?? []).ToList();
        var index = values.FindIndex(item => item.Id == "materialSchema");
        values[index] = new("materialSchema", "string", JsonSerializer.SerializeToElement("{invalid"));
        try
        {
            GraphWorkspaceViewModel.ValidateSnapshot(invalid with
            {
                Nodes = invalid.Nodes.Select(item => item.Id == target.Id ? item with { Parameters = values } : item).ToArray()
            });
            throw new InvalidOperationException("Invalid stored material schema was accepted.");
        }
        catch (InvalidDataException) { }
    }

    private static EngineAsset Asset(params (string Name, string Kind, JsonElement Value)[] firstSlotParameters) =>
        new("/Game/Meshes/SM_Test.SM_Test", "SM_Test", "model", "Unreal", 512, 512,
        [
            new EngineMaterial(0, "/Game/Materials/M_FX.M_FX", "M_FX", firstSlotParameters
                .Select(parameter => new EngineMaterialParameter(parameter.Name, parameter.Kind, parameter.Value))
                .ToArray()),
            new EngineMaterial(1, "/Game/Materials/M_Secondary.M_Secondary", "M_Secondary",
                [new EngineMaterialParameter("Pulse", "scalar", JsonSerializer.SerializeToElement(1d))])
        ]);

    private static void VerifyLocalMetadata()
    {
        var directory = Path.Combine(AppDataDirectory.Root, "LiberaFx", "asset-metadata-tests-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(directory, "asset-metadata.json");
        const string assetPath = "/Game/FX/T_Flame.T_Flame";
        try
        {
            var store = new AssetMetadataStore(file);
            store.Set("Unreal", assetPath, new AssetMetadata("Unreal", assetPath, "Orange flame impact",
                ["flame", "hitmark"], "flame hitmark", 4, 2, 18));
            var loaded = new AssetMetadataStore(file).Get("Unreal", assetPath);
            if (loaded.NormalizedTags.Count != 2 || loaded.SemanticUsage != "flame hitmark" ||
                loaded.AtlasColumns != 4 || loaded.AtlasRows != 2 || loaded.FramesPerSecond != 18 ||
                new AssetMetadataStore(file).Search("flame hitmark", "Unreal").Count != 1 ||
                Directory.Exists(Path.Combine(directory, "Game")))
                throw new InvalidOperationException("Local semantic/atlas metadata did not persist independently of engine asset files.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifySpriteModes()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var atlas = new EngineAsset("/Game/Textures/T_Atlas.T_Atlas", "T_Atlas",
            "texture", "Unreal", 1024, 512);
        var animated = RenderNodeAuthoring.CreateSprite(graph, atlas, 4, 2, 18);
        var animatedFrame = animated.Definition.Parameters.Single(parameter => parameter.Id == "frame");
        var animatedPlaying = animated.Definition.Parameters.Single(parameter => parameter.Id == "playing");
        if (animated.Definition.TypeName != "SpriteAnimation" ||
            animated.GetParameterValue(animatedFrame).GetDouble() != -1 ||
            !animated.GetParameterValue(animatedPlaying).GetBoolean() ||
            animated.GetParameterValue(animated.Definition.Parameters.Single(parameter => parameter.Id == "columns")).GetInt32() != 4)
            throw new InvalidOperationException("Animated sprite atlas defaults or authored layout were not preserved.");
        var staticSprite = RenderNodeAuthoring.CreateSprite(graph, atlas, 2, 1, 0);
        if (staticSprite.Definition.TypeName != "SpriteDraw" ||
            staticSprite.GetParameterValue(staticSprite.Definition.Parameters.Single(parameter => parameter.Id == "frame")).GetDouble() != 0 ||
            staticSprite.GetParameterValue(staticSprite.Definition.Parameters.Single(parameter => parameter.Id == "playing")).GetBoolean())
            throw new InvalidOperationException("Static sprite defaults were not preserved.");
    }
}
