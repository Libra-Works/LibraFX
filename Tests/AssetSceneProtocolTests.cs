using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using LiberaFx.Services;
using LiberaFx.Models.Graph;

internal static class AssetSceneProtocolTests
{
    public static void Run()
    {
        VerifyGpuScenes();
        VerifyQueriesAsync().GetAwaiter().GetResult();
    }

    private static void VerifyGpuScenes()
    {
        var gpu = new GraphNodeSnapshot(Guid.NewGuid(), GpuParticleEditorService.Kind, "GPU", 0, 0,
            "Float", GraphPortCategory.Parameter);
        var template = new GraphNodeSnapshot(Guid.NewGuid(), GraphNodeKind.SpawnableGroup, "Template", 0, 0,
            "Signal", GraphPortCategory.Signal, ChildGraph: new([gpu with { Id = Guid.NewGuid() }], []));
        var graph = new GraphSnapshot([gpu, template], []);
        var frame = CompiledPreviewProtocol.CreateScene(GpuPreviewScene.CreateScene(graph, 73));
        using var scene = JsonDocument.Parse(frame.AsMemory(CompiledPreviewProtocol.HeaderSize));
        var programs = scene.RootElement.GetProperty("programs");
        Require(programs.GetArrayLength() == 1 && programs[0].GetProperty("nodeId").GetGuid() == gpu.Id &&
            programs[0].GetProperty("programSequence").GetInt64() == 73 &&
            programs[0].GetProperty("registerKernels")[0].GetProperty("stage").ValueKind == JsonValueKind.String,
            "GPU scenes must carry executable packets with matched sequences, excluding unspawned templates.");
        using var empty = JsonDocument.Parse(GpuPreviewScene.CreateScene(new([], []), 74));
        Require(empty.RootElement.GetProperty("programs").GetArrayLength() == 0,
            "Removing GPU containers must publish an explicit empty replacement.");
        var invalidState = new GpuEditorState { Program = LibraGpu.Core.GpuSamples.CreateEmitter() with { Capacity = 0 } };
        var invalidGpu = gpu with
        {
            Parameters = [new(GpuParticleEditorService.ParameterId, "gpuState",
                JsonSerializer.SerializeToElement(invalidState, GpuParticleEditorService.Json))]
        };
        var failure = new CompiledPreviewSender().Prepare(new PreviewPublication(75, "invalid-gpu",
            new([invalidGpu], []), GraphNodeDefinition.All, null));
        Require(failure?.Kind == CompiledPreviewKind.CompileError,
            "Invalid GPU programs must fail before replacing an otherwise valid numeric preview program.");
        try
        {
            GpuPreviewScene.CreateScene(new(Enumerable.Range(0, GpuPreviewScene.MaxPrograms + 1)
                .Select(_ => gpu with { Id = Guid.NewGuid() }).ToArray(), []), 75);
            throw new Exception("Oversized GPU container sets must be rejected.");
        }
        catch (InvalidDataException) { }
    }

    private static async Task VerifyQueriesAsync()
    {
        var telemetry = new PreviewTelemetryStore();
        var assets = new PreviewAssetStore(telemetry);
        var peer = Guid.NewGuid();
        var sawQuery = false;
        telemetry.Connect(peer, 3, bytes =>
        {
            Require(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)) == 5 &&
                bytes.AsSpan(8, 36).IndexOfAnyExcept((byte)0) < 0, "Asset requests must be out-of-band scene frames.");
            using var command = JsonDocument.Parse(bytes.AsMemory(CompiledPreviewProtocol.HeaderSize));
            var request = command.RootElement.GetProperty("requestId").GetGuid();
            var type = command.RootElement.GetProperty("type").GetString();
            sawQuery = true;
            object response = type == "asset_catalog" ?
                new { type = "asset_result", requestId = request, operation = "catalog", total = 1, offset = 0,
                    assets = new[] { new { path = "/Engine/BasicShapes/Cube.Cube", name = "Cube", assetClass = "model", engine = "Unreal" } },
                    error = (string?)null } :
                new { type = "asset_result", requestId = request, operation = "reconnect", error = (string?)null };
            Require(assets.Receive(peer, JsonSerializer.SerializeToUtf8Bytes(response)), "Matching replies must complete requests.");
            return Task.CompletedTask;
        }, "127.0.0.1");
        Require(telemetry.Receive(peer, Encoding.UTF8.GetBytes(
            """{"type":"hello","engine":"Unreal","role":"editor","version":1,"capabilities":["assets","rendering","gpuParticles"],"viewerName":"Test preview","engineVersion":"5.7.4","platform":"Windows"}""")),
            "Asset editor handshake failed.");
        var page = await assets.CatalogAsync("Cube", "model");
        Require(sawQuery && page.Assets.Single().Path == "/Engine/BasicShapes/Cube.Cube" &&
            telemetry.Clients().Single().ViewerName == "Test preview", "Real reply fields and viewer identity must be retained.");
        await assets.ReconnectAsync(peer);
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
}
