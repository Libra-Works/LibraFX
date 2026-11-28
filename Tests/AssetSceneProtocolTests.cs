using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Threading;
using LiberaFx;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Preview;
using LiberaFx.Services.Samples;
using LiberaFx.ViewModels.Assets;
using LiberaFx.ViewModels.Main;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Resources;

internal static class AssetSceneProtocolTests
{
    public static void Run()
    {
        VerifyConnectionInvitation();
        RunWithDispatcher(VerifyQueriesAsync());
        RunWithDispatcher(VerifyResourceIdentityAuthoringLoopbackAsync());
        VerifyAssetWindowRequestLifetimeAsync().GetAwaiter().GetResult();
        VerifyLocalDiscoveryAsync().GetAwaiter().GetResult();
    }

    private static async Task VerifyResourceIdentityAuthoringLoopbackAsync()
    {
        if (Application.Current is null)
            AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
        var projectId = Guid.NewGuid();
        var main = new MainViewModel();
        using var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        using var preview = new PreviewHost(port);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-LiberaFx-Preview-Token", preview.AccessToken);
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is not null && string.Equals(
                Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())),
                preview.Fingerprint, StringComparison.OrdinalIgnoreCase);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task? editor = null;
        Guid peer = Guid.Empty;
        try
        {
            await preview.StartAsync(main);
            await socket.ConnectAsync(new Uri($"wss://127.0.0.1:{port}/preview?protocol=3"), stop.Token);
            await socket.SendAsync(Encoding.UTF8.GetBytes(
                """{"type":"hello","engine":"Unity","role":"editor","version":1,"capabilities":["resourceIdentity"],"viewerName":"Unity loopback"}"""),
                WebSocketMessageType.Text, true, stop.Token);
            editor = FakeUnityEditorLoopAsync(socket, projectId, stop.Token);
            for (var attempt = 0; attempt < 200 && main.PreviewTelemetry.Clients().Count == 0; attempt++)
                await Task.Delay(10, stop.Token);
            peer = main.PreviewTelemetry.Clients().Single().ClientId;

            var node = RenderNodeAuthoring.CreateModel(main.Graph,
                new EngineAsset("/Game/Legacy/Model.Model", "Legacy model", "model", "Unity"));
            using var pane = new ResourceIdentityPaneViewModel(main);
            await pane.RefreshProjectCommand.ExecuteAsync(null);
            Require(pane.ProjectState?.Project == new EngineProjectIdentity("Unity", projectId),
                "The authoring pane must acquire the selected editor's persistent project identity.");
            pane.SelectedTargetNode = pane.TargetNodes.Single(target => target.Id == node.Id);
            pane.SelectedLocation = pane.Locations.Single(location => location.Id == "assetPath");
            pane.ResourceName = "Registered model";
            pane.ResourceKind = "model";
            pane.BindingValue = "a".PadLeft(32, '0') + ":12345";
            await pane.RegisterAndAssignCommand.ExecuteAsync(null);

            var identity = main.Graph.ResourceIdentities;
            if (identity is not { Resources.Count: 1, ResourceBindings.Count: 1, Uses.Count: 1 })
                throw new InvalidOperationException(
                    $"The UI did not persist its resource identity and graph use (status: '{pane.Status}', " +
                    $"CanRegister: {pane.CanRegister}, project status: '{pane.ProjectState?.Status}', " +
                    $"target: '{pane.SelectedTargetNode?.Id}', location: '{pane.SelectedLocation?.Id}').");
            Require(identity.Uses[0].NodeId == node.Id && identity.Uses[0].Location == "assetPath" &&
                identity.Uses[0].ResourceId == identity.Resources[0].Id &&
                main.Graph.GetNode(node.Id).Parameters!.Single(parameter => parameter.Id == "assetPath")
                    .Value.GetString() == "/Game/Legacy/Model.Model",
                "The UI must query an explicit opaque binding, create an identity/use and preserve the original graph value.");

            await pane.DeliverUnityBindingsCommand.ExecuteAsync(null);
            Require(pane.Status.Contains("Assets/LiberaFx/LiberaFxBindings.asset", StringComparison.Ordinal),
                "The UI delivery command must receive confirmation of the Unity binding asset path.");
            main.Graph.History.Undo();
            Require(main.Graph.ResourceIdentities is null &&
                main.Graph.GetNode(node.Id).Parameters!.Single(parameter => parameter.Id == "assetPath")
                    .Value.GetString() == "/Game/Legacy/Model.Model",
                "Undoing a migrated identity use must retain the legacy graph value.");
            main.Graph.History.Redo();
            Require(main.Graph.ResourceIdentities?.Uses.Single().ResourceId == identity.Resources[0].Id,
                "Redo must restore the exact resource identity and use.");
        }
        finally
        {
            stop.Cancel();
            socket.Abort();
            if (editor is not null)
                try { await editor; }
                catch (OperationCanceledException) { }
                catch (WebSocketException) { }
                catch (ObjectDisposedException) { }
            await preview.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (peer != Guid.Empty) main.PreviewAssets.Disconnect(peer);
            main.DisposeStorageFiles();
        }

        static async Task FakeUnityEditorLoopAsync(ClientWebSocket client, Guid project, CancellationToken cancellationToken)
        {
            var buffer = new byte[2 * 1024 * 1024];
            while (!cancellationToken.IsCancellationRequested && client.State == WebSocketState.Open)
            {
                var length = 0;
                WebSocketReceiveResult result;
                do
                {
                    result = await client.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length),
                        cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    length += result.Count;
                    if (length == buffer.Length) return;
                } while (!result.EndOfMessage);
                if (result.MessageType != WebSocketMessageType.Binary || length < CompiledPreviewProtocol.HeaderSize ||
                    BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(6)) != (ushort)CompiledPreviewKind.Scene)
                    continue;
                var payloadLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(44)));
                if (payloadLength <= 0 || payloadLength != length - CompiledPreviewProtocol.HeaderSize) continue;
                using var command = JsonDocument.Parse(buffer.AsMemory(CompiledPreviewProtocol.HeaderSize, payloadLength));
                var root = command.RootElement;
                var type = root.GetProperty("type").GetString();
                if (type is not ("resource_identity_project" or "resource_identity_probe" or
                    "resource_identity_deliver")) continue;
                var requestId = root.GetProperty("requestId").GetGuid();
                object response = type switch
                {
                    "resource_identity_project" => new
                    {
                        type = "resource_identity_result", requestId,
                        operation = root.GetProperty("operation").GetString(),
                        status = "Ready", projectState = "Ready",
                        project = new { engine = "Unity", projectId = project.ToString("D") },
                        detail = "Unity test project is ready."
                    },
                    "resource_identity_probe" => Probe(root, requestId, project),
                    "resource_identity_deliver" => new
                    {
                        type = "resource_identity_result", requestId, operation = "deliver",
                        status = "Resolved", assetPath = "Assets/LiberaFx/LiberaFxBindings.asset"
                    },
                    _ => throw new InvalidOperationException("Unexpected resource identity request.")
                };
                await client.SendAsync(JsonSerializer.SerializeToUtf8Bytes(response),
                    WebSocketMessageType.Text, true, cancellationToken);
            }

            static object Probe(JsonElement request, Guid requestId, Guid project)
            {
                var binding = request.GetProperty("binding");
                Require(binding.GetProperty("format").GetString() == "unity.asset-guid-local-id.v1" &&
                    binding.GetProperty("value").GetString() == "0000000000000000000000000000000a:12345",
                    "Resource registration must probe the exact author-supplied Unity GUID/local-file-ID binding.");
                return new
                {
                    type = "resource_identity_result", requestId, operation = "probe",
                    status = "Resolved", actualKind = "model",
                    project = new { engine = "Unity", projectId = project.ToString("D") },
                    detail = "Exact Unity object resolved."
                };
            }
        }
    }

    private static async Task VerifyLocalDiscoveryAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var name = PreviewDiscoveryPipe.Prefix + Environment.ProcessId + ".test." + Guid.NewGuid().ToString("N");
        var token = new string('c', 64);
        using var server = new PreviewDiscoveryPipe("wss://127.0.0.1:19427/preview",
            token, new string('d', 64), name);
        async Task<string> Exchange(string request)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await client.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            return await reader.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
        }
        Require(await Exchange(new string('x', PreviewDiscoveryPipe.Request.Length)).ConfigureAwait(false) == "",
            "Malformed discovery requests must not disclose preview credentials.");
        for (var i = 0; i < 2; i++)
        {
            using var invitation = JsonDocument.Parse(await Exchange(PreviewDiscoveryPipe.Request).ConfigureAwait(false));
            var root = invitation.RootElement;
            Require(root.GetProperty("type").GetString() == "librafx_preview_connection" &&
                root.GetProperty("port").GetInt32() == 19427 && root.GetProperty("accessToken").GetString() == token &&
                !root.TryGetProperty("mcpToken", out _), "Local discovery must return preview-only invitations and remain reusable.");
        }
        server.Dispose();
        server.Dispose();
    }

    private static async Task VerifyAssetWindowRequestLifetimeAsync()
    {
        var main = new MainViewModel();
        var peer = Guid.NewGuid();
        main.PreviewTelemetry.Connect(peer, 3, bytes =>
        {
            using var command = JsonDocument.Parse(bytes.AsMemory(CompiledPreviewProtocol.HeaderSize));
            var request = command.RootElement.GetProperty("requestId").GetGuid();
            Require(main.PreviewAssets.Receive(peer, JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "asset_result", requestId = request, operation = "catalog",
                total = 0, offset = 0, assets = Array.Empty<object>(), error = (string?)null
            })), "Asset catalog test response must complete its matching request.");
            return Task.CompletedTask;
        });
        Require(main.PreviewTelemetry.Receive(peer, Encoding.UTF8.GetBytes(
            """{"type":"hello","engine":"Unreal","role":"editor","version":1,"capabilities":["assets"]}""")),
            "Asset browser lifetime test must connect an asset-capable editor.");
        try
        {
            using var browser = new EngineAssetsWindowViewModel(main);
            await browser.SearchCommand.ExecuteAsync(null);
            await browser.SearchCommand.ExecuteAsync(null);
            browser.Dispose();
            browser.Dispose();
            await browser.SearchCommand.ExecuteAsync(null);
        }
        finally
        {
            main.PreviewTelemetry.Disconnect(peer);
            main.PreviewAssets.Disconnect(peer);
            main.DisposeStorageFiles();
        }
    }

    private static void VerifyConnectionInvitation()
    {
        var token = new string('a', 64);
        var fingerprint = new string('b', 64);
        using var invitation = JsonDocument.Parse(PreviewConnectionInvitation.Create(
            "wss://<editor-ip>:19426/preview", token, fingerprint));
        var root = invitation.RootElement;
        Require(root.GetProperty("type").GetString() == "librafx_preview_connection" &&
            root.GetProperty("host").GetString() == "127.0.0.1" &&
            root.GetProperty("port").GetInt32() == 19426 &&
            root.GetProperty("accessToken").GetString() == token &&
            root.GetProperty("certificateFingerprint").GetString() == fingerprint,
            "Manual preview invitations must use the configured TLS preview endpoint and credentials.");
        try
        {
            PreviewConnectionInvitation.Create("ws://localhost:19426/preview", token, fingerprint);
            throw new Exception("Insecure preview invitations must be rejected.");
        }
        catch (ArgumentException) { }
    }

    private static async Task VerifyQueriesAsync()
    {
        var telemetry = new PreviewTelemetryStore();
        var assets = new PreviewAssetStore(telemetry);
        var peer = Guid.NewGuid();
        var otherPeer = Guid.NewGuid();
        byte[]? reconnectReply = null;
        var sawQuery = false;
        telemetry.Connect(peer, 3, bytes =>
        {
            Require(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)) == 5 &&
                bytes.AsSpan(8, 36).IndexOfAnyExcept((byte)0) < 0, "Asset requests must be out-of-band scene frames.");
            using var command = JsonDocument.Parse(bytes.AsMemory(CompiledPreviewProtocol.HeaderSize));
            var request = command.RootElement.GetProperty("requestId").GetGuid();
            var type = command.RootElement.GetProperty("type").GetString();
            sawQuery = true;
            if (type == "reconnect")
            {
                reconnectReply = JsonSerializer.SerializeToUtf8Bytes(
                    new { type = "asset_result", requestId = request, operation = "reconnect", error = (string?)null });
                return Task.CompletedTask;
            }
            var response = new { type = "asset_result", requestId = request, operation = "catalog", total = 1, offset = 0,
                assets = new[] { new { path = "/Engine/BasicShapes/Cube.Cube", name = "Cube", assetClass = "model", engine = "Unreal" } },
                error = (string?)null };
            Require(assets.Receive(peer, JsonSerializer.SerializeToUtf8Bytes(response)), "Matching replies must complete requests.");
            return Task.CompletedTask;
        }, "127.0.0.1");
        Require(telemetry.Receive(peer, Encoding.UTF8.GetBytes(
            """{"type":"hello","engine":"Unreal","role":"editor","version":1,"capabilities":["assets","rendering","gpuParticles"],"viewerName":"Test preview","engineVersion":"5.7.4","platform":"Windows"}""")),
            "Asset editor handshake failed.");
        var page = await assets.CatalogAsync("Cube", "model");
        Require(sawQuery && page.Assets.Single().Path == "/Engine/BasicShapes/Cube.Cube" &&
            telemetry.Clients().Single().ViewerName == "Test preview", "Real reply fields and viewer identity must be retained.");
        telemetry.Connect(otherPeer, 3, _ => Task.CompletedTask);
        Require(telemetry.Receive(otherPeer, Encoding.UTF8.GetBytes(
            """{"type":"hello","version":1,"engine":"Unreal","role":"editor","capabilities":["assets"]}""")),
            "The second editor must negotiate the same asset capability.");
        var reconnect = assets.ReconnectAsync(peer);
        Require(reconnectReply is not null && !reconnect.IsCompleted,
            "Sending the reconnect command must not report a remote acknowledgement.");
        Require(!assets.Receive(otherPeer, reconnectReply!) && !reconnect.IsCompleted,
            "An acknowledgement from another editor must not complete the request.");
        Require(assets.Receive(peer, reconnectReply!), "The selected editor must be able to acknowledge reconnect.");
        await reconnect.WaitAsync(TimeSpan.FromSeconds(2));
        telemetry.Disconnect(otherPeer);
        var nodeId = Guid.NewGuid();
        byte[] GpuStatus(long sequence) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "gpu_status", version = 1, programSequence = sequence, runtimeExecuted = true,
            mode = "GpuRegisterInterpreter", programs = new[] { new { nodeId, contentHash = "test",
                capacity = 4096, playing = true } }, errors = Array.Empty<string>()
        });
        Require(assets.Receive(peer, GpuStatus(8)) && assets.Receive(peer, GpuStatus(7)) &&
            assets.GpuStatus(peer)!.Value.GetProperty("programSequence").GetInt64() == 8,
            "GPU feedback must be retained without allowing stale status to replace newer state.");
        var second = Guid.NewGuid();
        telemetry.Connect(second, 3, _ => Task.CompletedTask);
        Require(telemetry.Receive(second, Encoding.UTF8.GetBytes(
            """{"type":"hello","engine":"Unreal","role":"editor","version":1,"capabilities":["assets"]}""")), "Second editor handshake failed.");
        try { assets.SelectPeer(); throw new Exception("Multiple asset editors must require an explicit selection."); }
        catch (InvalidOperationException) { }
        Require(assets.SelectPeer(peer) == peer, "Explicit requests must retain their target.");
        var gpuOnly = Guid.NewGuid();
        telemetry.Connect(gpuOnly, 3, _ => Task.CompletedTask);
        Require(telemetry.Receive(gpuOnly, Encoding.UTF8.GetBytes(
            """{"type":"hello","engine":"Unreal","role":"editor","version":1,"capabilities":["gpuParticles"]}""")) &&
            assets.Receive(gpuOnly, GpuStatus(9)) &&
            assets.GpuStatus(gpuOnly)!.Value.GetProperty("programSequence").GetInt64() == 9,
            "GPU-only preview peers must not require unrelated asset catalog capabilities.");
        foreach (var path in new[] { @"C:\secret.png", "/Game/../Engine/Secret.Secret", "/Game/a\file" })
        {
            try { PreviewAssetStore.ValidateAssetPath(path); throw new Exception("Invalid paths must be rejected."); }
            catch (ArgumentException) { }
        }
        try
        {
            CompiledPreviewProtocol.CreateScene(Encoding.UTF8.GetBytes("""{"type":"reconnect","type":"asset_catalog"}"""));
            throw new Exception("Duplicate scene command fields must be rejected.");
        }
        catch (ArgumentException) { }
        telemetry.Disconnect(peer);
        assets.Disconnect(peer);
        try { assets.SelectPeer(peer); throw new Exception("A disconnected selection must not redirect to another editor."); }
        catch (InvalidOperationException) { }
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static void RunWithDispatcher(Task task, int timeoutSeconds = 35)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var registration = timeout.Token.Register(() =>
                Dispatcher.UIThread.Post(() => frame.Continue = false));
            _ = task.ContinueWith(_ =>
                Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
        }
        if (!task.IsCompleted)
        {
            throw new TimeoutException("Preview asset protocol check timed out.");
        }
        task.GetAwaiter().GetResult();
    }
}
