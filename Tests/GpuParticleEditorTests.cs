using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
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
    public static void Run()
    {
        var history = new UndoRedoManager();
        var graph = new GraphWorkspaceViewModel(history);
        var container = GpuParticleEditorService.Create(graph);
        Check(container.Parameters?.Any(p => p.Id == "gpuState") == true, "Creation persists actual IR, not transient registry/default");
        var other = GpuParticleEditorService.Create(graph, 400, 80);
        var notified = new HashSet<Guid>();
        graph.GpuProgramChanged += (_, id) => notified.Add(id);
        var otherBefore = JsonSerializer.Serialize(GpuParticleEditorService.ReadState(other), GpuParticleEditorService.Json);
        using var vm = new GpuWorkspaceViewModel(graph, container.Id);
        Check(vm.Kernel is null && vm.State.Program.Flow.Count == 3, "GPU flow starter");
        var render = vm.State.Program.Flow.Single(f => f.Kind == FlowKind.RenderBillboard);
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
        view.Workspace.EnterKernel(view.Workspace.State.Program.Kernels[0].Id);
        Check(view.FindControl<Avalonia.Controls.Canvas>("NodeCanvas")!.Children.Count > 0, "Actual visual graph nodes");
        view.CloseWorkspace();
        Check(!view.IsVisible && graph.Nodes.Any(n => n.Id == other.Id), "Closing mode keeps CPU document");
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
