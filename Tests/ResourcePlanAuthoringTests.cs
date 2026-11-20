using System.Text.Json;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LibraGpu.Core;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using LiberaFx.Views;

namespace LiberaFx.Tests;

internal static class ResourcePlanAuthoringTests
{
    public static void Run()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var node = GpuParticleEditorService.Create(graph);
        GpuParticleEditorService.Apply(graph, node.Id, [new() { Action = "fluid-enable" }], requireValid: true);
        var original = graph.CreateSnapshot();
        var view = new GpuWorkspaceView();
        view.Open(graph, node.Id);
        var vm = view.Workspace!;
        var window = new Window { Width = 1440, Height = 1000, Content = view };
        window.Show();
        try
        {
            Pump();
            Find<Expander>("ResourcePlanEditor").IsExpanded = true;
            Pump();
            Invoke("ApplyResourcePlan");
            Require(GpuParticleEditorService.ResourcePlanRuntimeSupported(vm.State),
                "Applying an unchanged built-in fluid plan must preserve playback support.");
            Invoke("ResetResourcePlan");
            Require(GraphSnapshot.ContentEquals(original, graph.CreateSnapshot()),
                "Resetting the source plan must restore the original fluid state.");

            var plan = new GpuResourcePlan
            {
                Resources =
                [
                    new() { Id = "particles", Kind = GpuFlowResourceKind.ParticleBuffer, Format = GpuResourceFormat.Particle80,
                        Width = 4, Lifetime = GpuResourceLifetime.Persistent, Initialization = GpuResourceInitialization.Zero },
                    new() { Id = "scratch", Kind = GpuFlowResourceKind.StorageBuffer, Format = GpuResourceFormat.Float4,
                        Width = 8, Lifetime = GpuResourceLifetime.Temporary, Initialization = GpuResourceInitialization.Zero },
                    new() { Id = "color", Kind = GpuFlowResourceKind.Texture2D, Format = GpuResourceFormat.Rgba16Float,
                        Width = 8, Height = 8, Lifetime = GpuResourceLifetime.Persistent, Initialization = GpuResourceInitialization.Zero },
                    new() { Id = "volume", Kind = GpuFlowResourceKind.Texture3D, Format = GpuResourceFormat.R32Float,
                        Width = 8, Height = 4, Depth = 2, Lifetime = GpuResourceLifetime.Persistent, Initialization = GpuResourceInitialization.Zero },
                    new() { Id = "mainColor", Kind = GpuFlowResourceKind.RenderTarget, Format = GpuResourceFormat.Rgba16Float,
                        Width = 640, Height = 480, Lifetime = GpuResourceLifetime.External,
                        Initialization = GpuResourceInitialization.External, ExternalBinding = "host.main.color" }
                ],
                Passes =
                [
                    new() { Id = "compute", Operation = "CustomCompute", Uses = [new("result", "scratch", 1, GpuResourceAccess.StorageWrite)] },
                    new() { Id = "draw", Operation = "CustomDraw", Uses =
                        [new("data", "scratch", 1, GpuResourceAccess.StorageRead), new("image", "color", 0, GpuResourceAccess.Sample),
                            new("target", "mainColor", 1, GpuResourceAccess.RenderTarget)] }
                ]
            };
            Find<Expander>("ResourcePlanEditor").IsExpanded = true;
            Pump();
            Find<TextBox>("ResourcePlanSource").Text = GpuResourcePlanCompiler.Serialize(plan);
            Invoke("ApplyResourcePlan");
            var state = vm.State;
            Require(state.ResourcePlanDraft?.Resources.Count == 5 &&
                GpuResourcePlanCompiler.Compile(state.ResourcePlanDraft).OwnedBytes == 1216,
                "The actual editor must retain typed resource declarations and allocation accounting.");
            Require(GpuParticleEditorService.Diagnostics(state).Any(error => error.Code == "ResourcePlanRuntimeUnsupported"),
                "Unsupported general operations and external targets must not be presented as playable.");
            try
            {
                GpuProgramDeliveryService.CreateExecutablePayload(graph.CreateSnapshot());
                throw new InvalidOperationException("Unsupported resource operations were silently discarded.");
            }
            catch (GpuValidationException error)
            {
                Require(error.Diagnostics.Any(item => item.Code == "ResourcePlanRuntimeUnsupported"), "Playback must explain the unsupported plan.");
            }
            Require(GpuResourcePlanCompiler.Parse(GpuParticleEditorService.Export(state, "resources")).Resources.Count == 5,
                "A source plan remains exportable without a runtime backend.");
            using (var metadata = JsonDocument.Parse(JsonSerializer.Serialize(GpuParticleEditorService.ExportMetadata(state, "resources"))))
                Require(!metadata.RootElement.GetProperty("runtimeExecuted").GetBoolean() &&
                    !metadata.RootElement.GetProperty("runtimeSupported").GetBoolean(), "Source-plan metadata must distinguish compilation and execution.");
            var saved = graph.CreateSnapshot();
            using (var stream = new MemoryStream())
            {
                new ProjectFileService().SaveAsync(stream, graph).GetAwaiter().GetResult();
                stream.Position = 0;
                Require(GraphSnapshot.ContentEquals(saved, ProjectDocumentReader.LoadAsync(stream).GetAwaiter().GetResult().Graph),
                    "Resource declarations, external bindings and pass versions must survive project/history storage.");
            }
            Require(graph.History.Undo() && vm.State.ResourcePlanDraft is null && graph.History.Redo(),
                "Applying a resource plan must be one undoable edit.");
            Pump();
            Require(Find<TextBox>("ResourcePlanSource").Text!.Contains("host.main.color", StringComparison.Ordinal),
                "Redo must refresh the actual source editor.");
            var history = graph.History.CurrentNodeId;
            Find<TextBox>("ResourcePlanSource").Text = "{\"version\":1,\"resources\":null}";
            Invoke("ApplyResourcePlan");
            Require(graph.History.CurrentNodeId == history && GraphSnapshot.ContentEquals(saved, graph.CreateSnapshot()),
                "An invalid source edit must leave graph/history untouched.");
            var provider = new ActiveGraphProvider(graph);
            using (var description = JsonDocument.Parse(GpuParticleMcpTools.Describe()))
                Require(description.RootElement.GetProperty("editActions").EnumerateArray().Any(action => action.GetString() == "resource-plan") &&
                    description.RootElement.GetProperty("fluid2D").GetProperty("presets").GetArrayLength() == 4,
                    "MCP discovery must describe resource-plan editing and all four current fluid models.");
            using (var exported = JsonDocument.Parse(Complete(GpuParticleMcpTools.Export(provider, node.Id.ToString(), "resources"))))
                Require(exported.RootElement.GetProperty("source").GetString()!.Contains("host.main.color", StringComparison.Ordinal),
                    "MCP must export the authored resource contract.");
            Complete(GpuParticleMcpTools.ApplyPlan(provider, node.Id.ToString(), "[{\"action\":\"resource-plan-reset\"}]", true));
            Pump();
            Require(vm.State.ResourcePlanDraft is null && graph.History.Undo(),
                "MCP reset must restore playback through an undoable edit.");
            Pump();
            Invoke("ResetResourcePlan");
            Require(GpuParticleEditorService.Diagnostics(vm.State).Count == 0,
                "Reset must restore supported fluid execution without editing the particle graph.");
        }
        finally { view.CloseWorkspace(); window.Close(); }

        T Find<T>(string name) where T : Control => view.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
        void Pump() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        void Invoke(string name) { new ButtonAutomationPeer(Find<Button>(name)).GetProvider<IInvokeProvider>()!.Invoke(); Pump(); }
    }

    private static string Complete(Task<string> task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var registration = timeout.Token.Register(() => Dispatcher.UIThread.Post(() => frame.Continue = false));
            _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
            Require(task.IsCompleted, "Resource-plan MCP timed out.");
        }
        return task.GetAwaiter().GetResult();
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
