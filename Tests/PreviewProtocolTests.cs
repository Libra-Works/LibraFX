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
        VerifyPackageParameterDelta();
        RunWithDispatcher(VerifyUnrealPackagePreviewDoesNotResendProgramAsync());
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

    private static void VerifyPackageParameterDelta()
    {
        var fountainPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Samples", "CpuFountain.lfxproj");
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
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(deltaBytes.AsSpan(8)) != 123)
        {
            throw new InvalidOperationException("Parameter delta frame sequence was not preserved.");
        }
        var resetGraph = new LiberaFx.Models.Graph.GraphSnapshot([floatNode with { Parameters = [] }], []);
        if (!PackageParameterDelta.TryCreatePayload(editedGraph, resetGraph, 124, out var resetBytes) || resetBytes is null)
        {
            throw new InvalidOperationException("Removing a parameter did not restore its authoring default.");
        }
        using (var reset = JsonDocument.Parse(resetBytes.AsMemory(LiberaFx.Services.CompiledPreviewProtocol.HeaderSize)))
        {
            var value = reset.RootElement.GetProperty("updates")[0].GetProperty("floatValue").GetDouble();
            if (value != floatDef.Parameters.Single(item => item.Id == "value").DefaultValue.GetDouble())
            {
                throw new InvalidOperationException("Parameter removal did not transmit the definition default.");
            }
        }
        var groupDef = LiberaFx.Models.Graph.GraphNodeDefinition.Find(LiberaFx.Models.Graph.GraphNodeKind.GraphGroup)!;
        var group = new LiberaFx.Models.Graph.GraphNodeSnapshot(Guid.NewGuid(), groupDef.Kind, "Group", 0, 0,
            groupDef.PortType, groupDef.PortCategory, ChildGraph: baseGraph);
        var nestedOld = new LiberaFx.Models.Graph.GraphSnapshot([group], []);
        var nestedNew = new LiberaFx.Models.Graph.GraphSnapshot([group with { ChildGraph = editedGraph }], []);
        if (!PackageParameterDelta.TryCreatePayload(nestedOld, nestedNew, 125, out var nestedBytes) || nestedBytes is null)
        {
            throw new InvalidOperationException("Nested group delta was not generated.");
        }
        using (var nested = JsonDocument.Parse(nestedBytes.AsMemory(LiberaFx.Services.CompiledPreviewProtocol.HeaderSize)))
        {
            if (nested.RootElement.GetProperty("updates")[0].GetProperty("containerId").GetString() != Guid.Empty.ToString("D"))
            {
                throw new InvalidOperationException("Flattened group used the wrong runtime container.");
            }
        }
        var spawnDef = LiberaFx.Models.Graph.GraphNodeDefinition.Find(LiberaFx.Models.Graph.GraphNodeKind.SpawnableGroup)!;
        var spawn = new LiberaFx.Models.Graph.GraphNodeSnapshot(Guid.NewGuid(), spawnDef.Kind, "Spawn", 0, 0,
            spawnDef.PortType, spawnDef.PortCategory, ChildGraph: nestedOld);
        var spawnOld = new LiberaFx.Models.Graph.GraphSnapshot([spawn], []);
        var spawnNew = new LiberaFx.Models.Graph.GraphSnapshot([spawn with { ChildGraph = nestedNew }], []);
        if (!PackageParameterDelta.TryCreatePayload(spawnOld, spawnNew, 126, out var spawnBytes) || spawnBytes is null)
        {
            throw new InvalidOperationException("Nested Spawnable delta was not generated.");
        }
        using (var spawnDelta = JsonDocument.Parse(spawnBytes.AsMemory(LiberaFx.Services.CompiledPreviewProtocol.HeaderSize)))
        {
            if (spawnDelta.RootElement.GetProperty("updates")[0].GetProperty("containerId").GetGuid() != spawn.Id)
            {
                throw new InvalidOperationException("Nested graph did not use its owning Spawnable container.");
            }
        }
        var itemId = Guid.NewGuid();
        var exposed = new LiberaFx.Models.Graph.GroupExposedItem(itemId, "Speed", "", "", 0, "parameter",
            [new(floatNode.Id, "value")]);
        var childWithExposure = baseGraph with { ExposedItems = [exposed] };
        var groupWithOverride = group with { ChildGraph = childWithExposure, GroupOverrides =
            [new(itemId, [new(JsonSerializer.SerializeToElement(13d))])] };
        var groupChanged = groupWithOverride with { GroupOverrides =
            [new(itemId, [new(JsonSerializer.SerializeToElement(27d))])] };
        var groupOld = new LiberaFx.Models.Graph.GraphSnapshot([groupWithOverride], []);
        var groupNew = new LiberaFx.Models.Graph.GraphSnapshot([groupChanged], []);
        if (!PackageParameterDelta.TryCreatePayload(groupOld, groupNew, 126, out var groupBytes) || groupBytes is null)
        {
            throw new InvalidOperationException("Exposed group override did not produce a delta.");
        }
        using (var groupDelta = JsonDocument.Parse(groupBytes.AsMemory(LiberaFx.Services.CompiledPreviewProtocol.HeaderSize)))
        {
            var update = groupDelta.RootElement.GetProperty("updates")[0];
            if (update.GetProperty("nodeId").GetGuid() != floatNode.Id || update.GetProperty("floatValue").GetDouble() != 27)
            {
                throw new InvalidOperationException("Exposed override did not resolve to the underlying node.");
            }
        }
        var groupReset = new LiberaFx.Models.Graph.GraphSnapshot([groupWithOverride with { GroupOverrides = null }], []);
        if (!PackageParameterDelta.TryCreatePayload(groupOld, groupReset, 127, out var groupResetBytes) || groupResetBytes is null)
        {
            throw new InvalidOperationException("Removing a group override did not produce a delta.");
        }
        using (var groupDelta = JsonDocument.Parse(groupResetBytes.AsMemory(LiberaFx.Services.CompiledPreviewProtocol.HeaderSize)))
        {
            if (groupDelta.RootElement.GetProperty("updates")[0].GetProperty("floatValue").GetDouble() != 10)
            {
                throw new InvalidOperationException("Removing a group override did not restore the authored child value.");
            }
        }
        var enumeration = LiberaFx.Models.Graph.GraphEnumDefinition.All.Values.First(item => item.Values.Count > 1);
        var enumDefinition = LiberaFx.Models.Graph.GraphNodeDefinition.Find(new LiberaFx.Models.Graph.GraphNodeKind(
            LiberaFx.Models.Graph.GraphEnumBranchSchema.ValueKind(enumeration.TypeId)))!;
        var enumNode = new LiberaFx.Models.Graph.GraphNodeSnapshot(Guid.NewGuid(), enumDefinition.Kind, "Enum", 0, 0,
            enumDefinition.PortType, enumDefinition.PortCategory);
        var enumType = $"enum:{enumeration.TypeId:D}";
        var enumOld = new LiberaFx.Models.Graph.GraphSnapshot([enumNode], []);
        var enumNew = new LiberaFx.Models.Graph.GraphSnapshot([enumNode with { Parameters =
            [new("value", enumType, JsonSerializer.SerializeToElement(enumeration.Values[1].Value))] }], []);
        if (!PackageParameterDelta.TryCreatePayload(enumOld, enumNew, 128, out var enumBytes) || enumBytes is null)
        {
            throw new InvalidOperationException("Enum value did not produce a delta.");
        }
        using (var enumDelta = JsonDocument.Parse(enumBytes.AsMemory(LiberaFx.Services.CompiledPreviewProtocol.HeaderSize)))
        {
            var update = enumDelta.RootElement.GetProperty("updates")[0];
            if (!update.GetProperty("isInt32").GetBoolean() || update.GetProperty("intValue").GetInt32() != enumeration.Values[1].Value)
            {
                throw new InvalidOperationException("Enum delta must preserve its Int32 value.");
            }
        }
        var compose = LiberaFx.Models.Graph.GraphNodeDefinition.Find(new LiberaFx.Models.Graph.GraphNodeKind("MatrixCompose"))!;
        var matrixNode = new LiberaFx.Models.Graph.GraphNodeSnapshot(Guid.NewGuid(), compose.Kind, "Matrix", 0, 0,
            compose.PortType, compose.PortCategory);
        var matrixOld = new LiberaFx.Models.Graph.GraphSnapshot([matrixNode], []);
        var matrixNew = new LiberaFx.Models.Graph.GraphSnapshot([matrixNode with { Parameters =
            [new("row1", "vector4", JsonSerializer.SerializeToElement(new[] { 4d, 5d, 6d, 7d }))] }], []);
        if (!PackageParameterDelta.TryCreatePayload(matrixOld, matrixNew, 127, out var matrixBytes) || matrixBytes is null)
        {
            throw new InvalidOperationException("Matrix edit did not produce a delta.");
        }
        using (var matrixDelta = JsonDocument.Parse(matrixBytes.AsMemory(LiberaFx.Services.CompiledPreviewProtocol.HeaderSize)))
        {
            var update = matrixDelta.RootElement.GetProperty("updates")[0];
            var rows = LiberaFx.Models.Graph.GraphMatrixLowering.Lower(matrixNew).Outputs[(matrixNode.Id, "value")];
            if (update.GetProperty("kind").GetString() != "matrix" || update.GetProperty("values").GetArrayLength() != 16 ||
                update.GetProperty("values")[4].GetDouble() != 4 ||
                !update.GetProperty("rowNodeIds").EnumerateArray().Select(value => value.GetGuid()).SequenceEqual(rows.Select(row => row.NodeId)))
            {
                throw new InvalidOperationException("Matrix delta does not match the compiled row identities and values.");
            }
        }
        var pointDefinition = LiberaFx.Models.Graph.GraphNodeDefinition.Find(new LiberaFx.Models.Graph.GraphNodeKind("TransformPoint"))!;
        var point = new LiberaFx.Models.Graph.GraphNodeSnapshot(Guid.NewGuid(), pointDefinition.Kind, "Point", 0, 0,
            pointDefinition.PortType, pointDefinition.PortCategory);
        var pointOld = new LiberaFx.Models.Graph.GraphSnapshot([point], []);
        var pointNew = new LiberaFx.Models.Graph.GraphSnapshot([point with { Parameters =
            [new("point", "vector3", JsonSerializer.SerializeToElement(new[] { 1d, 2d, 3d }))] }], []);
        if (!PackageParameterDelta.TryCreatePayload(pointOld, pointNew, 129, out var pointBytes) || pointBytes is null)
        {
            throw new InvalidOperationException("Lowered matrix numeric input did not produce a delta.");
        }
        using (var pointDelta = JsonDocument.Parse(pointBytes.AsMemory(LiberaFx.Services.CompiledPreviewProtocol.HeaderSize)))
        {
            var update = pointDelta.RootElement.GetProperty("updates")[0];
            var runtimeId = update.GetProperty("nodeId").GetGuid();
            var lowered = LiberaFx.Models.Graph.GraphMatrixLowering.Lower(pointNew).Graph;
            if (runtimeId == point.Id || update.GetProperty("inputSlot").GetInt32() != 0 ||
                !lowered.Nodes.Any(node => node.Id == runtimeId && node.Parameters?.Any(parameter => parameter.Id == "value") == true))
            {
                throw new InvalidOperationException("Matrix input delta did not target the lowered default node.");
            }
        }
        var curveDefinition = LiberaFx.Models.Graph.GraphNodeDefinition.Find(LiberaFx.Models.Graph.GraphNodeKind.FloatFCurve)!;
        var curve = new LiberaFx.Models.Graph.GraphNodeSnapshot(Guid.NewGuid(), curveDefinition.Kind, "Curve", 0, 0,
            curveDefinition.PortType, curveDefinition.PortCategory, Curve: new LiberaFx.Models.Graph.FCurveSnapshot([]));
        var curveBefore = new LiberaFx.Models.Graph.GraphSnapshot([curve], []);
        var curveAfter = new LiberaFx.Models.Graph.GraphSnapshot([curve with { Curve = new LiberaFx.Models.Graph.FCurveSnapshot(
            [new(Guid.NewGuid(), 0, 1, 0, 0, LiberaFx.Models.Graph.CurveInterpolation.Linear)]) }], []);
        if (PackageParameterDelta.TryCreatePayload(curveBefore, curveAfter, 130, out _))
        {
            throw new InvalidOperationException("Clients without FCurve support must receive a full package.");
        }
        if (!PackageParameterDelta.TryCreatePayload(curveBefore, curveAfter, 130, out var curveBytes, supportsCurveDelta: true) || curveBytes is null)
        {
            throw new InvalidOperationException("An FCurve edit must produce a parameter delta.");
        }
        using (var curveDelta = JsonDocument.Parse(curveBytes.AsMemory(LiberaFx.Services.CompiledPreviewProtocol.HeaderSize)))
        {
            var update = curveDelta.RootElement.GetProperty("updates")[0];
            var key = update.GetProperty("keys")[0];
            if (update.GetProperty("kind").GetString() != "fcurve" ||
                update.GetProperty("nodeId").GetGuid() != curve.Id ||
                key.GetProperty("time").GetDouble() != 0 || key.GetProperty("value").GetDouble() != 1 ||
                key.GetProperty("interpolation").GetInt32() != 1)
            {
                throw new InvalidOperationException("FCurve delta does not match the Core wire contract.");
            }
        }
        var curveSender = new GodotPackagePreviewSender();
        var curveInitial = new PreviewPublication(1, "curve", curveBefore, GraphNodeDefinition.All);
        var curveFirst = curveSender.Prepare(curveInitial, false)!;
        curveSender.Sent(curveFirst);
        var curvePublication = new PreviewPublication(2, "curve", curveAfter, GraphNodeDefinition.All);
        if (curveSender.Prepare(curvePublication, false)!.Kind != CompiledPreviewKind.Program ||
            curveSender.Prepare(curvePublication, false, supportsCurveDelta: true)!.Kind != CompiledPreviewKind.Scene)
        {
            throw new InvalidOperationException("FCurve live updates must require an explicit engine capability.");
        }
        var signalDefinition = LiberaFx.Models.Graph.GraphNodeDefinition.Find(LiberaFx.Models.Graph.GraphNodeKind.Signal)!;
        var signal = new LiberaFx.Models.Graph.GraphNodeSnapshot(Guid.NewGuid(), signalDefinition.Kind, "Signal", 0, 0,
            signalDefinition.PortType, signalDefinition.PortCategory);
        var signalBefore = new LiberaFx.Models.Graph.GraphSnapshot([signal], []);
        var signalAfter = new LiberaFx.Models.Graph.GraphSnapshot([signal with
        {
            SignalKeys = [new(Guid.NewGuid(), 1, LiberaFx.Models.Graph.SignalTimelineMode.Runtime)]
        }], []);
        if (PackageParameterDelta.TryCreatePayload(signalBefore, signalAfter, 131, out _))
        {
            throw new InvalidOperationException("SignalKeys edits must use full-package fallback until Core supports them.");
        }
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
            var fingerprint = Convert.FromHexString(host.Fingerprint);
            foreach (var legacy in new[] { "1", "2" })
            {
                using var rejected = new ClientWebSocket();
                rejected.Options.SetRequestHeader("X-LiberaFx-Preview-Token", host.AccessToken);
                rejected.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && CryptographicOperations.FixedTimeEquals(
                        SHA256.HashData(certificate.GetRawCertData()), fingerprint);
                try
                {
                    await rejected.ConnectAsync(new Uri($"wss://localhost:{port}/preview?protocol={legacy}"), token);
                    throw new InvalidOperationException("Legacy preview protocol was accepted.");
                }
                catch (WebSocketException)
                {
                }
            }
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("X-LiberaFx-Preview-Token", host.AccessToken);
            socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is not null && CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(certificate.GetRawCertData()), fingerprint);
            await socket.ConnectAsync(new Uri($"wss://localhost:{port}/preview?protocol=3&unrealPackagePreview=1"), token);
            var hello = Encoding.UTF8.GetBytes("""
                {"type":"hello","engine":"Unreal","role":"editor","version":1,"capabilities":["unrealPackagePreview","parameterDeltaV1"]}
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

            var scalarId = await Dispatcher.UIThread.InvokeAsync(() => model.Graph.CreateNode(GraphNodeKind.Float).Id);
            var added = await ReceiveBinaryFrameAsync(socket, token);
            if ((CompiledPreviewKind)added[6] != CompiledPreviewKind.Program)
            {
                throw new InvalidOperationException("A structural edit must replace the package.");
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                for (var value = 1; value <= 40; value++)
                {
                    model.Graph.SetParameter(model.Graph.GetNode(scalarId), "value", JsonSerializer.SerializeToElement((double)value));
                }
            });
            var lastSequence = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(added.AsSpan(8));
            while (true)
            {
                var frame = await ReceiveBinaryFrameAsync(socket, token);
                var sequence = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(8));
                if (sequence <= lastSequence || (CompiledPreviewKind)frame[6] != CompiledPreviewKind.Scene)
                {
                    throw new InvalidOperationException("A coalesced parameter delta lost its monotonic baseline.");
                }
                lastSequence = sequence;
                using var command = JsonDocument.Parse(frame.AsMemory(CompiledPreviewProtocol.HeaderSize));
                if (command.RootElement.GetProperty("updates").EnumerateArray().Any(update =>
                    update.GetProperty("nodeId").GetGuid() == scalarId && update.GetProperty("floatValue").GetDouble() == 40))
                {
                    break;
                }
            }
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"resync\"}"), WebSocketMessageType.Text, true, token);
            var resync = await ReceiveBinaryFrameAsync(socket, token);
            if ((CompiledPreviewKind)resync[6] != CompiledPreviewKind.Program ||
                !resync.AsSpan(CompiledPreviewProtocol.HeaderSize, 4).SequenceEqual("LFXP"u8))
            {
                throw new InvalidOperationException("Resync must send a complete package.");
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
