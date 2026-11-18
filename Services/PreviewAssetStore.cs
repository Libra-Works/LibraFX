using System.Text.Json;
using LiberaFx.Models.Resources;

namespace LiberaFx.Services;

public sealed class PreviewAssetStore(PreviewTelemetryStore telemetry)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        { PropertyNameCaseInsensitive = true };
    private readonly object _sync = new();
    private readonly Dictionary<Guid, PendingRequest> _pending = [];
    private readonly Dictionary<Guid, JsonElement> _renderStatus = [];
    private readonly Dictionary<Guid, JsonElement> _gpuStatus = [];
    private sealed record PendingRequest(Guid Peer, string Operation, string Capability, string ResultType,
        TaskCompletionSource<JsonElement> Completion);

    public bool Supports(Guid id, string capability = "assets") => telemetry.Clients().Any(peer =>
        peer.ClientId == id && peer.Protocol == 3 && peer.Role == "editor" && peer.Capabilities.Contains(capability));

    public Guid SelectPeer(Guid? id = null, string capability = "assets")
    {
        var peers = telemetry.Clients().Where(peer => Supports(peer.ClientId, capability)).ToArray();
        if (id is { } selected && peers.Any(peer => peer.ClientId == selected)) return selected;
        if (id is null && peers.Length == 1) return peers[0].ClientId;
        throw new InvalidOperationException(id.HasValue ? "Selected editor is disconnected or unsupported." :
            "Select one editor explicitly when multiple editors are connected.");
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

    public async Task<EngineProjectConnectionState> EngineProjectAsync(Guid clientId, string operation = "status")
    {
        if (operation is not ("status" or "initialize" or "keep" or "fork"))
            throw new ArgumentException("Unsupported project identity operation.", nameof(operation));
        var response = await RequestAsync(operation, clientId, "resourceIdentity", "resource_identity_result",
            request => new { type = "resource_identity_project", requestId = request, operation });
        var status = RequiredText(response, "status", 64);
        var projectState = response.TryGetProperty("projectState", out var stateValue) &&
            stateValue.ValueKind == JsonValueKind.String ? stateValue.GetString() : status;
        EngineProjectIdentity? project = null;
        if (response.TryGetProperty("project", out var projectValue) && projectValue.ValueKind == JsonValueKind.Object)
        {
            var engine = RequiredText(projectValue, "engine", 64);
            var peer = telemetry.Clients(clientId).Single();
            if (!string.Equals(peer.Engine, engine, StringComparison.Ordinal))
                throw new InvalidDataException("Editor project engine does not match the connected peer.");
            var projectIdText = projectValue.TryGetProperty("projectId", out var idValue) &&
                idValue.ValueKind == JsonValueKind.String ? idValue.GetString() : null;
            if (Guid.TryParseExact(projectIdText, "D", out var projectId) && projectId != Guid.Empty)
                project = new EngineProjectIdentity(engine, projectId);
            else if (!string.Equals(projectState, "Unregistered", StringComparison.Ordinal))
                throw new InvalidDataException("Editor returned an invalid engine project GUID.");
        }
        var detail = response.TryGetProperty("detail", out var detailValue) &&
            detailValue.ValueKind == JsonValueKind.String ? detailValue.GetString() : null;
        return new(project, projectState ?? status, status, detail);
    }

    public async Task<ResourceProbeResult> ProbeResourceAsync(Guid clientId, EngineProjectIdentity project,
        OpaqueBinding binding)
    {
        ValidateProjectRequest(clientId, project);
        ValidateOpaqueBinding(binding);
        var response = await RequestAsync("probe", clientId, "resourceIdentity", "resource_identity_result",
            request => new
            {
                type = "resource_identity_probe", requestId = request, projectId = project.ProjectId.ToString("D"),
                binding = new { binding.Format, binding.Value }
            });
        return ParseProbe(response, project);
    }

    public async Task<ResourceProbeResult> ProbeParameterAsync(Guid clientId, EngineProjectIdentity project,
        OpaqueBinding resourceBinding, OpaqueBinding parameterBinding)
    {
        ValidateProjectRequest(clientId, project);
        ValidateOpaqueBinding(resourceBinding);
        ValidateOpaqueBinding(parameterBinding);
        var response = await RequestAsync("probe", clientId, "resourceIdentity", "resource_identity_result",
            request => new
            {
                type = "resource_identity_probe", requestId = request, projectId = project.ProjectId.ToString("D"),
                resourceBinding = new { resourceBinding.Format, resourceBinding.Value },
                parameterBinding = new { parameterBinding.Format, parameterBinding.Value }
            });
        return ParseProbe(response, project);
    }

    public async Task<IReadOnlyList<EngineMaterialParameterMetadata>> MaterialSchemaAsync(Guid clientId,
        EngineProjectIdentity project, OpaqueBinding materialBinding)
    {
        ValidateProjectRequest(clientId, project);
        ValidateOpaqueBinding(materialBinding);
        var response = await RequestAsync("schema", clientId, "resourceIdentity", "resource_identity_result",
            request => new
            {
                type = "resource_identity_schema", requestId = request,
                projectId = project.ProjectId.ToString("D"),
                resourceBinding = new { materialBinding.Format, materialBinding.Value }
            });
        if (ParseProbe(response, project).Status != ResourceResolutionStatus.Resolved)
        {
            var status = response.TryGetProperty("status", out var state) ? state.ToString() : "QueryFailure";
            var detail = response.TryGetProperty("detail", out var message) ? message.GetString() : null;
            throw new InvalidOperationException(detail ?? $"Could not read material parameter schema: {status}.");
        }
        if (!response.TryGetProperty("parameters", out var array) || array.ValueKind != JsonValueKind.Array ||
            array.GetArrayLength() > 256)
            throw new InvalidDataException("Editor returned an invalid material parameter schema.");
        var result = new List<EngineMaterialParameterMetadata>();
        var seen = new HashSet<(string, int, int)>();
        foreach (var item in array.EnumerateArray())
        {
            var name = RequiredText(item, "name", 256);
            var kind = RequiredText(item, "kind", 32);
            if (kind is not ("scalar" or "vector" or "texture"))
                throw new InvalidDataException("Editor returned an unsupported material parameter type.");
            var association = item.GetProperty("association").GetInt32();
            var index = item.GetProperty("index").GetInt32();
            if (association is < 0 or > 2 || index is < -1 or > 65535 ||
                !seen.Add((name, association, index)))
                throw new InvalidDataException("Editor returned duplicate or invalid material parameter locators.");
            var defaultValue = ParseSchemaJson(item, "defaultValueJson", required: true)!.Value;
            var minimum = ParseSchemaJson(item, "minimumJson", required: false);
            var maximum = ParseSchemaJson(item, "maximumJson", required: false);
            var definition = new MaterialParameterDefinition(Guid.NewGuid(), kind, name,
                defaultValue, minimum, maximum);
            ResourceIdentityStore.Validate(new ResourceIdentityDocument(1,
                [new ResourceDefinition(Guid.NewGuid(), "material", "Schema", [definition])], [], [], [], []));
            result.Add(new(name, kind, defaultValue, minimum, maximum, association, index));
        }
        return result.AsReadOnly();
    }

    public async Task<string?> DeliverUnityBindingsAsync(Guid clientId, EngineProjectIdentity project,
        IReadOnlyList<UnityRuntimeBindingDelivery> resources)
    {
        ValidateProjectRequest(clientId, project);
        if (project.Engine != "Unity") throw new ArgumentException("Unity binding delivery requires a Unity project.");
        if (resources is null || resources.Count is < 1 or > 256)
            throw new ArgumentException("Unity binding delivery must include 1..256 resources.");
        var response = await RequestAsync("deliver", clientId, "resourceIdentity", "resource_identity_result",
            request => new
            {
                type = "resource_identity_deliver", requestId = request,
                projectId = project.ProjectId.ToString("D"), resources
            });
        var status = RequiredText(response, "status", 64);
        if (status != nameof(ResourceResolutionStatus.Resolved))
            throw new InvalidOperationException(response.TryGetProperty("detail", out var detail) &&
                detail.ValueKind == JsonValueKind.String ? detail.GetString() : $"Unity binding delivery failed: {status}.");
        return response.TryGetProperty("assetPath", out var path) && path.ValueKind == JsonValueKind.String
            ? path.GetString() : null;
    }

    private async Task<JsonElement> RequestAsync(string operation, Guid? clientId, Func<Guid, object> command) =>
        await RequestAsync(operation, clientId, "assets", "asset_result", command);

    private async Task<JsonElement> RequestAsync(string operation, Guid? clientId, string capability,
        string resultType, Func<Guid, object> command)
    {
        var peer = SelectPeer(clientId, capability);
        var requestId = Guid.NewGuid();
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            if (_pending.Count >= 16) throw new InvalidOperationException("Asset request queue is full.");
            _pending.Add(requestId, new(peer, operation, capability, resultType, completion));
        }
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(command(requestId), JsonOptions);
            if (bytes.Length > CompiledPreviewProtocol.MaxSceneBytes - CompiledPreviewProtocol.HeaderSize)
                throw new InvalidDataException("Resource identity request exceeds the preview message limit.");
            await telemetry.SceneControlAsync(bytes, peer, capability);
            var reply = await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (reply.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                throw new InvalidOperationException(error.GetString() ?? "Engine asset operation failed.");
            return reply;
        }
        finally { lock (_sync) _pending.Remove(requestId); }
    }

    public bool Receive(Guid peer, ReadOnlyMemory<byte> json)
    {
        if (json.Length is 0 or > CompiledPreviewProtocol.MaxSceneBytes ||
            !Supports(peer) && !Supports(peer, "gpuParticles") && !Supports(peer, "resourceIdentity")) return false;
        try
        {
            _ = new System.Text.UTF8Encoding(false, true).GetString(json.Span);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            CompiledPreviewProtocol.ValidateUniqueProperties(root);
            var type = root.GetProperty("type").GetString();
            if (type == "gpu_status")
            {
                if (!Supports(peer, "gpuParticles") || root.GetProperty("version").GetInt32() != 1 ||
                    root.GetProperty("programSequence").GetInt64() < 0 ||
                    root.GetProperty("runtimeExecuted").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    root.GetProperty("mode").GetString() != "GpuRegisterInterpreter" ||
                    root.GetProperty("programs").GetArrayLength() > GpuPreviewScene.MaxPrograms ||
                    root.GetProperty("errors").GetArrayLength() > 128) return false;
                var ids = new HashSet<Guid>();
                foreach (var program in root.GetProperty("programs").EnumerateArray())
                {
                    if (!program.GetProperty("nodeId").TryGetGuid(out var nodeId) || nodeId == Guid.Empty ||
                        !ids.Add(nodeId) || program.GetProperty("capacity").GetUInt32() == 0 ||
                        program.GetProperty("contentHash").GetString() is not { Length: <= 128 } ||
                        program.GetProperty("playing").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        return false;
                }
                foreach (var error in root.GetProperty("errors").EnumerateArray())
                    if (error.GetString() is not { Length: <= 4096 }) return false;
                lock (_sync)
                {
                    if (!_gpuStatus.TryGetValue(peer, out var previous) ||
                        root.GetProperty("programSequence").GetInt64() >= previous.GetProperty("programSequence").GetInt64())
                        _gpuStatus[peer] = root.Clone();
                }
                return true;
            }
            if (type == "resource_identity_result")
            {
                if (!Supports(peer, "resourceIdentity")) return false;
            }
            else if (!Supports(peer)) return false;
            if (type == "render_status")
            {
                if (!Supports(peer, "rendering") || root.GetProperty("programSequence").GetInt64() < 0 ||
                    !double.IsFinite(root.GetProperty("timeSeconds").GetDouble()) ||
                    root.GetProperty("nodes").GetArrayLength() > 128 ||
                    root.GetProperty("errors").GetArrayLength() > 128) return false;
                lock (_sync) _renderStatus[peer] = root.Clone();
                return true;
            }
            if (type is not ("asset_result" or "resource_identity_result") ||
                !Guid.TryParseExact(root.GetProperty("requestId").GetString(), "D", out var id))
                return false;
            PendingRequest? pending;
            lock (_sync) _pending.TryGetValue(id, out pending);
            if (pending is null) return true;
            if (pending.Peer != peer || pending.Operation != root.GetProperty("operation").GetString() ||
                pending.ResultType != type || !Supports(peer, pending.Capability)) return false;
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

    public JsonElement? GpuStatus(Guid? clientId = null)
    {
        var peer = SelectPeer(clientId, "gpuParticles");
        lock (_sync) return _gpuStatus.TryGetValue(peer, out var result) ? result : null;
    }

    public void Disconnect(Guid peer)
    {
        lock (_sync)
        {
            _renderStatus.Remove(peer);
            _gpuStatus.Remove(peer);
            foreach (var pending in _pending.Values.Where(request => request.Peer == peer))
                pending.Completion.TrySetException(new InvalidOperationException("Asset editor disconnected."));
        }
    }

    public static void ValidateAssetPath(string path) => EngineAssetPath.Validate(path);

    private void ValidateProjectRequest(Guid clientId, EngineProjectIdentity project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.ProjectId == Guid.Empty || project.Engine is not ("Unity" or "Unreal"))
            throw new ArgumentException("A verified Unity or Unreal project identity is required.", nameof(project));
        var peer = telemetry.Clients(clientId).SingleOrDefault();
        if (peer is null || !Supports(clientId, "resourceIdentity") ||
            !string.Equals(peer.Engine, project.Engine, StringComparison.Ordinal))
            throw new InvalidOperationException("Selected editor is disconnected or belongs to a different engine.");
    }

    private static void ValidateOpaqueBinding(OpaqueBinding binding)
    {
        if (binding is null || string.IsNullOrWhiteSpace(binding.Format) || binding.Format.Length > 256 ||
            string.IsNullOrWhiteSpace(binding.Value) || binding.Value.Length > 4096 ||
            binding.Format.Any(char.IsControl) || binding.Value.Any(char.IsControl))
            throw new ArgumentException("Opaque resource binding is invalid.", nameof(binding));
    }

    private static ResourceProbeResult ParseProbe(JsonElement response, EngineProjectIdentity expectedProject)
    {
        if (!response.TryGetProperty("project", out var project) || project.ValueKind != JsonValueKind.Object ||
            project.GetProperty("engine").GetString() != expectedProject.Engine ||
            !Guid.TryParseExact(project.GetProperty("projectId").GetString(), "D", out var projectId) ||
            projectId != expectedProject.ProjectId)
            return new(ResourceResolutionStatus.Offline, Detail: "The active editor project differs from the selected project.");
        var statusText = RequiredText(response, "status", 64);
        if (!Enum.TryParse<ResourceResolutionStatus>(statusText, out var status) ||
            status is ResourceResolutionStatus.Unregistered)
            return new(ResourceResolutionStatus.QueryFailure, Detail: "Editor returned an unsupported probe state.");
        var actualKind = response.TryGetProperty("actualKind", out var actual) && actual.ValueKind == JsonValueKind.String
            ? actual.GetString() : null;
        var detail = response.TryGetProperty("detail", out var detailValue) && detailValue.ValueKind == JsonValueKind.String
            ? detailValue.GetString() : null;
        if (status == ResourceResolutionStatus.Resolved && string.IsNullOrWhiteSpace(actualKind))
            return new(ResourceResolutionStatus.QueryFailure, Detail: "Resolved probe omitted the actual resource kind.");
        return new(status, actualKind, detail);
    }

    private static string RequiredText(JsonElement value, string name, int maximum)
    {
        var text = value.GetProperty(name).GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text.Any(char.IsControl))
            throw new InvalidDataException($"Editor response has invalid '{name}'.");
        return text;
    }

    private static JsonElement? ParseSchemaJson(JsonElement item, string field, bool required)
    {
        if (!item.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            if (required) throw new InvalidDataException($"Editor omitted material schema '{field}'.");
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Editor returned invalid material schema '{field}'.");
        if (!required && string.IsNullOrWhiteSpace(value.GetString())) return null;
        try
        {
            using var parsed = JsonDocument.Parse(value.GetString() ?? "");
            return parsed.RootElement.Clone();
        }
        catch (JsonException exception)
        { throw new InvalidDataException($"Editor returned malformed material schema '{field}'.", exception); }
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
