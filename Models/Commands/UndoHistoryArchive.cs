using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiberaFx.Models.Graph;

namespace LiberaFx.Models.Commands;

public sealed record UndoHistoryArchive(Guid Id, int Count, int UncompressedBytes, byte[] Data)
{
    private const int MaximumBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private WeakReference<GraphSnapshot[]>? _cache;
    private GraphSnapshot[]? _expanded;
    private readonly object _sync = new();
    private UndoHistoryArchive(UndoHistoryArchive original)
    {
        _sync = new();
        Id = original.Id; Count = original.Count; UncompressedBytes = original.UncompressedBytes; Data = original.Data;
        // A record 'with' may replace compressed bytes or metadata; never copy decoded caches.
    }

    public static UndoHistoryArchive Compress(IReadOnlyList<GraphSnapshot> states)
    {
        if (states.Count is < 1 or > 200) throw new InvalidDataException("Invalid history archive size.");
        using var memory = new MemoryStream();
        int bytes;
        using (var compressor = new BrotliStream(memory, CompressionLevel.Fastest, leaveOpen: true))
        using (var counted = new BudgetWriteStream(compressor))
        {
            JsonSerializer.Serialize(counted, states, Json);
            bytes = counted.Bytes;
        }
        return new(Guid.NewGuid(), states.Count, bytes, memory.ToArray());
    }
    public GraphSnapshot Read(int index)
    {
        if (index < 0 || index >= Count) throw new InvalidDataException("Invalid archived history index.");
        return ReadAll()[index];
    }
    internal void SetExpanded(bool expanded)
    {
        lock (_sync) _expanded = expanded ? ReadAll() : null;
    }
    private GraphSnapshot[] ReadAll()
    {
        lock (_sync)
        {
            if (Id == Guid.Empty || Count is < 1 or > 200 || UncompressedBytes is < 1 or > MaximumBytes ||
                Data is null || Data.Length is < 1 or > MaximumBytes)
                throw new InvalidDataException("Invalid compressed history block.");
            if (_expanded is not null) return _expanded;
            if (_cache?.TryGetTarget(out var cached) == true) return cached;
            using var input = new MemoryStream(Data, writable: false);
            using var decompressor = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(UncompressedBytes);
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = decompressor.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (output.Length + read > UncompressedBytes) throw new InvalidDataException("History archive exceeds its declared size.");
                output.Write(buffer, 0, read);
            }
            if (output.Length != UncompressedBytes) throw new InvalidDataException("Truncated history archive.");
            var states = JsonSerializer.Deserialize<GraphSnapshot[]>(output.GetBuffer().AsSpan(0, UncompressedBytes), Json);
            if (states is null || states.Length != Count || states.Any(state => state is null))
                throw new InvalidDataException("Invalid archived history states.");
            _cache = new(states);
            return states;
        }
    }

    private sealed class BudgetWriteStream(Stream destination) : Stream
    {
        public int Bytes { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Bytes;
        public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public override void Flush() => destination.Flush();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > MaximumBytes - Bytes) throw new InvalidDataException("History block exceeds the compression budget.");
            destination.Write(buffer); Bytes += buffer.Length;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

public sealed record UndoRedoHistoryEntry
{
    public Guid Id { get; init; }
    public Guid? ParentId { get; init; }
    public Guid? PreferredChildId { get; init; }
    public string Description { get; init; }
    public string BranchName { get; init; }
    public bool IsPinned { get; init; }
    [JsonPropertyName("State"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GraphSnapshot? UncompressedState { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ArchiveId { get; init; }
    public int ArchiveIndex { get; init; }
    [JsonIgnore] internal UndoHistoryArchive? Archive { get; private set; }
    [JsonIgnore] public GraphSnapshot State
    {
        get => UncompressedState ?? Archive?.Read(ArchiveIndex) ?? throw new InvalidDataException("The history entry has no state.");
        init { UncompressedState = value; ArchiveId = null; Archive = null; ArchiveIndex = 0; }
    }

    [JsonConstructor]
    public UndoRedoHistoryEntry(Guid id, Guid? parentId, Guid? preferredChildId, string description,
        string branchName, bool isPinned, GraphSnapshot? uncompressedState, Guid? archiveId = null, int archiveIndex = 0)
    {
        Id = id; ParentId = parentId; PreferredChildId = preferredChildId; Description = description;
        BranchName = branchName; IsPinned = isPinned; UncompressedState = uncompressedState;
        ArchiveId = archiveId; ArchiveIndex = archiveIndex;
    }
    internal void AttachArchive(UndoHistoryArchive archive)
    {
        if (ArchiveId != archive.Id || UncompressedState is not null || ArchiveIndex < 0 || ArchiveIndex >= archive.Count)
            throw new InvalidDataException("Invalid history archive reference.");
        Archive = archive;
    }
}

public sealed record UndoRedoHistorySnapshot
{
    public Guid RootId { get; init; }
    public Guid CurrentNodeId { get; init; }
    public Guid TimelineRevisionId { get; init; }
    public int NextBranchNumber { get; init; }
    public IReadOnlyList<UndoRedoHistoryEntry> Entries { get; init; }
    public IReadOnlyList<UndoHistoryArchive> Archives { get; init; }
    [JsonConstructor]
    public UndoRedoHistorySnapshot(Guid rootId, Guid currentNodeId, Guid timelineRevisionId,
        int nextBranchNumber, IReadOnlyList<UndoRedoHistoryEntry> entries, IReadOnlyList<UndoHistoryArchive>? archives = null)
    {
        RootId = rootId; CurrentNodeId = currentNodeId; TimelineRevisionId = timelineRevisionId;
        NextBranchNumber = nextBranchNumber; Entries = entries ?? throw new InvalidDataException("Missing history entries."); Archives = archives ?? [];
        var blocks = new Dictionary<Guid, UndoHistoryArchive>();
        foreach (var block in Archives)
            if (block is null || block.Id == Guid.Empty || !blocks.TryAdd(block.Id, block) ||
                Entries.Any(entry => entry?.Id == block.Id))
                throw new InvalidDataException("Invalid or duplicate history archive identity.");
        foreach (var entry in Entries)
            if (entry?.ArchiveId is { } id)
            {
                if (!blocks.TryGetValue(id, out var archive)) throw new InvalidDataException("Missing history archive.");
                entry.AttachArchive(archive);
            }
    }
}

public static class UndoHistoryPreferences
{
    // Zero is intentionally unlimited. A finite cap is applied only when saving.
    public static int MaximumSavedEntries { get; set; }
}
