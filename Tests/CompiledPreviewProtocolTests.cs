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
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;

internal static class CompiledPreviewProtocolTests
{
    public static void Run()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        graph.CreateNode(GraphNodeKind.Float);
        var snapshot = graph.CreateSnapshot();
        PreviewPublication Publication(long sequence, string document, GraphSnapshot value) =>
            new(sequence, document, value, GraphNodeDefinition.All, new(true, 30, 60, 0, 120, true));
        var sender = new CompiledPreviewSender();
        var first = sender.Prepare(Publication(1, "a", snapshot))!;
        Require(first.Kind == CompiledPreviewKind.Program && first.Bytes.AsSpan(48).SequenceEqual(
            VfxProgram.Compile(snapshot).ToBinary()), "Program must carry real compiler bytes.");
        Require(first.Bytes.AsSpan(0, 4).SequenceEqual("LFXP"u8) &&
            U16(first.Bytes, 4) == 1 && U16(first.Bytes, 6) == 1 && U64(first.Bytes, 8) == 1 &&
            U64(first.Bytes, 16) == 0 && U32(first.Bytes, 44) == first.Bytes.Length - 48 &&
            BitConverter.Int64BitsToDouble((long)U64(first.Bytes, 24)) == 0.5 &&
            BitConverter.Int64BitsToDouble((long)U64(first.Bytes, 32)) == 1 && U32(first.Bytes, 40) == 3,
            "The binary header is incorrect.");
        sender.Sent(first);
        var tick = sender.Prepare(Publication(9, "a", snapshot))!;
        Require(tick.Kind == CompiledPreviewKind.Playback && tick.Bytes.Length == 48 &&
            U64(tick.Bytes, 16) == 1, "Playback must use the last sent sequence.");
        sender.Sent(tick);
        var forced = sender.Prepare(Publication(9, "a", snapshot), true)!;
        Require(forced.Kind == CompiledPreviewKind.Program, "Resync must send a full program.");
        sender.Sent(forced);
        graph.CreateNode(GraphNodeKind.Time);
        var edited = graph.CreateSnapshot();
        var edit = sender.Prepare(Publication(10, "a", edited))!;
        Require(edit.Kind == CompiledPreviewKind.Program, "Graph edits require a program.");
        sender.Sent(edit);
        var switched = sender.Prepare(Publication(11, "b", edited))!;
        Require(switched.Kind == CompiledPreviewKind.Program, "Document switches require a program.");
        sender.Sent(switched);
        var invalid = edited with
        {
            Connections = [.. edited.Connections, new(Guid.NewGuid(), edited.Nodes[0].Id, null)]
        };
        var failure = sender.Prepare(Publication(12, "b", invalid))!;
        Require(failure.Kind == CompiledPreviewKind.CompileError && failure.Bytes.Length <= 48 + 4096,
            "Compiler failures must produce bounded diagnostics.");
        sender.Sent(failure);
        Require(sender.Prepare(Publication(13, "b", invalid)) is null, "Identical failures must not flood.");
        Require(sender.Prepare(Publication(13, "b", invalid), true)!.Kind == CompiledPreviewKind.CompileError,
            "Resync must report an invalid current graph.");
        var recovered = sender.Prepare(Publication(14, "b", edited))!;
        Require(recovered.Kind == CompiledPreviewKind.Playback && U64(recovered.Bytes, 16) == 12,
            "A failure must preserve the last good graph and last sent message baseline.");
        RunWithDispatcher(VerifyTransportAsync());
        RunWithDispatcher(VerifyProjectMappedRenderAsync());
    }

    public static void RunUnrealGpuPreview() => RunWithDispatcher(VerifyUnrealGpuPreviewAsync(), 330);

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

    private static async Task VerifyProjectMappedRenderAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var host = new PreviewHost(port);
        var model = new MainViewModel();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var stop = timeout.Token;
        const string legacy = "/Game/Test/Legacy.Legacy";
        const string mapped = "/Game/Test/Mapped.Mapped";
        var project = Guid.NewGuid();
        var resource = Guid.NewGuid();
        var draw = model.Graph.CreateNode(new("ModelDraw"));
        model.Graph.SetParameter(draw, "assetPath", JsonSerializer.SerializeToElement(legacy));
        void SetMapping(Guid projectId) => model.Graph.SetResourceIdentityDocument(new ResourceIdentityDocument(1,
            [new(resource, "model", "Mapped model")],
            [new(new("Unreal", projectId), resource, "model", new("unreal.object-path", mapped))],
            [], [new(draw.Id, "assetPath", resource)], []), "Set test mapping");
        SetMapping(project);
        try
        {
            await host.StartAsync(model);
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("X-LiberaFx-Preview-Token", host.AccessToken);
            var fingerprint = Convert.FromHexString(host.Fingerprint);
            socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is not null && CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(certificate.GetRawCertData()), fingerprint);
            await socket.ConnectAsync(new Uri($"wss://localhost:{port}/preview?protocol=3"), stop);
            _ = await ReceiveAsync(socket, stop);
            await Send(new { type = "hello", engine = "Unreal", role = "editor", version = 1,
                capabilities = new[] { "rendering", "resourceIdentity" } });
            await ReplyProject();
            using (var probe = await ReceiveScene("resource_identity_probe"))
            {
                var request = probe.RootElement;
                Require(request.GetProperty("projectId").GetGuid() == project &&
                        request.GetProperty("binding").GetProperty("value").GetString() == mapped,
                    "Live query must use only the peer's project and explicit opaque binding.");
                await Send(new { type = "resource_identity_result", operation = "probe",
                    requestId = request.GetProperty("requestId").GetGuid(), status = "Resolved", actualKind = "model",
                    project = new { engine = "Unreal", projectId = project } });
            }
            Require(U16(await ReceiveAsync(socket, stop), 6) == (ushort)CompiledPreviewKind.Program,
                "A resolved project mapping should admit the program.");
            using (var scene = await ReceiveScene("render_config"))
                Require(scene.RootElement.GetProperty("nodes")[0].GetProperty("assetPath").GetString() == mapped,
                    "The rendered scene must use the live mapped path.");

            await Dispatcher.UIThread.InvokeAsync(() => SetMapping(Guid.NewGuid()));
            await ReplyProject();
            var failure = await ReceiveAsync(socket, stop);
            Require(U16(failure, 6) == (ushort)CompiledPreviewKind.CompileError &&
                    Encoding.UTF8.GetString(failure, 48, failure.Length - 48).Contains("Unregistered", StringComparison.Ordinal),
                "An unmapped project must receive a diagnostic rather than falling back to the legacy render path.");

            async Task Send(object message) => await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message),
                WebSocketMessageType.Text, true, stop);

            async Task<JsonDocument> ReceiveScene(string type)
            {
                var frame = await ReceiveAsync(socket, stop);
                Require(U16(frame, 6) == (ushort)CompiledPreviewKind.Scene, $"Expected scene message {type}.");
                var document = JsonDocument.Parse(frame.AsMemory(48));
                if (document.RootElement.GetProperty("type").GetString() == type) return document;
                document.Dispose();
                throw new InvalidOperationException("Unexpected scene message: " + type);
            }

            async Task ReplyProject()
            {
                using var request = await ReceiveScene("resource_identity_project");
                await Send(new { type = "resource_identity_result", operation = "status",
                    requestId = request.RootElement.GetProperty("requestId").GetGuid(), status = "Ready",
                    projectState = "Ready", project = new { engine = "Unreal", projectId = project } });
            }
        }
        finally
        {
            await host.StopAsync();
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

    private static async Task VerifyUnrealGpuPreviewAsync()
    {
        var executable = Environment.GetEnvironmentVariable("LIBERAFX_UNREAL_EDITOR")
            ?? throw new InvalidOperationException("Set LIBERAFX_UNREAL_EDITOR to UnrealEditor-Cmd.exe.");
        var project = Environment.GetEnvironmentVariable("LIBERAFX_UNREAL_PROJECT")
            ?? throw new InvalidOperationException("Set LIBERAFX_UNREAL_PROJECT to the preview-enabled .uproject.");
        var texture = Environment.GetEnvironmentVariable("LIBERAFX_UNREAL_TEXTURE")
            ?? "builtin:white";
        var useDiscovery = Environment.GetEnvironmentVariable("LIBERAFX_UNREAL_DISCOVERY") == "1";
        var verifyMappedModel = Environment.GetEnvironmentVariable("LIBERAFX_UNREAL_MAPPED_MODEL") == "1";
        const string mappedModelPath = "/Engine/BasicShapes/Cube.Cube";
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var host = new PreviewHost(port);
        var model = new MainViewModel();
        Process? native = null;
        PreviewDiscoveryPipe? discovery = null;
        try
        {
            var node = model.Graph.CreateNode(GpuParticleEditorService.Kind);
            GpuParticleEditorService.Apply(model.Graph, node.Id, [new GpuEdit
            {
                Action = "resource", Resource = GpuParticleEditorService.ReadProgram(node).Resources.Single(),
                AssetPath = texture
            }], requireValid: true);
            model.Playback.Loop = false;
            model.Playback.EndText = "6000";
            await host.StartAsync(model);
            if (useDiscovery)
                discovery = new PreviewDiscoveryPipe(host.ActualEndpoint, host.AccessToken, host.Fingerprint,
                    PreviewDiscoveryPipe.Prefix + Environment.ProcessId + ".integration");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            foreach (var argument in new[] { project, "/Engine/Maps/Entry", "-unattended", "-NoCrashDialog",
                "-nosplash", "-ExecCmds=Automation RunTests Libra.Editor.Preview." +
                    (useDiscovery ? "PostStartupDiscovery" : "GpuSceneTransactions"),
                "-TestExit=Automation Test Queue Empty" })
                start.ArgumentList.Add(argument);
            if (Environment.GetEnvironmentVariable("LIBERAFX_UNREAL_LOG") is { Length: > 0 } log)
                start.ArgumentList.Add("-AbsLog=" + log);
            if (useDiscovery)
            {
                start.ArgumentList.Add("-LibraPreviewDiscoveryPipe=" + discovery!.Name);
                foreach (var variable in new[] { "LIBRA_PREVIEW_PORT", "LIBRA_PREVIEW_TOKEN",
                    "LIBRA_PREVIEW_FINGERPRINT", "LIBRA_AUTOMATION_PREVIEW" })
                    start.Environment.Remove(variable);
            }
            else
            {
                start.ArgumentList.Add("-LibraPreview");
                start.Environment["LIBRA_PREVIEW_PORT"] = port.ToString();
                start.Environment["LIBRA_PREVIEW_TOKEN"] = host.AccessToken;
                start.Environment["LIBRA_PREVIEW_FINGERPRINT"] = host.Fingerprint;
            }
            native = Process.Start(start) ?? throw new InvalidOperationException("Unreal failed to start.");
            Guid? peer = null;
            Guid? mappedModel = null;
            JsonElement? lastStatus = null;
            JsonElement? lastRenderStatus = null;
            var modelRendered = !verifyMappedModel;
            var executed = false;
            var startedPlayback = false;
            var deadline = DateTime.UtcNow.AddSeconds(240);
            while (!native.HasExited && DateTime.UtcNow < deadline)
            {
                peer ??= model.PreviewTelemetry.Clients().FirstOrDefault(client =>
                    client.Engine == "Unreal" && client.Capabilities.Contains("gpuParticles"))?.ClientId;
                if (peer is { } id)
                {
                    if (!model.PreviewAssets.Supports(id, "gpuParticles")) break;
                    if (verifyMappedModel && mappedModel is null)
                    {
                        var projectState = await model.PreviewAssets.EngineProjectAsync(id);
                        var projectIdentity = projectState.Project;
                        Require(projectState.ProjectState == "Ready" && projectIdentity is not null,
                            "Initialize the isolated Unreal verification project's identity first: " + projectState.ProjectState);
                        mappedModel = await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            var draw = model.Graph.CreateNode(new("ModelDraw"));
                            model.Graph.SetParameter(draw, "assetPath", JsonSerializer.SerializeToElement(
                                "/Game/LibraVerification/MissingLegacyModel.MissingLegacyModel"));
                            var resource = Guid.NewGuid();
                            model.Graph.SetResourceIdentityDocument(new ResourceIdentityDocument(1,
                                [new(resource, "model", "Explicit mapped preview model")],
                                [new(projectIdentity!, resource, "model", new("unreal.object-path", mappedModelPath))],
                                [], [new(draw.Id, "assetPath", resource)], []), "Map isolated preview model");
                            return draw.Id;
                        });
                        if (Environment.GetEnvironmentVariable("LIBERAFX_UNREAL_MAPPED_PACKAGE") is { Length: > 0 } fixture)
                        {
                            var graph = await Dispatcher.UIThread.InvokeAsync(() => model.Graph.CreateSnapshot());
                            // Package verification uses an ordinary model, independently of GPU-only preview playback.
                            graph = graph with { Nodes = graph.Nodes.Where(item => item.Id == mappedModel).ToArray() };
                            var context = await PreviewResourceBindings.ResolveAsync(graph, model.PreviewAssets, id,
                                CancellationToken.None);
                            var package = SpawnableGroupPackage.Compile(graph, resourceContext: context);
                            File.WriteAllBytes(fixture, SpawnableGroupPackageBinary.Serialize(package));
                        }
                    }
                    if (!modelRendered && mappedModel is { } drawId)
                    {
                        lastRenderStatus = model.PreviewAssets.RenderStatus(id);
                        modelRendered = lastRenderStatus is { } render &&
                            render.GetProperty("errors").GetArrayLength() == 0 &&
                            render.GetProperty("nodes").EnumerateArray().Any(item =>
                                item.GetProperty("nodeId").GetGuid() == drawId &&
                                item.GetProperty("assetPath").GetString() == mappedModelPath &&
                                item.GetProperty("rendered").GetBoolean());
                    }
                    try { lastStatus = model.PreviewAssets.GpuStatus(id); }
                    catch (InvalidOperationException) when (!model.PreviewAssets.Supports(id, "gpuParticles")) { break; }
                    if (!startedPlayback && lastStatus is { } applied &&
                        applied.GetProperty("programs").EnumerateArray().Any(program =>
                            program.GetProperty("nodeId").GetGuid() == node.Id))
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => model.Playback.Play());
                        startedPlayback = true;
                    }
                    if (modelRendered && lastStatus is { } status && status.GetProperty("runtimeExecuted").GetBoolean() &&
                        status.GetProperty("errors").GetArrayLength() == 0 &&
                        status.GetProperty("programs").EnumerateArray().Any(program =>
                            program.GetProperty("nodeId").GetGuid() == node.Id))
                    {
                        executed = true;
                        break;
                    }
                }
                await Task.Delay(50);
            }
            Require(modelRendered, "Mapped model did not render on the real Unreal peer: " +
                (lastRenderStatus?.GetRawText() ?? "no render feedback"));
            Require(executed, "Actual Root-to-Unreal GPU delivery failed: " + (lastStatus?.GetRawText() ?? "no GPU feedback"));
            await Dispatcher.UIThread.InvokeAsync(() => model.Playback.Pause());
            var paused = false;
            deadline = DateTime.UtcNow.AddSeconds(3);
            while (!native.HasExited && DateTime.UtcNow < deadline)
            {
                var status = model.PreviewAssets.GpuStatus(peer!.Value);
                if (status is { } value && value.GetProperty("errors").GetArrayLength() == 0 &&
                    value.GetProperty("programs").EnumerateArray().Any(program =>
                        program.GetProperty("nodeId").GetGuid() == node.Id && !program.GetProperty("playing").GetBoolean()))
                {
                    paused = true;
                    break;
                }
                await Task.Delay(50);
            }
            Require(paused, "Remote pause did not reach the actual GPU preview.");
            using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await native.WaitForExitAsync(exitTimeout.Token);
            Require(native.ExitCode == 0, "Unreal preview automation did not exit cleanly.");
            Console.WriteLine("Actual Root-to-Unreal GPU publication, execution and feedback passed.");
            if (verifyMappedModel)
                Console.WriteLine("Real Unreal WSS peer rendered the project-mapped model instead of the legacy path.");
        }
        finally
        {
            discovery?.Dispose();
            await host.StopAsync();
            if (native is not null)
            {
                if (!native.HasExited)
                {
                    using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try { await native.WaitForExitAsync(shutdownTimeout.Token); }
                    catch (OperationCanceledException)
                    {
                        native.Kill(entireProcessTree: true);
                        await native.WaitForExitAsync();
                    }
                }
                native.Dispose();
            }
            await Dispatcher.UIThread.InvokeAsync(model.DisposeStorageFiles);
        }
    }

    private static async Task VerifyTransportAsync()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var host = new PreviewHost(port);
        var model = new MainViewModel();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = cancellation.Token;
        try
        {
            await host.StartAsync(model);
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("X-LiberaFx-Preview-Token", host.AccessToken);
            var fingerprint = Convert.FromHexString(host.Fingerprint);
            socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is not null && CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(certificate.GetRawCertData()), fingerprint);
            await socket.ConnectAsync(new Uri($"wss://localhost:{port}/preview?protocol=3"), token);
            var first = await ReceiveAsync(socket, token);
            Require(U16(first, 6) == 1 && first.AsSpan(48, 4).SequenceEqual("LFXB"u8),
                "Initial transport message must be a compiled program.");
            await socket.SendAsync(Encoding.UTF8.GetBytes(
                """{"type":"hello","engine":"Unreal","role":"runtime","version":1,"capabilities":["profiling","values"]}"""),
                WebSocketMessageType.Text, true, token);
            while (model.PreviewTelemetry.Clients().SingleOrDefault()?.Engine is null) await Task.Delay(10, token);
            await model.PreviewTelemetry.ProfileControlAsync(true, true, false, 500);
            var control = await ReceiveAsync(socket, token);
            Require(U16(control, 6) == 4 && U64(control, 8) == 0 && U64(control, 16) == 0,
                "A negotiated control must bypass the compiled sequence cursor.");
            // A real fragmented telemetry frame exceeds the old resync-only 256-byte buffer.
            var feedback = Encoding.UTF8.GetBytes(
                $$"""{"type":"values","programSequence":{{U64(first, 8)}},"evaluationTime":0.125,"values":[{"nodeId":"8e7416b4-1a9c-4ab1-aabe-409e4cf19d3f","components":[3.5]}],"padding":"{{new string('x', 600)}}"}""");
            await socket.SendAsync(feedback.AsMemory(0, 200), WebSocketMessageType.Text, false, token);
            await socket.SendAsync(feedback.AsMemory(200), WebSocketMessageType.Text, true, token);
            while (model.PreviewTelemetry.Clients().Single().Values is null) await Task.Delay(10, token);
            Require(model.PreviewTelemetry.Clients().Single().Values is { EvaluationTime: 0.125 } values &&
                values.ProgramSequence == (long)U64(first, 8) && values.Values.Single().Components.Single() == 3.5,
                "Fragmented native value feedback must retain exact metadata.");
            await Dispatcher.UIThread.InvokeAsync(() => model.Playback.Frame = 1);
            var tick = await ReceiveAsync(socket, token);
            Require(U16(tick, 6) == 2 && U64(tick, 16) == U64(first, 8),
                "Playback transport baseline is incorrect.");
            await Dispatcher.UIThread.InvokeAsync(() => model.Graph.CreateNode(GraphNodeKind.Float));
            var edit = await ReceiveAsync(socket, token);
            Require(U16(edit, 6) == 1, "Graph edit must send a program.");
            await Dispatcher.UIThread.InvokeAsync(() => model.Graph.CreateNode(
                GraphNodeDefinition.Core.Single(definition => definition.Name == "Bool").Kind));
            var failure = await ReceiveAsync(socket, token);
            Require(U16(failure, 6) == 3, "Unsupported node must send a compile error.");
            await socket.SendAsync(Encoding.UTF8.GetBytes("""{"type":"resync"}"""), WebSocketMessageType.Text, true, token);
            var failedResync = await ReceiveAsync(socket, token);
            Require(U16(failedResync, 6) == 3 && U64(failedResync, 8) == U64(failure, 8),
                "Resync of an invalid graph must return its error.");
            await Dispatcher.UIThread.InvokeAsync(() => model.Graph.History.Undo());
            var recovered = await ReceiveAsync(socket, token);
            Require(U16(recovered, 6) == 1, "Undo must restore a compiled program.");
            await socket.SendAsync(Encoding.UTF8.GetBytes("""{"type":"resync"}"""), WebSocketMessageType.Text, true, token);
            var resync = await ReceiveAsync(socket, token);
            Require(U16(resync, 6) == 1 && U64(resync, 8) == U64(recovered, 8),
                "Resync must return the latest complete program.");
            await Dispatcher.UIThread.InvokeAsync(() => model.NewDocumentCommand.Execute(null));
            var switched = await ReceiveAsync(socket, token);
            Require(U16(switched, 6) == 1, "Switching documents must send a program.");
            using var gpuSocket = new ClientWebSocket();
            gpuSocket.Options.SetRequestHeader("X-LiberaFx-Preview-Token", host.AccessToken);
            gpuSocket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is not null && CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(certificate.GetRawCertData()), fingerprint);
            await gpuSocket.ConnectAsync(new Uri($"wss://localhost:{port}/preview?protocol=3"), token);
            Require(U16(await ReceiveAsync(gpuSocket, token), 6) == 1,
                "A newly connected GPU viewer must first receive the current parent program.");
            await gpuSocket.SendAsync(Encoding.UTF8.GetBytes(
                """{"type":"hello","engine":"Unreal","role":"editor","version":1,"capabilities":["assets","rendering","gpuParticles"]}"""),
                WebSocketMessageType.Text, true, token);
            Require(U16(await ReceiveAsync(gpuSocket, token), 6) == 1,
                "Negotiating GPU preview must resend the parent program before scene configuration.");
            Require(U16(await ReceiveAsync(gpuSocket, token), 6) == 5, "CPU render bindings must remain available.");
            using (var emptyGpu = JsonDocument.Parse((await ReceiveAsync(gpuSocket, token)).AsMemory(48)))
                Require(emptyGpu.RootElement.GetProperty("type").GetString() == "gpu_config" &&
                    emptyGpu.RootElement.GetProperty("programs").GetArrayLength() == 0,
                    "A GPU-capable viewer must receive an empty replacement for an empty document.");
            await Dispatcher.UIThread.InvokeAsync(() => model.Graph.CreateNode(GpuParticleEditorService.Kind));
            var gpuProgram = await ReceiveAsync(gpuSocket, token);
            Require(U16(gpuProgram, 6) == 1 && U16(await ReceiveAsync(gpuSocket, token), 6) == 5,
                "GPU authoring must preserve compiled numeric program and CPU scene ordering.");
            using (var gpuScene = JsonDocument.Parse((await ReceiveAsync(gpuSocket, token)).AsMemory(48)))
                Require(gpuScene.RootElement.GetProperty("programSequence").GetUInt64() == U64(gpuProgram, 8) &&
                    gpuScene.RootElement.GetProperty("programs").GetArrayLength() == 1,
                    "Actual WebSocket GPU delivery must match the accepted parent program sequence.");
            await Dispatcher.UIThread.InvokeAsync(() => model.Playback.Frame = 2);
            Require(U16(await ReceiveAsync(gpuSocket, token), 6) == 2,
                "Playback must not resend or reset GPU programs.");
            gpuSocket.Abort();
            try
            {
                await socket.SendAsync(new byte[PreviewTelemetryStore.MaxFrameBytes + 1], WebSocketMessageType.Text, true, token);
            }
            catch (WebSocketException) { /* The receiver may abort before this oversized send finishes. */ }
            while (model.PreviewTelemetry.Clients().Count != 0) await Task.Delay(10, token);
            Require(model.PreviewTelemetry.Clients().Count == 0, "Oversized incoming frames must disconnect and remove feedback.");
            socket.Abort();
        }
        finally
        {
            await host.StopAsync();
            await Dispatcher.UIThread.InvokeAsync(model.DisposeStorageFiles);
        }
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
