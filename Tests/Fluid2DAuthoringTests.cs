using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LibraGpu.Core;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Gpu;
using LiberaFx.Services.Mcp;
using LiberaFx.Services.Project;
using LiberaFx.Services.Samples;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;
using LiberaFx.Views.Gpu;
using LiberaFx.Services.Compilation;

namespace LiberaFx.Tests;

internal static class Fluid2DAuthoringTests
{
    public static void Run()
    {
        ResourcePlanAuthoringTests.Run();
        VerifyGeneratedBindings();
        VerifyModelsAndDrawBindings();
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var node = GpuParticleEditorService.Create(graph);
        var before = graph.CreateSnapshot();
        using (var json = JsonDocument.Parse(GpuProgramDeliveryService.CreateExecutablePayload(before)))
        {
            Require(json.RootElement.GetProperty("version").GetInt32() == 4 &&
                !json.RootElement.TryGetProperty("fluidPrograms", out _), "Particle-only payloads use the current manifest and omit absent fluid programs.");
        }
        var view = new GpuWorkspaceView();
        view.Open(graph, node.Id);
        var window = new Window { Width = 1440, Height = 840, Content = view };
        window.Show();
        try
        {
            Pump(window);
            Invoke(Find<Button>("FluidModeButton"));
            Pump(window);
            var vm = view.Workspace!;
            Require(vm.State.Fluid2D is not null && vm.ValidateSources() &&
                view.GetVisualDescendants().OfType<Border>().Count(card => card.Tag is Fluid2DPass) == 11,
                "The actual mode action must display the resource-first fixed solver Flow.");
            Require(Find<TextBlock>("CapacityLabel").Text == LocalizationService.Shared.Get("Fluid.CellCount"),
                "Fluid mode must label its disabled capacity as grid cells.");
            Find<TextBox>("FluidWidth").Text = "24";
            Invoke(Find<Button>("ApplyFluidWidth"));
            Pump(window);
            Find<TextBox>("FluidHeight").Text = "40";
            Invoke(Find<Button>("ApplyFluidHeight"));
            Pump(window);
            Require(vm.State.Fluid2D is { Width: 24, Height: 40 }, "Fluid settings controls must commit undoable edits.");
            Find<TextBox>("FluidVorticity").Text = "12.5";
            Invoke(Find<Button>("ApplyFluidVorticity"));
            Pump(window);
            Require(vm.State.Fluid2D!.Vorticity == 12.5f, "Vorticity control must commit an undoable fluid setting.");
            var authored = graph.CreateSnapshot();
            var historyId = graph.History.CurrentNodeId;
            Find<TextBox>("FluidSourceRadius").Text = "0";
            Invoke(Find<Button>("ApplyFluidSourceRadius"));
            Pump(window);
            Require(graph.History.CurrentNodeId == historyId &&
                GraphSnapshot.ContentEquals(authored, graph.CreateSnapshot()), "Invalid fluid settings must be atomic.");
            Require(graph.History.Undo() && vm.State.Fluid2D!.Vorticity == 0 &&
                graph.History.Redo() && vm.State.Fluid2D.Vorticity == 12.5f, "Fluid vorticity must support Undo/Redo.");
            vm.Edit(new() { Action = "fluid-output", AutomaticPlane = false });
            Pump(window);
            Require(Find<CheckBox>("FluidAutomaticPlane").IsChecked == false,
                "Output-plane edits must refresh the actual inspector.");
            Require(graph.History.Undo(), "Output-plane edit can undo.");
            Pump(window);
            Require(Find<CheckBox>("FluidAutomaticPlane").IsChecked is null,
                "Undo must refresh the actual output-plane checkbox to Auto.");
            Require(graph.History.Redo(), "Output-plane edit can redo.");
            Pump(window);
            Require(Find<CheckBox>("FluidAutomaticPlane").IsChecked == false,
                "Redo must refresh the actual output-plane checkbox to Off.");
            Require(graph.History.Undo(), "Restore automatic output for the legacy payload check.");
            Pump(window);
            using (var stream = new MemoryStream())
            {
                new ProjectFileService().SaveAsync(stream, graph).GetAwaiter().GetResult();
                stream.Position = 0;
                var loaded = ProjectDocumentReader.LoadAsync(stream).GetAwaiter().GetResult();
                Require(GraphSnapshot.ContentEquals(authored, loaded.Graph), "Fluid state must survive project/history storage.");
            }
            using (var payload = JsonDocument.Parse(GpuProgramDeliveryService.CreateExecutablePayload(authored)))
            {
                var root = payload.RootElement;
                Require(root.GetProperty("version").GetInt32() == 4 &&
                    root.GetProperty("programs").GetArrayLength() == 0 &&
                    root.GetProperty("fluidPrograms")[0].GetProperty("nodeId").GetGuid() == node.Id &&
                    root.GetProperty("fluidPrograms")[0].GetProperty("settings").GetProperty("vorticity").GetSingle() == 12.5f &&
                    root.GetProperty("fluidPrograms")[0].GetProperty("settings").GetProperty("height").GetInt32() == 40,
                    "Fluid mode must export current resource execution, never dormant particle kernels.");
            }
            var package = SpawnableGroupPackage.Compile(authored);
            Require(Encoding.UTF8.GetString(package.RootGpuPrograms).Contains("\"fluidPrograms\"", StringComparison.Ordinal),
                "The whole-effect package must carry the executable fluid scope.");
            Require(GpuParticleEditorService.Export(vm.State, "compute").Contains("#pragma kernel LibraFluidJacobi", StringComparison.Ordinal) &&
                Fluid2DCompiler.Parse(GpuParticleEditorService.Export(vm.State, "json")).Width == 24,
                "Source exports must describe the selected simulation, not the dormant particle graph.");
            Require(GpuParticleEditorService.ExportMetadata(vm.State, "compute") is Fluid2DExportMetadata
                { Kind: "Fluid2D", UniformBufferBytes: 512, RuntimeExecuted: false } &&
                ResourceAssignment.Targets(graph.GetNode(node.Id),
                    new EngineAsset("builtin:white", "White", "texture", "Core")).Count == 0,
                "Fluid source metadata and resource assignment must not expose dormant particle resources.");
            var provider = new ActiveGraphProvider(graph);
            using (var compiled = JsonDocument.Parse(Complete(GpuParticleMcpTools.Compile(provider, node.Id.ToString()))))
            {
                Require(compiled.RootElement.GetProperty("kind").GetString() == "Fluid2D" &&
                    !compiled.RootElement.GetProperty("runtimeExecuted").GetBoolean() &&
                    compiled.RootElement.GetProperty("metadata").GetProperty("resourcePlan").GetProperty("ownedBytes").GetUInt64() == 24 * 40 * 144 &&
                    compiled.RootElement.GetProperty("metadata").GetProperty("resources").GetArrayLength() == 10,
                    "MCP must return the actual fluid resource plan instead of particle register metadata.");
            }
            using (var source = JsonDocument.Parse(Complete(GpuParticleMcpTools.Export(provider, node.Id.ToString(), "compute"))))
            {
                Require(source.RootElement.GetProperty("source").GetString()!.Contains("LibraFluidJacobi", StringComparison.Ordinal),
                    "MCP fluid shader export must preserve the selected mode.");
            }
            using (var bundle = JsonDocument.Parse(Complete(GpuParticleMcpTools.ExportBundle(provider, node.Id.ToString()))))
            {
                Require(bundle.RootElement.GetProperty("version").GetInt32() == 4 &&
                    bundle.RootElement.GetProperty("fluidPrograms").GetArrayLength() == 1,
                    "MCP must use the current fluid execution manifest.");
            }
            try
            {
                VfxProgram.Compile(authored);
                throw new InvalidOperationException("Raw CPU compilation silently discarded the fluid resource execution.");
            }
            catch (InvalidDataException)
            {
            }
            Invoke(Find<Button>("FluidModeButton"));
            Pump(window);
            Require(vm.State.Fluid2D is null &&
                GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()), "Returning to particles must retain the original graph.");
            Require(graph.History.Undo() && vm.State.Fluid2D!.Height == 40, "Undo must restore the complete fluid mode.");
        }
        finally
        {
            view.CloseWorkspace();
            window.Close();
        }
        var reopened = new GpuWorkspaceView();
        reopened.Open(graph, node.Id);
        var reopenedWindow = new Window { Width = 1440, Height = 840, Content = reopened };
        var text = LocalizationService.Shared;
        var language = text.SelectedLanguage;
        var reopenedHistory = graph.History.CurrentNodeId;
        reopenedWindow.Show();
        try
        {
            foreach (var id in new[] { "en", "ja" })
            {
                text.SelectedLanguage = text.Languages.Single(item => item.Id == id);
                Pump(reopenedWindow);
                Require(reopened.FindControl<TextBlock>("CapacityLabel")!.Text == text.Get("Fluid.CellCount") &&
                    reopened.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "FluidWidth").Text == "24" &&
                    graph.History.CurrentNodeId == reopenedHistory,
                    "Opening saved fluid mode and changing language must preserve its grid caption and authored settings.");
            }
        }
        finally
        {
            reopened.CloseWorkspace();
            reopenedWindow.Close();
            text.SelectedLanguage = language;
        }

        T Find<T>(string name) where T : Control =>
            view.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    }

    public static void WriteFixtures(string directory)
    {
        RequireNewDirectory(directory);
        foreach (var mode in new[] { "fluid-only", "mixed", "nested", "odd-grid", "draw-matrix", "owner-matrix", "vorticity", "generated-output",
            "free-surface", "free-surface-source", "shallow-water", "shallow-water-source", "combustion", "cold-fuel", "combustion-oracle", "force-acceleration", "force-noise", "force-curl", "force-masked", "force-masked-empty",
            "generated-sprite", "generated-flexible", "generated-material", "generated-material-dye", "generated-material-fields", "generated-fields", "generated-mask" })
        {
            var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
            GraphNodeViewModel? owner = null;
            var scope = graph;
            if (mode == "nested")
            {
                owner = graph.CreateNode(GraphNodeKind.SpawnableGroup);
                var start = graph.CreateNode(GraphNodeDefinition.Core.Single(item => item.TypeName == "OnStart").Kind);
                Require(graph.TryConnectNodes(start, owner, out var error, targetPortId: "spawn", sourcePortId: "then"),
                    error ?? "Cannot connect the nested fluid lifetime fixture.");
                scope = graph.OpenInlineGraph(owner);
                var lifetime = scope.CreateNode(new("LifeTime"));
                scope.SetParameter(lifetime, "seconds", JsonSerializer.SerializeToElement(2d));
            }
            var fluid = GpuParticleEditorService.Create(scope);
            scope.RenameNode(fluid, "2D Smoke");
            GpuParticleEditorService.Apply(scope, fluid.Id, [new()
            {
                Action = "fluid-enable",
                Fluid2D = new()
                {
                    Width = mode == "odd-grid" ? 23u : 24u, Height = mode == "odd-grid" ? 37u : 40u,
                    PressureIterations = 39, ColorG = 0.25f, ColorB = 0.1f,
                    Vorticity = mode == "vorticity" ? 12.5f : 0
                }
            }], requireValid: true);
            var isFreeSurface = mode.StartsWith("free-surface", StringComparison.Ordinal);
            var isShallowWater = mode.StartsWith("shallow-water", StringComparison.Ordinal);
            if (isFreeSurface || isShallowWater)
            {
                GpuParticleEditorService.Apply(scope, fluid.Id, [new()
                {
                    Action = "fluid-settings", Fluid2D = GpuParticleEditorService.ReadState(fluid).Fluid2D! with
                    {
                        Model = isFreeSurface ? Fluid2DModel.FreeSurface : Fluid2DModel.ShallowWater,
                        DomainWidth = isShallowWater ? 10 : 1, DomainHeight = isShallowWater ? 10 : 1,
                        PressureIterations = 128, MaximumSubsteps = 32,
                        SourceEnabled = mode.EndsWith("-source", StringComparison.Ordinal),
                        SourceX = .5f, SourceY = isShallowWater ? .5f : .75f,
                        SourceRadius = isShallowWater ? 1 : .08f, SourceDensity = isShallowWater ? .2f : 2,
                        SourceVelocityX = 0, SourceVelocityY = 0, Buoyancy = 0,
                        InitialWaterDepth = isShallowWater ? .25f : .35f,
                        Gravity = 9.81f, MaximumWaterSpeed = 4,
                        VelocityDissipation = 0, DensityDissipation = 0,
                        ColorR = .1f, ColorG = .4f, ColorB = 1
                    }
                }], requireValid: true);
            }
            if (mode is "combustion" or "cold-fuel" or "combustion-oracle")
            {
                GpuParticleEditorService.Apply(scope, fluid.Id, [new()
                {
                    Action = "fluid-settings", Fluid2D = GpuParticleEditorService.ReadState(fluid).Fluid2D! with
                    {
                        Model = Fluid2DModel.Combustion, SourceTemperature = mode == "cold-fuel" ? 0 : 2,
                        FuelBurnRate = 4, IgnitionTemperature = .5f, HeatYield = 2,
                        SourceRadius = .12f, SourceVelocityY = mode == "combustion-oracle" ? 0 : .5f,
                        Buoyancy = mode == "combustion-oracle" ? 0 : .25f,
                        DensityDissipation = mode == "combustion-oracle" ? 0 : .1f,
                        CoolingRate = mode == "combustion-oracle" ? 0 : .25f
                    }
                }], requireValid: true);
            }
            if (mode.StartsWith("force-", StringComparison.Ordinal))
            {
                var kind = mode == "force-acceleration" ? Fluid2DForceKind.Acceleration
                    : mode == "force-noise" ? Fluid2DForceKind.Noise : Fluid2DForceKind.CurlNoise;
                GpuParticleEditorService.Apply(scope, fluid.Id, [new()
                {
                    Action = "fluid-force-add", FluidForce = new()
                    {
                        Id = Guid.Parse("031f52eb-c8be-4afd-b76f-564f74a74f12"), Kind = kind,
                        X = .4f, Y = .2f, Strength = 2, Frequency = 3, Seed = 42, Octaves = 3,
                        Roughness = .5f, AnimationSpeed = .2f
                    }
                }], requireValid: true);
            }
            if (mode is "force-masked" or "force-masked-empty")
            {
                var position = Guid.Parse("41a5fe78-b54a-4cd7-aadf-7fbb753b7f20");
                var time = Guid.Parse("1fcf4e37-2ee8-481a-8a2d-18c5f286e2b8");
                var noise = Guid.Parse("c125c6b8-4428-4f2d-9d4f-38a6cf5bdc40");
                var density = Guid.Parse("57b99dd1-366d-45b6-b410-f818f93f45dd");
                var multiply = Guid.Parse("cd74abed-63d7-457e-a92d-aa5d30b31d7e");
                GpuParticleEditorService.Apply(scope, fluid.Id, [new()
                {
                    Action = "fluid-settings", Fluid2D = GpuParticleEditorService.ReadState(fluid).Fluid2D! with
                    {
                        SourceEnabled = mode == "force-masked", SourceVelocityY = 0, Buoyancy = 0,
                        Forces = [new() { Id = position, Kind = Fluid2DForceKind.CellPosition },
                            new() { Id = time, Kind = Fluid2DForceKind.Time },
                            new() { Id = noise, Kind = Fluid2DForceKind.CurlNoise, InputA = position, InputB = time, Frequency = 3, AnimationSpeed = .2f },
                            new() { Id = density, Kind = Fluid2DForceKind.Density },
                            new() { Id = multiply, Kind = Fluid2DForceKind.Multiply, InputA = noise, InputB = density }]
                    }
                }], requireValid: true);
            }
            if (mode is "generated-sprite" or "generated-flexible")
            {
                var draw = scope.CreateNode(new(mode == "generated-sprite" ? "SpriteDraw" : "FlexiblePlaneDraw"));
                scope.SetParameter(draw, "assetPath", JsonSerializer.SerializeToElement("/Game/LibraTests/UnusedTexture.UnusedTexture"));
                if (mode == "generated-sprite")
                {
                    scope.SetParameter(draw, "scale", JsonSerializer.SerializeToElement(new[] { 100d, 100d, 100d }));
                    scope.SetParameter(draw, "billboard", JsonSerializer.SerializeToElement(false));
                }
                else
                {
                    var points = scope.CreateNode(new("FlexiblePlanePointList"));
                    scope.SetParameter(points, "pointData", JsonSerializer.SerializeToElement(
                        "[{\"position\":[-50,0,0],\"width\":100,\"color\":[1,1,1,1]},{\"position\":[50,0,0],\"width\":100,\"color\":[1,1,1,1]}]"));
                    Require(scope.TryConnectNodes(points, draw, out var error, targetPortId: "points"),
                        error ?? "Cannot connect the generated Flexible Plane point source.");
                }
                GpuParticleEditorService.Apply(scope, fluid.Id, [new()
                {
                    Action = "fluid-consumer-bind", GeneratedConsumer = new(draw.Id, "assetPath", "ColorOutput")
                }], requireValid: true);
            }
            if (mode.StartsWith("generated-material", StringComparison.Ordinal))
            {
                var draw = scope.CreateNode(new("ModelDraw"));
                scope.SetParameter(draw, "assetPath", JsonSerializer.SerializeToElement("/Game/LibraTests/GeneratedMesh.GeneratedMesh"));
                scope.SetParameter(draw, "scale", JsonSerializer.SerializeToElement(new[] { 1d, 1d, 1d }));
                var asset = new EngineAsset("/Game/LibraTests/GeneratedMesh.GeneratedMesh", "Generated test mesh", "model", "Unreal", Materials:
                    [new(0, "/Game/LibraTests/GeneratedMaterial.GeneratedMaterial", "Generated test material",
                        [new("FluidTexture", "texture", JsonSerializer.SerializeToElement(""))])]);
                RenderNodeAuthoring.AssignAsset(scope, draw, asset);
                var schema = RenderNodeSchema.Encode(asset);
                var parameter = RenderNodeSchema.Describe(schema).Single().Id;
                GpuParticleEditorService.Apply(scope, fluid.Id, [new()
                {
                    Action = "fluid-consumer-bind", GeneratedConsumer = new(draw.Id, parameter,
                        mode.EndsWith("-fields", StringComparison.Ordinal) ? "FieldOutput" : mode.EndsWith("-dye", StringComparison.Ordinal) ? "DyeOutput" : "ColorOutput")
                }], requireValid: true);
            }
            if (mode is "generated-fields" or "generated-mask")
                CreateGeneratedFieldConsumer(scope, fluid.Id, mode == "generated-mask");
            if (mode == "mixed")
            {
                GpuParticleEditorService.Create(scope, 420, 80);
            }
            if (mode == "generated-output") CreateGeneratedConsumer(scope, fluid.Id);
            if (mode is "draw-matrix" or "owner-matrix")
            {
                var matrix = scope.CreateNode(new(mode == "draw-matrix" ? "Transform" : "OwnerMatrix"));
                if (mode == "draw-matrix")
                {
                    scope.SetParameter(matrix, "position", JsonSerializer.SerializeToElement(new[] { 25d, 0, 0 }));
                }
                Require(scope.TryConnectNodes(matrix, fluid, out var error,
                    targetPortId: "drawMatrix", sourcePortId: "value"),
                    error ?? "Cannot connect the compiled fluid Draw Matrix fixture.");
            }
            var snapshot = graph.CreateSnapshot() with { AutoDestroyWhenIdle = false };
            if (owner is not null)
            {
                snapshot = snapshot with
                {
                    Nodes = snapshot.Nodes.Select(item => item.Id == owner.Id
                        ? item with { ChildGraph = item.ChildGraph! with { AutoDestroyWhenIdle = false } } : item).ToArray()
                };
            }
            File.WriteAllBytes(Path.Combine(directory, mode + ".lfxp"),
                SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(snapshot)));
            File.WriteAllBytes(Path.Combine(directory, mode + ".json"),
                GpuProgramDeliveryService.CreateExecutablePayload(scope.CreateCurrentSnapshot()));
        }
    }

    private static void VerifyModelsAndDrawBindings()
    {
        foreach (var model in Enum.GetValues<Fluid2DModel>())
        {
            var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
            var producer = GpuParticleEditorService.Create(graph);
            GpuParticleEditorService.Apply(graph, producer.Id, [new() { Action = "fluid-enable", Fluid2D = Fluid2DPresets.Create(model, 24, 40) }], requireValid: true);
            var force = new Fluid2DForceNode { Id = Guid.NewGuid(), Kind = Fluid2DForceKind.CurlNoise, Frequency = 3 };
            GpuParticleEditorService.Apply(graph, producer.Id, [new() { Action = "fluid-force-add", FluidForce = force }], requireValid: true);
            Require(GpuParticleEditorService.ReadState(producer).Fluid2D!.Forces!.Single() == force,
                "Cell forces must be part of the authored, undoable solver state.");
            Require(graph.History.Undo() && GpuParticleEditorService.ReadState(graph.GetNode(producer.Id)).Fluid2D!.Forces is null && graph.History.Redo(),
                "Force nodes must support Undo/Redo.");
            var before = graph.CreateSnapshot();
            var draw = graph.CreateNode(new("SpriteDraw"));
            graph.SetParameter(draw, "assetPath", JsonSerializer.SerializeToElement("/Game/LibraTests/UnusedTexture.UnusedTexture"));
            GpuParticleEditorService.Apply(graph, producer.Id, [new() { Action = "fluid-consumer-bind", GeneratedConsumer = new(draw.Id, "assetPath") }], requireValid: true);
            var snapshot = graph.CreateSnapshot();
            var package = SpawnableGroupPackage.Compile(snapshot);
            using var payload = JsonDocument.Parse(package.RootGpuPrograms);
            using var render = JsonDocument.Parse(package.RootRenderConfiguration);
            Require(payload.RootElement.GetProperty("version").GetInt32() == 4 &&
                payload.RootElement.GetProperty("generatedDrawBindings")[0].GetProperty("outputId").GetString() == "ColorOutput" &&
                !payload.RootElement.GetProperty("fluidPrograms")[0].GetProperty("automaticPlane").GetBoolean() &&
                render.RootElement.GetProperty("nodes").EnumerateArray().Single(node => node.GetProperty("nodeId").GetGuid() == draw.Id)
                    .GetProperty("assetPath").GetString() == "builtin:generated-texture",
                "Ordinary draws must carry explicit generated references and suppress the source renderer.");
            var replacementProducer = Guid.NewGuid();
            var replacementDraw = Guid.NewGuid();
            var source = snapshot.Nodes.Single(node => node.Id == producer.Id);
            var mapped = GpuParticleEditorService.RemapReferences(source, new Dictionary<Guid, Guid> { [producer.Id] = replacementProducer, [draw.Id] = replacementDraw });
            var mappedState = GpuParticleEditorService.ReadState(source with { Id = replacementProducer, Parameters = mapped });
            Require(mappedState.GeneratedConsumers!.Single().TargetNodeId == replacementDraw, "Cloning a producer and draw must remap the consumer identity.");
            var detached = GpuParticleEditorService.ReadState(source with
            {
                Id = replacementProducer, Parameters = GpuParticleEditorService.RemapReferences(source, new Dictionary<Guid, Guid> { [producer.Id] = replacementProducer })
            });
            Require(detached.GeneratedConsumers!.Count == 0, "Cloning a producer alone must not duplicate bindings on the original draw.");
            using var stream = new MemoryStream();
            new ProjectFileService().SaveAsync(stream, graph).GetAwaiter().GetResult();
            stream.Position = 0;
            Require(GraphSnapshot.ContentEquals(snapshot, ProjectDocumentReader.LoadAsync(stream).GetAwaiter().GetResult().Graph),
                "Models, force nodes and ordinary draw references must survive project save/load.");
        }
    }

    private static GraphNodeViewModel CreateGeneratedFieldConsumer(GraphWorkspaceViewModel graph, Guid producer, bool mask)
    {
        var consumer = GpuParticleEditorService.Create(graph, 420, 80);
        var resource = new ResourceDeclaration { Id = "generatedField", Kind = ResourceKind.FlowMap2D,
            FlowMap = new() { Encoding = mask ? FlowMapEncoding.FloatDensity : FlowMapEncoding.FloatVelocity,
                Coordinates = FlowMapCoordinates.GeneratedPlane } };
        GpuParticleEditorService.Apply(graph, consumer.Id, [
            new() { Action = "capacity", Capacity = 1 },
            new() { Action = "resource", Resource = resource, GeneratedResource = new(producer, mask ? "DensityOutput" : "VelocityOutput") },
            new() { Action = "text", KernelId = "spawn", Text =
                "@Position = float3(0,-30,0); @Velocity = float3(0,0,0); @Lifetime = 100; @Size = float2(.1,.1); @Color = float4(1,1,1,1);" },
            new() { Action = "text", KernelId = "update", Text = mask
                ? "@Alive = greaterthan(sampleFlowMap(\"generatedField\", @Position).x, .01);"
                : "@Velocity = sampleFlowMap(\"generatedField\", @Position);" }
        ], requireValid: true);
        return consumer;
    }

    private static GraphNodeViewModel CreateGeneratedConsumer(GraphWorkspaceViewModel graph, Guid producer)
    {
        var consumer = GpuParticleEditorService.Create(graph, 420, 80);
        GpuParticleEditorService.Apply(graph, consumer.Id, [
            new() { Action = "capacity", Capacity = 1 },
            new() { Action = "text", KernelId = "spawn", Text = "@Position = float3(0,0,0); @Velocity = float3(0,0,0); @Lifetime = 100; @Size = float2(1,1); @Color = float4(1,1,1,1);" },
            new() { Action = "text", KernelId = "update", Text = "@Position = @Position;" },
            new() { Action = "resource", Resource = new() { Id = "billboardTexture", Kind = ResourceKind.Texture2D },
                GeneratedResource = new(producer, "DyeOutput") }
        ], requireValid: true);
        return consumer;
    }

    private static void VerifyGeneratedBindings()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var producer = GpuParticleEditorService.Create(graph);
        GpuParticleEditorService.Apply(graph, producer.Id, [new() { Action = "fluid-enable" }], requireValid: true);
        var consumer = CreateGeneratedConsumer(graph, producer.Id);
        var snapshot = graph.CreateSnapshot();
        using (var payload = JsonDocument.Parse(GpuProgramDeliveryService.CreateExecutablePayload(snapshot)))
        {
            var root = payload.RootElement;
            Require(root.GetProperty("version").GetInt32() == 4 &&
                !root.GetProperty("fluidPrograms")[0].GetProperty("automaticPlane").GetBoolean() &&
                root.GetProperty("programs")[0].GetProperty("unreal").GetProperty("generatedResources")
                    .GetProperty("billboardTexture").GetProperty("producerNodeId").GetGuid() == producer.Id,
                "Generated outputs must preserve producer identity and suppress the automatic plane through current manifest fields.");
        }
        Require(graph.History.Undo() && graph.History.Redo() &&
            GraphSnapshot.ContentEquals(snapshot, graph.CreateSnapshot()), "Generated bindings must survive Undo/Redo.");
        using (var stream = new MemoryStream())
        {
            new ProjectFileService().SaveAsync(stream, graph).GetAwaiter().GetResult();
            stream.Position = 0;
            Require(GraphSnapshot.ContentEquals(snapshot, ProjectDocumentReader.LoadAsync(stream).GetAwaiter().GetResult().Graph),
                "Generated bindings must survive project persistence.");
        }
        foreach (var reference in new[] { new GeneratedGpuResourceReference(Guid.NewGuid(), "DyeOutput"), new(producer.Id, "MissingOutput") })
        {
            GpuParticleEditorService.Apply(graph, consumer.Id, [new() { Action = "resource",
                Resource = new() { Id = "billboardTexture", Kind = ResourceKind.Texture2D }, GeneratedResource = reference }], requireValid: true);
            Require(GpuProgramDeliveryService.Validate(graph.CreateSnapshot()).Count > 0,
                "Unknown outputs and producers outside the scope must be diagnosed.");
            try
            {
                GpuProgramDeliveryService.CreateExecutablePayload(graph.CreateSnapshot());
                throw new InvalidOperationException("Invalid generated resource was exported.");
            }
            catch (GpuValidationException) { }
        }
        GpuParticleEditorService.Apply(graph, consumer.Id, [new() { Action = "resource",
            Resource = new() { Id = "billboardTexture", Kind = ResourceKind.Texture2D }, AssetPath = "builtin:white" }], requireValid: true);
        Require(GpuParticleEditorService.ReadState(graph.GetNode(consumer.Id)).GeneratedResources?.Count == 0 &&
            GpuProgramDeliveryService.Validate(graph.CreateSnapshot()).Count == 0,
            "Assigning an engine asset must replace the generated binding atomically.");
    }

    public static void Capture(string directory)
    {
        RequireNewDirectory(directory);
        _ = new ThemeSettingsService();
        var text = LocalizationService.Shared;
        var language = text.SelectedLanguage;
        try
        {
            foreach (var id in new[] { "en", "ja" })
            foreach (var model in Enum.GetValues<Fluid2DModel>())
            {
                text.SelectedLanguage = text.Languages.Single(item => item.Id == id);
                var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
                var node = GpuParticleEditorService.Create(graph);
                graph.RenameNode(node, model.ToString());
                var settings = Fluid2DPresets.Create(model, 24, 40);
                var fixtures = Environment.GetEnvironmentVariable("LIBERAFX_FLUID_DOCUMENTATION_FIXTURES");
                if (!string.IsNullOrEmpty(fixtures))
                {
                    var name = model switch { Fluid2DModel.FreeSurface => "free-surface", Fluid2DModel.ShallowWater => "shallow-water-source", Fluid2DModel.Combustion => "combustion", _ => "fluid-only" };
                    using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixtures, name + ".json")));
                    settings = Fluid2DCompiler.Parse(fixture.RootElement.GetProperty("fluidPrograms")[0].GetProperty("settings").GetRawText());
                }
                GpuParticleEditorService.Apply(graph, node.Id, [new() { Action = "fluid-enable", Fluid2D = settings }], requireValid: true);
                var view = new GpuWorkspaceView();
                view.Open(graph, node.Id);
                var window = new Window { Width = 1440, Height = 1000, Content = view };
                window.Show();
                try
                {
                    Pump(window);
                    Require(view.Workspace!.ValidateSources(), "Documentation must capture a valid fluid Flow.");
                    Save("flow");
                    if (model == Fluid2DModel.Smoke)
                    {
                        var declarations = view.GetVisualDescendants().OfType<Expander>()
                            .Single(control => control.Name == "ResourcePlanEditor");
                        declarations.IsExpanded = true;
                        Pump(window);
                        Save("resources");
                        declarations.IsExpanded = false;
                        Pump(window);
                    }
                    var flow = view.FindControl<ScrollViewer>("FlowScroll")!;
                    flow.Offset = new Vector(0, flow.Extent.Height);
                    Pump(window);
                    Save("output");

                    void Save(string screen)
                    {
                        var frame = new DispatcherFrame();
                        DispatcherTimer.RunOnce(() => frame.Continue = false, TimeSpan.FromMilliseconds(100));
                        Dispatcher.UIThread.PushFrame(frame);
                        Pump(window);
                        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                        bitmap.Render(window);
                        bitmap.Save(Path.Combine(directory, "fluid-2d-" + (model == Fluid2DModel.Smoke ? "" : model.ToString().ToLowerInvariant() + "-") + screen + "-" + id + ".png"), PngBitmapEncoderOptions.Default);
                    }
                }
                finally
                {
                    view.CloseWorkspace();
                    window.Close();
                }
            }
        }
        finally
        {
            text.SelectedLanguage = language;
        }
    }

    private static void RequireNewDirectory(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || Directory.Exists(directory))
        {
            throw new ArgumentException("Supply an absolute, new isolated directory.");
        }
        Directory.CreateDirectory(directory);
    }
    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
    private static void Invoke(Button button) =>
        new ButtonAutomationPeer(button).GetProvider<IInvokeProvider>()!.Invoke();
    private static string Complete(Task<string> task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var registration = timeout.Token.Register(() => Dispatcher.UIThread.Post(() => frame.Continue = false));
            _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
            Require(task.IsCompleted, "Fluid MCP validation timed out.");
        }
        return task.GetAwaiter().GetResult();
    }
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
