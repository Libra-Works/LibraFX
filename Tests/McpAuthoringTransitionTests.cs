using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using LiberaFx.Views;

internal static class McpAuthoringTransitionTests
{
    public static void Run()
    {
        var task = VerifyAsync();
        var frame = new DispatcherFrame();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var registration = timeout.Token.Register(() => Dispatcher.UIThread.Post(() => frame.Continue = false));
        _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.UIThread.PushFrame(frame);
        if (!task.IsCompleted) throw new TimeoutException("MCP authoring transitions timed out.");
        task.GetAwaiter().GetResult();
    }

    private static async Task VerifyAsync()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var window = new Window { Width = 1280, Height = 800, Content = new GraphEditorView { DataContext = graph } };
        window.Show();
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var host = new McpHost(port);
        var timings = new List<object>();
        try
        {
            await host.StartAsync(new ActiveGraphProvider(graph));
            using var client = new LocalPreviewMcpClient(host);
            async Task<JsonDocument> Call(string name, object args)
            {
                var watch = Stopwatch.StartNew();
                var result = await client.CallAsync(name, args);
                timings.Add(new { name, milliseconds = watch.Elapsed.TotalMilliseconds });
                Require(watch.Elapsed < TimeSpan.FromSeconds(10), "MCP operation exceeded 10 seconds: " + name);
                // Force the real canvas to update between operations, not just at the end of a batch.
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout);
                return JsonDocument.Parse(result);
            }
            async Task<Guid> Create(string kind)
            {
                using var result = await Call("libera_graph_create_node", new { kind });
                return result.RootElement.GetProperty("id").GetGuid();
            }
            using (await client.SendAsync("initialize", new { protocolVersion = "2025-03-26", capabilities = new { },
                       clientInfo = new { name = "release-authoring-transitions", version = "1" } })) { }
            using (await client.SendAsync("tools/list")) { }
            var group = await Create("SpawnableGroup");
            var scalar = await Create("Float");
            Require(!graph.GetNode(scalar).HasLegacyInput, "Scalar must offer only its output.");
            using (await Call("libera_graph_move_into_group", new { nodeIds = new[] { scalar }, groupId = group })) { }
            Require(graph.Nodes.All(n => n.Id != scalar) && graph.GetNode(group).ChildGraph!.Nodes.Any(n => n.Id == scalar),
                "MCP move must update parent and child immediately.");
            using (await Call("libera_graph_undo", new { })) { }
            Require(graph.Nodes.Any(n => n.Id == scalar), "Undo must return the node to its parent.");
            using (await Call("libera_graph_redo", new { })) { }
            for (var i = 0; i < 3; i++)
            {
                var revision = graph.History.CurrentNodeId;
                using (await Call("libera_graph_enter", new { nodeId = group })) { }
                Require(graph.Nodes.Any(n => n.Id == scalar), "Entering a group must preserve moved IDs.");
                if (i == 0)
                {
                    var signal = await Create("Signal");
                    Require(!graph.GetNode(signal).HasLegacyInput, "Signal entry must offer only its output.");
                    var vector = await Create("FloatX3");
                    Require(!graph.GetNode(vector).HasLegacyInput, "Vector constant must offer only its output.");
                }
                using (await Call("libera_graph_read", new { })) { }
                using (await Call("libera_graph_up", new { })) { }
                if (i != 0) Require(revision == graph.History.CurrentNodeId, "Navigation must not add history entries.");
            }
            var gpu = await Create("GpuParticle");
            Require(graph.GetNode(gpu).HasCustomInputs && graph.GetNode(gpu).InputPorts.Any(p => p.Id == "drawMatrix"),
                "GPU matrix inputs must be visible on the outer node.");
            var matrix = await Create("Transform");
            using (await Call("libera_graph_connect_nodes", new { sourceId = matrix, targetId = gpu,
                       targetPortId = "emitterMatrix", sourcePortId = "value" })) { }
            using (var result = await Call("libera_graph_compile_spawnable", new { }))
            {
                var bytes = Convert.FromBase64String(result.RootElement.GetProperty("rootRenderConfigurationBase64").GetString()!);
                using var render = JsonDocument.Parse(bytes);
                Require(render.RootElement.GetProperty("gpuEmitterTransforms")[0].GetProperty("rowNodeIds").GetArrayLength() == 4,
                    "MCP Matrix connection must reach the executable render scope.");
            }
            foreach (var sample in new[] { "CpuMeshSurface", "CpuFountain" })
            {
                var beforeSample = graph.CreateSnapshot();
                using (await Call("libera_create_particle_sample", new { style = sample })) { }
                using (await Call("libera_graph_compile_spawnable", new { })) { }
                using (await Call("libera_graph_undo", new { })) { }
                Require(GraphSnapshot.ContentEquals(beforeSample, graph.CreateSnapshot()),
                    "Creating a particle sample through MCP must be a single undoable edit.");
            }
            foreach (var archetype in new[] { "Rain", "Snow", "Lightning", "MagicCircle" })
            {
                var beforeTemplate = graph.CreateSnapshot();
                using (await Call("libera_create_game_effect", new { archetype })) { }
                using (await Call("libera_graph_compile_spawnable", new { })) { }
                using (await Call("libera_graph_undo", new { })) { }
                Require(GraphSnapshot.ContentEquals(beforeTemplate, graph.CreateSnapshot()),
                    "Creating a game effect through MCP must be a single undoable edit.");
            }
            await Dispatcher.UIThread.InvokeAsync(() => EditorInterfaceTests.VerifyContainerPresentation());
            await Dispatcher.UIThread.InvokeAsync(() => EditorInterfaceTests.VerifyConstantEditors());
            await Dispatcher.UIThread.InvokeAsync(WorkspaceNavigationTests.Run);
            await Dispatcher.UIThread.InvokeAsync(ConnectionSnapTests.Run);
            await Dispatcher.UIThread.InvokeAsync(SignalTimelineTests.VerifyAutomaticTracks);
            var snapshot = graph.CreateSnapshot();
            GraphValidator.ValidateDocument(snapshot);
            using var stream = new MemoryStream();
            await new ProjectFileService().SaveAsync(stream, graph);
            stream.Position = 0;
            Require(GraphSnapshot.ContentEquals(snapshot, (await ProjectDocumentReader.LoadAsync(stream)).Graph),
                "MCP transition results must survive save and reload.");
            Console.WriteLine("MCP authoring transitions passed: " + JsonSerializer.Serialize(timings));
        }
        finally { await host.StopAsync(); window.Close(); }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
