using System.Text.Json;

namespace LiberaFx.Services;

public sealed class PreviewAssetStore(PreviewTelemetryStore telemetry)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        { PropertyNameCaseInsensitive = true };
    private readonly object _sync = new();
    private readonly Dictionary<Guid, PendingRequest> _pending = [];
    private readonly Dictionary<Guid, JsonElement> _renderStatus = [];
    private sealed record PendingRequest(Guid Peer, string Operation, TaskCompletionSource<JsonElement> Completion);

    public bool Supports(Guid id, string capability = "assets") => telemetry.Clients().Any(peer =>
        peer.ClientId == id && peer.Protocol == 3 && peer.Role == "editor" && peer.Capabilities.Contains(capability));

    public Guid SelectPeer(Guid? id = null)
    {
        var peers = telemetry.Clients().Where(peer => Supports(peer.ClientId)).ToArray();
        if (id is { } selected && peers.Any(peer => peer.ClientId == selected)) return selected;
        if (id is null && peers.Length == 1) return peers[0].ClientId;
        throw new InvalidOperationException(id.HasValue ? "Selected asset editor is disconnected or unsupported." :
            "Select one asset editor explicitly when multiple editors are connected.");
    }

    public async Task<EngineAssetPage> CatalogAsync(string query = "", string assetClass = "", int offset = 0,
        int limit = 32, Guid? clientId = null)
    {
        if (query.Length > 256 || assetClass is not ("" or "model" or "texture" or "material") ||
            offset < 0 || limit is < 1 or > 64) throw new ArgumentException("Invalid asset catalog query.");
        var response = await RequestAsync("catalog", clientId, request =>
            new { type = "asset_catalog", requestId = request, query, assetClass, offset, limit });
        var assets = response.GetProperty("assets").EnumerateArray().Select(ParseAsset).ToArray();
        var total = response.GetProperty("total").GetInt32();
        var receivedOffset = response.GetProperty("offset").GetInt32();
        if (assets.Length > limit || total < 0 || receivedOffset != offset ||
            assets.Length > 0 && total < offset + assets.Length)
            throw new InvalidDataException("Invalid asset catalog response.");
        return new(assets, total, receivedOffset);
    }

    public async Task<EngineAsset> DetailsAsync(string path, Guid? clientId = null)
    {
        ValidateAssetPath(path);
        var response = await RequestAsync("details", clientId, request =>
            new { type = "asset_details", requestId = request, path });
        var asset = ParseAsset(response.GetProperty("asset"));
        if (asset.Path != path) throw new InvalidDataException("Asset details do not match the requested path.");
        return asset;
    }

    public async Task<EngineAssetThumbnail> ThumbnailAsync(string path, int size = 128, Guid? clientId = null)
    {
        ValidateAssetPath(path);
        if (size is < 16 or > 256) throw new ArgumentException("Thumbnail size must be 16..256.");
        var response = await RequestAsync("thumbnail", clientId, request =>
            new { type = "asset_thumbnail", requestId = request, path, size });
        var width = response.GetProperty("width").GetInt32();
        var height = response.GetProperty("height").GetInt32();
        var mime = response.GetProperty("mime").GetString();
        if (response.GetProperty("path").GetString() != path || mime != "image/png" ||
            width is < 1 or > 256 || height is < 1 or > 256)
            throw new InvalidDataException("Invalid thumbnail metadata.");
        var image = Convert.FromBase64String(response.GetProperty("imageBase64").GetString() ?? "");
        if (image.Length is < 24 or > 262144 ||
            !image.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(16)) != width ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(20)) != height)
            throw new InvalidDataException("Invalid PNG thumbnail.");
        return new(path, mime, image, width, height);
    }

    public async Task ReconnectAsync(Guid clientId)
    {
        _ = await RequestAsync("reconnect", clientId, request => new { type = "reconnect", requestId = request });
    }

    private async Task<JsonElement> RequestAsync(string operation, Guid? clientId, Func<Guid, object> command)
    {
        var peer = SelectPeer(clientId);
        var requestId = Guid.NewGuid();
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            if (_pending.Count >= 16) throw new InvalidOperationException("Asset request queue is full.");
            _pending.Add(requestId, new(peer, operation, completion));
        }
        try
        {
            await telemetry.SceneControlAsync(JsonSerializer.SerializeToUtf8Bytes(command(requestId), JsonOptions), peer);
            var reply = await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (reply.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                throw new InvalidOperationException(error.GetString() ?? "Engine asset operation failed.");
            return reply;
        }
        finally { lock (_sync) _pending.Remove(requestId); }
    }

    public bool Receive(Guid peer, ReadOnlyMemory<byte> json)
    {
        if (json.Length is 0 or > CompiledPreviewProtocol.MaxSceneBytes || !Supports(peer)) return false;
        try
        {
            _ = new System.Text.UTF8Encoding(false, true).GetString(json.Span);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            CompiledPreviewProtocol.ValidateUniqueProperties(root);
            var type = root.GetProperty("type").GetString();
            if (type == "render_status")
            {
                if (!Supports(peer, "rendering") || root.GetProperty("programSequence").GetInt64() < 0 ||
                    !double.IsFinite(root.GetProperty("timeSeconds").GetDouble()) ||
                    root.GetProperty("nodes").GetArrayLength() > 128 ||
                    root.GetProperty("errors").GetArrayLength() > 128) return false;
                lock (_sync) _renderStatus[peer] = root.Clone();
                return true;
            }
            if (type != "asset_result" || !Guid.TryParseExact(root.GetProperty("requestId").GetString(), "D", out var id))
                return false;
            PendingRequest? pending;
            lock (_sync) _pending.TryGetValue(id, out pending);
            if (pending is null) return true;
            if (pending.Peer != peer || pending.Operation != root.GetProperty("operation").GetString()) return false;
            pending.Completion.TrySetResult(root.Clone());
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or
            FormatException or System.Text.DecoderFallbackException or OverflowException)
        { return false; }
    }

    public JsonElement? RenderStatus(Guid? clientId = null)
    {
        var peer = SelectPeer(clientId);
        lock (_sync) return _renderStatus.TryGetValue(peer, out var result) ? result : null;
    }

    public void Disconnect(Guid peer)
    {
        lock (_sync)
        {
            _renderStatus.Remove(peer);
            foreach (var pending in _pending.Values.Where(request => request.Peer == peer))
                pending.Completion.TrySetException(new InvalidOperationException("Asset editor disconnected."));
        }
    }

    public static void ValidateAssetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 ||
            !(path.StartsWith("/Game/", StringComparison.Ordinal) || path.StartsWith("/Engine/", StringComparison.Ordinal)) ||
            path.Contains("..", StringComparison.Ordinal) || path.IndexOfAny(['\\', ':']) >= 0 || path.Any(char.IsControl))
            throw new ArgumentException("Use an Unreal /Game/ or /Engine/ object path.");
    }

    private static EngineAsset ParseAsset(JsonElement value)
    {
        var asset = value.Deserialize<EngineAsset>(JsonOptions) ?? throw new InvalidDataException("Invalid asset.");
        ValidateAssetPath(asset.Path);
        if (string.IsNullOrWhiteSpace(asset.Name) || asset.Name.Length > 256 || asset.Engine != "Unreal" ||
            asset.AssetClass is not ("model" or "texture" or "material") ||
            asset.Width < 0 || asset.Height < 0 || asset.Materials?.Count > 128)
            throw new InvalidDataException("Invalid engine asset metadata.");
        foreach (var material in asset.Materials ?? [])
        {
            ValidateAssetPath(material.Path);
            if (material.Slot < 0 || material.Slot > 127 || material.Parameters.Count > 256)
                throw new InvalidDataException("Invalid material slot.");
            foreach (var parameter in material.Parameters)
            {
                if (string.IsNullOrWhiteSpace(parameter.Name) || parameter.Name.Length > 256 ||
                    parameter.Kind is not ("scalar" or "vector" or "texture"))
                    throw new InvalidDataException("Invalid material parameter.");
                if (parameter.Kind == "scalar" && !double.IsFinite(parameter.Value.GetDouble()) ||
                    parameter.Kind == "vector" && (parameter.Value.GetArrayLength() != 4 ||
                        parameter.Value.EnumerateArray().Any(lane => !double.IsFinite(lane.GetDouble()))))
                    throw new InvalidDataException("Non-finite material parameter.");
                if (parameter.Kind == "texture" && parameter.Value.ValueKind != JsonValueKind.Null)
                    ValidateAssetPath(parameter.Value.GetString() ?? "");
            }
        }
        return asset;
    }
}
