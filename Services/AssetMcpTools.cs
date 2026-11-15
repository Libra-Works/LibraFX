using System.ComponentModel;
using System.Text.Json;
using Avalonia.Threading;
using LiberaFx.Models.Graph;
using LiberaFx.ViewModels;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace LiberaFx.Services;

[McpServerToolType]
public sealed class AssetMcpTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static string Json(object? value) => JsonSerializer.Serialize(value, JsonOptions);
    private static Guid? Peer(string? clientId) => clientId is null ? null :
        Guid.TryParseExact(clientId, "D", out var id) && id != Guid.Empty ? id : throw new McpException("Invalid clientId.");

    [McpServerTool(Name = "libera_assets_search", ReadOnly = true, OpenWorld = false)]
    [Description("Search the selected Unreal editor's asset registry with pagination. Classes: model, texture, material. Includes Libera-only tags, semantic usage and atlas metadata; use these to select fire/flash/impact hitmark textures rather than guessing from filenames.")]
    public static async Task<string> Search(PreviewAssetStore assets, AssetMetadataStore metadata,
        string query = "", string assetClass = "", int offset = 0, int limit = 32, string? clientId = null)
    {
        var page = await assets.CatalogAsync(query, assetClass, offset, limit, Peer(clientId));
        return Json(new { page.Total, page.Offset, assets = page.Assets.Select(asset => new
            { asset, metadata = metadata.Get(asset.Engine, asset.Path) }) });
    }

    [McpServerTool(Name = "libera_asset_details", ReadOnly = true, OpenWorld = false)]
    [Description("Read actual mesh material slots, overridable scalar/vector/texture parameters, texture dimensions and local metadata. Material schema generates stable dynamic pin IDs including slot and parameter association.")]
    public static async Task<string> Details(PreviewAssetStore assets, AssetMetadataStore metadata,
        string path, string? clientId = null)
    {
        var asset = await assets.DetailsAsync(path, Peer(clientId));
        return Json(new { asset, metadata = metadata.Get(asset.Engine, asset.Path),
            materialInputs = RenderNodeSchema.Describe(RenderNodeSchema.Encode(asset)) });
    }

    [McpServerTool(Name = "libera_asset_thumbnail", ReadOnly = true, OpenWorld = false)]
    [Description("Fetch a bounded PNG texture/asset thumbnail from the selected editor. includeImage defaults false to avoid unnecessary image data; true returns base64 PNG for local inspection.")]
    public static async Task<string> Thumbnail(PreviewAssetStore assets, string path, int size = 128,
        bool includeImage = false, string? clientId = null)
    {
        var image = await assets.ThumbnailAsync(path, size, Peer(clientId));
        return Json(new { image.Path, image.Mime, image.Width, image.Height, bytes = image.Image.Length,
            imageBase64 = includeImage ? Convert.ToBase64String(image.Image) : null });
    }

    [McpServerTool(Name = "libera_asset_metadata_search", ReadOnly = true, OpenWorld = false)]
    [Description("Search Libera-only asset descriptions, tags and semantic usage (e.g flame hitmark, flash impact). Returns atlas layout/FPS for automatic sprite sample authoring. Does not query or modify engine asset files.")]
    public static string MetadataSearch(AssetMetadataStore metadata, string query, string? engine = null, int limit = 100) =>
        Json(metadata.Search(query, engine, limit));

    [McpServerTool(Name = "libera_asset_metadata_set", Destructive = false, OpenWorld = false)]
    [Description("Store local semantic description/tags and sprite atlas columns/rows/FPS for an asset. Metadata belongs to Libera FX only; original engine assets are never edited.")]
    public static string MetadataSet(AssetMetadataStore metadata, string engine, string path,
        string description = "", string semanticUsage = "", string[]? tags = null,
        int columns = 1, int rows = 1, double framesPerSecond = 12)
    {
        metadata.Set(engine, path, new AssetMetadata(engine, path, description, tags, semanticUsage,
            columns, rows, framesPerSecond));
        return Json(metadata.Get(engine, path));
    }

    [McpServerTool(Name = "libera_render_create_model", Destructive = false, OpenWorld = false)]
    [Description("Fetch a model's actual assigned material schema and add a model draw node with dynamic scalar/vector pins and texture properties, without replacing existing graph nodes.")]
    public static async Task<string> CreateModel(ActiveGraphProvider graph, PreviewAssetStore assets,
        string path, double? x = null, double? y = null, string? clientId = null)
    {
        var asset = await assets.DetailsAsync(path, Peer(clientId));
        return await Dispatcher.UIThread.InvokeAsync(() => Json(RenderNodeAuthoring.CreateModel(graph.Graph, asset, x, y).Id));
    }

    [McpServerTool(Name = "libera_render_create_sprite", Destructive = false, OpenWorld = false)]
    [Description("Create a textured sprite or sprite-animation node. Uses local atlas metadata by default; fps=0 creates a static sprite. Existing graph content is preserved.")]
    public static async Task<string> CreateSprite(ActiveGraphProvider graph, PreviewAssetStore assets,
        AssetMetadataStore metadata, string path, int? columns = null, int? rows = null,
        double? fps = null, double? x = null, double? y = null, string? clientId = null)
    {
        var asset = await assets.DetailsAsync(path, Peer(clientId));
        var settings = metadata.Get(asset.Engine, asset.Path);
        return await Dispatcher.UIThread.InvokeAsync(() => Json(RenderNodeAuthoring.CreateSprite(graph.Graph, asset,
            columns ?? settings.AtlasColumns, rows ?? settings.AtlasRows, fps ?? settings.FramesPerSecond, x, y).Id));
    }

    [McpServerTool(Name = "libera_render_assign_asset", Destructive = false, OpenWorld = false)]
    [Description("Assign an asset to a draw node, updating dynamic material pins undoably. Compatible pose/material connections and existing values are kept; only invalidated material links are removed.")]
    public static async Task<string> Assign(ActiveGraphProvider graph, PreviewAssetStore assets,
        string nodeId, string path, string? clientId = null)
    {
        var asset = await assets.DetailsAsync(path, Peer(clientId));
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!Guid.TryParseExact(nodeId, "D", out var id)) throw new McpException("Invalid nodeId.");
            var node = graph.Graph.Nodes.SingleOrDefault(node => node.Id == id) ?? throw new McpException("Node not found.");
            RenderNodeAuthoring.AssignAsset(graph.Graph, node, asset);
            return Json(new { node.Id, pins = node.Definition.InputPorts });
        });
    }

    [McpServerTool(Name = "libera_create_effect_sample", Destructive = false, OpenWorld = false)]
    [Description("Append a complete undoable sample: color-box (Time/Sin drives an actual exposed model material color), sprite, sprite-animation, or hitmark (HDR flash tint, alpha fade and atlas playback). path may be omitted when semanticQuery uniquely matches Libera metadata. Inspect returned render status to verify engine application. Existing nodes remain unchanged.")]
    public static async Task<string> CreateSample(ActiveGraphProvider graph, PreviewAssetStore assets,
        AssetMetadataStore metadata, EditorPlayback playback, string style, string? path = null,
        string? semanticQuery = null, double duration = 0.5, bool autoplay = false, string? clientId = null)
    {
        if (path is null)
        {
            var matches = metadata.Search(semanticQuery ?? style, "Unreal", 2);
            if (matches.Count != 1) throw new McpException("Provide a path or a unique semantic metadata query.");
            path = matches[0].Path;
        }
        var asset = await assets.DetailsAsync(path, Peer(clientId));
        var settings = metadata.Get(asset.Engine, asset.Path);
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var before = graph.Graph.Nodes.Select(node => node.Id).ToHashSet();
            _ = EffectSampleAuthoring.Create(graph.Graph, asset, settings, style, duration);
            if (autoplay)
            {
                playback.Pause();
                playback.StartText = "0";
                playback.EndText = (style == "hitmark" ? duration * playback.Snapshot.FramesPerSecond :
                    10 * playback.Snapshot.FramesPerSecond).ToString(System.Globalization.CultureInfo.CurrentCulture);
                playback.Frame = 0;
                playback.Loop = style != "hitmark";
                playback.Play();
            }
            return Json(new { style, asset.Path, addedNodes = graph.Graph.Nodes.Where(node => !before.Contains(node.Id))
                .Select(node => new { node.Id, node.Name, kind = node.Definition.Kind,
                    inputPorts = node.Definition.InputPorts }), autoplay });
        });
    }

    [McpServerTool(Name = "libera_render_status", ReadOnly = true, OpenWorld = false)]
    [Description("Read last actual engine render application: matching program sequence, evaluated time, model material parameter values, sprite frame/UV rectangle and errors. This is engine feedback, not a claimed GPU timing measurement.")]
    public static string RenderStatus(PreviewAssetStore assets, string? clientId = null) =>
        Json(assets.RenderStatus(Peer(clientId)));

    [McpServerTool(Name = "libera_preview_reconnect", Destructive = false, OpenWorld = false)]
    [Description("Ask exactly the selected editor to reconnect its preview transport. Acknowledgement means the request was received; verify a new peer in libera_preview_clients. Never restarts the entire engine.")]
    public static async Task<string> Reconnect(PreviewAssetStore assets, string clientId)
    {
        await assets.ReconnectAsync(Peer(clientId)!.Value);
        return """{"acknowledged":true}""";
    }

    [McpServerTool(Name = "libera_preview_playback", Destructive = false, OpenWorld = false)]
    [Description("Control the shared editor timeline directly, rather than graph-only keyboard simulation. Frame is clamped to the existing playback range.")]
    public static async Task<string> Playback(EditorPlayback playback, bool? playing = null, double? frame = null) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (frame is { } value)
            {
                if (!double.IsFinite(value)) throw new McpException("Invalid frame.");
                playback.Frame = value;
            }
            if (playing == true) playback.Play();
            else if (playing == false) playback.Pause();
            return Json(playback.Snapshot);
        });
}
