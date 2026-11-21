using LiberaFx.Models.Graph;

namespace LiberaFx.Models.Commands;

public sealed partial class UndoRedoManager
{
    public const int CompressionBlockSize = 200;
    private Action<Action> _publishCompression = action => action();
    private bool _compressionPending;
    private readonly HashSet<Guid> _expandedArchives = [];

    private void QueueColdCompression()
    {
        if (_compressionPending || _nodesById.Count < CompressionBlockSize * 2) return;
        var recent = _nodesById.Values.TakeLast(CompressionBlockSize).Select(node => node.Id).ToHashSet();
        bool Cold(HistoryNode node) => node.Archive is null && node.UncompressedState is not null &&
            node != _current && !node.IsPinned && !recent.Contains(node.Id) && node.Children.Count <= 1;
        HistoryNode[]? candidates = null;
        foreach (var first in _nodesById.Values)
        {
            if (!Cold(first)) continue;
            var chain = new List<HistoryNode>(CompressionBlockSize);
            for (var node = first; Cold(node) && chain.Count < CompressionBlockSize;)
            {
                chain.Add(node);
                if (node.Children.Count != 1) break;
                node = node.Children[0];
            }
            if (chain.Count == CompressionBlockSize) { candidates = chain.ToArray(); break; }
        }
        if (candidates is null) return;
        var states = candidates.Select(node => node.State).ToArray();
        _compressionPending = true;
        _ = CompressAsync(candidates, states);
    }

    private async Task CompressAsync(HistoryNode[] nodes, GraphSnapshot[] states)
    {
        UndoHistoryArchive? archive = null;
        try
        {
            archive = await Task.Run(() =>
            {
                var count = states.Length;
                while (true)
                {
                    try { return UndoHistoryArchive.Compress(states.Take(count).ToArray()); }
                    catch (InvalidDataException) when (count > 1) { count = Math.Max(1, count / 2); }
                }
            }).ConfigureAwait(false);
            nodes = nodes.Take(archive.Count).ToArray();
        }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or NotSupportedException) { }
        _publishCompression(() =>
        {
            _compressionPending = false;
            // Undo, a new branch, pinning and document replacement may have happened
            // while Brotli ran. The representation change is atomic on the owner thread.
            if (archive is null || nodes.Any(node => node == _current || node.IsPinned || node.Children.Count > 1 ||
                !_nodesById.TryGetValue(node.Id, out var live) || !ReferenceEquals(live, node))) return;
            for (var index = 0; index < nodes.Length; index++)
            {
                nodes[index].Archive = archive;
                nodes[index].ArchiveIndex = index;
                nodes[index].UncompressedState = null;
            }
            OnStateChanged();
            QueueColdCompression();
        });
    }

    private void ExpandForNode(HistoryNode node)
    {
        if (node.Archive is not { } archive) return;
        _expandedArchives.Add(archive.Id);
        archive.SetExpanded(true);
    }
    public void ToggleArchive(Guid id)
    {
        var archive = _nodesById.Values.Select(node => node.Archive).FirstOrDefault(block => block?.Id == id);
        if (archive is null) return;
        if (!_expandedArchives.Remove(id)) _expandedArchives.Add(id);
        archive.SetExpanded(_expandedArchives.Contains(id));
        OnStateChanged();
    }

    public IReadOnlyList<UndoRedoHistoryItem> GetDisplayItems()
    {
        var all = GetItems();
        var groups = _nodesById.Values.Where(node => node.Archive is not null)
            .GroupBy(node => node.Archive!.Id).ToArray();
        var mapped = all.ToDictionary(item => item.Id, item => item.Id);
        foreach (var group in groups)
            if (!_expandedArchives.Contains(group.Key)) foreach (var node in group) mapped[node.Id] = group.Key;
        var result = new List<UndoRedoHistoryItem>();
        var firsts = new Dictionary<Guid, Guid>();
        foreach (var group in groups)
        {
            var nodes = group.OrderBy(node => node.ArchiveIndex).ToArray();
            var first = nodes[0];
            firsts[first.Id] = group.Key;
            result.Add(new(group.Key, "Compressed history", first.BranchName, 0, false, false,
                first.Parent is { } parent ? mapped[parent.Id] : null, true, nodes.Length));
        }
        foreach (var item in all)
        {
            if (mapped[item.Id] != item.Id) continue;
            var parent = firsts.TryGetValue(item.Id, out var header) ? header :
                item.ParentId is { } id ? mapped[id] : (Guid?)null;
            result.Add(item with { ParentId = parent });
        }
        return result;
    }

    private static UndoRedoHistorySnapshot LimitSavedHistory(UndoRedoHistorySnapshot snapshot, int maximum)
    {
        if (maximum <= 0 || snapshot.Entries.Count <= maximum) return snapshot;
        var source = snapshot.Entries.ToDictionary(entry => entry.Id);
        var keep = new HashSet<Guid> { snapshot.CurrentNodeId };
        if (maximum > 1) keep.Add(snapshot.RootId);
        foreach (var entry in snapshot.Entries.Reverse())
            if (keep.Count < maximum) keep.Add(entry.Id);
        var root = maximum == 1 ? snapshot.CurrentNodeId : snapshot.RootId;
        var entries = snapshot.Entries.Where(entry => keep.Contains(entry.Id)).Select(entry =>
        {
            var parent = entry.ParentId;
            while (parent is { } id && !keep.Contains(id)) parent = source[id].ParentId;
            // A cap deliberately removes old intermediate states and branches.
            // Every retained state remains a full state, so the shortened edges are valid.
            return new UndoRedoHistoryEntry(entry.Id, entry.Id == root ? null : parent ?? root, null,
                entry.Description, entry.BranchName, entry.IsPinned, entry.State);
        }).ToArray();
        var children = entries.Where(entry => entry.ParentId is not null).GroupBy(entry => entry.ParentId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.Id).ToArray());
        entries = entries.Select(entry => entry with { PreferredChildId = children.TryGetValue(entry.Id, out var ids)
            ? source[entry.Id].PreferredChildId is { } preferred && ids.Contains(preferred) ? preferred : ids[^1]
            : null }).ToArray();
        return new(root, snapshot.CurrentNodeId, snapshot.TimelineRevisionId, snapshot.NextBranchNumber, entries);
    }
}
