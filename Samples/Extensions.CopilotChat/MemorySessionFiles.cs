using System.Text;
using Microsoft.Copilot;
using Microsoft.Copilot.Rpc;

namespace LiberaFx.Samples.CopilotChat;

/// <summary>
/// Bounded virtual POSIX filesystem for SDK session events/state. It never uses
/// System.IO to read or write host files. Unsupported capabilities fail closed.
/// </summary>
internal sealed class MemorySessionFiles : SessionFsProvider, IDisposable
{
    private sealed record Entry(string? Content, DateTimeOffset Created, DateTimeOffset Modified);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _disposed;
    private const int MaximumEntries = 512;
    private const int MaximumFileBytes = 2 * 1024 * 1024;
    private const int MaximumTotalBytes = 8 * 1024 * 1024;

    public MemorySessionFiles()
    {
        foreach (var path in new[] { "/", "/work", "/state", "/tmp" })
            _entries.Add(path, new(null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    }

    private Task<T> InMemory<T>(CancellationToken cancellation, Func<T> operation)
    {
        cancellation.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Task.FromResult(operation());
        }
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 2048 || path.Any(char.IsControl) ||
            path.Contains('\\') || path.Contains(':'))
            throw new IOException("Invalid virtual session path.");
        var absolute = path.StartsWith('/') ? path : "/work/" + path;
        var parts = absolute.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or "..")) throw new IOException("Virtual path traversal rejected.");
        if (parts.Length > 0 && parts[0] is not ("work" or "state" or "tmp"))
            throw new IOException("Path is outside the virtual session roots.");
        return "/" + string.Join('/', parts);
    }

    private static string Parent(string path) => path[..Math.Max(1, path.LastIndexOf('/'))];
    private static bool Within(string path, string root) => path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
    private Entry Find(string path) => _entries.TryGetValue(path, out var entry)
        ? entry : throw new FileNotFoundException("Virtual session entry does not exist.");

    private void EnsureDirectory(string path, bool recursive)
    {
        if (_entries.TryGetValue(path, out var existing))
        {
            if (existing.Content is not null) throw new IOException("Virtual entry is not a directory.");
            return;
        }
        var parent = Parent(path);
        if (!recursive && !_entries.ContainsKey(parent)) throw new DirectoryNotFoundException("Virtual parent does not exist.");
        if (recursive) EnsureDirectory(parent, true);
        if (Find(parent).Content is not null || _entries.Count >= MaximumEntries)
            throw new IOException("Virtual session directory limit or type mismatch.");
        _entries.Add(path, new(null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    }

    private void Write(string path, string content, bool append)
    {
        if (path == "/") throw new IOException("Cannot write the virtual root.");
        _entries.TryGetValue(path, out var old);
        if (old is { Content: null }) throw new IOException("Cannot replace a virtual directory with a file.");
        var value = append ? (old?.Content ?? "") + content : content;
        var size = Encoding.UTF8.GetByteCount(value);
        var total = _entries.Values.Sum(entry => entry.Content is null ? 0L : Encoding.UTF8.GetByteCount(entry.Content));
        if (size > MaximumFileBytes || total - (old?.Content is { } before ? Encoding.UTF8.GetByteCount(before) : 0) +
            size > MaximumTotalBytes || old is null && _entries.Count >= MaximumEntries)
            throw new IOException("Virtual session memory limit exceeded.");
        EnsureDirectory(Parent(path), true);
        if (old is null && _entries.Count >= MaximumEntries) throw new IOException("Virtual entry limit exceeded.");
        _entries[path] = new(value, old?.Created ?? DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    protected override Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default) =>
        InMemory(cancellationToken, () => Find(Normalize(path)).Content ?? throw new IOException("Virtual entry is a directory."));

    protected override Task WriteFileAsync(string path, string content, int? mode, CancellationToken cancellationToken = default) =>
        InMemory(cancellationToken, () => { Write(Normalize(path), content, false); return true; });

    protected override Task AppendFileAsync(string path, string content, int? mode, CancellationToken cancellationToken = default) =>
        InMemory(cancellationToken, () => { Write(Normalize(path), content, true); return true; });

    protected override Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
        InMemory(cancellationToken, () => _entries.ContainsKey(Normalize(path)));

    protected override Task<SessionFsStatResult> StatAsync(string path, CancellationToken cancellationToken = default) =>
        InMemory(cancellationToken, () =>
        {
            var entry = Find(Normalize(path));
            return new SessionFsStatResult
            {
                IsDirectory = entry.Content is null, IsFile = entry.Content is not null,
                Size = entry.Content is null ? 0 : Encoding.UTF8.GetByteCount(entry.Content),
                Birthtime = entry.Created, Mtime = entry.Modified
            };
        });

    protected override Task MakeDirectoryAsync(string path, bool recursive, int? mode, CancellationToken cancellationToken = default) =>
        InMemory(cancellationToken, () => { EnsureDirectory(Normalize(path), recursive); return true; });

    private string[] Children(string path)
    {
        if (Find(path).Content is not null) throw new IOException("Virtual entry is not a directory.");
        return _entries.Keys.Where(key => key != path && Parent(key) == path).Order(StringComparer.Ordinal).ToArray();
    }

    protected override Task<IList<string>> ReadDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        InMemory<IList<string>>(cancellationToken, () => Children(Normalize(path))
            .Select(key => key[(key.LastIndexOf('/') + 1)..]).ToArray());

    protected override Task<IList<SessionFsReaddirWithTypesEntry>> ReadDirectoryWithTypesAsync(string path,
        CancellationToken cancellationToken = default) =>
        InMemory<IList<SessionFsReaddirWithTypesEntry>>(cancellationToken, () => Children(Normalize(path))
            .Select(key => new SessionFsReaddirWithTypesEntry
            {
                Name = key[(key.LastIndexOf('/') + 1)..],
                Type = Find(key).Content is null ? SessionFsReaddirWithTypesEntryType.Directory : SessionFsReaddirWithTypesEntryType.File
            }).ToArray());

    protected override Task RemoveAsync(string path, bool recursive, bool force, CancellationToken cancellationToken = default) =>
        InMemory(cancellationToken, () =>
        {
            var key = Normalize(path);
            if (key == "/") throw new IOException("Cannot remove the virtual root.");
            if (!_entries.ContainsKey(key))
            {
                if (!force) throw new FileNotFoundException("Virtual entry does not exist.");
                return true;
            }
            var descendants = _entries.Keys.Where(candidate => Within(candidate, key)).ToArray();
            if (!recursive && descendants.Length > 1) throw new IOException("Virtual directory is not empty.");
            foreach (var child in descendants) _entries.Remove(child);
            return true;
        });

    protected override Task RenameAsync(string src, string dest, CancellationToken cancellationToken = default) =>
        InMemory(cancellationToken, () =>
        {
            var source = Normalize(src);
            var target = Normalize(dest);
            var sourceEntry = Find(source);
            if (source == target) return true;
            if (source == "/" || target == "/" || Within(target, source))
                throw new IOException("Unsupported virtual rename.");
            _entries.TryGetValue(target, out var targetEntry);
            if (targetEntry is not null &&
                ((sourceEntry.Content is null) != (targetEntry.Content is null) ||
                 targetEntry.Content is null && Children(target).Length != 0))
                throw new IOException("Virtual rename target has an incompatible type or is not empty.");
            EnsureDirectory(Parent(target), true);
            var moved = _entries.Where(item => Within(item.Key, source)).ToArray();
            if (targetEntry is not null) _entries.Remove(target);
            foreach (var item in moved) _entries.Remove(item.Key);
            foreach (var item in moved) _entries.Add(target + item.Key[source.Length..], item.Value);
            return true;
        });

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _entries.Clear(); }
    }
}
