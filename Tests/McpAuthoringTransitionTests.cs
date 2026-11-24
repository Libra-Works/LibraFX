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
        var graphProvider = new ActiveGraphProvider(graph);
        var timings = new List<object>();
        try
        {
            await host.StartAsync(graphProvider);
            using var chatClient = new ChatMcpClient(host.ActualEndpoint, host.AccessToken);
            await chatClient.ConnectAsync(CancellationToken.None);
            Require(chatClient.Tools.Any(tool => tool.Name == "libera_graph_analyze_optimization"),
                "Optimization analysis must be discoverable by the in-editor MCP chat client.");
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
                using (var compiled = await Call("libera_graph_compile_spawnable", new { }))
                    Require(Convert.FromBase64String(compiled.RootElement.GetProperty("cpuProgramsBase64").GetString()!).Length > 0,
                        "MCP compilation must expose executable root CPU programs.");
                if (sample == "CpuFountain")
                {
                    var beforeAnalysis = graph.CreateSnapshot();
                    var revision = graph.History.CurrentNodeId;
                    using var report = await Call("libera_graph_analyze_optimization", new { });
                    Require(!report.RootElement.GetProperty("runtimeExecuted").GetBoolean() &&
                        report.RootElement.GetProperty("measuredFrameMilliseconds").ValueKind == JsonValueKind.Null &&
                        report.RootElement.GetProperty("binary").GetProperty("cpuBytesSaved").GetInt64() >= 1280,
                        "MCP must report actual CPU export savings without claiming runtime timings.");
                    Require(revision == graph.History.CurrentNodeId && GraphSnapshot.ContentEquals(beforeAnalysis, graph.CreateSnapshot()),
                        "Optimization analysis must preserve graph state and undo history.");
                    Require(!ChatMcpClient.NeedsConfirmation("libera_graph_analyze_optimization"),
                        "Read-only optimization analysis must not be treated as a graph edit.");
                    using var lease = await Dispatcher.UIThread.InvokeAsync(() => ChatMcpExecution.Capture(graphProvider));
                    using var chatReport = JsonDocument.Parse(await chatClient.CallAsync(new("optimization-analysis",
                        "libera_graph_analyze_optimization", JsonSerializer.SerializeToElement(new { })), lease.Token, CancellationToken.None));
                    Require(!chatReport.RootElement.TryGetProperty("isError", out var error) || !error.GetBoolean(),
                        "Graph-scoped chat must be able to read optimization analysis.");
                    Require(revision == graph.History.CurrentNodeId && GraphSnapshot.ContentEquals(beforeAnalysis, graph.CreateSnapshot()),
                        "Chat optimization analysis must preserve source and history too.");
                }
                using (await Call("libera_graph_undo", new { })) { }
                Require(GraphSnapshot.ContentEquals(beforeSample, graph.CreateSnapshot()),
                    "Creating a particle sample through MCP must be a single undoable edit.");
            }
            var cpu = await Create("CpuParticle");
            using (await Call("libera_graph_enter", new { nodeId = cpu })) { }
            var cpuDraw = graph.Nodes.Single(node => CpuParticleSchema.Name(node.Definition.Kind) == "CpuParticleSpriteDraw").Id;
            using (await Call("libera_graph_set_parameter", new { nodeId = cpuDraw, parameterId = "assetPath", value = "builtin:white" })) { }
            using (await Call("libera_graph_set_parameter", new { nodeId = cpuDraw, parameterId = "atlasColumns", value = 2 })) { }
            using (await Call("libera_graph_set_parameter", new { nodeId = cpuDraw, parameterId = "atlasRows", value = 2 })) { }
            using (await Call("libera_graph_set_parameter", new { nodeId = cpuDraw, parameterId = "atlasFps", value = 4 })) { }
            var beforeFlipbook = graph.CreateSnapshot();
            using (await Call("libera_graph_set_parameter", new { nodeId = cpuDraw, parameterId = "flipbookEnabled", value = true })) { }
            using (await Call("libera_graph_up", new { })) { }
            using (var compiled = await Call("libera_graph_compile_spawnable", new { }))
            {
                using var render = JsonDocument.Parse(Convert.FromBase64String(compiled.RootElement.GetProperty("rootRenderConfigurationBase64").GetString()!));
                var scene = render.RootElement;
                Require(scene.GetProperty("cpuParticleVersion").GetInt32() == 3 &&
                    scene.GetProperty("cpuParticles")[0].GetProperty("flipbook").GetProperty("columns").GetInt32() == 2,
                    "MCP Flipbook edits must reach the compiled particle renderer.");
            }
            using (await Call("libera_graph_undo", new { })) { }
            Require(GraphSnapshot.ContentEquals(beforeFlipbook, graph.CreateSnapshot()), "Flipbook enable must be an undoable document edit.");
            using (await Call("libera_graph_enter",new {nodeId=cpu})) { }
            var beforeSprite=graph.CreateSnapshot();
            var cell=new SpriteAnimationCell(Guid.NewGuid(),"White",0,0,1,1);
            var sprite=new SpriteAnimationData {TextureWidth=1,TextureHeight=1,Fps=4,Cells=[cell],
                Frames=[new(Guid.NewGuid(),cell.Id,1),new(Guid.NewGuid(),cell.Id,3)],StartFrame=0,EndFrame=1};
            using (await Call("libera_graph_set_parameter",new {nodeId=cpuDraw,parameterId="spriteData",value=sprite})) { }
            using (await Call("libera_graph_up",new { })) { }
            using (var compiled=await Call("libera_graph_compile_spawnable",new { }))
            {
                using var render=JsonDocument.Parse(Convert.FromBase64String(compiled.RootElement.GetProperty("rootRenderConfigurationBase64").GetString()!));
                Require(render.RootElement.GetProperty("cpuParticleVersion").GetInt32()==4 &&
                    render.RootElement.GetProperty("cpuParticles")[0].GetProperty("spriteAnimation").GetProperty("partCount").GetInt32()==1,
                    "MCP Sprite editor animation did not reach CPU rendering.");
            }
            using (await Call("libera_graph_undo",new { })) { }
            Require(GraphSnapshot.ContentEquals(beforeSprite,graph.CreateSnapshot()),"Particle Sprite editor data edit did not undo atomically.");
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
