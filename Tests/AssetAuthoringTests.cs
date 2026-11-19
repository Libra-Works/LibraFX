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
        VerifyProjectAssetRoot();
        VerifyTextureOnlyDrawAssignment("FlexiblePlaneDraw");
        VerifyCallerTargetBinding();
        VerifyCallerTargetBinding(inline: true);
        VerifyCallerTargetBinding(material: true);
        VerifyCallerTargetBinding(inline: true, material: true);
    }

    private static void VerifyTextureOnlyDrawAssignment(string typeName)
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var definition = GraphNodeDefinition.All.Single(item => item.TypeName == typeName);
        var node = graph.CreateNode(definition.Kind);
        var id = node.Id;
        string PathValue() => graph.GetNode(id).GetParameterValue(
            definition.Parameters.Single(parameter => parameter.Id == "assetPath")).GetString()!;
        var original = PathValue();
        var texture = new EngineAsset("/Game/Textures/T_Ribbon", "Ribbon", "texture", "Unreal",
            Materials: [new(0, "/Game/Materials/M_Ribbon", "Ribbon", [])]);
        var targets = ResourceAssignment.Targets(node, texture);
        if (targets.Count != 1 || targets[0].Kind != ResourceTargetKind.RenderAsset ||
            ResourceAssignment.AssetClassForParameter(node, "assetPath") != "texture")
        {
            throw new InvalidOperationException($"{typeName} must expose one typed primary texture target.");
        }
        ResourceAssignment.Assign(graph, node, texture, targets[0]);
        if (PathValue() != texture.Path ||
            graph.GetNode(id).Parameters?.Any(parameter => parameter.Id == "materialSchema") == true ||
            ResourceAssignment.Targets(graph.GetNode(id),
                new EngineAsset("/Game/Materials/M_Ribbon", "Ribbon", "material", "Unreal")).Count != 0)
        {
            throw new InvalidOperationException($"{typeName} texture assignment must not invent material slots or schema.");
        }
        graph.History.Undo();
        if (PathValue() != original)
        {
            throw new InvalidOperationException($"{typeName} texture assignment must be undoable.");
        }
        graph.History.Redo();
        if (PathValue() != texture.Path)
        {
            throw new InvalidOperationException($"{typeName} texture assignment must be redoable.");
        }
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
        var textureParameterId = described.Single(item => item.Kind == "texture").Id;
        var textureTarget = ResourceAssignment.TargetForParameter(node, textureParameterId);
        if (textureTarget is not { Kind: ResourceTargetKind.MaterialTexture } ||
            textureTarget.Id != textureParameterId ||
            ResourceAssignment.AssetClassForParameter(node, textureTarget.Id) != "texture")
            throw new InvalidOperationException("A material texture parameter did not expose a typed resource target.");
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

    private static void VerifyProjectAssetRoot()
    {
        var directory = Path.Combine(Path.GetTempPath(), "liberafx-asset-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var projectPath = Path.Combine(directory, "Test.lfxproj");
        try
        {
            File.WriteAllText(projectPath, "{}");
            var root = ProjectAssetRoot.FromProjectFileKey(new Uri(projectPath).AbsoluteUri);
            if (root is null || !Path.GetFullPath(root).Equals(Path.GetFullPath(directory),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                ProjectAssetRoot.FromProjectFileKey(null) is not null)
                throw new InvalidOperationException("The physical root was not derived from the saved .lfxproj URI.");

            var created = ProjectAssetRoot.CreateFolder(root, AssetBrowserRootKind.PhysicalProject, "Textures");
            if (!Directory.Exists(created) || ProjectAssetRoot.List(root).All(entry =>
                    entry.Name != "Textures" || !entry.IsDirectory))
                throw new InvalidOperationException("A project-root folder was not created and surfaced in the listing.");
            var nested = ProjectAssetRoot.CreateFolder(root, AssetBrowserRootKind.PhysicalProject,
                "Nested", "Textures");
            if (!Directory.Exists(nested) || ProjectAssetRoot.List(root, "Textures").All(entry =>
                    entry.Name != "Nested" || !entry.IsDirectory))
                throw new InvalidOperationException("A nested project folder escaped the physical-root listing.");
            var peer = Guid.NewGuid();
            var engineAsset = new EngineAsset("/Game/Textures/T_Test.T_Test", "T_Test", "texture", "Unreal");
            var virtualReference = new AssetBrowserRootEntry(AssetBrowserRootKind.VirtualEngine,
                engineAsset.Name, engineAsset.Path, false, engineAsset, peer).Reference;
            var physicalReference = new AssetBrowserRootEntry(AssetBrowserRootKind.PhysicalProject,
                "T_Local.png", Path.Combine("Textures", "T_Local.png"), false, ProjectRoot: root).Reference;
            if (virtualReference is not { Root: AssetBrowserRootKind.VirtualEngine, SessionPeerId: var boundPeer } ||
                boundPeer != peer || physicalReference is not
                    { Root: AssetBrowserRootKind.PhysicalProject, ProjectRoot: var boundRoot } ||
                boundRoot != root)
                throw new InvalidOperationException("Browser selection did not retain typed root and caller context.");

            try
            {
                ProjectAssetRoot.CreateFolder(root, AssetBrowserRootKind.VirtualEngine, "NeverCreate");
                throw new InvalidOperationException("A physical folder operation was accepted for the virtual root.");
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("physical project root", StringComparison.OrdinalIgnoreCase)) { }

            try
            {
                ProjectAssetRoot.CreateFolder(root, AssetBrowserRootKind.PhysicalProject, "..");
                throw new InvalidOperationException("A path traversal folder name was accepted.");
            }
            catch (ArgumentException) { }

            try
            {
                ProjectAssetRoot.CreateFolder(root, AssetBrowserRootKind.PhysicalProject, "Escape", "..");
                throw new InvalidOperationException("A traversal parent folder escaped the physical root.");
            }
            catch (UnauthorizedAccessException) { }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyCallerTargetBinding(bool inline = false, bool material = false)
    {
        var main = new MainViewModel();
        var peerId = Guid.NewGuid();
        var sentFrames = 0;
        main.PreviewTelemetry.Connect(peerId, 3, _ =>
        {
            sentFrames++;
            return Task.CompletedTask;
        });
        try
        {
            var hello = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "hello",
                version = 1,
                engine = "Unreal",
                role = "editor",
                capabilities = new[] { "assets" }
            });
            if (!main.PreviewTelemetry.Receive(peerId, hello))
                throw new InvalidOperationException("The in-memory asset peer fixture was rejected.");
            main.RefreshPreviewConnections();

            var coordinator = main.AssetBrowser;
            var sharedViewModel = coordinator.ViewModel;
            if (sharedViewModel.SelectedPeer?.ClientId != peerId)
                throw new InvalidOperationException("The in-memory asset peer was not selected.");

            var document = main.ActiveDocument
                ?? throw new InvalidOperationException("The test main view model has no active document.");
            var graph = document.Workspace;
            if (inline)
            {
                graph = graph.OpenInlineGraph(graph.CreateNode(GraphNodeKind.GraphGroup));
                graph.ActivateEditingWorkspace();
            }
            var sourceAsset = new EngineAsset("/Game/Meshes/SM_Binding.SM_Binding",
                "SM_Binding", "model", "Unreal");
            var selectedAsset = material
                ? new EngineAsset("/Game/Materials/M_Selected.M_Selected", "M_Selected", "material", "Unreal",
                    Materials: [new(0, "/Game/Materials/M_Selected.M_Selected", "M_Selected",
                        [new("Color", "vector", JsonSerializer.SerializeToElement(new[] { 1d, 1, 1, 1 }))])])
                : new EngineAsset("/Game/Meshes/SM_Selected.SM_Selected", "SM_Selected", "model", "Unreal");
            var parameterId = material ? RenderNodeSchema.MaterialSlotId(0) : "assetPath";
            var firstNode = RenderNodeAuthoring.CreateModel(graph, sourceAsset);
            var secondNode = RenderNodeAuthoring.CreateModel(graph,
                new EngineAsset("/Game/Meshes/SM_Other.SM_Other", "SM_Other", "model", "Unreal"));

            ResourceAssignmentTarget TargetFor(GraphNodeViewModel node) =>
                ResourceAssignment.TargetForParameter(node, parameterId)
                ?? throw new InvalidOperationException("The model asset parameter did not expose a caller target.");

            AssetSelectionRequest BeginFor(GraphNodeViewModel node)
            {
                graph.SelectNode(node);
                var target = TargetFor(node);
                var request = coordinator.BeginForParameter(document, graph, node, parameterId,
                    target, selectedAsset.AssetClass, static () => true)
                    ?? throw new InvalidOperationException("A current resource parameter did not bind to the browser.");
                if (!coordinator.IsCurrent(request, peerId))
                    throw new InvalidOperationException("A freshly captured resource target was unexpectedly stale.");
                return request;
            }

            bool TryApply(ResourceAssignmentTarget target) =>
                coordinator.TrySelect(selectedAsset,
                    new AssetBrowserAssetReference(AssetBrowserRootKind.VirtualEngine,
                        selectedAsset.Engine, selectedAsset.Path, peerId),
                    peerId, target, out _);

            var documentBound = BeginFor(firstNode);
            main.NewDocumentCommand.Execute(null);
            if (TryApply(TargetFor(firstNode)) || !documentBound.IsStale)
                throw new InvalidOperationException("A resource selection applied after switching documents.");
            main.ActivateDocument(document);

            var nodeBound = BeginFor(firstNode);
            graph.SelectNode(secondNode);
            if (TryApply(TargetFor(firstNode)) || !nodeBound.IsStale)
                throw new InvalidOperationException("A resource selection applied after switching nodes.");

            var parameterBound = BeginFor(firstNode);
            if (material)
                graph.AssignRenderMaterial(firstNode,
                    new("/Game/Materials/M_Changed.M_Changed", "M_Changed", "material", "Unreal",
                        Materials: [new(0, "/Game/Materials/M_Changed.M_Changed", "M_Changed", [])]), 0);
            else
                graph.SetParameter(firstNode, "assetPath",
                    JsonSerializer.SerializeToElement("/Game/Meshes/SM_Changed.SM_Changed"));
            if (TryApply(TargetFor(firstNode)) || !parameterBound.IsStale)
                throw new InvalidOperationException("A resource selection applied after its parameter value changed.");

            var replacedCaller = BeginFor(firstNode);
            graph.SelectNode(secondNode);
            var receiverCalled = false;
            var otherCaller = coordinator.BeginForOtherWindow(document, graph, secondNode,
                static () => true, _ =>
                {
                    receiverCalled = true;
                    return true;
                }, "model")
                ?? throw new InvalidOperationException("A second caller was not bound to the shared browser.");
            if (!replacedCaller.IsStale || coordinator.IsCurrent(replacedCaller, peerId))
                throw new InvalidOperationException("Replacing a caller left its previous target current.");

            graph.SelectNode(null);
            if (coordinator.BeginForCurrentSelection() is not null ||
                coordinator.Request is not null || !otherCaller.IsStale ||
                TryApply(TargetFor(secondNode)) || receiverCalled)
                throw new InvalidOperationException("Clearing the active caller left a stale selection applicable.");

            if (!ReferenceEquals(sharedViewModel, coordinator.ViewModel))
                throw new InvalidOperationException("Caller replacement discarded the MainViewModel-owned shared VM.");
            BeginFor(firstNode);
            if (!TryApply(TargetFor(firstNode)) ||
                material && firstNode.InputPorts.All(port => port.Name != "Color"))
                throw new InvalidOperationException("A current resource selection did not assign its metadata to the caller.");
            if (sentFrames != 0)
                throw new InvalidOperationException("The caller-binding source test unexpectedly sent a preview request.");
        }
        finally
        {
            main.DisposeAssetBrowser();
            main.PreviewTelemetry.Disconnect(peerId);
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
