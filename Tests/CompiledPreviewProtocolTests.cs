using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Resources;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Preview;
using LiberaFx.ViewModels.Graph;
using LiberaFx.ViewModels.Main;
using LiberaFx.Services.Compilation;

internal static class CompiledPreviewProtocolTests
{
    public static void Run()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        graph.CreateNode(GraphNodeKind.Float);
        var snapshot = graph.CreateSnapshot();
        PreviewPublication Publication(long sequence, GraphSnapshot value) =>
            new(sequence, "package", value, GraphNodeDefinition.All, new(true, 30, 60, 0, 120, true));
        var sender = new GodotPackagePreviewSender();
        var first = sender.Prepare(Publication(1, snapshot), false)!;
        Require(first.Kind == CompiledPreviewKind.Program && first.Bytes.AsSpan(48, 4).SequenceEqual("LFXP"u8),
            "Program must contain an LFXP package.");
        Require(U16(first.Bytes, 4) == 1 && U16(first.Bytes, 6) == 1 && U64(first.Bytes, 8) == 1 &&
            U64(first.Bytes, 16) == 0 && U32(first.Bytes, 44) == first.Bytes.Length - 48,
            "The binary header is incorrect.");
        sender.Sent(first);
        var tick = sender.Prepare(Publication(9, snapshot), false)!;
        Require(tick.Kind == CompiledPreviewKind.Playback && U64(tick.Bytes, 16) == 1,
            "Playback must use the last sent sequence.");
        sender.Sent(tick);
        var scalar = graph.Nodes.Single();
        graph.SetParameter(scalar, "value", JsonSerializer.SerializeToElement(4d));
        var edited = graph.CreateSnapshot();
        var delta = sender.Prepare(Publication(10, edited), false)!;
        Require(delta.Kind == CompiledPreviewKind.Scene && U64(delta.Bytes, 8) == 10,
            "An eligible edit must emit a sequenced package delta.");
        sender.Sent(delta);
        var afterDelta = sender.Prepare(Publication(11, edited), false)!;
        Require(afterDelta.Kind == CompiledPreviewKind.Playback && U64(afterDelta.Bytes, 16) == 10,
            "Playback after a delta must use the delta's sent sequence.");
        sender.Sent(afterDelta);
        graph.SetParameter(scalar, "value", JsonSerializer.SerializeToElement(5d));
        var newer = graph.CreateSnapshot();
        Require(sender.Prepare(Publication(12, newer), false, supportsParameterDelta: false)!.Kind == CompiledPreviewKind.Program,
            "An engine without atomic parameter deltas must receive a full package.");
        var differentPlayback = new PreviewPublication(12, "package", newer, GraphNodeDefinition.All,
            new(false, 40, 60, 0, 120, true));
        Require(sender.Prepare(differentPlayback, false)!.Kind == CompiledPreviewKind.Program,
            "A coalesced playback change must not be lost in a parameter delta.");
        Require(sender.Prepare(Publication(12, edited), true)!.Kind == CompiledPreviewKind.Program,
            "Resync must send a full package.");
        var liveGraph = edited with { AutoDestroyWhenIdle = false };
        var livePackage = LiveGameGraphCompiler.Compile(new(liveGraph, "live"));
        Require(livePackage.AsSpan(0, 4).SequenceEqual("LFXP"u8),
            "Live Game must accept a pure numeric current-version package.");
        graph.CreateNode(GraphNodeKind.FloatFCurve);
        try
        {
            _ = LiveGameGraphCompiler.Compile(new(graph.CreateSnapshot() with { AutoDestroyWhenIdle = false }, "curve"));
            throw new InvalidOperationException("Live Game accepted a non-restartable curve program.");
        }
        catch (NotSupportedException)
        {
        }
    }

    public static void RunUnityMappedPackage() => RunWithDispatcher(VerifyUnityMappedPackageAsync(), 210);

    private static async Task VerifyUnityMappedPackageAsync()
    {
        string Env(string name) => Environment.GetEnvironmentVariable(name) ??
            throw new InvalidOperationException("Set " + name + " to an isolated verification path.");
        var executable = Env("LIBERAFX_UNITY_EDITOR");
        var projectPath = Env("LIBERAFX_VERIFICATION_PROJECT");
        var scratch = Env("LIBERAFX_RESOURCE_WSS_FIXTURE");
        Require(Path.IsPathFullyQualified(executable) && File.Exists(executable) &&
                Path.IsPathFullyQualified(projectPath) && Directory.Exists(projectPath) &&
                Path.IsPathFullyQualified(scratch) && !Directory.Exists(scratch) && !File.Exists(scratch),
            "Use an isolated Unity project and a fresh absolute fixture directory.");
        Directory.CreateDirectory(scratch);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var host = new PreviewHost(port);
        var model = new MainViewModel();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var stop = timeout.Token;
        Process? engine = null;
        try
        {
            await host.StartAsync(model);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            foreach (var argument in new[] { "-projectPath", projectPath, "-batchmode", "-force-d3d11",
                         "-enable-builtin-rp-fallback", "-executeMethod",
                         "LiberaFx.Unity.Editor.LiberaFxResourceIdentityVerification.RunMappedPackageWssBatch",
                         "-logFile", Path.Combine(scratch, "unity.log") })
            {
                start.ArgumentList.Add(argument);
            }
            start.Environment["LIBERAFX_RESOURCE_WSS_ENDPOINT"] = $"wss://127.0.0.1:{port}/preview?protocol=3";
            start.Environment["LIBERAFX_RESOURCE_WSS_TOKEN"] = host.AccessToken;
            start.Environment["LIBERAFX_RESOURCE_WSS_FINGERPRINT"] = host.Fingerprint;
            engine = Process.Start(start) ?? throw new InvalidOperationException("Unity did not start.");
            var identityPath = Path.Combine(scratch, "unity-bindings.json");
            Guid? peer = null;
            while (!engine.HasExited && (!File.Exists(identityPath) || peer is null))
            {
                peer = model.PreviewTelemetry.Clients().FirstOrDefault(client =>
                    client.Engine == "Unity" && client.Role == "editor" &&
                    client.Capabilities.Contains("unityPackagePreview"))?.ClientId;
                await Task.Delay(50, stop);
            }
            Require(!engine.HasExited && peer.HasValue, "Unity did not connect; see the isolated unity.log.");
            var clientId = peer!.Value;
            var state = await model.PreviewAssets.EngineProjectAsync(clientId, cancellation: stop);
            if (state.ProjectState == "Unregistered")
            {
                state = await model.PreviewAssets.EngineProjectAsync(clientId, "initialize", stop);
            }
            var project = state.Project;
            Require(state.ProjectState == "Ready" && project is not null,
                "The isolated Unity project identity is not ready: " + state.ProjectState);
            using var identities = JsonDocument.Parse(await File.ReadAllTextAsync(identityPath, stop));
            const string assetFormat = "unity.asset-guid-local-id.v1";
            const string legacy = "/Game/Test/MissingLegacyModel.MissingLegacyModel";
            const string legacyMaterial = "/Game/Test/MissingLegacyMaterial.MissingLegacyMaterial";
            var mesh = new OpaqueBinding(assetFormat, identities.RootElement.GetProperty("mesh").GetString()!);
            var replacement = new OpaqueBinding(assetFormat, identities.RootElement.GetProperty("replacement").GetString()!);
            var material = new OpaqueBinding(assetFormat, identities.RootElement.GetProperty("material").GetString()!);
            var meshId = Guid.NewGuid();
            var materialId = Guid.NewGuid();
            var document = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var draw = model.Graph.CreateNode(new("ModelDraw"));
                model.Graph.AssignRenderAsset(draw, new EngineAsset(legacy, "Legacy mesh", "model", "Unreal",
                    Materials: [new EngineMaterial(0, legacyMaterial, "Legacy material", [])]));
                var mappings = new ResourceIdentityDocument(1,
                    [new(meshId, "model", "Mapped cube"), new(materialId, "material", "Mapped material")],
                    [new(project!, meshId, "model", mesh), new(project!, materialId, "material", material)],
                    [], [new(draw.Id, "assetPath", meshId), new(draw.Id, "material:0", materialId)], []);
                model.Graph.SetResourceIdentityDocument(mappings, "Map Unity fixture mesh and material");
                return mappings;
            });
            async Task WaitForUnityAsync(string marker)
            {
                var path = Path.Combine(scratch, marker);
                while (!engine.HasExited && !File.Exists(path))
                {
                    await Task.Delay(50, stop);
                }
                Require(File.Exists(path), "Unity did not confirm " + marker + "; see the isolated unity.log.");
            }
            async Task<long?> WaitForAcknowledgementAsync()
            {
                PreviewPeer CurrentPeer() => model.PreviewTelemetry.Clients().Single(item => item.ClientId == clientId);
                while (!engine.HasExited && CurrentPeer().AwaitingProgramAcknowledgement)
                {
                    await Task.Delay(50, stop);
                }
                return CurrentPeer().AcknowledgedProgramSequence;
            }
            await WaitForUnityAsync("unity-live-first.ok");
            var firstSequence = await WaitForAcknowledgementAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                model.Graph.SetResourceIdentityDocument(document with
                {
                    ResourceBindings = [new(project! with { ProjectId = Guid.NewGuid() }, meshId, "model", mesh),
                        new(project!, materialId, "material", material)]
                }, "Reject a same-path mapping owned by the wrong Unity project");
            });
            await WaitForUnityAsync("unity-live-rejected.ok");
            var graph = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                model.Graph.SetResourceIdentityDocument(document with
                {
                    ResourceBindings = [new(project!, meshId, "model", replacement),
                        new(project!, materialId, "material", material)]
                }, "Replace the live Unity model binding");
                return model.Graph.CreateSnapshot();
            });
            await WaitForUnityAsync("unity-live-second.ok");
            var finalSequence = await WaitForAcknowledgementAsync();
            Require(firstSequence is not null && finalSequence > firstSequence,
                "Unity did not acknowledge both live preview packages.");
            await Dispatcher.UIThread.InvokeAsync(() => { model.Playback.TimeSeconds = .25; });
            await WaitForUnityAsync("unity-live-seek.ok");
            await Dispatcher.UIThread.InvokeAsync(() => { model.Playback.TimeSeconds = 0; });
            await WaitForUnityAsync("unity-live-rewind.ok");
            var context = await PreviewResourceBindings.ResolveAsync(graph, model.PreviewAssets, clientId, stop);
            var package = SpawnableGroupPackage.Compile(graph, resourceContext: context);
            var delivered = await model.PreviewAssets.DeliverUnityBindingsAsync(clientId, project!,
                [new(meshId, "model", replacement, []), new(materialId, "material", material, [])]);
            Require(delivered == "Assets/LiberaFx/LiberaFxBindings.asset",
                "Unity did not acknowledge the actual runtime binding asset.");
            var temporary = Path.Combine(scratch, "unity-mapped.tmp");
            await File.WriteAllBytesAsync(temporary, SpawnableGroupPackageBinary.Serialize(package), stop);
            File.Move(temporary, Path.Combine(scratch, "unity-mapped.lfxp"));
            await engine.WaitForExitAsync(stop);
            var proof = Path.Combine(scratch, "unity-result.txt");
            Require(engine.ExitCode == 0 && File.Exists(proof) &&
                File.Exists(Path.Combine(scratch, "unity-mapped.png")),
                "Unity runtime package verification failed; see the isolated unity.log.");
            Console.Write(await File.ReadAllTextAsync(proof, stop));
        }
        finally
        {
            await host.StopAsync();
            if (engine is not null)
            {
                if (!engine.HasExited)
                {
                    if (!engine.WaitForExit(5000))
                    {
                        engine.Kill();
                    }
                    await engine.WaitForExitAsync();
                }
                engine.Dispose();
            }
            await Dispatcher.UIThread.InvokeAsync(model.DisposeStorageFiles);
        }
    }

    internal static void RunWithDispatcher(Task task, int timeoutSeconds = 35)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var registration = timeout.Token.Register(() => Dispatcher.UIThread.Post(() => frame.Continue = false));
            _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
        }
        if (!task.IsCompleted) throw new TimeoutException("Compiled preview transport timed out.");
        task.GetAwaiter().GetResult();
    }

    private static async Task<byte[]> ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), token);
            if (result.MessageType != WebSocketMessageType.Binary ||
                stream.Length + result.Count > CompiledPreviewProtocol.MaxProgramBytes + 48)
                throw new InvalidDataException("Invalid binary preview message.");
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return stream.ToArray();
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
