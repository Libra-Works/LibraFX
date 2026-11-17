using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;

namespace LiberaFx.Services;

public enum CompiledPreviewKind : ushort { Program = 1, Playback = 2, CompileError = 3, Control = 4, Scene = 5 }

public sealed record CompiledPreviewMessage(byte[] Bytes, CompiledPreviewKind Kind, PreviewPublication Publication)
{
    public byte[]? EvaluatedRenderScene { get; init; }
    public bool EditorNativeExecution { get; init; }
}

public static class CompiledPreviewProtocol
{
    public const int HeaderSize = 48;
    public const int MaxProgramBytes = 128 * 1024 * 1024;
    public const int MaxErrorBytes = 4096;
    public const int MaxControlBytes = 4096;
    public const int MaxSceneBytes = 1024 * 1024;
    private static readonly ConditionalWeakTable<GraphSnapshot, Lazy<Compilation>> Cache = new();

    private sealed record Compilation(byte[]? Bytes, byte[]? Error);

    public static byte[] CreateScene(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length is 0 or > MaxSceneBytes) throw new ArgumentException("Invalid scene payload size.");
        try
        {
            _ = new UTF8Encoding(false, true).GetString(utf8Json);
            using var json = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                type.GetString() is not ("asset_catalog" or "asset_details" or "asset_thumbnail" or "render_config" or "gpu_config" or "reconnect"))
                throw new ArgumentException("Unknown scene command.");
            ValidateUniqueProperties(json.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or DecoderFallbackException)
        { throw new ArgumentException("Invalid scene command JSON.", nameof(utf8Json), ex); }
        var bytes = new byte[HeaderSize + utf8Json.Length];
        "LFXP"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)CompiledPreviewKind.Scene);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44), (uint)utf8Json.Length);
        utf8Json.CopyTo(bytes.AsSpan(HeaderSize));
        return bytes;
    }

    internal static void ValidateUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate object property.");
                ValidateUniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateUniqueProperties(item);
    }

    public static byte[] CreateControl(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length is 0 or > MaxControlBytes) throw new ArgumentException("Invalid control payload size.");
        try
        {
            _ = new UTF8Encoding(false, true).GetString(utf8Json);
            using var json = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            PreviewTelemetryStore.ValidateControl(json.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or
            KeyNotFoundException or DecoderFallbackException or OverflowException)
        {
            throw new ArgumentException("Invalid preview control JSON.", nameof(utf8Json), ex);
        }
        var bytes = new byte[HeaderSize + utf8Json.Length];
        "LFXP"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)CompiledPreviewKind.Control);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44), (uint)utf8Json.Length);
        utf8Json.CopyTo(bytes.AsSpan(HeaderSize));
        return bytes;
    }

    private static Compilation Compile(GraphSnapshot graph)
    {
        try
        {
            GpuPreviewScene.Validate(graph);
            var bytes = VfxProgram.Compile(graph).ToBinary();
            if (bytes.Length == 0 || bytes.Length > MaxProgramBytes)
                throw new InvalidDataException("Compiled program exceeds the live preview size limit.");
            return new(bytes, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            var error = new byte[MaxErrorBytes];
            Encoding.UTF8.GetEncoder().Convert(ex.Message.AsSpan(), error.AsSpan(), true, out _, out var written, out _);
            return new(null, error[..written]);
        }
    }

    internal static CompiledPreviewMessage Create(PreviewPublication publication, bool playback, ulong baseSequence)
    {
        var result = playback ? null : Cache.GetValue(publication.Graph,
            graph => new Lazy<Compilation>(() => Compile(graph), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        var kind = playback ? CompiledPreviewKind.Playback :
            result!.Bytes is not null ? CompiledPreviewKind.Program : CompiledPreviewKind.CompileError;
        var payload = result?.Bytes ?? result?.Error ?? [];
        return CreatePayload(publication, kind, payload, baseSequence);
    }

    internal static CompiledPreviewMessage CreatePayload(PreviewPublication publication, CompiledPreviewKind kind,
        byte[] payload, ulong baseSequence, bool editorNative = false)
    {
        var playback = kind == CompiledPreviewKind.Playback;
        var bytes = new byte[checked(HeaderSize + payload.Length)];
        "LFXP"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)kind);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), checked((ulong)publication.Sequence));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), playback ? baseSequence : 0);
        var state = publication.Playback;
        var time = state is null ? 0 : state.Frame / state.FramesPerSecond;
        if (!double.IsFinite(time))
        {
            if (kind != CompiledPreviewKind.CompileError) throw new InvalidDataException("Invalid preview playback time.");
            time = 0;
        }
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), BitConverter.DoubleToInt64Bits(time));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), BitConverter.DoubleToInt64Bits(1));
        // Root native drives evaluated scene snapshots. UE must not free-run
        // this shell or interpret the editor's Play flag as engine ownership.
        var flags = editorNative ? 0u : (state?.Playing == true ? 1u : 0u) | (state?.Loop == true ? 2u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44), (uint)payload.Length);
        payload.CopyTo(bytes, HeaderSize);
        return new(bytes, kind, publication) { EditorNativeExecution = editorNative };
    }
}

// One instance per client. Call Sent only after the WebSocket send succeeds.
public sealed class CompiledPreviewSender : IDisposable
{
    private PreviewPublication? _good;
    private PreviewPublication? _failed;
    private long _lastSent;
    private EditorNativePreviewSession? _native;
    private GraphSnapshot? _nativeGraph;
    private string? _nativeDocument;
    private readonly string? _nativeLibraryPath;
    private GraphSnapshot? _signalGraph;
    private string? _signalDocument;
    private long _signalSequence;
    private bool _disposed;

    public CompiledPreviewSender(string? nativeLibraryPath = null) => _nativeLibraryPath = nativeLibraryPath;

    public CompiledPreviewMessage? Prepare(PreviewPublication publication, bool forceSnapshot = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (forceSnapshot || _signalDocument != publication.DocumentId || !ReferenceEquals(_signalGraph, publication.Graph))
        {
            _signalDocument = publication.DocumentId;
            _signalGraph = publication.Graph;
            _signalSequence = publication.SignalRequestSequence;
        }
        if (_native is not null && (_nativeDocument != publication.DocumentId ||
            !ReferenceEquals(_nativeGraph, publication.Graph))) ReleaseNative();
        if (!forceSnapshot && _failed is not null && _failed.DocumentId == publication.DocumentId &&
            ReferenceEquals(_failed.Graph, publication.Graph) &&
            _failed.Playback?.SignalEpoch == publication.Playback?.SignalEpoch &&
            _failed.SignalRequestSequence == publication.SignalRequestSequence) return null;
        var playback = !forceSnapshot && _good is not null && _lastSent < publication.Sequence &&
            _good.DocumentId == publication.DocumentId && ReferenceEquals(_good.Graph, publication.Graph);
        if (_native is not null || EditorNativePreviewSession.RequiresNative(publication.Graph) ||
            publication.SignalRequests.Count > 0)
        {
            try
            {
                if (_native is null)
                {
                    _native = new EditorNativePreviewSession(publication.Graph, _nativeLibraryPath);
                    _nativeGraph = publication.Graph;
                    _nativeDocument = publication.DocumentId;
                }
                var requests = publication.SignalRequests.Where(item => item.Sequence > _signalSequence).ToArray();
                if (publication.SignalRequestSequence < _signalSequence ||
                    publication.SignalRequestSequence - _signalSequence != requests.Length ||
                    requests.Where((item, index) => item.Sequence != _signalSequence + index + 1).Any())
                    throw new InvalidDataException("Preview Send input history exceeded its bounded window; resync preview. Inputs were not silently dropped.");
                _signalSequence = publication.SignalRequestSequence;
                if (publication.SignalRequests.Count > 0) _native.EnableManualSignals();
                var state = publication.Playback ?? new PreviewPlaybackState(false, 0, 60, 0, 120, false);
                if (state is not null && (!double.IsFinite(state.FramesPerSecond) || state.FramesPerSecond <= 0))
                    throw new InvalidDataException("Editor-native preview needs a positive finite frame rate.");
                var time = state is null ? 0 : state.Frame / state.FramesPerSecond;
                var scene = _native.EvaluateScene(time, playback ? _good!.Sequence : publication.Sequence, state);
                foreach (var request in requests) _native.SendSignal(request.NodeId);
                if (requests.Length > 0)
                    scene = _native.EvaluateScene(time, playback ? _good!.Sequence : publication.Sequence, state);
                var kind = playback ? CompiledPreviewKind.Playback : CompiledPreviewKind.Program;
                return CompiledPreviewProtocol.CreatePayload(publication, kind,
                    playback ? [] : EditorNativePreviewSession.TransportShell, checked((ulong)_lastSent), true)
                    with { EvaluatedRenderScene = scene };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                var diagnostic = ex.Message;
                try { ReleaseNative(); }
                catch (Exception cleanup) { diagnostic += " Native cleanup failed: " + cleanup.Message; }
                var error = new byte[CompiledPreviewProtocol.MaxErrorBytes];
                Encoding.UTF8.GetEncoder().Convert(diagnostic.AsSpan(), error, true, out _, out var written, out _);
                return CompiledPreviewProtocol.CreatePayload(publication, CompiledPreviewKind.CompileError,
                    error[..written], 0);
            }
        }
        return CompiledPreviewProtocol.Create(publication, playback, checked((ulong)_lastSent));
    }

    public void Sent(CompiledPreviewMessage message)
    {
        _lastSent = message.Publication.Sequence;
        if (message.Kind == CompiledPreviewKind.Program) { _good = message.Publication; _failed = null; }
        else if (message.Kind == CompiledPreviewKind.CompileError) _failed = message.Publication;
        else _failed = null;
    }
    private void ReleaseNative()
    {
        var native = _native;
        _native = null;
        _nativeGraph = null;
        _nativeDocument = null;
        native?.Dispose();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseNative();
    }
}
