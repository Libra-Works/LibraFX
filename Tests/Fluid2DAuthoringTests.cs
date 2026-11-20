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
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;
using LiberaFx.Views;

namespace LiberaFx.Tests;

internal static class Fluid2DAuthoringTests
{
    public static void Run()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var node = GpuParticleEditorService.Create(graph);
        var before = graph.CreateSnapshot();
        using (var json = JsonDocument.Parse(GpuProgramDeliveryService.CreateExecutablePayload(before)))
        {
            Require(json.RootElement.GetProperty("version").GetInt32() == 1 &&
                !json.RootElement.TryGetProperty("fluidPrograms", out _), "Particle-only payloads must retain version 1.");
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
                view.GetVisualDescendants().OfType<Border>().Count(card => card.Tag is Fluid2DPass) == 9,
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
            var authored = graph.CreateSnapshot();
            var historyId = graph.History.CurrentNodeId;
            Find<TextBox>("FluidSourceRadius").Text = "0";
            Invoke(Find<Button>("ApplyFluidSourceRadius"));
            Pump(window);
            Require(graph.History.CurrentNodeId == historyId &&
                GraphSnapshot.ContentEquals(authored, graph.CreateSnapshot()), "Invalid fluid settings must be atomic.");
            Require(graph.History.Undo() && vm.State.Fluid2D!.Height == 128 &&
                graph.History.Redo() && vm.State.Fluid2D.Height == 40, "Fluid settings must support Undo/Redo.");
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
                Require(root.GetProperty("version").GetInt32() == 2 &&
                    root.GetProperty("programs").GetArrayLength() == 0 &&
                    root.GetProperty("fluidPrograms")[0].GetProperty("nodeId").GetGuid() == node.Id &&
                    root.GetProperty("fluidPrograms")[0].GetProperty("settings").GetProperty("height").GetInt32() == 40,
                    "Fluid mode must export explicit versioned resource execution, never dormant particle kernels.");
            }
            var package = SpawnableGroupPackage.Compile(authored);
            Require(Encoding.UTF8.GetString(package.RootGpuPrograms).Contains("\"fluidPrograms\"", StringComparison.Ordinal),
                "The whole-effect package must carry the executable fluid scope.");
            Require(GpuParticleEditorService.Export(vm.State, "compute").Contains("#pragma kernel LibraFluidJacobi", StringComparison.Ordinal) &&
                Fluid2DCompiler.Parse(GpuParticleEditorService.Export(vm.State, "json")).Width == 24,
                "Source exports must describe the selected simulation, not the dormant particle graph.");
            Require(GpuParticleEditorService.ExportMetadata(vm.State, "compute") is Fluid2DExportMetadata
                { Kind: "Fluid2D", UniformBufferBytes: 80, RuntimeExecuted: false } &&
                ResourceAssignment.Targets(graph.GetNode(node.Id),
                    new EngineAsset("builtin:white", "White", "texture", "Core")).Count == 0,
                "Fluid source metadata and resource assignment must not expose dormant particle resources.");
            var provider = new ActiveGraphProvider(graph);
            using (var compiled = JsonDocument.Parse(Complete(GpuParticleMcpTools.Compile(provider, node.Id.ToString()))))
            {
                Require(compiled.RootElement.GetProperty("kind").GetString() == "Fluid2D" &&
                    !compiled.RootElement.GetProperty("runtimeExecuted").GetBoolean() &&
                    compiled.RootElement.GetProperty("metadata").GetProperty("resources").GetArrayLength() == 8,
                    "MCP must return the actual fluid resource plan instead of particle register metadata.");
            }
            using (var source = JsonDocument.Parse(Complete(GpuParticleMcpTools.Export(provider, node.Id.ToString(), "compute"))))
            {
                Require(source.RootElement.GetProperty("source").GetString()!.Contains("LibraFluidJacobi", StringComparison.Ordinal),
                    "MCP fluid shader export must preserve the selected mode.");
            }
            using (var bundle = JsonDocument.Parse(Complete(GpuParticleMcpTools.ExportBundle(provider, node.Id.ToString()))))
            {
                Require(bundle.RootElement.GetProperty("version").GetInt32() == 2 &&
                    bundle.RootElement.GetProperty("fluidPrograms").GetArrayLength() == 1,
                    "MCP must explicitly version its fluid execution bundle.");
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
        foreach (var mode in new[] { "fluid-only", "mixed", "nested", "odd-grid", "draw-matrix", "owner-matrix" })
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
                    PressureIterations = 39, ColorG = 0.25f, ColorB = 0.1f
                }
            }], requireValid: true);
            if (mode == "mixed")
            {
                GpuParticleEditorService.Create(scope, 420, 80);
            }
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
            File.WriteAllBytes(Path.Combine(directory, mode + ".lfxb"),
                SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(snapshot)));
            File.WriteAllBytes(Path.Combine(directory, mode + ".json"),
                GpuProgramDeliveryService.CreateExecutablePayload(scope.CreateCurrentSnapshot()));
        }
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
            {
                text.SelectedLanguage = text.Languages.Single(item => item.Id == id);
                var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
                var node = GpuParticleEditorService.Create(graph);
                graph.RenameNode(node, "2D Smoke");
                GpuParticleEditorService.Apply(graph, node.Id, [new() { Action = "fluid-enable" }], requireValid: true);
                var view = new GpuWorkspaceView();
                view.Open(graph, node.Id);
                var window = new Window { Width = 1440, Height = 1000, Content = view };
                window.Show();
                try
                {
                    Pump(window);
                    Require(view.Workspace!.ValidateSources(), "Documentation must capture a valid fluid Flow.");
                    Save("flow");
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
                        bitmap.Save(Path.Combine(directory, "fluid-2d-" + screen + "-" + id + ".png"), PngBitmapEncoderOptions.Default);
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
