using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia.Threading;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Mcp;
using LiberaFx.Services.Preview;
using LiberaFx.ViewModels.Graph;
using LiberaFx.ViewModels.Main;

internal static class UnrealPeerMcpTests
{
    public static void Run() => CompiledPreviewProtocolTests.RunWithDispatcher(VerifyAsync(), 180);

    private static async Task VerifyAsync()
    {
        var executable = RequiredPath("LIBERAFX_UNREAL_EDITOR");
        var project = RequiredPath("LIBERAFX_UNREAL_PROJECT");
        var scratch = RequiredPath("LIBERAFX_UNREAL_PEER_MCP_DIR");
        var texture = Environment.GetEnvironmentVariable("LIBERAFX_UNREAL_MCP_TEXTURE")
            ?? "/Engine/EngineResources/DefaultTexture.DefaultTexture";
        Require(File.Exists(executable) && File.Exists(project) && Path.GetExtension(project) == ".uproject" &&
            !Directory.Exists(scratch) && !File.Exists(scratch),
            "Use an isolated Unreal project and a fresh output directory.");
        Directory.CreateDirectory(scratch);

        var previewPort = ReservePort();
        using var preview = new PreviewHost(previewPort);
        var mcp = new McpHost(ReservePort());
        var model = new MainViewModel();
        var metadata = new AssetMetadataStore(Path.Combine(scratch, "assets.json"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        using var http = new LocalPreviewMcpClient(mcp);
        Process? unreal = null;
        try
        {
            await preview.StartAsync(model);
            await mcp.StartAsync(model.ActiveGraphProvider, model.PreviewTelemetry, model.PreviewAssets,
                metadata, model.Playback, model);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            foreach (var argument in new[] { project, "/Engine/Maps/Entry", "-unattended", "-NoCrashDialog",
                         "-nosplash", "-NoP4", "-d3d11", "-LibraPreview", "-UserDir=" + scratch,
                         "-AbsLog=" + Path.Combine(scratch, "unreal.log") })
            {
                start.ArgumentList.Add(argument);
            }
            start.Environment["LIBRA_PREVIEW_PORT"] = previewPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.Environment["LIBRA_PREVIEW_TOKEN"] = preview.AccessToken;
            start.Environment["LIBRA_PREVIEW_FINGERPRINT"] = preview.Fingerprint;
            unreal = Process.Start(start) ?? throw new InvalidOperationException("Unreal Editor did not start.");

            async Task WaitAsync(Func<bool> ready, string operation)
            {
                while (!ready())
                {
                    Require(!unreal.HasExited && !timeout.IsCancellationRequested,
                        "Unreal did not confirm " + operation + "; see isolated unreal.log.");
                    await Task.Delay(50);
                }
            }
            PreviewPeer? ReadyPeer(Guid? excluded = null) => model.PreviewTelemetry.Clients().FirstOrDefault(peer =>
                peer.ClientId != excluded && peer.Engine == "Unreal" && peer.Role == "editor" &&
                peer.Capabilities.Contains("assets") && peer.Capabilities.Contains("rendering") &&
                peer.AcknowledgedProgramSequence.HasValue && !peer.AwaitingProgramAcknowledgement);
            async Task<JsonElement> CallAsync(string name, object arguments) =>
                JsonSerializer.Deserialize<JsonElement>(await http.CallAsync(name, arguments));

            await WaitAsync(() => ReadyPeer() is not null, "initial pinned connection and program acknowledgement");
            var first = ReadyPeer()!.ClientId;
            var clientId = first.ToString("D");
            await CallAsync("libera_asset_metadata_set", new
            {
                engine = "Unreal", path = texture, semanticUsage = "isolated sprite retirement fixture",
                tags = new[] { "retirement" }, columns = 2, rows = 2
            });
            var search = await CallAsync("libera_assets_search", new { clientId, query = texture, assetClass = "texture" });
            Require(search.GetProperty("assets").EnumerateArray().Any(item =>
                item.GetProperty("asset").GetProperty("path").GetString() == texture &&
                item.GetProperty("metadata").GetProperty("semanticUsage").GetString() == "isolated sprite retirement fixture"),
                "The real Unreal catalog did not return the exact fixture and semantic metadata.");
            var authored = await CallAsync("libera_create_effect_sample", new
            {
                style = "sprite", semanticQuery = "retirement fixture", clientId
            });
            var sprite = authored.GetProperty("addedNodes").EnumerateArray().Single(node =>
                node.GetProperty("kind").GetString() == "SpriteDraw").GetProperty("id").GetGuid();
            bool Rendered(Guid peer, double frame) => model.PreviewAssets.RenderStatus(peer) is { } status &&
                status.GetProperty("errors").GetArrayLength() == 0 &&
                status.GetProperty("nodes").EnumerateArray().Any(item => item.GetProperty("nodeId").GetGuid() == sprite &&
                    item.GetProperty("assetPath").GetString() == texture && item.GetProperty("rendered").GetBoolean() &&
                    item.GetProperty("frame").GetDouble() == frame);
            await WaitAsync(() => Rendered(first, 0), "the MCP-authored sprite render");
            await CallAsync("libera_graph_set_parameter", new { nodeId = sprite, parameterId = "frame", value = 3 });
            await WaitAsync(() => Rendered(first, 3), "the MCP frame edit in the actual Unreal renderer");

            var reconnect = await CallAsync("libera_preview_reconnect", new { clientId });
            Require(reconnect.GetProperty("acknowledged").GetBoolean(), "The real Unreal peer did not acknowledge reconnect.");
            await WaitAsync(() => ReadyPeer(first) is { } peer && Rendered(peer.ClientId, 3) &&
                model.PreviewTelemetry.Clients().All(item => item.ClientId != first),
                "the new peer's acknowledged snapshot and old peer retirement");
            var replacement = ReadyPeer(first)!.ClientId;
            const string missing = "/Game/LibraVerification/MissingPeerMcpTexture.MissingPeerMcpTexture";
            await CallAsync("libera_graph_set_parameter", new { nodeId = sprite, parameterId = "assetPath", value = missing });
            await WaitAsync(() => model.PreviewAssets.RenderStatus(replacement) is { } status &&
                status.GetProperty("errors").EnumerateArray().Any(error =>
                    error.GetString()?.Contains(missing, StringComparison.Ordinal) == true),
                "the actual engine error for the missing sprite texture");
            var feedback = await CallAsync("libera_render_status", new { clientId = replacement });
            Require(feedback.GetProperty("errors").GetArrayLength() > 0, "The engine error was not visible through MCP.");
            await CallAsync("libera_graph_undo", new { });
            await WaitAsync(() => Rendered(replacement, 3), "render recovery after MCP undo");
            await CallAsync("libera_graph_delete_node", new { nodeId = sprite });
            await WaitAsync(() => model.PreviewAssets.RenderStatus(replacement) is { } status &&
                status.GetProperty("errors").GetArrayLength() == 0 && status.GetProperty("nodes").GetArrayLength() == 0,
                "render retirement after deleting the sprite over MCP");
            Console.WriteLine("Real Unreal WSS reconnect ACK and MCP search, semantic authoring, edit, delivery and retirement passed.");
        }
        finally
        {
            await mcp.StopAsync();
            await preview.StopAsync();
            if (unreal is not null)
            {
                if (!unreal.HasExited)
                {
                    unreal.CloseMainWindow();
                    if (!unreal.WaitForExit(5000))
                    {
                        unreal.Kill();
                        await unreal.WaitForExitAsync();
                    }
                }
                unreal.Dispose();
            }
            await Dispatcher.UIThread.InvokeAsync(model.DisposeStorageFiles);
        }
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string RequiredPath(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return Path.IsPathFullyQualified(value ?? "") ? value! :
            throw new InvalidOperationException("Set " + key + " to an absolute path.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
