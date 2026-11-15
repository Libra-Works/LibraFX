using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using LiberaFx.Services;

internal static class AssetSceneProtocolTests
{
    public static void Run() => VerifyQueriesAsync().GetAwaiter().GetResult();

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
            """{"type":"hello","engine":"Unreal","role":"editor","version":1,"capabilities":["assets","rendering"],"viewerName":"Test preview","engineVersion":"5.7.4","platform":"Windows"}""")),
            "Asset editor handshake failed.");
        var page = await assets.CatalogAsync("Cube", "model");
        Require(sawQuery && page.Assets.Single().Path == "/Engine/BasicShapes/Cube.Cube" &&
            telemetry.Clients().Single().ViewerName == "Test preview", "Real reply fields and viewer identity must be retained.");
        await assets.ReconnectAsync(peer);
        var second = Guid.NewGuid();
        telemetry.Connect(second, 3, _ => Task.CompletedTask);
        Require(telemetry.Receive(second, Encoding.UTF8.GetBytes(
            """{"type":"hello","engine":"Unreal","role":"editor","version":1,"capabilities":["assets"]}""")), "Second editor handshake failed.");
        try { assets.SelectPeer(); throw new Exception("Multiple asset editors must require an explicit selection."); }
        catch (InvalidOperationException) { }
        Require(assets.SelectPeer(peer) == peer, "Explicit requests must retain their target.");
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
