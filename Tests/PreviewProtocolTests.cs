using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;

internal static class PreviewProtocolTests
{
    public static void Run()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var scalar = graph.CreateNode(GraphNodeKind.Float);
        var original = new PreviewPublication(1, "test-document", graph.CreateSnapshot(), GraphNodeDefinition.All);
        graph.SetParameter(scalar, "value", JsonSerializer.SerializeToElement(4d));
        var latest = new PreviewPublication(30, "test-document", graph.CreateSnapshot(), GraphNodeDefinition.All);
        using (var delta = JsonDocument.Parse(PreviewProtocol.Encode(latest, original)))
        {
            var root = delta.RootElement;
            if (root.GetProperty("type").GetString() != "parameters" ||
                root.GetProperty("baseSequence").GetInt64() != 1 || root.GetProperty("sequence").GetInt64() != 30 ||
                root.GetProperty("changes").GetArrayLength() != 1 ||
                root.GetProperty("changes")[0].GetProperty("value").GetDouble() != 4)
                throw new InvalidOperationException("Parameter patches were not based on the actual sent baseline.");
        }
        using (var reset = JsonDocument.Parse(PreviewProtocol.Encode(
                   new PreviewPublication(31, "test-document", original.Graph, GraphNodeDefinition.All), latest)))
            if (!reset.RootElement.GetProperty("changes")[0].GetProperty("remove").GetBoolean())
                throw new InvalidOperationException("Undoing a parameter override did not reset it to its definition default.");
        foreach (var bytes in new[]
                 {
                     PreviewProtocol.Encode(latest, original, true),
                     PreviewProtocol.Encode(new PreviewPublication(31, "other-document", latest.Graph, GraphNodeDefinition.All), latest)
                 })
        {
            using var snapshot = JsonDocument.Parse(bytes);
            if (snapshot.RootElement.GetProperty("type").GetString() != "snapshot")
                throw new InvalidOperationException("Resynchronization or document switching did not send a full snapshot.");
        }
        graph.CreateNode(GraphNodeKind.Time);
        using (var structural = JsonDocument.Parse(PreviewProtocol.Encode(
                   new PreviewPublication(32, "test-document", graph.CreateSnapshot(), GraphNodeDefinition.All), latest)))
            if (structural.RootElement.GetProperty("type").GetString() != "snapshot")
                throw new InvalidOperationException("A structural graph change was incorrectly sent as a parameter patch.");
        RunWithDispatcher(VerifyTransportAsync());
    }

    private static void RunWithDispatcher(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            using var registration = timeout.Token.Register(() => Dispatcher.UIThread.Post(() => frame.Continue = false));
            _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
        }
        if (!task.IsCompleted) throw new TimeoutException("The isolated preview protocol check did not stop.");
        task.GetAwaiter().GetResult();
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
            await socket.ConnectAsync(new Uri($"wss://localhost:{port}/preview?protocol=2"), token);
            using var first = await ReceiveAsync(socket, token);
            if (first.RootElement.GetProperty("type").GetString() != "snapshot")
                throw new InvalidOperationException("A new v2 preview client did not receive a full snapshot.");
            var id = await Dispatcher.UIThread.InvokeAsync(() => model.Graph.CreateNode(GraphNodeKind.Float).Id);
            using var created = await ReceiveAsync(socket, token);
            await Dispatcher.UIThread.InvokeAsync(() => model.Graph.CreateNode(GraphNodeKind.GraphGroup));
            using var grouped = await ReceiveAsync(socket, token);
            var sequence = grouped.RootElement.GetProperty("sequence").GetInt64();
            var beforeInlineFocus = sequence;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var document = model.ActiveDocument!.Workspace;
                var group = document.Nodes.Single(node => node.Definition.Kind == GraphNodeKind.GraphGroup);
                document.OpenInlineGraph(group).ActivateEditingWorkspace();
                for (var value = 1; value <= 40; value++)
                    document.SetParameter(document.GetNode(id), "value", JsonSerializer.SerializeToElement((double)value));
            });
            while (true)
            {
                using var message = await ReceiveAsync(socket, token);
                var root = message.RootElement;
                if (root.GetProperty("type").GetString() != "parameters" ||
                    root.GetProperty("baseSequence").GetInt64() != sequence)
                    throw new InvalidOperationException("A slow preview client lost its parameter patch baseline.");
                sequence = root.GetProperty("sequence").GetInt64();
                if (root.GetProperty("changes").EnumerateArray().Any(change => change.GetProperty("value").GetDouble() == 40))
                    break;
            }
            if (sequence != beforeInlineFocus + 40)
                throw new InvalidOperationException("Inline focus reset or republished an unchanged preview document.");
            await socket.SendAsync(Encoding.UTF8.GetBytes("""{"type":"resync"}"""), WebSocketMessageType.Text, true, token);
            using var resync = await ReceiveAsync(socket, token);
            if (resync.RootElement.GetProperty("type").GetString() != "snapshot" ||
                resync.RootElement.GetProperty("sequence").GetInt64() != sequence)
                throw new InvalidOperationException("An explicit resync did not recover the latest complete state.");
            socket.Abort();
        }
        finally
        {
            await host.StopAsync();
            await Dispatcher.UIThread.InvokeAsync(model.DisposeStorageFiles);
        }
        VerifyPackageParameterDelta();
        await VerifyUnrealPackagePreviewDoesNotResendProgramAsync();
    }

    private static void VerifyPackageParameterDelta()
    {
        var fountainPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Assets", "Samples", "CpuFountain.lfxproj");
        if (!File.Exists(fountainPath))
            fountainPath = Path.GetFullPath(Path.Combine("Assets", "Samples", "CpuFountain.lfxproj"));
        using var stream = File.OpenRead(fountainPath);
        var snapshot = Task.Run(() => ProjectDocumentReader.LoadAsync(stream)).GetAwaiter().GetResult().Graph;
        var package = LiberaFx.Services.Compilation.SpawnableGroupPackage.Compile(snapshot, includePreviewSignalKeys: true);
        var bytes = LiberaFx.Services.Compilation.SpawnableGroupPackageBinary.Serialize(package);
        if (bytes.Length < 16 || !bytes.AsSpan(0, 4).SequenceEqual("LFXP"u8))
            throw new InvalidOperationException("CpuFountain did not compile to a valid LFXP package.");

        var beamPath = Path.Combine(Path.GetDirectoryName(fountainPath)!, "ProceduralBeam.lfxproj");
        using var beamStream = File.OpenRead(beamPath);
        var beamSnapshot = Task.Run(() => ProjectDocumentReader.LoadAsync(beamStream)).GetAwaiter().GetResult().Graph;
        var beamPackage = LiberaFx.Services.Compilation.SpawnableGroupPackage.Compile(beamSnapshot, includePreviewSignalKeys: true);
        var beamBytes = LiberaFx.Services.Compilation.SpawnableGroupPackageBinary.Serialize(beamPackage);
        if (beamBytes.Length < 16 || !beamBytes.AsSpan(0, 4).SequenceEqual("LFXP"u8))
            throw new InvalidOperationException("ProceduralBeam did not compile to a valid LFXP package.");
        using (var beamScene = JsonDocument.Parse(beamPackage.RootRenderConfiguration))
        {
            foreach (var proc in beamScene.RootElement.GetProperty("proceduralResources").EnumerateArray())
            {
                var procKind = proc.GetProperty("kind").GetString();
                var stages = procKind == "ProceduralModel"
                    ? proc.GetProperty("recipe").GetProperty("modifiers").GetArrayLength()
                    : proc.GetProperty("recipe").GetProperty("operations").GetArrayLength();
                var blocks = procKind == "ProceduralModel" ? 4 : 3;
                if (proc.GetProperty("inputs").GetArrayLength() != stages * blocks)
                    throw new InvalidOperationException($"Procedural resource {procKind} input block count mismatch.");
            }
        }

        var floatDef = LiberaFx.Models.Graph.GraphNodeDefinition.Find(new LiberaFx.Models.Graph.GraphNodeKind("Float"))!;
        var floatNode = new LiberaFx.Models.Graph.GraphNodeSnapshot(
            Guid.NewGuid(),
            floatDef.Kind,
            "Speed",
            0,
            0,
            floatDef.PortType,
            floatDef.PortCategory,
            null,
            null,
            [new LiberaFx.Models.Graph.NodeParameterSnapshot("value", "float", JsonSerializer.SerializeToElement(10.0))]);
        var baseGraph = new LiberaFx.Models.Graph.GraphSnapshot([floatNode], []);
        var editedGraph = new LiberaFx.Models.Graph.GraphSnapshot(
            [floatNode with { Parameters = [new LiberaFx.Models.Graph.NodeParameterSnapshot("value", "float", JsonSerializer.SerializeToElement(42.0))] }],
            []);
        if (!PackageParameterDelta.TryCreatePayload(baseGraph, editedGraph, 123, out var deltaBytes) || deltaBytes is null)
            throw new InvalidOperationException("PackageParameterDelta did not emit a delta payload for Float value.");
        var frameText = Encoding.UTF8.GetString(deltaBytes.AsSpan(LiberaFx.Services.CompiledPreviewProtocol.HeaderSize));
        using var doc = JsonDocument.Parse(frameText);
        if (doc.RootElement.GetProperty("type").GetString() != "parameter_delta" ||
            doc.RootElement.GetProperty("updates").GetArrayLength() == 0)
            throw new InvalidOperationException("PackageParameterDelta payload did not contain parameter_delta updates.");
    }

    private static async Task VerifyUnrealPackagePreviewDoesNotResendProgramAsync()
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
            await socket.ConnectAsync(new Uri($"wss://localhost:{port}/preview?protocol=3&unrealPackagePreview=1"), token);
            var hello = Encoding.UTF8.GetBytes("""
                {"type":"hello","engine":"Unreal","role":"editor","version":1,"capabilities":["unrealPackagePreview"]}
                """);
            await socket.SendAsync(hello, WebSocketMessageType.Text, true, token);

            static async Task<byte[]> ReceiveBinaryFrameAsync(ClientWebSocket ws, CancellationToken ct)
            {
                while (true)
                {
                    using var ms = new MemoryStream();
                    var buf = new byte[16384];
                    ValueWebSocketReceiveResult res;
                    do
                    {
                        res = await ws.ReceiveAsync(buf.AsMemory(), ct);
                        ms.Write(buf, 0, res.Count);
                    } while (!res.EndOfMessage);
                    var bytes = ms.ToArray();
                    if (bytes.Length >= CompiledPreviewProtocol.HeaderSize && bytes[6] != (byte)CompiledPreviewKind.Control)
                        return bytes;
                }
            }

            var first = await ReceiveBinaryFrameAsync(socket, token);
            if ((CompiledPreviewKind)first[6] != CompiledPreviewKind.Program)
                throw new InvalidOperationException("Initial unrealPackagePreview frame was not Program.");

            await Dispatcher.UIThread.InvokeAsync(() => model.Playback.Frame = 5);
            var second = await ReceiveBinaryFrameAsync(socket, token);
            if ((CompiledPreviewKind)second[6] != CompiledPreviewKind.Playback)
            {
                var seq1 = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(first.AsSpan(8));
                var seq2 = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(second.AsSpan(8));
                throw new InvalidOperationException($"Playback update resent {(CompiledPreviewKind)second[6]} (seq1={seq1}, seq2={seq2}) instead of Playback for unrealPackagePreview.");
            }

            socket.Abort();
        }
        finally
        {
            await host.StopAsync();
            await Dispatcher.UIThread.InvokeAsync(model.DisposeStorageFiles);
        }
    }

    private static async Task<JsonDocument> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellation)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), cancellation);
            if (result.MessageType != WebSocketMessageType.Text || stream.Length + result.Count > 16 * 1024 * 1024)
                throw new InvalidDataException("Invalid isolated preview response.");
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return JsonDocument.Parse(stream.ToArray());
    }
}
