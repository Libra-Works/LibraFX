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
        [Description("Search text; empty lists all matching classes.")] string query = "",
        [Description("model, texture, material, or empty for all classes.")] string assetClass = "",
        [Description("Zero-based page offset.")] int offset = 0,
        [Description("Maximum assets on this page.")] int limit = 32,
        [Description("Optional editor peer GUID from libera_preview_clients.")] string? clientId = null,
        [Description("Optional engine folder, including descendants, e.g. /Game/VFX. Empty searches all permitted folders; tags are not required.")] string folder = "")
    {
        var page = await assets.CatalogAsync(query, assetClass, offset, limit, Peer(clientId), folder: folder);
        return Json(new { page.Total, page.Offset, assets = page.Assets.Select(asset => new
            { asset, metadata = metadata.Get(asset.Engine, asset.Path) }) });
    }

    [McpServerTool(Name = "libera_asset_folders", ReadOnly = true, OpenWorld = false)]
    [Description("Browse immediate child folders in a connected Unreal editor without loading asset objects. Empty folder lists /Game and /Engine. Results are paged; use a chosen path as libera_assets_search folder.")]
    public static async Task<string> Folders(PreviewAssetStore assets,
        [Description("Engine folder, e.g. /Game/VFX, or empty for roots.")] string folder = "",
        [Description("Zero-based folder page offset.")] int offset = 0,
        [Description("Optional editor peer GUID.")] string? clientId = null) =>
        Json(await assets.FoldersAsync(folder, offset, Peer(clientId)));

    [McpServerTool(Name = "libera_asset_details", ReadOnly = true, OpenWorld = false)]
    [Description("Read actual mesh material slots, overridable scalar/vector/texture parameters, texture dimensions and local metadata. Material schema generates stable dynamic pin IDs including slot and parameter association.")]
    public static async Task<string> Details(PreviewAssetStore assets, AssetMetadataStore metadata,
        [Description("Engine object path from libera_assets_search or local metadata.")] string path,
        [Description("Optional editor peer GUID from libera_preview_clients.")] string? clientId = null)
    {
        var asset = await assets.DetailsAsync(path, Peer(clientId));
        return Json(new { asset, metadata = metadata.Get(asset.Engine, asset.Path),
            materialInputs = RenderNodeSchema.Describe(RenderNodeSchema.Encode(asset)) });
    }

    [McpServerTool(Name = "libera_asset_thumbnail", ReadOnly = true, OpenWorld = false)]
    [Description("Fetch a bounded PNG texture/asset thumbnail from the selected editor. includeImage defaults false to avoid unnecessary image data; true returns base64 PNG for local inspection.")]
    public static async Task<string> Thumbnail(PreviewAssetStore assets,
        [Description("Engine object path from libera_assets_search.")] string path,
        [Description("Requested thumbnail size in pixels.")] int size = 128,
        [Description("Include the PNG as base64 instead of metadata only.")] bool includeImage = false,
        [Description("Optional editor peer GUID from libera_preview_clients.")] string? clientId = null)
    {
        var image = await assets.ThumbnailAsync(path, size, Peer(clientId));
        return Json(new { image.Path, image.Mime, image.Width, image.Height, bytes = image.Image.Length,
            imageBase64 = includeImage ? Convert.ToBase64String(image.Image) : null });
    }

    [McpServerTool(Name = "libera_asset_metadata_search", ReadOnly = true, OpenWorld = false)]
    [Description("Search Libera-only asset descriptions, tags and semantic usage (e.g flame hitmark, flash impact). Returns atlas layout/FPS for automatic sprite sample authoring. Does not query or modify engine asset files.")]
    public static string MetadataSearch(AssetMetadataStore metadata,
        [Description("Text to match in paths, descriptions, tags and semantic usage.")] string query,
        [Description("Optional engine name filter.")] string? engine = null,
        [Description("Maximum number of matching metadata rows.")] int limit = 100) =>
        Json(metadata.Search(query, engine, limit));

    [McpServerTool(Name = "libera_asset_metadata_set", Destructive = false, OpenWorld = false)]
    [Description("Store local semantic description/tags and sprite atlas columns/rows/FPS for an asset. Metadata belongs to Libera FX only; original engine assets are never edited.")]
    public static string MetadataSet(AssetMetadataStore metadata,
        [Description("Engine name owning the asset.")] string engine,
        [Description("Engine binding path identifying this asset.")] string path,
        [Description("Human-readable asset notes stored in LIBERA FX.")] string description = "",
        [Description("Intended usage, e.g. fire hitmark or flash impact.")] string semanticUsage = "",
        [Description("Search tags stored in LIBERA FX.")] string[]? tags = null,
        [Description("Number of atlas columns.")] int columns = 1,
        [Description("Number of atlas rows.")] int rows = 1,
        [Description("Atlas playback frames per second.")] double framesPerSecond = 12)
    {
        metadata.Set(engine, path, new AssetMetadata(engine, path, description, tags, semanticUsage,
            columns, rows, framesPerSecond));
        return Json(metadata.Get(engine, path));
    }

    [McpServerTool(Name = "libera_render_create_model", Destructive = false, OpenWorld = false)]
    [Description("Fetch a model's actual assigned material schema and add a model draw node with dynamic scalar/vector pins and texture properties, without replacing existing graph nodes.")]
    public static async Task<string> CreateModel(ActiveGraphProvider graph, PreviewAssetStore assets,
        [Description("Model object path from libera_assets_search.")] string path,
        [Description("Optional horizontal graph coordinate.")] double? x = null,
        [Description("Optional vertical graph coordinate.")] double? y = null,
        [Description("Optional editor peer GUID from libera_preview_clients.")] string? clientId = null)
    {
        var asset = await assets.DetailsAsync(path, Peer(clientId));
        return await Dispatcher.UIThread.InvokeAsync(() => Json(RenderNodeAuthoring.CreateModel(graph.Graph, asset, x, y).Id));
    }

    [McpServerTool(Name = "libera_render_create_sprite", Destructive = false, OpenWorld = false)]
    [Description("Create a textured sprite or sprite-animation node. Uses local atlas metadata by default; fps=0 creates a static sprite. Existing graph content is preserved.")]
    public static async Task<string> CreateSprite(ActiveGraphProvider graph, PreviewAssetStore assets,
        AssetMetadataStore metadata,
        [Description("Texture object path from libera_assets_search.")] string path,
        [Description("Override the sprite atlas columns from metadata.")] int? columns = null,
        [Description("Override the sprite atlas rows from metadata.")] int? rows = null,
        [Description("Override atlas playback speed. Zero creates a static sprite.")] double? fps = null,
        [Description("Optional horizontal graph coordinate.")] double? x = null,
        [Description("Optional vertical graph coordinate.")] double? y = null,
        [Description("Optional editor peer GUID from libera_preview_clients.")] string? clientId = null)
    {
        var asset = await assets.DetailsAsync(path, Peer(clientId));
        var settings = metadata.Get(asset.Engine, asset.Path);
        return await Dispatcher.UIThread.InvokeAsync(() => Json(RenderNodeAuthoring.CreateSprite(graph.Graph, asset,
            columns ?? settings.AtlasColumns, rows ?? settings.AtlasRows, fps ?? settings.FramesPerSecond, x, y).Id));
    }

    [McpServerTool(Name = "libera_render_assign_asset", Destructive = false, OpenWorld = false)]
    [Description("Assign an asset to a draw node, updating dynamic material pins undoably. Compatible pose/material connections and existing values are kept; only invalidated material links are removed.")]
    public static async Task<string> Assign(ActiveGraphProvider graph, PreviewAssetStore assets,
        [Description("Draw node GUID in the current graph.")] string nodeId,
        [Description("Replacement engine object path from libera_assets_search.")] string path,
        [Description("Optional editor peer GUID from libera_preview_clients.")] string? clientId = null)
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
        AssetMetadataStore metadata, EditorPlayback playback,
        [Description("color-box, sprite, sprite-animation, or hitmark.")] string style,
        [Description("Engine object path; omit to resolve via local metadata.")] string? path = null,
        [Description("Search text that must match exactly one local metadata entry when path is absent. Defaults to style.")] string? semanticQuery = null,
        [Description("Hitmark duration in seconds.")] double duration = 0.5,
        [Description("Start playback immediately after adding the sample.")] bool autoplay = false,
        [Description("Optional editor peer GUID from libera_preview_clients.")] string? clientId = null)
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
    public static string RenderStatus(PreviewAssetStore assets,
        [Description("Optional editor peer GUID from libera_preview_clients.")] string? clientId = null) =>
        Json(assets.RenderStatus(Peer(clientId)));

    [McpServerTool(Name = "libera_create_game_effect", Destructive = false, OpenWorld = false)]
    [Description("Append an editable game-effect prototype using ordinary CPU particle/procedural graphs. Archetypes: HitImpact (打撃), HitSlash (斬撃), FireMagic (炎), MagicCircle (魔法陣), DarkMagic (闇), LightMagic (光), HealingMagic (回復), DustPuff (土煙), Explosion (爆発), Rain (雨), Snow (雪), Lightning (雷). Rain/snow follow camera position; lightning stays world anchored. No imported images or dedicated effect nodes. Interpret FF/Dragon Quest references as timing, silhouette and palette briefs, then tune the source graph. Verify actual appearance with preview status and a captured frame.")]
    public static async Task<string> CreateGameEffect(ActiveGraphProvider graph,
        [Description("HitImpact, HitSlash, FireMagic, MagicCircle, DarkMagic, LightMagic, HealingMagic, DustPuff, Explosion, Rain, Snow or Lightning.")] string archetype,
        [Description("Particle lifetime or fade duration, 0.05–30 seconds. Omit for a suitable per-template default.")] double? duration = null,
        [Description("auto, fire, dark, light, heal, ice or earth.")] string theme = "auto") =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var seconds = duration ?? GameEffectTemplates.DefaultDuration(archetype);
            var sample = GameEffectTemplates.Create(archetype, seconds, theme);
            var before = graph.Graph.Nodes.Select(node => node.Id).ToHashSet();
            graph.Graph.PasteSnapshot(sample, 40, 40);
            return Json(new { archetype, duration = seconds, theme, runtimeExecuted = false,
                addedNodes = graph.Graph.Nodes.Where(node => !before.Contains(node.Id)).Select(node => node.Id).ToArray() });
        });

    [McpServerTool(Name = "libera_create_particle_sample", Destructive = false, OpenWorld = false)]
    [Description("Append an editable CPU/GPU particle or Fluid2D sample as one undoable edit. No imported assets or hand-written GPU JSON needed. Styles: CpuRadial, CpuFountain, CpuMeshSurface, Fireworks, GpuCurl100k, GpuCurlTurbulence, GpuFluidVelocity, FluidSmoke, FluidInk, FluidWater, FluidShallowWater, FluidFire. All source graphs use X-right/Y-up/Z-forward and metres. GPU/fluid visibility requires a connected supported GPU host; capacity is not live count.")]
    public static async Task<string> CreateParticleSample(ActiveGraphProvider graph, EditorPlayback playback,
        [Description("Exact sample style; use CpuFountain for the simple fountain starter.")] string style = "CpuFountain",
        [Description("Start playback with an appropriate timeline range.")] bool autoplay = false) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var sample = ParticleSamples.Create(style);
            var before = graph.Graph.Nodes.Select(node => node.Id).ToHashSet();
            graph.Graph.PasteSnapshot(sample, 40, 40);
            if (autoplay)
            {
                playback.Pause(); playback.StartText = "0";
                playback.EndText = style == "Fireworks" ? "300" : "1200";
                playback.Frame = 0; playback.Play();
            }
            return Json(new { style, coordinates = "X-right/Y-up/Z-forward; metres",
                addedNodes = graph.Graph.Nodes.Where(node => !before.Contains(node.Id)).Select(node => node.Id).ToArray(),
                runtimeExecuted = false });
        });

    [McpServerTool(Name = "gpu_runtime_status", ReadOnly = true, OpenWorld = false)]
    [Description("Read the selected GPU-capable editor's last actual GPU preview feedback, interpreter mode, program sequence, container capacity, playback state and errors. Null means no feedback yet; capacity is not a live particle count. Source-export runtimeExecuted remains false.")]
    public static string GpuRuntimeStatus(PreviewAssetStore assets,
        [Description("Optional editor peer GUID from libera_preview_clients.")] string? clientId = null) =>
        Json(assets.GpuStatus(Peer(clientId)));

    [McpServerTool(Name = "libera_preview_reconnect", Destructive = false, OpenWorld = false)]
    [Description("Ask exactly the selected editor to reconnect its preview transport. Acknowledgement means the request was received; verify a new peer in libera_preview_clients. Never restarts the entire engine.")]
    public static async Task<string> Reconnect(PreviewAssetStore assets,
        [Description("Exact editor peer GUID to reconnect, from libera_preview_clients.")] string clientId)
    {
        await assets.ReconnectAsync(Peer(clientId)!.Value);
        return """{"acknowledged":true}""";
    }

    [McpServerTool(Name = "libera_preview_playback", Destructive = false, OpenWorld = false)]
    [Description("Control the shared editor timeline directly, rather than graph-only keyboard simulation. Frame is clamped to the existing playback range.")]
    public static async Task<string> Playback(EditorPlayback playback,
        [Description("True plays, false pauses, and omission preserves the current state.")] bool? playing = null,
        [Description("Optional frame number to seek to, not seconds.")] double? frame = null,
        [Description("Playback speed multiplier: 0 pauses effect time, 0.25 is quarter speed, 1 is normal.")] double? timeScale = null) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (timeScale is { } ratio)
            {
                if (!double.IsFinite(ratio) || ratio < 0 || ratio > 100) throw new McpException("Invalid Time Scale.");
                playback.TimeScale = ratio;
            }
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
