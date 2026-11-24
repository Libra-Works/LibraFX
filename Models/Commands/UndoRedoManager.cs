using LiberaFx.Models.Graph;

namespace LiberaFx.Models.Commands;

public sealed record UndoRedoHistoryItem(
    Guid Id,
    string Description,
    string BranchName,
    int Depth,
    bool IsCurrent,
    bool IsPinned,
    Guid? ParentId = null,
    bool IsArchive = false,
    int ArchiveCount = 0);

public sealed partial class UndoRedoManager
{
    private readonly Dictionary<Guid, HistoryNode> _nodesById = [];
    private Func<GraphSnapshot>? _captureState;
    private Action<GraphSnapshot>? _restoreState;
    private Action<GraphSnapshot>? _validateState;
    private HistoryNode? _root;
    private HistoryNode? _current;
    private int _nextBranchNumber = 1;
    private Guid _timelineRevisionId;

    public bool CanUndo => _current?.Parent is not null;

    public bool CanRedo => _current?.Children.Count > 0;

    public string CurrentBranch => _current?.BranchName ?? "Main";

    public string CurrentDescription => _current?.Description ?? "Start";

    public Guid CurrentNodeId => RequireCurrent().Id;

    public Guid TimelineRevisionId => _timelineRevisionId;

    public bool IsCurrentPinned => RequireCurrent().IsPinned;

    public event EventHandler? StateChanged;
    public event Action<string>? ActionPerformed;

    public void Configure(
        Func<GraphSnapshot> captureState,
        Action<GraphSnapshot> restoreState,
        Action<GraphSnapshot> validateState,
        Action<Action>? scheduleCompressionResult = null)
    {
        ArgumentNullException.ThrowIfNull(captureState);
        ArgumentNullException.ThrowIfNull(restoreState);
        ArgumentNullException.ThrowIfNull(validateState);
        if (_captureState is not null)
        {
            throw new InvalidOperationException("The undo manager is already configured.");
        }

        _captureState = captureState;
        _restoreState = restoreState;
        _validateState = validateState;
        _publishCompression = scheduleCompressionResult ?? (action => action());
        ResetToCurrentState();
    }

    public void Execute(IUndoableCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var current = RequireCurrent();
        command.Execute();

        var branchName = current.Children.Count == 0
            ? current.BranchName
            : CreateBranchName();
        var node = new HistoryNode(
            Guid.NewGuid(),
            current,
            command.Description,
            branchName,
            false,
            CaptureState());
        current.Children.Add(node);
        current.PreferredChildId = node.Id;
        _nodesById.Add(node.Id, node);
        _current = node;
        _timelineRevisionId = Guid.NewGuid();
        OnStateChanged();
        ActionPerformed?.Invoke(command.Description);
        QueueColdCompression();
    }

    public bool Undo()
    {
        var current = RequireCurrent();
        if (current.Parent is not { } parent)
        {
            return false;
        }

        ExpandForNode(parent);
        RestoreState(parent.State);
        if (SetPreferredChild(parent, current.Id))
        {
            _timelineRevisionId = Guid.NewGuid();
        }

        _current = parent;
        OnStateChanged();
        ActionPerformed?.Invoke($"Undo: {current.Description}");
        return true;
    }

    public bool Redo()
    {
        var current = RequireCurrent();
        var child = current.Children.FirstOrDefault(
            item => item.Id == current.PreferredChildId) ?? current.Children.LastOrDefault();
        if (child is null)
        {
            return false;
        }

        ExpandForNode(child);
        RestoreState(child.State);
        if (SetPreferredChild(current, child.Id))
        {
            _timelineRevisionId = Guid.NewGuid();
        }

        _current = child;
        OnStateChanged();
        ActionPerformed?.Invoke($"Redo: {child.Description}");
        return true;
    }

    public bool NavigateTo(Guid nodeId)
    {
        if (!_nodesById.TryGetValue(nodeId, out var target))
        {
            return false;
        }

        if (target == _current)
        {
            return true;
        }

        ExpandForNode(target);
        RestoreState(target.State);
        if (SetPreferredPath(target))
        {
            _timelineRevisionId = Guid.NewGuid();
        }

        _current = target;
        OnStateChanged();
        ActionPerformed?.Invoke($"History: {target.Description}");
        return true;
    }

    public void SetPinned(Guid nodeId, bool isPinned)
    {
        if (!_nodesById.TryGetValue(nodeId, out var node))
        {
            throw new KeyNotFoundException($"History entry '{nodeId}' was not found.");
        }

        if (node.IsPinned == isPinned)
        {
            return;
        }

        node.IsPinned = isPinned;
        _timelineRevisionId = Guid.NewGuid();
        OnStateChanged();
        ActionPerformed?.Invoke($"{(isPinned ? "Pin" : "Unpin")}: {node.Description}");
    }

    public bool DeleteNode(Guid nodeId)
    {
        if (!_nodesById.TryGetValue(nodeId, out var node) || node.Parent is null)
        {
            return false;
        }
        bool HasPinned(HistoryNode candidate) =>
            candidate.IsPinned || candidate.Children.Any(HasPinned);
        if (HasPinned(node))
        {
            return false;
        }
        bool Contains(HistoryNode root, HistoryNode? target) =>
            target is not null && (ReferenceEquals(root, target) || root.Children.Any(child => Contains(child, target)));
        if (Contains(node, _current) && !NavigateTo(node.Parent.Id))
        {
            return false;
        }
        node.Parent.Children.Remove(node);
        void RemoveSubtree(HistoryNode current)
        {
            foreach (var child in current.Children) RemoveSubtree(child);
            _nodesById.Remove(current.Id);
        }
        RemoveSubtree(node);
        _timelineRevisionId = Guid.NewGuid();
        OnStateChanged();
        ActionPerformed?.Invoke($"Delete history: {node.Description}");
        return true;
    }

    public IReadOnlyList<UndoRedoHistoryItem> GetItems()
    {
        var items = new List<UndoRedoHistoryItem>(_nodesById.Count);
        if (_root is not null)
        {
            AppendItems(_root, 0, items);
        }

        return items;
    }

    public UndoRedoHistorySnapshot CreateSnapshot(bool forSaving = false)
    {
        var root = _root ?? throw new InvalidOperationException("The undo manager is not configured.");
        var current = RequireCurrent();
        var entries = new List<UndoRedoHistoryEntry>(_nodesById.Count);
        AppendEntries(root, entries);
        var snapshot = new UndoRedoHistorySnapshot(
            root.Id,
            current.Id,
            _timelineRevisionId,
            _nextBranchNumber,
            entries, _nodesById.Values.Select(node => node.Archive).OfType<UndoHistoryArchive>().DistinctBy(block => block.Id).ToArray());
        return forSaving ? LimitSavedHistory(snapshot, UndoHistoryPreferences.MaximumSavedEntries) : snapshot;
    }

    public void LoadSnapshot(UndoRedoHistorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_captureState is null)
        {
            throw new InvalidOperationException("The undo manager is not configured.");
        }

        if (snapshot.Entries is null ||
            snapshot.Entries.Count == 0 ||
            snapshot.NextBranchNumber < 1)
        {
            throw new InvalidDataException("The project contains an invalid history timeline.");
        }

        var entriesById = new Dictionary<Guid, UndoRedoHistoryEntry>();
        foreach (var entry in snapshot.Entries)
        {
            if (entry is null ||
                entry.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(entry.Description) ||
                string.IsNullOrWhiteSpace(entry.BranchName) ||
                entry.State is null ||
                !entriesById.TryAdd(entry.Id, entry))
            {
                throw new InvalidDataException("The project contains an invalid or duplicate history entry.");
            }

            _validateState!(entry.State);
        }

        var roots = snapshot.Entries.Where(entry => entry.ParentId is null).ToArray();
        if (roots.Length != 1 || roots[0].Id != snapshot.RootId)
        {
            throw new InvalidDataException("The project history must contain exactly one root entry.");
        }

        foreach (var entry in snapshot.Entries)
        {
            if (entry.ParentId is { } parentId && !entriesById.ContainsKey(parentId))
            {
                throw new InvalidDataException("The project history references a missing parent entry.");
            }
        }

        var nodes = snapshot.Entries.ToDictionary(
            entry => entry.Id,
            entry => new HistoryNode(
                entry.Id,
                null,
                entry.Description,
                entry.BranchName,
                entry.IsPinned,
                entry.UncompressedState) { Archive = entry.Archive, ArchiveIndex = entry.ArchiveIndex });
        foreach (var entry in snapshot.Entries)
        {
            var node = nodes[entry.Id];
            node.PreferredChildId = entry.PreferredChildId;
            if (entry.ParentId is { } parentId)
            {
                var parent = nodes[parentId];
                node.Parent = parent;
                parent.Children.Add(node);
            }
        }

        foreach (var node in nodes.Values)
        {
            if (node.PreferredChildId is { } preferredId &&
                !node.Children.Any(child => child.Id == preferredId))
            {
                throw new InvalidDataException("The project history has an invalid redo branch.");
            }
        }

        var root = nodes[snapshot.RootId];
        var visited = new HashSet<Guid>();
        Visit(root, visited);
        if (visited.Count != nodes.Count || !nodes.TryGetValue(snapshot.CurrentNodeId, out var current))
        {
            throw new InvalidDataException("The project history is disconnected or has no current entry.");
        }

        _expandedArchives.Clear();
        ExpandForNode(current);
        RestoreState(current.State);
        _nodesById.Clear();
        foreach (var node in nodes.Values)
        {
            _nodesById.Add(node.Id, node);
        }

        _root = root;
        _current = current;
        _timelineRevisionId = snapshot.TimelineRevisionId == Guid.Empty
            ? Guid.NewGuid()
            : snapshot.TimelineRevisionId;
        _nextBranchNumber = snapshot.NextBranchNumber;
        OnStateChanged();

        void Visit(HistoryNode node, HashSet<Guid> seen)
        {
            var pending = new Stack<HistoryNode>(); pending.Push(node);
            while (pending.TryPop(out var next))
            {
                if (!seen.Add(next.Id)) throw new InvalidDataException("The project history contains a cycle.");
                foreach (var child in next.Children) pending.Push(child);
            }
        }
    }

    public void Clear()
    {
        ResetToCurrentState();
        OnStateChanged();
    }

    private void ResetToCurrentState()
    {
        var state = CaptureState();
        var root = new HistoryNode(Guid.NewGuid(), null, "Start", "Main", false, state);
        _nodesById.Clear();
        _nodesById.Add(root.Id, root);
        _root = root;
        _current = root;
        _timelineRevisionId = Guid.NewGuid();
        _nextBranchNumber = 1;
        _expandedArchives.Clear();
    }

    private bool SetPreferredPath(HistoryNode node)
    {
        var path = new Stack<HistoryNode>();
        for (var current = node; current.Parent is not null; current = current.Parent)
        {
            path.Push(current);
        }

        var changed = false;
        while (path.TryPop(out var child))
        {
            changed |= SetPreferredChild(child.Parent!, child.Id);
        }

        return changed;
    }

    private static bool SetPreferredChild(HistoryNode node, Guid childId)
    {
        if (node.PreferredChildId == childId)
        {
            return false;
        }

        node.PreferredChildId = childId;
        return true;
    }

    private string CreateBranchName()
    {
        string branchName;
        do
        {
            branchName = $"Branch {_nextBranchNumber++}";
        }
        while (_nodesById.Values.Any(node => node.BranchName == branchName));

        return branchName;
    }

    private void AppendItems(HistoryNode node, int depth, ICollection<UndoRedoHistoryItem> items)
    {
        var pending = new Stack<(HistoryNode Node, int Depth)>(); pending.Push((node, depth));
        while (pending.TryPop(out var item))
        {
            var current = item.Node;
            items.Add(new(current.Id, current.Description, current.BranchName, item.Depth,
                current == _current, current.IsPinned, current.Parent?.Id));
            for (var index = current.Children.Count - 1; index >= 0; --index) pending.Push((current.Children[index], item.Depth + 1));
        }
    }

    private static void AppendEntries(HistoryNode node, ICollection<UndoRedoHistoryEntry> entries)
    {
        var pending = new Stack<HistoryNode>(); pending.Push(node);
        while (pending.TryPop(out var current))
        {
            var entry = new UndoRedoHistoryEntry(current.Id, current.Parent?.Id, current.PreferredChildId,
                current.Description, current.BranchName, current.IsPinned, current.UncompressedState,
                current.Archive?.Id, current.ArchiveIndex);
            if (current.Archive is { } archive) entry.AttachArchive(archive);
            entries.Add(entry);
            for (var index = current.Children.Count - 1; index >= 0; --index) pending.Push(current.Children[index]);
        }
    }

    private GraphSnapshot CaptureState() =>
        (_captureState ?? throw new InvalidOperationException("The undo manager is not configured."))();

    private void RestoreState(GraphSnapshot state) =>
        (_restoreState ?? throw new InvalidOperationException("The undo manager is not configured."))(state);

    private HistoryNode RequireCurrent() =>
        _current ?? throw new InvalidOperationException("The undo manager is not configured.");

    private void OnStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class HistoryNode(
        Guid id,
        HistoryNode? parent,
        string description,
        string branchName,
        bool isPinned,
        GraphSnapshot? state)
    {
        public Guid Id { get; } = id;

        public HistoryNode? Parent { get; set; } = parent;

        public string Description { get; } = description;

        public string BranchName { get; } = branchName;

        public bool IsPinned { get; set; } = isPinned;

        public GraphSnapshot? UncompressedState { get; set; } = state;
        public UndoHistoryArchive? Archive { get; set; }
        public int ArchiveIndex { get; set; }
        public GraphSnapshot State => UncompressedState ?? Archive?.Read(ArchiveIndex)
            ?? throw new InvalidDataException("History state is unavailable.");

        public List<HistoryNode> Children { get; } = [];

        public Guid? PreferredChildId { get; set; }
    }
}
