using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
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
        graph.CreateNode(GraphNodeKind.Int32);
        var invalid = graph.CreateSnapshot();
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
        if (!task.IsCompleted) throw new TimeoutException("Compiled preview transport timed out.");
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
            await Dispatcher.UIThread.InvokeAsync(() => model.Graph.CreateNode(GraphNodeKind.Int32));
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
