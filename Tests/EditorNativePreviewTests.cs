using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;

internal static class EditorNativePreviewTests
{
    public static void Run(string output)
    {
        if (!Path.IsPathFullyQualified(output)) throw new ArgumentException("Use an absolute isolated fixture output directory.");
        var library = Environment.GetEnvironmentVariable("LIBERAFX_PREVIEW_NATIVE_LIBRARY")
            ?? Environment.GetEnvironmentVariable("LIBERAFX_NATIVE_TEST_LIBRARY")
            ?? throw new InvalidOperationException("Set LIBERAFX_PREVIEW_NATIVE_LIBRARY to the built native runtime DLL.");
        Directory.CreateDirectory(output);
        VerifyMalformedGraphs();
        var sourceSample = Environment.GetEnvironmentVariable("LIBERAFX_FIREWORKS_SAMPLE")
            ?? Path.Combine(AppContext.BaseDirectory, "Assets", "Samples", "Fireworks.lfxproj");
        if (!Path.IsPathFullyQualified(sourceSample) || !File.Exists(sourceSample))
            throw new InvalidOperationException("Build the delivered Fireworks sample or set LIBERAFX_FIREWORKS_SAMPLE to its absolute .lfxproj path.");
        GraphSnapshot graph;
        using (var source = new MemoryStream(File.ReadAllBytes(sourceSample)))
        {
            var project = new ProjectFileService().LoadAsync(source).GetAwaiter().GetResult();
            Require(project.History is not null, "Delivered Fireworks sample has no history.");
            graph = project.Graph;
            Require(graph.Nodes.Any(node => node.Kind == GraphNodeKind.Comment &&
                node.Parameters?.Any(parameter => parameter.Id == "text" && parameter.Value.GetString()!.Contains("End = 240")) == true),
                "Distributed Fireworks sample must prominently explain End=240 before Play.");
        }
        var ownerBaseline = EditorNativeHostSession.ActiveOwnerCount;
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        workspace.InitializeFromSnapshot(graph);
        using (var memory = new MemoryStream())
        {
            new ProjectFileService().SaveAsync(memory, workspace).GetAwaiter().GetResult();
            File.WriteAllBytes(Path.Combine(output, "Fireworks.lfxproj"), memory.ToArray());
        }
        using (var missing = new CompiledPreviewSender(Path.Combine(output, "missing-native.dll")))
        {
            var error = missing.Prepare(Publication(1, graph, 0))!;
            Require(error.Kind == CompiledPreviewKind.CompileError && error.EvaluatedRenderScene is null &&
                Encoding.UTF8.GetString(error.Bytes, 48, error.Bytes.Length - 48).Contains("LIBERAFX_PREVIEW_NATIVE_LIBRARY"),
                "Missing native runtime must give an actionable error, not fake playback.");
        }
        using var sender = new CompiledPreviewSender(library);
        using (var jump = new CompiledPreviewSender(library))
        {
            var start = jump.Prepare(Publication(1, graph, 0))!;
            jump.Sent(start);
            var jumped = jump.Prepare(Publication(2, graph, 2.5))!;
            using var scene = JsonDocument.Parse(jumped.EvaluatedRenderScene!);
            var nodes = scene.RootElement.GetProperty("nodes").EnumerateArray().ToArray();
            Require(nodes.Length == 12 && nodes.All(node => Math.Abs(node.GetProperty("localTimeSeconds").GetDouble() - 0.5) < 1e-8),
                "Skipped deadline created an age-zero Burst instead of replaying its 0.5-second age.");
            Near(Position(nodes[0]), [55, 0, 230]);
            jump.Sent(jumped);
            var paused = jump.Prepare(Publication(3, graph, 2.5, false))!;
            jump.Sent(paused);
            var resumed = jump.Prepare(Publication(4, graph, 2.5))!;
            Require(jumped.EvaluatedRenderScene.AsSpan().SequenceEqual(paused.EvaluatedRenderScene) &&
                paused.EvaluatedRenderScene!.AsSpan().SequenceEqual(resumed.EvaluatedRenderScene),
                "Pause/resume restarted OnStart, regenerated expired events or changed Burst age.");
            jump.Sent(resumed);
            var resync = jump.Prepare(Publication(4, graph, 2.5), true)!;
            using var resynced = JsonDocument.Parse(resync.EvaluatedRenderScene!);
            Require(resynced.RootElement.GetProperty("nodes").GetRawText() == scene.RootElement.GetProperty("nodes").GetRawText(),
                "Same-time resync recreated a different lifecycle/renderer state.");
            var switchedDocument = jump.Prepare(new PreviewPublication(5, "different-document", graph, GraphNodeDefinition.All,
                new(false, 0, 60, 0, 300, false)))!;
            using var reset = JsonDocument.Parse(switchedDocument.EvaluatedRenderScene!);
            Require(switchedDocument.Kind == CompiledPreviewKind.Program && reset.RootElement.GetProperty("nodes").GetArrayLength() == 1,
                "Changing document did not dispose/recreate its owner.");
        }
        var scope = Guid.NewGuid();
        var drawId = Guid.NewGuid();
        Require(EditorNativePreviewSession.RendererId(scope, 1, drawId) != EditorNativePreviewSession.RendererId(scope, 2, drawId) &&
            EditorNativePreviewSession.RendererId(scope, 1, drawId) != EditorNativePreviewSession.RendererId(Guid.NewGuid(), 1, drawId),
            "Renderer identity must include both container and runtime instance.");
        using (var repeated = new CompiledPreviewSender(library))
        {
            var publication = Publication(1, graph, 1);
            var first = repeated.Prepare(publication)!;
            var again = repeated.Prepare(publication)!;
            Require(first.Bytes.AsSpan().SequenceEqual(again.Bytes) &&
                first.EvaluatedRenderScene!.AsSpan().SequenceEqual(again.EvaluatedRenderScene), "Retry before Sent changed native state.");
            repeated.Sent(first);
            var forcedRetry = repeated.Prepare(publication, true)!;
            Require(first.EvaluatedRenderScene.AsSpan().SequenceEqual(forcedRetry.EvaluatedRenderScene), "Resync restarted or advanced native pools.");
        }
        var frames = new JsonArray();
        long sequence = 0;
        foreach (var time in new[] { 0d, 1d, 2d, 2.5d, 3.5d })
        {
            var message = sender.Prepare(Publication(++sequence, graph, time))!;
            Require(message.Kind == (sequence == 1 ? CompiledPreviewKind.Program : CompiledPreviewKind.Playback) &&
                message.EditorNativeExecution && message.EvaluatedRenderScene is not null,
                "Native evaluated preview frame missing: " + (message.Kind == CompiledPreviewKind.CompileError ?
                    Encoding.UTF8.GetString(message.Bytes, 48, message.Bytes.Length - 48) : message.Kind.ToString()));
            Require(BinaryPrimitives.ReadUInt32LittleEndian(message.Bytes.AsSpan(40)) == 0, "UE must not free-run editor-owned execution.");
            if (sequence == 1)
                Require(message.Bytes.AsSpan(48).SequenceEqual(EditorNativePreviewSession.TransportShell) &&
                    BinaryPrimitives.ReadUInt32LittleEndian(message.Bytes.AsSpan(56)) == 1 && message.Bytes[48 + 12 + 16] == 1,
                    "Transport shell must be one explicitly inert Scalar, never a Time-only substitute.");
            using var scene = JsonDocument.Parse(message.EvaluatedRenderScene!);
            Require(scene.RootElement.GetProperty("executionOwner").GetString() == "editor-native" &&
                !scene.RootElement.GetProperty("standaloneEngineLifecycle").GetBoolean() &&
                scene.RootElement.GetProperty("programSequence").GetInt64() == 1,
                "Native ownership or accepted program sequence is incorrect.");
            var nodes = scene.RootElement.GetProperty("nodes").EnumerateArray().ToArray();
            var count = time < 2 ? 1 : time < 3.5 ? 12 : 0;
            Require(nodes.Length == count, "Actual native lifetime did not replace Launch with Burst/remove expired renderers.");
            Require(nodes.Select(node => node.GetProperty("nodeId").GetGuid()).Distinct().Count() == nodes.Length,
                "Per-instance renderer identities collided.");
            foreach (var node in nodes)
                foreach (var input in node.GetProperty("inputs").EnumerateArray())
                    Require(input.GetProperty("sourceNodeId").ValueKind == JsonValueKind.Null &&
                        input.GetProperty("defaults").EnumerateArray().All(lane => double.IsFinite(lane.GetDouble())),
                        "UE received an unresolved runtime source or nonfinite evaluated default.");
            if (time < 2) Near(Position(nodes[0]), [0, 0, 20 + 120 * time]);
            else if (time == 2) foreach (var node in nodes) Near(Position(node), [0, 0, 260]);
            else if (time == 2.5) Near(Position(nodes[0]), [55, 0, 230]);
            var name = $"frame-{sequence - 1:000}";
            var directory = Path.Combine(output, name);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "program.lfxb"), EditorNativePreviewSession.TransportShell);
            File.WriteAllBytes(Path.Combine(directory, "publication.lfxp"), message.Bytes);
            File.WriteAllBytes(Path.Combine(directory, "render-config.json"), message.EvaluatedRenderScene!);
            File.WriteAllBytes(Path.Combine(directory, "scene.lfxp"), CompiledPreviewProtocol.CreateScene(message.EvaluatedRenderScene!));
            File.WriteAllText(Path.Combine(directory, "expected.json"), Expected(scene.RootElement, time).ToJsonString());
            frames.Add(new JsonObject { ["directory"] = name, ["timeSeconds"] = time, ["renderers"] = nodes.Length });
            sender.Sent(message);
        }
        File.WriteAllText(Path.Combine(output, "frames.json"), new JsonObject
        {
            ["executionOwner"] = "editor-native", ["frames"] = frames
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        // Pause/resume does not restart pools, duplicate OnStart or evaluate at
        // the same sampled time. Backwards seek and loop deterministically replay.
        using (var native = new EditorNativePreviewSession(graph, library))
        {
            var start = native.EvaluateScene(0, 1);
            var seed = native.Events.Single().Seed;
            native.EvaluateScene(1, 1);
            var events = native.Events.Count;
            var paused = native.EvaluateScene(1, 1);
            var resumed = native.EvaluateScene(1, 1);
            Require(events == native.Events.Count && paused.AsSpan().SequenceEqual(resumed), "Pause/resume changed native state.");
            native.EvaluateScene(2.5, 1);
            var rewound = native.EvaluateScene(0, 1);
            Require(start.AsSpan().SequenceEqual(rewound) && native.Events.Single().Seed == seed &&
                native.Events.Single().Kind == 1, "Seek/loop teardown emitted Destroy or failed deterministic replay.");
            var direct = native.EvaluateScene(2.5, 1);
            native.EvaluateScene(0, 1);
            native.EvaluateScene(1, 1);
            Require(direct.AsSpan().SequenceEqual(native.EvaluateScene(2.5, 1)), "Dropped frames changed deterministic native replay.");
        }
        var rewindMessage = sender.Prepare(Publication(++sequence, graph, 0, false))!;
        Require(JsonDocument.Parse(rewindMessage.EvaluatedRenderScene!).RootElement.GetProperty("nodes").GetArrayLength() == 1,
            "Sender did not reset stopped/looped native state.");
        sender.Sent(rewindMessage);
        var forced = sender.Prepare(Publication(++sequence, graph, 0), true)!;
        Require(forced.Kind == CompiledPreviewKind.Program && forced.EvaluatedRenderScene is not null, "Native resync needs shell and full evaluated scene.");
        sender.Sent(forced);
        var empty = new GraphSnapshot([], []);
        var switched = sender.Prepare(Publication(++sequence, empty, 0))!;
        Require(!switched.EditorNativeExecution && switched.EvaluatedRenderScene is null, "Changing to numeric graph retained stale native instances.");
        sender.Sent(switched);
        var restored = sender.Prepare(Publication(++sequence, graph, 0))!;
        Require(restored.Kind == CompiledPreviewKind.Program && restored.EvaluatedRenderScene is not null, "Returning to lifecycle graph failed to recreate pools.");
        using (var bounded = new CompiledPreviewSender(library))
        {
            var tooLong = bounded.Prepare(Publication(1, graph, EditorNativePreviewSession.MaximumReplaySeconds + 1))!;
            Require(tooLong.Kind == CompiledPreviewKind.CompileError && tooLong.EvaluatedRenderScene is null, "Unbounded seek must fail explicitly.");
            var burst = graph.Nodes.Single(node => node.Name == "Burst");
            var model = burst.ChildGraph!.Nodes.First(node => node.Kind.Value == "ModelDraw");
            var overflow = graph with { Nodes = graph.Nodes.Select(node => node.Id != burst.Id ? node : node with
            {
                ChildGraph = node.ChildGraph! with { Nodes = node.ChildGraph.Nodes.Concat(
                    Enumerable.Range(0, 117).Select(_ => model with { Id = Guid.NewGuid() })).ToArray() }
            }).ToArray() };
            var tooMany = bounded.Prepare(Publication(2, overflow, 2))!;
            Require(tooMany.Kind == CompiledPreviewKind.CompileError && tooMany.EvaluatedRenderScene is null,
                "More than 128 renderers must fail explicitly.");
            var rootDraw = model with { Id = Guid.NewGuid(), Name = "Root draw for aggregate scene bound" };
            var aggregate = graph with { Nodes = graph.Nodes.Select(node => node.Id != burst.Id ? node : node with
            {
                ChildGraph = node.ChildGraph! with { Nodes = node.ChildGraph.Nodes.Concat(
                    Enumerable.Range(0, 116).Select(_ => model with { Id = Guid.NewGuid() })).ToArray() }
            }).Concat([rootDraw]).ToArray() };
            var aggregateError = bounded.Prepare(Publication(3, aggregate, 2))!;
            Require(aggregateError.Kind == CompiledPreviewKind.CompileError && aggregateError.EvaluatedRenderScene is null,
                "Combined root/template renderer count above 128 was published.");
            var gpuDefinition = GraphNodeDefinition.All.Single(definition => definition.Kind == GpuParticleEditorService.Kind);
            var gpu = new GraphNodeSnapshot(Guid.NewGuid(), gpuDefinition.Kind, "Unsupported mixed GPU preview", 0, 0,
                gpuDefinition.PortType, gpuDefinition.PortCategory);
            var mixed = bounded.Prepare(Publication(4, graph with { Nodes = graph.Nodes.Concat([gpu]).ToArray() }, 0))!;
            Require(mixed.Kind == CompiledPreviewKind.CompileError && mixed.EvaluatedRenderScene is null &&
                Encoding.UTF8.GetString(mixed.Bytes, 48, mixed.Bytes.Length - 48).Contains("GPU"),
                "Root GPU containers were silently dropped by native lifecycle preview.");
        }
        VerifyLegacyThen(graph, library);
        var rootModel = graph.Nodes.Single(node => node.Name == "Launch").ChildGraph!.Nodes.First(node => node.Kind.Value == "ModelDraw")
            with { Id = Guid.NewGuid(), Name = "Root native evaluated draw" };
        var rootSource = graph.Nodes.Single(node => node.Name == "Launch initial position");
        var rootGraph = graph with
        {
            Nodes = graph.Nodes.Concat([rootModel]).ToArray(),
            Connections = graph.Connections.Concat([new GraphConnectionSnapshot(rootSource.Id, rootModel.Id, "position")]).ToArray()
        };
        using (var root = new CompiledPreviewSender(library))
        {
            using var scene = JsonDocument.Parse(root.Prepare(Publication(1, rootGraph, 0))!.EvaluatedRenderScene!);
            var node = scene.RootElement.GetProperty("nodes").EnumerateArray().Single(item => item.GetProperty("instanceId").GetUInt64() == 0);
            Near(Position(node), [0, 0, 20]);
        }
        sender.Dispose();
        Require(EditorNativeHostSession.ActiveOwnerCount == ownerBaseline, "Reset/graph-change/error/sender disposal leaked a native owner.");
        var previous = Environment.GetEnvironmentVariable("LIBERAFX_PREVIEW_NATIVE_LIBRARY");
        Environment.SetEnvironmentVariable("LIBERAFX_PREVIEW_NATIVE_LIBRARY", library);
        try { RunWithDispatcher(VerifyTransportAsync(graph)); }
        finally { Environment.SetEnvironmentVariable("LIBERAFX_PREVIEW_NATIVE_LIBRARY", previous); }
        Require(EditorNativeHostSession.ActiveOwnerCount == ownerBaseline, "Preview disconnect leaked its editor-native owner.");
        Console.WriteLine("Editor-native preview PASS: real C++ lifecycle -> per-instance render defaults; 1/1/12/12/0 renderers at 0/1/2/2.5/3.5s.");
        Console.WriteLine("TLS/WebSocket shell + evaluated Scene delivery, t0->t2.5 jump (captured Z260; Burst age0.5), Pause/Resume, seek/loop replay, owner disposal and missing-library errors passed.");
        Console.WriteLine($"UE CPU/pixel fixtures: {output}\\frame-000 .. frame-004 (executionOwner=editor-native; UE owns rendering only).");
    }
    private static void VerifyMalformedGraphs()
    {
        var group = GraphNodeDefinition.Core.Single(item => item.Kind == GraphNodeKind.GraphGroup);
        var spawnable = GraphNodeDefinition.Core.Single(item => item.Kind == GraphNodeKind.SpawnableGroup);
        var malformed = new GraphSnapshot[]
        {
            null!, new(null!, []), new([], null!), new([null!], []), new([], [null!]),
            new([new(Guid.NewGuid(), group.Kind, "Invalid child", 0, 0, group.PortType, group.PortCategory,
                ChildGraph: new GraphSnapshot(null!, []))], []),
            new([new(Guid.NewGuid(), spawnable.Kind, "Invalid template", 0, 0, spawnable.PortType, spawnable.PortCategory,
                ChildGraph: new GraphSnapshot(null!, []))], []),
            new([new(Guid.NewGuid(), spawnable.Kind, "Invalid template edges", 0, 0, spawnable.PortType, spawnable.PortCategory,
                ChildGraph: new GraphSnapshot([], null!))], [])
        };
        foreach (var graph in malformed)
        {
            Reject(() => LiberaFx.Services.Compilation.VfxProgram.Compile(graph));
            Reject(() => LiberaFx.Services.Compilation.SpawnableGroupPackage.Compile(graph));
        }
        static void Reject(Action action)
        {
            try { action(); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Malformed graph was silently accepted instead of returning a compiler validation error.");
        }
    }

    private static void VerifyLegacyThen(GraphSnapshot graph, string library)
    {
        var launch = graph.Nodes.Single(node => node.Name == "Launch");
        var burst = graph.Nodes.Single(node => node.Name == "Burst");
        var origin = graph.Nodes.Single(node => node.Name == "Launch initial position");
        var input = burst.ChildGraph!.Nodes.Single(node => SpawnableGroupSchema.IsInput(node.Kind));
        var legacy = graph with
        {
            Connections = graph.Connections.Where(edge => edge.SourceNodeId != launch.Id || edge.SourcePortId == "destroy")
                .Select(edge => edge.SourceNodeId == launch.Id ? edge with { SourcePortId = "then" } : edge)
                .Concat([new GraphConnectionSnapshot(origin.Id, burst.Id, SpawnableGroupSchema.PortId(input.Id))]).ToArray()
        };
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        workspace.InitializeFromSnapshot(legacy);
        using var stream = new MemoryStream();
        var files = new ProjectFileService();
        files.SaveAsync(stream, workspace).GetAwaiter().GetResult();
        stream.Position = 0;
        var loaded = files.LoadAsync(stream).GetAwaiter().GetResult().Graph;
        Require(loaded.Connections.Any(edge => edge.SourceNodeId == launch.Id && edge.SourcePortId == "then" &&
            edge.TargetNodeId == burst.Id), "Saved legacy then pin was replaced/migrated to natural Destroy.");
        using var native = new EditorNativePreviewSession(loaded, library);
        using var scene = JsonDocument.Parse(native.EvaluateScene(0, 1));
        Require(native.ActiveCount == 2 && native.Events.Count(value => value.Kind == 1) == 2 &&
            scene.RootElement.GetProperty("nodes").GetArrayLength() == 13, "Legacy then must still spawn immediately, not at expiry.");
        native.EvaluateScene(3.5, 1);
        Require(native.Events.Count(value => value.Kind == 1) == 2, "Natural Destroy incorrectly re-fired legacy then.");
    }
    private static PreviewPublication Publication(long sequence, GraphSnapshot graph, double time, bool playing = true) =>
        new(sequence, "fireworks-editor-native", graph, GraphNodeDefinition.All, new(playing, time * 60, 60, 0, 240, true));
    private static double[] Position(JsonElement node) => node.GetProperty("inputs").EnumerateArray()
        .Single(input => input.GetProperty("id").GetString() == "position").GetProperty("defaults").EnumerateArray().Select(lane => lane.GetDouble()).ToArray();
    private static JsonObject Expected(JsonElement scene, double time)
    {
        var nodes = new JsonArray();
        foreach (var node in scene.GetProperty("nodes").EnumerateArray())
        {
            var inputs = node.GetProperty("inputs").EnumerateArray().ToArray();
            var parameters = new JsonArray();
            foreach (var input in inputs.Where(input => input.TryGetProperty("materialParameter", out var value) && value.ValueKind == JsonValueKind.Object))
            {
                var material = input.GetProperty("materialParameter");
                parameters.Add(new JsonObject
                {
                    ["name"] = material.GetProperty("name").GetString(), ["slot"] = material.GetProperty("slot").GetInt32(),
                    ["association"] = material.GetProperty("association").GetInt32(), ["index"] = material.GetProperty("index").GetInt32(),
                    ["value"] = JsonNode.Parse(input.GetProperty("defaults").GetRawText())
                });
            }
            JsonNode Value(string id) => JsonNode.Parse(inputs.Single(input => input.GetProperty("id").GetString() == id).GetProperty("defaults").GetRawText())!;
            nodes.Add(new JsonObject
            {
                ["nodeId"] = node.GetProperty("nodeId").GetString(),
                ["position"] = Value("position"), ["scale"] = Value("scale"), ["rotation"] = Value("rotation"),
                ["parameters"] = parameters
            });
        }
        return new JsonObject { ["frames"] = new JsonArray(new JsonObject { ["timeSeconds"] = time, ["nodes"] = nodes }) };
    }
    private static void RunWithDispatcher(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            using var registration = cancellation.Token.Register(() => Dispatcher.UIThread.Post(() => frame.Continue = false));
            _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
        }
        if (!task.IsCompleted) throw new TimeoutException("Editor-native transport verification timed out.");
        task.GetAwaiter().GetResult();
    }
    private static async Task VerifyTransportAsync(GraphSnapshot graph)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        using var host = new PreviewHost(port);
        var model = new MainViewModel();
        model.Graph.DeleteNodes(model.Graph.Nodes.ToArray());
        model.Graph.History.Clear();
        model.Graph.InitializeFromSnapshot(graph);
        model.Playback.EndText = "240";
        model.Playback.Frame = 0;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = cancel.Token;
        try
        {
            await host.StartAsync(model);
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("X-LiberaFx-Preview-Token", host.AccessToken);
            var fingerprint = Convert.FromHexString(host.Fingerprint);
            socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null &&
                CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), fingerprint);
            await socket.ConnectAsync(new Uri($"wss://localhost:{port}/preview?protocol=3"), token);
            Require(Kind(await Receive(socket, token)) == 1, "Host did not send the native transport shell.");
            await socket.SendAsync(Encoding.UTF8.GetBytes(
                """{"type":"hello","engine":"Unreal","role":"editor","version":1,"capabilities":["assets","rendering","gpuParticles"]}"""),
                WebSocketMessageType.Text, true, token);
            var program = await Receive(socket, token);
            Require(Kind(program) == 1, "Rendering negotiation must resync the native shell.");
            var scene = await Receive(socket, token);
            Require(Kind(scene) == 5, "Host did not send evaluated initial renderer scene.");
            using (var json = JsonDocument.Parse(scene.AsMemory(48)))
                Require(json.RootElement.GetProperty("executionOwner").GetString() == "editor-native" &&
                    json.RootElement.GetProperty("nodes").GetArrayLength() == 1, "Host initial scene has no real Launch.");
            using (var gpu = JsonDocument.Parse((await Receive(socket, token)).AsMemory(48)))
                Require(gpu.RootElement.GetProperty("type").GetString() == "gpu_config" &&
                    gpu.RootElement.GetProperty("programs").GetArrayLength() == 0, "Native lifecycle program must retire prior GPU scenes.");
            await Dispatcher.UIThread.InvokeAsync(() => model.Playback.Frame = 120);
            var playback = await Receive(socket, token);
            Require(Kind(playback) == 2, "Root playback must retain the accepted shell program.");
            var burst = await Receive(socket, token);
            using (var json = JsonDocument.Parse(burst.AsMemory(48)))
                Require(Kind(burst) == 5 && json.RootElement.GetProperty("nodes").GetArrayLength() == 12 &&
                    json.RootElement.GetProperty("programSequence").GetUInt64() == BinaryPrimitives.ReadUInt64LittleEndian(program.AsSpan(8)),
                    "Actual playback Scene omitted native Destroy/Burst or used the playback sequence as its program identity.");
            await Dispatcher.UIThread.InvokeAsync(() => model.Playback.Frame = 210);
            Require(Kind(await Receive(socket, token)) == 2, "Expiry playback missing.");
            using (var json = JsonDocument.Parse((await Receive(socket, token)).AsMemory(48)))
                Require(json.RootElement.GetProperty("nodes").GetArrayLength() == 0, "Expired Burst renderers were not removed.");
            socket.Abort();
        }
        finally
        {
            await host.StopAsync();
            await Dispatcher.UIThread.InvokeAsync(model.DisposeStorageFiles);
        }
    }
    private static ushort Kind(byte[] bytes) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6));
    private static async Task<byte[]> Receive(ClientWebSocket socket, CancellationToken token)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        ValueWebSocketReceiveResult message;
        do
        {
            message = await socket.ReceiveAsync(buffer.AsMemory(), token);
            Require(message.MessageType == WebSocketMessageType.Binary && stream.Length + message.Count < 2 * 1024 * 1024,
                "Invalid bounded preview frame.");
            stream.Write(buffer, 0, message.Count);
        } while (!message.EndOfMessage);
        return stream.ToArray();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Near(double[] actual, double[] expected) =>
        Require(expected.Select((lane, index) => Math.Abs(actual[index] - lane) < 1e-8).All(value => value), "Evaluated native position mismatch.");
}
