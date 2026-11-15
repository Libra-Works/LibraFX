using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;

namespace LiberaFx.Services;

public enum CompiledPreviewKind : ushort { Program = 1, Playback = 2, CompileError = 3, Control = 4 }

public sealed record CompiledPreviewMessage(byte[] Bytes, CompiledPreviewKind Kind, PreviewPublication Publication);

public static class CompiledPreviewProtocol
{
    public const int HeaderSize = 48;
    public const int MaxProgramBytes = 128 * 1024 * 1024;
    public const int MaxErrorBytes = 4096;
    public const int MaxControlBytes = 4096;
    private static readonly ConditionalWeakTable<GraphSnapshot, Lazy<Compilation>> Cache = new();

    private sealed record Compilation(byte[]? Bytes, byte[]? Error);

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
        var bytes = new byte[checked(HeaderSize + payload.Length)];
        "LFXP"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)kind);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), checked((ulong)publication.Sequence));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), playback ? baseSequence : 0);
        var state = publication.Playback;
        var time = state is null ? 0 : state.Frame / state.FramesPerSecond;
        if (!double.IsFinite(time)) throw new InvalidDataException("Invalid preview playback time.");
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), BitConverter.DoubleToInt64Bits(time));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), BitConverter.DoubleToInt64Bits(1));
        var flags = (state?.Playing == true ? 1u : 0u) | (state?.Loop == true ? 2u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44), (uint)payload.Length);
        payload.CopyTo(bytes, HeaderSize);
        return new(bytes, kind, publication);
    }
}

// One instance per client. Call Sent only after the WebSocket send succeeds.
public sealed class CompiledPreviewSender
{
    private PreviewPublication? _good;
    private PreviewPublication? _failed;
    private long _lastSent;

    public CompiledPreviewMessage? Prepare(PreviewPublication publication, bool forceSnapshot = false)
    {
        if (!forceSnapshot && _failed is not null && _failed.DocumentId == publication.DocumentId &&
            ReferenceEquals(_failed.Graph, publication.Graph)) return null;
        var playback = !forceSnapshot && _good is not null && _lastSent < publication.Sequence &&
            _good.DocumentId == publication.DocumentId && ReferenceEquals(_good.Graph, publication.Graph);
        return CompiledPreviewProtocol.Create(publication, playback, checked((ulong)_lastSent));
    }

    public void Sent(CompiledPreviewMessage message)
    {
        _lastSent = message.Publication.Sequence;
        if (message.Kind == CompiledPreviewKind.Program) { _good = message.Publication; _failed = null; }
        else if (message.Kind == CompiledPreviewKind.CompileError) _failed = message.Publication;
        else _failed = null;
    }
}
