using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Platform;
using Avalonia.LogicalTree;
using ModelContextProtocol;
using LibraGpu.Core;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;
using LiberaFx.Views;

namespace LiberaFx.Tests;

internal static class GpuParticleEditorTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void VerifyFlowVisualization()
    {
        var program = GpuSamples.CreateEmitter();
        var map = GpuFlowVisualization.Create(new() { Program = program });
        Check(map.SourceCompiled && map.Steps.Count == program.Flow.Count &&
            map.Steps.All(step => step.CompiledMetadata is not null),
            "Flow map uses the compiled source metadata while preserving authored flow steps");
        Check(map.Steps.Single(step => step.Flow.Id == "spawnStep").Inputs.Any(port =>
                port.Name == "emitterPosition" && port.Detail.Contains("external host uniform")) &&
            map.Transfers.Any(transfer => transfer.ProducerFlowId == "spawnStep" &&
                transfer.ConsumerFlowId == "updateStep" && transfer.Attribute == "Position" &&
                transfer.IsTypeCompatible && transfer.ProducerPrecedesConsumer),
            "Flow map distinguishes host uniforms and projects real attribute producer/consumer ports");
        Check(map.Transfers.All(transfer => transfer.ConsumerFlowId != "renderStep"),
            "Billboard rendering is not assigned invented particle-attribute consumers");

        var updateKernel = program.Kernels.Single(kernel => kernel.Id == "update");
        var disconnectedRead = new GpuNode
        {
            Id = "unusedSizeRead",
            Op = GpuOp.ReadAttribute,
            Type = GpuType.Float2,
            Attribute = "Size"
        };
        var withDisconnectedRead = program with
        {
            Kernels = program.Kernels.Select(kernel => kernel.Id == updateKernel.Id
                ? kernel with { Nodes = [.. kernel.Nodes, disconnectedRead] }
                : kernel).ToList()
        };
        var disconnectedMap = GpuFlowVisualization.Create(new() { Program = withDisconnectedRead });
        Check(disconnectedMap.Steps.Single(step => step.Flow.Id == "updateStep").Inputs.Any(port =>
                port.Name == "Size" && !port.IsReachableFromWrite) &&
            disconnectedMap.Transfers.All(transfer => transfer.Attribute != "Size"),
            "Unconnected reads are inspectable but do not fabricate cross-flow transfers");

        var reversed = GpuFlowVisualization.Create(new()
        {
            Program = program with { Flow = [program.Flow[1], program.Flow[0], program.Flow[2]] }
        });
        Check(!reversed.SourceCompiled &&
            reversed.Transfers.Any(transfer => transfer.Attribute == "Position" &&
                transfer.ProducerFlowId == "spawnStep" && transfer.ConsumerFlowId == "updateStep" &&
                !transfer.ProducerPrecedesConsumer),
            "Invalid authored order retains the real dependency and flags its direction rather than inventing execution");

        var unsupported = GpuFlowVisualization.Create(new()
        {
            Program = program with
            {
                Flow = program.Flow.Select(flow => flow.Id == "renderStep"
                    ? flow with { Kind = FlowKind.ComputePass }
                    : flow).ToList()
            }
        });
        var unsupportedStep = unsupported.Steps.Single(step => step.Flow.Id == "renderStep");
        Check(!unsupported.SourceCompiled && unsupportedStep.UnsupportedReasons.Count > 0 &&
            unsupportedStep.Inputs.Any(port => port.Name == "billboardTexture"),
            "Unsupported flow relationships and their authored resource bindings remain inspectable");
    }
    private static void VerifyDefaultLayout(GraphNodeViewModel container)
    {
        var state = GpuParticleEditorService.ReadState(container);
        foreach (var kernel in state.Program.Kernels)
        {
            foreach (var node in kernel.Nodes)
            {
                var position = state.Positions[GpuParticleEditorService.Key(kernel.Id, node.Id)];
                foreach (var input in node.Inputs)
                    Check(state.Positions[GpuParticleEditorService.Key(kernel.Id, input)].X + 230 < position.X,
                        "Default GPU connections flow left to right with space for wires");
                foreach (var other in kernel.Nodes.Where(other => other.Id != node.Id))
                {
                    var otherPosition = state.Positions[GpuParticleEditorService.Key(kernel.Id, other.Id)];
                    Check(Math.Abs(position.X - otherPosition.X) >= 230 ||
                        position.Y + GpuGraphLayout.NodeHeight(node) <= otherPosition.Y ||
                        otherPosition.Y + GpuGraphLayout.NodeHeight(other) <= position.Y,
                        "Default GPU layout separates nodes including tall multi-input operations");
                }
            }
        }
        var history = new UndoRedoManager();
        var workspace = new GraphWorkspaceViewModel(history);
        var saved = GpuParticleEditorService.Create(workspace);
        using var vm = new GpuWorkspaceViewModel(workspace, saved.Id);
        var update = vm.State.Program.Kernels.Single(k => k.Stage == KernelStage.Update);
        vm.EnterKernel(update.Id);
        var first = update.Nodes[0];
        var key = GpuParticleEditorService.Key(update.Id, first.Id);
        workspace.SetParameter(saved, GpuParticleEditorService.ParameterId,
            JsonSerializer.SerializeToElement(vm.State with { Positions = [] }, GpuParticleEditorService.Json));
        Check(vm.GetPosition(first) == GpuGraphLayout.CreatePositions(update)[key],
            "Legacy GPU state without saved coordinates uses dependency layout");
        vm.Edit(new() { Action = "move", KernelId = update.Id, NodeId = first.Id, Position = new(987, 654) });
        var before = JsonSerializer.Serialize(vm.State.Positions, GpuParticleEditorService.Json);
        using (var reopened = new GpuWorkspaceViewModel(workspace, saved.Id))
        {
            reopened.EnterKernel(update.Id);
            Check(reopened.GetPosition(first) == new GpuPosition(987, 654),
                "Opening a GPU workspace preserves manually saved coordinates");
        }
        var program = GpuCompiler.Serialize(vm.State.Program);
        vm.ArrangeNodes();
        var arranged = GpuGraphLayout.CreatePositions(update);
        Check(update.Nodes.All(n => vm.GetPosition(n) == arranged[GpuParticleEditorService.Key(update.Id, n.Id)]) &&
            GpuCompiler.Serialize(vm.State.Program) == program, "Explicit arrangement changes layout only");
        history.Undo();
        Check(JsonSerializer.Serialize(vm.State.Positions, GpuParticleEditorService.Json) == before,
            "One undo restores the entire prior GPU arrangement");
        history.Redo();
        Check(update.Nodes.All(n => vm.GetPosition(n) == arranged[GpuParticleEditorService.Key(update.Id, n.Id)]),
            "Redo restores the arranged GPU graph");
        var draft = new KernelGraph { Id = "draft", Nodes =
        [
            new() { Id = "a", Op = GpuOp.Add, Inputs = ["b", "missing"] },
            new() { Id = "b", Op = GpuOp.Add, Inputs = ["a"] },
            new() { Id = "unused", Op = GpuOp.Constant }
        ] };
        var draftBefore = JsonSerializer.Serialize(draft);
        Check(GpuGraphLayout.CreatePositions(draft).Count == 3 && JsonSerializer.Serialize(draft) == draftBefore,
            "Cyclic, disconnected and missing-input drafts remain editable without IR mutation");
    }
    public static void Run()
    {
        VerifyFlowVisualization();
        var history = new UndoRedoManager();
        var graph = new GraphWorkspaceViewModel(history);
        var container = GpuParticleEditorService.Create(graph);
        VerifyDefaultLayout(container);
        Check(container.Parameters?.Any(p => p.Id == "gpuState") == true, "Creation persists actual IR, not transient registry/default");
        var other = GpuParticleEditorService.Create(graph, 400, 80);
        var notified = new HashSet<Guid>();
        graph.GpuProgramChanged += (_, id) => notified.Add(id);
        var otherBefore = JsonSerializer.Serialize(GpuParticleEditorService.ReadState(other), GpuParticleEditorService.Json);
        using var vm = new GpuWorkspaceViewModel(graph, container.Id);
        Check(vm.Kernel is null && vm.State.Program.Flow.Count == 3, "GPU flow starter");
        var render = vm.State.Program.Flow.Single(f => f.Kind == FlowKind.RenderBillboard);
        Check(vm.State.ResourceAssetPaths[render.Resource!] == "builtin:white" &&
            vm.State.RendererSettings[render.Id].SizeScale == 100, "New GPU starter explicitly binds a visible builtin billboard");
        var spawn = vm.State.Program.Flow.Single(flow => flow.Kind == FlowKind.Spawn);
        Check(spawn.SpawnShape?.Kind == GpuSpawnShapeKind.Point,
            "New GPU Spawn explicitly exposes Point independently of the billboard renderer");
        var selectedShape = new GpuSpawnShapeSettings
        {
            Kind = GpuSpawnShapeKind.Sphere,
            Radius = 2.5
        };
        vm.Edit(new() { Action = "flow-update", Flow = spawn with { SpawnShape = selectedShape } });
        Check(vm.TryCompile(out var shapedProgram) && shapedProgram!.RegisterKernels
                .Single(kernel => kernel.Stage == KernelStage.Spawn).Instructions
                .Any(instruction => instruction.Opcode == GpuInstructionOpcode.Random) &&
            shapedProgram.EmitUnityCompute().Text.Contains("LibraGpuRandom(") &&
            shapedProgram.EmitUsf().Text.Contains("LibraGpuRandom(") &&
            shapedProgram.EmitHlslInclude().Text.Contains("LibraGpuRandom("),
            "Authored GPU shape compiles to existing Random/math operations in every exported shader format");
        var shapeJson = GpuParticleEditorService.Export(vm.State, "json");
        var shapeShaders = new[] { "usf", "hlsl", "compute" }
            .Select(format => GpuParticleEditorService.Export(vm.State, format)).ToArray();
        Check(GpuCompiler.Parse(shapeJson).Flow.Single(flow => flow.Kind == FlowKind.Spawn)
                .SpawnShape is { Kind: GpuSpawnShapeKind.Sphere, Radius: 2.5 } &&
            shapeShaders.All(source => source.Contains("LibraGpuRandom(") && source.Contains("LibraGpuPow(")),
            "GPU workspace preserves authored shape in JSON and lowers it through every source export");
        var authoredShapeSnapshot = graph.CreateSnapshot();
        var loadedShapeGraph = new GraphWorkspaceViewModel(new UndoRedoManager());
        loadedShapeGraph.InitializeFromSnapshot(JsonSerializer.Deserialize<GraphSnapshot>(
            JsonSerializer.Serialize(authoredShapeSnapshot))!);
        Check(GpuParticleEditorService.ReadProgram(loadedShapeGraph.GetNode(container.Id)).Flow
                .Single(flow => flow.Kind == FlowKind.Spawn).SpawnShape is
                { Kind: GpuSpawnShapeKind.Sphere, Radius: 2.5, HalfExtents: [1, 1, 1] },
            "GPU Spawn shape survives graph snapshot save/load");
        history.Undo();
        Check(vm.State.Program.Flow.Single(flow => flow.Kind == FlowKind.Spawn).SpawnShape?.Kind == GpuSpawnShapeKind.Point,
            "Undo restores the prior GPU Spawn shape");
        history.Redo();
        Check(vm.State.Program.Flow.Single(flow => flow.Kind == FlowKind.Spawn).SpawnShape is
                { Kind: GpuSpawnShapeKind.Sphere, Radius: 2.5, HalfExtents: [1, 1, 1] },
            "Redo restores the authored GPU Spawn shape and parameters");
        history.Undo();
        vm.Edit(new() { Action = "renderer", NodeId = render.Id, Material = "builtin:unlit-additive", SizeScale = 120 });
        var rendererDelivery = GpuProgramDeliveryService.Create(new([graph.CreateSnapshot().Nodes.Single(n => n.Id == container.Id)], []), 76).Single();
        Check(rendererDelivery.RendererBindingsVersion == 1 &&
            rendererDelivery.RendererBindings.Single() is { Renderer: "builtin:billboard", Material: "builtin:unlit-additive", SizeScale: 120 },
            "Authored GPU material and draw size reach live/export renderer bindings");
        history.Undo();
        Check(vm.State.RendererSettings[render.Id] == new GpuRendererSettings(SizeScale: 100),
            "GPU renderer configuration is undoable");
        GpuParticleEditorService.AssignBillboardTexture(graph, graph.GetNode(container.Id),
            new EngineAsset("/Game/Textures/T_Test.T_Test", "T_Test", "texture", "Unreal"));
        Check(vm.State.ResourceAssetPaths[render.Resource!] == "/Game/Textures/T_Test.T_Test",
            "Content texture assignment updates GPU billboard binding");
        history.Undo();
        Check(vm.State.ResourceAssetPaths[render.Resource!] == "builtin:white", "Undo restores the prior GPU texture");
        vm.Edit(new() { Action = "flow-remove", NodeId = render.Id });
        vm.AddFlow(FlowKind.RenderBillboard);
        Check(vm.TryCompile(out _), "Removed billboard can be re-added with valid texture binding");
        var kernel = vm.State.Program.Kernels.Single(k => k.Stage == KernelStage.Update);
        vm.EnterKernel(kernel.Id);
        var value = vm.Add(GpuOp.Constant, GpuType.Float);
        var math = vm.Add(GpuOp.Add, GpuType.Float);
        vm.Edit(
            new() { Action = "update", KernelId = kernel.Id, Node = value with { Value = [17] } },
            new() { Action = "connect", KernelId = kernel.Id, NodeId = math.Id, Input = 0, SourceId = value.Id },
            new() { Action = "connect", KernelId = kernel.Id, NodeId = math.Id, Input = 1, SourceId = value.Id },
            new() { Action = "move", KernelId = kernel.Id, NodeId = math.Id, Position = new(450, 330) },
            new() { Action = "write", KernelId = kernel.Id, Write = new("test_age", "Age", math.Id) });
        Check(vm.TryCompile(out var compiled) && compiled!.EmitUsf().Text.Contains("17.0"), "Graph edit reflected in source");
        Check(notified.SetEquals([container.Id]), "Program change events isolate containers");
        Check(!compiled!.RuntimeExecuted && compiled.Purpose == CompilationPurpose.SourceExport, "Offline source-only status");
        var delivery = GpuProgramDeliveryService.Create(new GraphSnapshot([graph.CreateSnapshot().Nodes.Single(n => n.Id == container.Id)], []), 77).Single();
        Check(delivery.NodeId == container.Id && delivery.ProgramSequence == 77 &&
            delivery.Metadata.ContentHash == compiled.ContentHash &&
            delivery.Metadata.SourceHash == compiled.EmitUsf().ContentHash && delivery.RegisterKernels.Count == 2 &&
            delivery.ShaderSource.Contains("17.0"), "Backend delivery matches edited source, IR/hash and sequence");
        Check(vm.Palette.All(p => Enum.IsDefined(p.Op)) && !GpuParticleMcpTools.Describe().Contains("\"Signal\""), "GPU-only palette");
        var emitted = GpuDsl.Emit(vm.Kernel!);
        vm.Edit(new() { Action = "text", KernelId = kernel.Id, Text = emitted.Text });
        Check(vm.Kernel!.Nodes.Any(n => n.Id == math.Id), "Generated text preserves mapped IR IDs");
        var beforeBad = GpuCompiler.Serialize(vm.State.Program);
        vm.Edit(new() { Action = "text", KernelId = kernel.Id, Text = "@Position = float3(;" });
        Check(GpuCompiler.Serialize(vm.State.Program) == beforeBad && vm.State.Drafts.ContainsKey(kernel.Id), "Invalid text retains original IR");
        Check(!vm.TryCompile(out _) && vm.Diagnostics.Any(d => d.Span is not null), "Text span diagnostics/export blocked");
        var snapshot = graph.CreateSnapshot();
        GraphValidator.Validate(snapshot);
        var serialized = JsonSerializer.Serialize(snapshot);
        var loaded = new GraphWorkspaceViewModel(new UndoRedoManager());
        loaded.InitializeFromSnapshot(JsonSerializer.Deserialize<GraphSnapshot>(serialized)!);
        var loadedState = GpuParticleEditorService.ReadState(loaded.GetNode(container.Id));
        Check(loadedState.Drafts[kernel.Id].Text == "@Position = float3(;" &&
            loadedState.Positions[GpuParticleEditorService.Key(kernel.Id, math.Id)] == new GpuPosition(450, 330), "Save/load IDs, layout, invalid text");
        history.Undo();
        Check(!GpuParticleEditorService.ReadState(graph.GetNode(container.Id)).Drafts.ContainsKey(kernel.Id), "Undo restores text and IR");
        history.Redo();
        Check(GpuParticleEditorService.ReadState(graph.GetNode(container.Id)).Drafts.ContainsKey(kernel.Id), "Redo restores invalid text");
        var atomicBefore = graph.CreateSnapshot();
        try
        {
            GpuParticleEditorService.Apply(graph, container.Id,
                [new() { Action = "capacity", Capacity = 8000 }, new() { Action = "unknown" }]);
            throw new Exception("Bad plan accepted");
        }
        catch (ArgumentException) { }
        Check(GraphSnapshot.ContentEquals(atomicBefore, graph.CreateSnapshot()), "MCP atomic plan rollback");
        try
        {
            GpuParticleEditorService.Apply(graph, container.Id, [new() { Action = "capacity", Capacity = 8000 }], requireValid: true);
            throw new Exception("Invalid text plan accepted with requireValid");
        }
        catch (GpuValidationException) { }
        Check(GraphSnapshot.ContentEquals(atomicBefore, graph.CreateSnapshot()), "Strict MCP plan rolls back on diagnostic");
        try
        {
            GpuParticleEditorService.Apply(graph, container.Id, [new() { Action = "capacity", Capacity = uint.MaxValue }]);
            throw new Exception("Capacity limit accepted");
        }
        catch (ArgumentException) { }
        Check(GraphSnapshot.ContentEquals(atomicBefore, graph.CreateSnapshot()), "Capacity limit rollback");
        vm.Edit(new() { Action = "discard-draft", KernelId = kernel.Id });
        vm.Edit(new() { Action = "text", KernelId = kernel.Id, Text = "@Age = param(\"time\") + 3;" });
        Check(vm.TryCompile(out _) && vm.Kernel!.Writes.Single().Attribute == "Age", "Text lowers to same typed IR");
        var invalid = vm.Kernel!.Nodes.First(n => n.Op == GpuOp.Add);
        vm.Edit(new() { Action = "update", KernelId = kernel.Id, Node = invalid with { Type = GpuType.Bool } });
        Check(graph.TypeProblems.Any(p => p.Node.Id == container.Id && p.Message.Contains(invalid.Id)), "Node-specific GPU diagnostics in Problems");
        Check(otherBefore == JsonSerializer.Serialize(GpuParticleEditorService.ReadState(graph.GetNode(other.Id)), GpuParticleEditorService.Json), "Container isolation");
        Check(VfxProgram.Compile(graph.CreateSnapshot()).Instructions.Count == 0, "CPU ignores GPU containers, validates independently");
        var futureGraph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var futureNode = GpuParticleEditorService.Create(futureGraph);
        var futureState = GpuParticleEditorService.Apply(futureGraph, futureNode.Id,
            [new() { Action = "resource", Resource = new() { Id = "future_tex", Kind = ResourceKind.StorageTexture2D, Width = 16, Height = 16 } }]);
        Check(GpuParticleEditorService.Diagnostics(futureState).Any(d => d.Message.Contains("resource", StringComparison.OrdinalIgnoreCase) ||
            d.Message.Contains("texture", StringComparison.OrdinalIgnoreCase)), "Unsupported storage texture fails independently of other graph errors");
        var request = Guid.Empty;
        graph.GpuEditorRequested += (_, id) => request = id;
        graph.OpenGpuEditor(graph.GetNode(container.Id));
        Check(request == container.Id, "GPU entry routing");
        var view = new GpuWorkspaceView();
        view.Open(graph, other.Id);
        Check(view.Workspace!.Kernel is null && view.IsVisible, "Actual flow view opens");
        var flowLabels = view.FindControl<StackPanel>("FlowPanel")!.GetLogicalDescendants()
            .OfType<TextBlock>().Select(block => block.Text ?? "").ToArray();
        var emitterShapePicker = view.FindControl<StackPanel>("FlowPanel")!.GetLogicalDescendants()
            .OfType<ComboBox>().Single(combo =>
                Avalonia.Automation.AutomationProperties.GetName(combo) ==
                LocalizationService.Shared.Get("Gpu.EmitterShape"));
        Check(new[] { "spawnStep", "updateStep", "renderStep" }.All(id =>
                flowLabels.Any(text => text.Contains(id, StringComparison.Ordinal))),
            "The actual flow view identifies every authored step");
        Check(emitterShapePicker.SelectedItem is ComboBoxItem { Content: "Point" } &&
            emitterShapePicker.Items.OfType<ComboBoxItem>().Count() == 4,
            "The Spawn card presents all initial-emission shapes, separately from RenderBillboard");
        Check(flowLabels.Any(text => text.Contains("spawnStep", StringComparison.Ordinal) &&
                text.Contains("updateStep", StringComparison.Ordinal) &&
                text.Contains("Position", StringComparison.Ordinal) &&
                text.Contains("not observed execution", StringComparison.Ordinal)) &&
            flowLabels.Any(text => text.Contains("emitterPosition", StringComparison.Ordinal) &&
                text.Contains("external host uniform", StringComparison.Ordinal)),
            "The actual flow view distinguishes static cross-flow dependencies from external uniforms and runtime traces");
        view.Workspace.EnterKernel(view.Workspace.State.Program.Kernels.Single(k => k.Stage == KernelStage.Update).Id);
        Check(view.FindControl<Avalonia.Controls.Canvas>("NodeCanvas")!.Children.Count > 0, "Actual visual graph nodes");
        var nodeLabels = view.FindControl<Avalonia.Controls.Canvas>("NodeCanvas")!.GetLogicalDescendants()
            .OfType<TextBlock>().Select(block => block.Text ?? "").ToArray();
        Check(nodeLabels.Any(text => text.Contains("Position", StringComparison.Ordinal)) &&
            nodeLabels.Any(text => text.Contains("Color", StringComparison.Ordinal)) &&
            nodeLabels.Any(text => text.Contains("deltaTime", StringComparison.Ordinal)) &&
            nodeLabels.All(text => !text.Contains("in0", StringComparison.Ordinal)),
            "GPU graph labels expose particle attributes, uniforms and semantic math inputs");
        foreach (var (language, expected) in new[] { ("en", "Kernel Graph"), ("ja", "カーネルグラフ") })
        {
            using var strings = AssetLoader.Open(new Uri($"avares://LiberaFx/Assets/Translations/Gpu.{language}.json"));
            var labels = JsonSerializer.Deserialize<Dictionary<string, string>>(strings)!;
            Check(labels["Gpu.Visual"] == expected, "Kernel Graph mode name: " + language);
            Check(labels.ContainsKey("Gpu.EmitterShape") && labels.ContainsKey("Gpu.EmitterShape.Box") &&
                labels.ContainsKey("Gpu.EmitterShape.Sphere") && labels.ContainsKey("Gpu.EmitterShape.Circle") &&
                labels.ContainsKey("Gpu.EmitterShapeHelp") && labels.ContainsKey("Gpu.EmitterRadius"),
                "GPU Spawn shape controls have complete " + language + " translations");
        }
        view.CloseWorkspace();
        Check(!view.IsVisible && graph.Nodes.Any(n => n.Id == other.Id), "Closing mode keeps CPU document");
        var shapeUiGraph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var shapeUiContainer = GpuParticleEditorService.Create(shapeUiGraph);
        var shapeUiView = new GpuWorkspaceView();
        shapeUiView.Open(shapeUiGraph, shapeUiContainer.Id);
        ComboBox ShapePicker() => shapeUiView.FindControl<StackPanel>("FlowPanel")!
            .GetLogicalDescendants().OfType<ComboBox>().Single(combo =>
                Avalonia.Automation.AutomationProperties.GetName(combo) ==
                LocalizationService.Shared.Get("Gpu.EmitterShape"));
        ShapePicker().SelectedItem = ShapePicker().Items.OfType<ComboBoxItem>()
            .Single(item => item.Tag is GpuSpawnShapeKind.Box);
        var boxInputs = shapeUiView.FindControl<StackPanel>("FlowPanel")!
            .GetLogicalDescendants().OfType<TextBox>().ToArray();
        Check(boxInputs.Length == 3, "Selecting Box reveals all three half-extents");
        boxInputs[0].Text = "2";
        boxInputs[1].Text = "3";
        boxInputs[2].Text = "4";
        var applyShape = shapeUiView.FindControl<StackPanel>("FlowPanel")!
            .GetLogicalDescendants().OfType<Button>().Single(button =>
                button.Content as string == LocalizationService.Shared.Get("Gpu.ApplyEmitterShape"));
        applyShape.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Check(GpuParticleEditorService.ReadProgram(shapeUiGraph.GetNode(shapeUiContainer.Id)).Flow
                .Single(flow => flow.Kind == FlowKind.Spawn).SpawnShape is
                { Kind: GpuSpawnShapeKind.Box, HalfExtents: [2, 3, 4] },
            "The Spawn card shape selector and apply control persist Box dimensions");
        shapeUiView.CloseWorkspace();
        var main = new MainViewModel();
        var window = new MainWindow { DataContext = main };
        var layout = main.Layout;
        var document = main.ActiveDocument;
        var activeGraph = main.Graph;
        var mainGpu = GpuParticleEditorService.Create(activeGraph);
        activeGraph.OpenGpuEditor(mainGpu);
        var overlay = window.FindControl<GpuWorkspaceView>("GpuWorkspace")!;
        Check(overlay.IsVisible && overlay.Workspace?.ContainerId == mainGpu.Id &&
            ReferenceEquals(main.Layout, layout) && ReferenceEquals(main.ActiveDocument, document), "MainWindow dedicated overlay preserves document/layout");
        overlay.CloseWorkspace();
        Check(ReferenceEquals(main.Graph, activeGraph) && ReferenceEquals(main.Layout, layout), "Back preserves CPU workspace");
        window.Close();
        VerifyMcp();
        GpuParticleDragTests.Run();
        var exportDirectory = Path.Combine(Environment.CurrentDirectory, "build", "gpu-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(exportDirectory);
        try
        {
            var destination = Path.Combine(exportDirectory, "emitter.usf");
            Complete(GpuParticleEditorService.WriteAtomicAsync(destination, delivery.ShaderSource));
            Check(File.ReadAllText(destination) == delivery.ShaderSource &&
                !File.ReadAllBytes(destination).Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }) &&
                Directory.GetFiles(exportDirectory).Length == 1, "Atomic UTF8 source export, no staging files");
        }
        finally { Directory.Delete(exportDirectory, recursive: true); }
        GraphTypeDiagnosticsTests.Run();
        Console.WriteLine("GPU workspace graph/text, persistence, undo, atomic plans, diagnostics and source-export checks passed.");
    }
    private static void VerifyMcp()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var provider = new ActiveGraphProvider(graph);
        using var created = JsonDocument.Parse(Complete(GpuParticleMcpTools.Create(provider)));
        var id = created.RootElement.GetProperty("nodeId").GetGuid();
        var kernel = GpuParticleEditorService.ReadProgram(graph.GetNode(id)).Kernels.Single(k => k.Stage == KernelStage.Update).Id;
        var before = graph.CreateSnapshot();
        var plan = JsonSerializer.Serialize(new GpuEdit[]
        {
            new() { Action = "capacity", Capacity = 6000 },
            new() { Action = "text", KernelId = kernel, Text = "@Age = 12;" }
        }, GpuParticleEditorService.Json);
        Complete(GpuParticleMcpTools.ApplyPlan(provider, id.ToString(), plan, requireValid: true));
        using var compiled = JsonDocument.Parse(Complete(GpuParticleMcpTools.Compile(provider, id.ToString())));
        Check(compiled.RootElement.GetProperty("success").GetBoolean() &&
            !compiled.RootElement.GetProperty("runtimeExecuted").GetBoolean() &&
            compiled.RootElement.GetProperty("source").ValueKind == JsonValueKind.Null, "Actual MCP compile returns simple source-only DTO, opt-in source");
        graph.History.Undo();
        Check(GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()), "Actual MCP plan is a single atomic undo step");
        try
        {
            Complete(GpuParticleMcpTools.ApplyPlan(provider, id.ToString(), """[{"action":"capacity","capacity":9000},{"action":"unknown"}]"""));
            throw new Exception("Invalid MCP plan accepted");
        }
        catch (McpException) { }
        Check(GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()), "Actual MCP failure leaves graph unchanged");
    }
    private static T Complete<T>(Task<T> task)
    {
        Wait(task);
        return task.GetAwaiter().GetResult();
    }
    private static void Complete(Task task) { Wait(task); task.GetAwaiter().GetResult(); }
    private static void Wait(Task task)
    {
        if (task.IsCompleted) return;
        var frame = new DispatcherFrame();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var registration = timeout.Token.Register(() => Dispatcher.UIThread.Post(() => frame.Continue = false));
        _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.UIThread.PushFrame(frame);
        if (!task.IsCompleted) throw new TimeoutException("GPU MCP validation timed out.");
    }
}
