using System.Text.Json;

namespace LiberaFx.Services;

public sealed record PreviewMetric(string Event, Guid? NodeId, ulong Calls, ulong InclusiveNs,
    ulong ExclusiveNs, ulong MinNs, ulong MaxNs)
{
    public double InclusiveMs => InclusiveNs / 1_000_000d;
    public double ExclusiveMs => ExclusiveNs / 1_000_000d;
    public double MinMs => MinNs / 1_000_000d;
    public double MaxMs => MaxNs / 1_000_000d;
}

public sealed record PreviewProfile(long ProgramSequence, ulong SnapshotId, ulong Dropped,
    IReadOnlyList<PreviewMetric> Metrics);
public sealed record PreviewValue(Guid NodeId, IReadOnlyList<double> Components);
public sealed record PreviewValues(long ProgramSequence, double EvaluationTime, IReadOnlyList<PreviewValue> Values);
public sealed record PreviewProfileControl(bool Enabled, bool Nodes, bool Accessors, int IntervalMs);
public sealed record PreviewPeer(Guid ClientId, int Protocol, string? Engine, string? Role,
    IReadOnlyList<string> Capabilities, PreviewProfile? Profile, PreviewValues? Values,
    bool ProfilePending, PreviewProfileControl? RequestedProfile, IReadOnlyList<Guid> WatchedNodes,
    string? ViewerName = null, string? EngineVersion = null, string? Platform = null,
    string? RemoteAddress = null, DateTimeOffset ConnectedAt = default, DateTimeOffset LastReceivedAt = default);

// Host-scoped, bounded and independent of the UI dispatcher. Only complete snapshots are published.
public sealed class PreviewTelemetryStore
{
    public const int MaxFrameBytes = 128 * 1024;
    public const int MaxMetrics = 10013;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, PeerState> _peers = [];

    private sealed class PeerState(int protocol, Func<byte[], Task> send)
    {
        public readonly int Protocol = protocol;
        public readonly Func<byte[], Task> Send = send;
        public string? Engine;
        public string? Role;
        public string[] Capabilities = [];
        public PreviewProfile? Profile;
        public PreviewValues? Values;
        public PendingProfile? Pending;
        public PreviewProfileControl? RequestedProfile;
        public IReadOnlyList<Guid> WatchedNodes = System.Array.Empty<Guid>();
        public string? ViewerName;
        public string? EngineVersion;
        public string? Platform;
        public string? RemoteAddress;
        public readonly DateTimeOffset ConnectedAt = DateTimeOffset.UtcNow;
        public DateTimeOffset LastReceivedAt = DateTimeOffset.UtcNow;
    }

    private sealed class PendingProfile(long sequence, ulong id, int total, ulong dropped)
    {
        public readonly long Sequence = sequence;
        public readonly ulong Id = id;
        public readonly int Total = total;
        public readonly ulong Dropped = dropped;
        public readonly List<PreviewMetric> Metrics = new(total);
        public readonly HashSet<(string, Guid?)> Keys = [];
    }

    public void Connect(Guid id, int protocol, Func<byte[], Task> send, string? remoteAddress = null)
    {
        lock (_sync)
        {
            if (_peers.Count >= 16 || !_peers.TryAdd(id, new(protocol, send)))
                throw new InvalidOperationException("Preview peer limit reached.");
            _peers[id].RemoteAddress = remoteAddress;
        }
    }

    public void Disconnect(Guid id) { lock (_sync) _peers.Remove(id); }

    public IReadOnlyList<PreviewPeer> Clients(Guid? id = null)
    {
        lock (_sync)
        {
            if (id.HasValue && !_peers.ContainsKey(id.Value))
                throw new InvalidOperationException("Preview client is disconnected or unknown.");
            return _peers.Where(pair => !id.HasValue || pair.Key == id.Value).Select(pair =>
            {
                var p = pair.Value;
                return new PreviewPeer(pair.Key, p.Protocol, p.Engine, p.Role,
                    System.Array.AsReadOnly(p.Capabilities), p.Profile, p.Values, p.Pending is not null,
                    p.RequestedProfile, p.WatchedNodes, p.ViewerName, p.EngineVersion, p.Platform,
                    p.RemoteAddress, p.ConnectedAt, p.LastReceivedAt);
            }).ToArray();
        }
    }

    public bool Receive(Guid id, ReadOnlyMemory<byte> utf8)
    {
        if (utf8.Length == 0 || utf8.Length > MaxFrameBytes) return false;
        try
        {
            _ = new System.Text.UTF8Encoding(false, true).GetString(utf8.Span);
            using var json = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 12 });
            var root = json.RootElement;
            ValidateObject(root);
            var type = Text(root, "type", 32);
            lock (_sync)
            {
                if (!_peers.TryGetValue(id, out var peer) || peer.Protocol != 3) return false;
                peer.LastReceivedAt = DateTimeOffset.UtcNow;
                switch (type)
                {
                    case "hello":
                        if (peer.Engine is not null || Int(root, "version") != 1) return false;
                        var engine = Text(root, "engine", 64);
                        var role = Text(root, "role", 16);
                        if (role is not ("editor" or "runtime" or "viewer")) return false;
                        var caps = Array(root, "capabilities", 16);
                        var capabilities = caps.EnumerateArray().Select(c => String(c, 32)).ToArray();
                        if (capabilities.Distinct(StringComparer.Ordinal).Count() != capabilities.Length) return false;
                        var viewerName = OptionalText(root, "viewerName", 128);
                        var engineVersion = OptionalText(root, "engineVersion", 64);
                        var platform = OptionalText(root, "platform", 64);
                        peer.Engine = engine;
                        peer.Role = role;
                        peer.Capabilities = capabilities;
                        peer.ViewerName = viewerName;
                        peer.EngineVersion = engineVersion;
                        peer.Platform = platform;
                        return true;
                    case "profile":
                        if (!peer.Capabilities.Contains("profiling", StringComparer.Ordinal)) return false;
                        return ReceiveProfile(peer, root);
                    case "values":
                        if (!peer.Capabilities.Contains("values", StringComparer.Ordinal)) return false;
                        return ReceiveValues(peer, root);
                    default: return false;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or
            OverflowException or KeyNotFoundException or System.Text.DecoderFallbackException)
        {
            return false;
        }
    }

    private static string? OptionalText(JsonElement root, string name, int limit) =>
        root.TryGetProperty(name, out var value) ? String(value, limit) : null;

    public Task SceneControlAsync(ReadOnlyMemory<byte> json, Guid clientId, string capability = "assets")
    {
        Func<byte[], Task> send;
        lock (_sync)
        {
            if (!_peers.TryGetValue(clientId, out var peer) || peer.Protocol != 3 ||
                peer.Role != "editor" || !peer.Capabilities.Contains(capability, StringComparer.Ordinal))
                throw new InvalidOperationException("Selected editor does not support this scene operation.");
            send = peer.Send;
        }
        return send(CompiledPreviewProtocol.CreateScene(json.Span));
    }

    private static bool ReceiveProfile(PeerState peer, JsonElement root)
    {
        var sequence = Sequence(root);
        var id = UInt(root, "snapshotId");
        var offset = Int(root, "offset");
        var total = Int(root, "total");
        var dropped = UInt(root, "dropped");
        var metrics = Array(root, "metrics", 256);
        var length = metrics.GetArrayLength();
        if (total is < 0 or > MaxMetrics || offset < 0 || offset > total ||
            length > total - offset || (total > 0 && length == 0)) return false;
        var parsed = new List<PreviewMetric>(length);
        foreach (var metric in metrics.EnumerateArray())
        {
            ValidateObject(metric);
            var node = metric.GetProperty("nodeId");
            Guid? nodeId = node.ValueKind == JsonValueKind.Null ? null : GuidValue(node);
            var calls = UInt(metric, "calls");
            var inclusive = UInt(metric, "inclusiveNs");
            var exclusive = UInt(metric, "exclusiveNs");
            var min = UInt(metric, "minNs");
            var max = UInt(metric, "maxNs");
            if (exclusive > inclusive || min > max ||
                (calls == 0 && (inclusive != 0 || exclusive != 0 || min != 0 || max != 0)) ||
                (calls > 0 && max > inclusive)) return false;
            parsed.Add(new(Text(metric, "event", 64), nodeId, calls, inclusive, exclusive, min, max));
        }
        if (offset == 0)
        {
            if (peer.Profile is { } previous &&
                (sequence < previous.ProgramSequence || (sequence == previous.ProgramSequence && id <= previous.SnapshotId)))
                return false;
            if (peer.Pending is { } old &&
                (sequence < old.Sequence || (sequence == old.Sequence && id <= old.Id))) return false;
            peer.Pending = new(sequence, id, total, dropped);
        }
        var pending = peer.Pending;
        if (pending is null || pending.Sequence != sequence || pending.Id != id || pending.Total != total ||
            pending.Dropped != dropped || pending.Metrics.Count != offset) return false;
        foreach (var metric in parsed)
            if (!pending.Keys.Add((metric.Event, metric.NodeId))) return false;
        pending.Metrics.AddRange(parsed);
        if (pending.Metrics.Count == total)
        {
            peer.Profile = new(sequence, id, dropped, pending.Metrics.AsReadOnly());
            peer.Pending = null;
        }
        return true;
    }

    private static bool ReceiveValues(PeerState peer, JsonElement root)
    {
        var sequence = Sequence(root);
        var time = Finite(root.GetProperty("evaluationTime"));
        var values = Array(root, "values", 64);
        var parsed = new List<PreviewValue>(values.GetArrayLength());
        var ids = new HashSet<Guid>();
        foreach (var value in values.EnumerateArray())
        {
            ValidateObject(value);
            var nodeId = GuidValue(value.GetProperty("nodeId"));
            var components = Array(value, "components", 4);
            if (components.GetArrayLength() == 0 || !ids.Add(nodeId)) return false;
            parsed.Add(new(nodeId, System.Array.AsReadOnly(components.EnumerateArray().Select(Finite).ToArray())));
        }
        if (peer.Values is { } previous && sequence < previous.ProgramSequence) return false;
        peer.Values = new(sequence, time, parsed.AsReadOnly());
        return true;
    }

    public async Task ProfileControlAsync(bool enabled, bool nodes, bool accessors, int intervalMs, Guid? clientId = null)
    {
        if (intervalMs is < 100 or > 5000) throw new ArgumentException("intervalMs must be 100..5000.");
        var control = new PreviewProfileControl(enabled, nodes, accessors, intervalMs);
        var (id, peer) = Resolve(clientId, "profiling");
        await peer.Send(CompiledPreviewProtocol.CreateControl(JsonSerializer.SerializeToUtf8Bytes(
            new { type = "profile_control", enabled, nodes, accessors, intervalMs })));
        lock (_sync)
            if (_peers.TryGetValue(id, out var current) && ReferenceEquals(peer, current)) current.RequestedProfile = control;
    }

    public async Task WatchValuesAsync(IEnumerable<Guid> nodeIds, Guid? clientId = null)
    {
        var ids = nodeIds.Take(65).ToArray();
        if (ids.Length > 64 || ids.Any(nodeId => nodeId == Guid.Empty) || ids.Distinct().Count() != ids.Length)
            throw new ArgumentException("Watch at most 64 distinct nonempty node IDs.");
        var (id, peer) = Resolve(clientId, "values");
        await peer.Send(CompiledPreviewProtocol.CreateControl(JsonSerializer.SerializeToUtf8Bytes(
            new { type = "watch_values", nodeIds = ids })));
        lock (_sync)
            if (_peers.TryGetValue(id, out var current) && ReferenceEquals(peer, current)) current.WatchedNodes = System.Array.AsReadOnly(ids);
    }

    private (Guid, PeerState) Resolve(Guid? id, string capability)
    {
        lock (_sync)
        {
            if (!id.HasValue)
            {
                if (_peers.Count != 1) throw new InvalidOperationException("Select clientId unless exactly one preview client is connected.");
                id = _peers.Keys.Single();
            }
            if (!_peers.TryGetValue(id.Value, out var peer)) throw new InvalidOperationException("Preview client is disconnected or unknown.");
            if (peer.Protocol != 3 || !peer.Capabilities.Contains(capability, StringComparer.Ordinal))
                throw new InvalidOperationException("Preview client has not advertised the required capability.");
            return (id.Value, peer);
        }
    }

    internal static void ValidateControl(JsonElement root)
    {
        ValidateObject(root);
        switch (Text(root, "type", 32))
        {
            case "profile_control":
                if (root.EnumerateObject().Count() != 5 || Int(root, "intervalMs") is < 100 or > 5000)
                    throw new InvalidOperationException("Invalid profile control.");
                root.GetProperty("enabled").GetBoolean();
                root.GetProperty("nodes").GetBoolean();
                root.GetProperty("accessors").GetBoolean();
                break;
            case "watch_values":
                if (root.EnumerateObject().Count() != 2) throw new InvalidOperationException("Invalid watch control.");
                var ids = Array(root, "nodeIds", 64).EnumerateArray().Select(GuidValue).ToArray();
                if (ids.Distinct().Count() != ids.Length) throw new InvalidOperationException("Duplicate watch IDs.");
                break;
            default: throw new InvalidOperationException("Unknown preview control.");
        }
    }

    private static void ValidateObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count())
            throw new InvalidOperationException("Expected an object with unique properties.");
    }

    private static JsonElement Array(JsonElement value, string key, int maximum)
    {
        var array = value.GetProperty(key);
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > maximum)
            throw new InvalidOperationException("Invalid array.");
        return array;
    }

    private static string Text(JsonElement value, string key, int maximum) => String(value.GetProperty(key), maximum);
    private static string String(JsonElement value, int maximum)
    {
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text.Any(char.IsControl))
            throw new InvalidOperationException("Invalid text.");
        return text;
    }
    private static Guid GuidValue(JsonElement value)
    {
        if (!Guid.TryParseExact(value.GetString(), "D", out var id) || id == Guid.Empty)
            throw new InvalidOperationException("Invalid node ID.");
        return id;
    }
    private static int Int(JsonElement value, string key) => value.GetProperty(key).GetInt32();
    private static ulong UInt(JsonElement value, string key) => value.GetProperty(key).GetUInt64();
    private static long Sequence(JsonElement value)
    {
        var sequence = value.GetProperty("programSequence").GetInt64();
        if (sequence < 0) throw new InvalidOperationException("Invalid program sequence.");
        return sequence;
    }
    private static double Finite(JsonElement value)
    {
        var number = value.GetDouble();
        if (!double.IsFinite(number)) throw new InvalidOperationException("Expected a finite number.");
        return number;
    }
}
