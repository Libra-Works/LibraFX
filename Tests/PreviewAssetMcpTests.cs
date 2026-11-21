using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using LiberaFx.Services;
using LiberaFx.ViewModels;

internal static class PreviewAssetMcpTests
{
    private const string AssetPath = "/Game/Textures/Smoke.Smoke";

    public static void Run() => RunWithDispatcher(VerifyAsync());

    private static async Task VerifyAsync()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var model = new MainViewModel();
        var peer = Guid.NewGuid();
        var peerConnected = false;
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "librafx-preview-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var metadata = new AssetMetadataStore(Path.Combine(temporaryDirectory, "assets.json"));
        var pendingCatalogReplies = 0;
        var catalogRequests = 0;
        model.PreviewTelemetry.Connect(peer, 3, bytes =>
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)) != (ushort)CompiledPreviewKind.Scene)
            {
                throw new InvalidDataException("MCP asset requests must use preview scene-control frames.");
            }

            using var command = JsonDocument.Parse(bytes.AsMemory(CompiledPreviewProtocol.HeaderSize));
            var root = command.RootElement;
            var requestId = root.GetProperty("requestId").GetGuid();
            var type = root.GetProperty("type").GetString();
            if (type == "asset_catalog") catalogRequests++;
            var catalogPending = type == "asset_catalog" && pendingCatalogReplies > 0;
            if (catalogPending) pendingCatalogReplies--;
            object response = type switch
            {
                "asset_catalog" when catalogPending => new
                {
                    type = "asset_result", requestId, operation = "catalog", pending = true,
                    total = 0, offset = root.GetProperty("offset").GetInt32(), assets = Array.Empty<object>(), error = (string?)null
                },
                "asset_catalog" => new
                {
                    type = "asset_result",
                    requestId,
                    operation = "catalog",
                    total = 1,
                    offset = root.GetProperty("offset").GetInt32(),
                    assets = new[] { new { path = AssetPath, name = "Smoke", assetClass = "texture", engine = "Unreal", width = 256, height = 256 } },
                    error = (string?)null
                },
                "asset_details" => new
                {
                    type = "asset_result",
                    requestId,
                    operation = "details",
                    asset = new { path = AssetPath, name = "Smoke", assetClass = "texture", engine = "Unreal", width = 256, height = 256 },
                    error = (string?)null
                },
                "reconnect" => new { type = "asset_result", requestId, operation = "reconnect", error = (string?)null },
                _ => throw new InvalidOperationException("Unexpected preview operation: " + type)
            };
            if (!model.PreviewAssets.Receive(peer, JsonSerializer.SerializeToUtf8Bytes(response)))
            {
                throw new InvalidDataException("Preview asset response was not accepted.");
            }

            return Task.CompletedTask;
        }, "127.0.0.1");
        peerConnected = model.PreviewTelemetry.Receive(peer, Encoding.UTF8.GetBytes(
            """{"type":"hello","version":1,"engine":"Unreal","role":"editor","capabilities":["assets","rendering"],"viewerName":"MCP asset fixture"}"""));
        if (!peerConnected)
        {
            throw new InvalidOperationException("The MCP asset peer did not negotiate.");
        }

        var host = new McpHost(port);
        await host.StartAsync(model.ActiveGraphProvider, model.PreviewTelemetry, model.PreviewAssets,
            metadata, model.Playback, model);
        try
        {
            pendingCatalogReplies = 2;
            var indexed = await model.PreviewAssets.CatalogAsync(clientId: peer);
            if (indexed.Assets.Count != 1 || catalogRequests != 3)
                throw new InvalidOperationException("Background catalog indexing must retry without exposing an empty completed page.");
            pendingCatalogReplies = int.MaxValue;
            using (var cancellation = new CancellationTokenSource())
            {
                var pending = model.PreviewAssets.CatalogAsync(clientId: peer, cancellation: cancellation.Token);
                cancellation.Cancel();
                try { await pending; throw new InvalidOperationException("Cancelled indexing kept polling."); }
                catch (OperationCanceledException) { }
            }
            pendingCatalogReplies = 0;
            using var client = new LocalPreviewMcpClient(host);
            var Call = client.CallAsync;

            using (var tools = await client.SendAsync("tools/list"))
            {
                var names = tools.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
                    .Select(tool => tool.GetProperty("name").GetString()).ToHashSet(StringComparer.Ordinal);
                if (!names.Contains("libera_assets_search") || !names.Contains("libera_asset_metadata_set") ||
                    !names.Contains("libera_create_effect_sample") || !names.Contains("libera_preview_reconnect"))
                {
                    throw new InvalidOperationException("Asset, authoring, or reconnect MCP tools were not registered.");
                }

            }

            _ = await Call("libera_asset_metadata_set", new
            {
                engine = "Unreal",
                path = AssetPath,
                description = "Smoke atlas",
                semanticUsage = "smoke impact sprite",
                tags = new[] { "smoke", "impact" },
                columns = 4,
                rows = 4,
                framesPerSecond = 12
            });
            using (var search = JsonDocument.Parse(await Call("libera_assets_search", new
            {
                query = "smoke",
                assetClass = "texture",
                clientId = peer.ToString("D")
            })))
            {
                if (search.RootElement.GetProperty("assets").GetArrayLength() != 1 ||
                    search.RootElement.GetProperty("assets")[0].GetProperty("metadata").GetProperty("semanticUsage").GetString() !=
                    "smoke impact sprite")
                {
                    throw new InvalidOperationException("MCP asset search did not return semantic metadata.");
                }
            }

            using (var authored = JsonDocument.Parse(await Call("libera_create_effect_sample", new
            {
                style = "sprite",
                semanticQuery = "smoke impact",
                clientId = peer.ToString("D")
            })))
            {
                if (authored.RootElement.GetProperty("addedNodes").GetArrayLength() == 0 ||
                    !model.Graph.History.CanUndo)
                {
                    throw new InvalidOperationException("Semantic MCP authoring did not create an undoable graph edit.");
                }
            }

            using (var graph = JsonDocument.Parse(await Call("libera_graph_read", new { })))
            {
                if (graph.RootElement.GetProperty("nodes").GetArrayLength() == 0)
                {
                    throw new InvalidOperationException("MCP-authored content was not available to graph delivery.");
                }
            }

            using (var reconnect = JsonDocument.Parse(await Call("libera_preview_reconnect", new
            {
                clientId = peer.ToString("D")
            })))
            {
                if (!reconnect.RootElement.GetProperty("acknowledged").GetBoolean())
                {
                    throw new InvalidOperationException("MCP reconnect did not wait for the peer acknowledgement.");
                }
            }
        }
        finally
        {
            await host.StopAsync();
            if (peerConnected)
            {
                model.PreviewTelemetry.Disconnect(peer);
                model.PreviewAssets.Disconnect(peer);
            }

            model.DisposeStorageFiles();
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static void RunWithDispatcher(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var registration = timeout.Token.Register(() => Dispatcher.UIThread.Post(() => frame.Continue = false));
            _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
        }

        if (!task.IsCompleted)
        {
            throw new TimeoutException("Preview asset MCP integration timed out.");
        }

        task.GetAwaiter().GetResult();
    }
}
