using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;

namespace LiberaFx.ViewModels;

public sealed class GraphWorkspaceViewModel : ObservableObject
{
    private const int MaxGraphDepth = 12;
    private readonly UndoRedoManager _history;
    private int _nextNodeIndex;
    private GraphNodeViewModel? _selectedNode;
    private readonly List<GraphNodeViewModel> _selectedNodes = [];
    private GraphNodeViewModel? _pendingOutputNode;
    private Point? _connectionPreviewPoint;
    private string _connectionStatus = "Choose a node output, then a compatible input.";
    private GraphSnapshot _rootSnapshot = new([], []);
    private readonly List<Guid> _graphPath = [];

    public ObservableCollection<GraphNodeViewModel> Nodes { get; } = [];
    public ObservableCollection<GraphBreadcrumb> Breadcrumbs { get; } = [new("Top", 0)];

    public ObservableCollection<GraphConnectionViewModel> Connections { get; } = [];

    public ObservableCollection<GraphConnectionViewModel> SelectedConnections { get; } = [];

    public IReadOnlyList<GraphNodeViewModel> SelectedNodes => _selectedNodes;

    public int SelectionCount => _selectedNodes.Count;

    public UndoRedoManager History => _history;

    public GraphNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        private set
        {
            if (!SetProperty(ref _selectedNode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(HasNoSelection));
            OnPropertyChanged(nameof(SelectedNodeName));
            OnPropertyChanged(nameof(SelectedNodeType));
            RefreshSelectedConnections();
            DeleteSelectedNodeCommand.NotifyCanExecuteChanged();
        }
    }

    public bool HasSelection => SelectedNode is not null;

    public bool HasNoSelection => SelectedNode is null;

    public string SelectedNodeName
    {
        get => SelectedNode?.Name ?? string.Empty;
        set
        {
            if (SelectedNode is not null)
            {
                SelectedNode.Name = value;
            }
        }
    }

    public string SelectedNodeType => SelectedNode?.Definition.Name ?? string.Empty;

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetProperty(ref _connectionStatus, value);
    }

    public GraphNodeViewModel? PendingOutputNode => _pendingOutputNode;

    public Point? ConnectionPreviewPoint
    {
        get => _connectionPreviewPoint;
        private set => SetProperty(ref _connectionPreviewPoint, value);
    }

    public IRelayCommand DeleteSelectedNodeCommand { get; }

    public GraphWorkspaceViewModel(UndoRedoManager history)
    {
        _history = history;
        DeleteSelectedNodeCommand = new RelayCommand(DeleteSelectedNode, () => HasSelection);
        Connections.CollectionChanged += (_, _) => RefreshSelectedConnections();
        _history.Configure(CreateSnapshot, RestoreFromHistory, ValidateSnapshot);
    }

    public void SelectNode(GraphNodeViewModel? node)
    {
        SetSelection(node is null ? [] : [node]);
    }

    public void ToggleSelection(GraphNodeViewModel node)
    {
        if (!Nodes.Contains(node))
        {
            return;
        }

        SetSelection(_selectedNodes.Contains(node)
            ? _selectedNodes.Where(selected => selected != node).ToArray()
            : _selectedNodes.Append(node).ToArray());
    }

    public void SetSelection(IEnumerable<GraphNodeViewModel> nodes)
    {
        var selection = nodes.Where(Nodes.Contains).Distinct().ToArray();
        if (SelectedNode is not null)
        {
            SelectedNode.PropertyChanged -= OnSelectedNodePropertyChanged;
        }

        foreach (var old in _selectedNodes)
        {
            old.SetSelected(false);
        }

        _selectedNodes.Clear();
        _selectedNodes.AddRange(selection);
        foreach (var selected in _selectedNodes)
        {
            selected.SetSelected(true);
        }

        SelectedNode = _selectedNodes.LastOrDefault();
        if (SelectedNode is not null)
        {
            SelectedNode.PropertyChanged += OnSelectedNodePropertyChanged;
        }

        OnPropertyChanged(nameof(SelectionCount));
    }

    public GraphSnapshot CreateSelectionSnapshot()
    {
        var ids = _selectedNodes.Select(node => node.Id).ToHashSet();
        var snapshot = CreateCurrentSnapshot();
        return new GraphSnapshot(
            snapshot.Nodes.Where(node => ids.Contains(node.Id)).ToArray(),
            snapshot.Connections.Where(connection =>
                ids.Contains(connection.SourceNodeId) && ids.Contains(connection.TargetNodeId)).ToArray());
    }

    public void PasteSnapshot(GraphSnapshot snapshot, double x, double y)
    {
        ValidateSnapshot(snapshot, new HashSet<Guid>(), _graphPath.Count);
        if (snapshot.Nodes.Count == 0 || !double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentException("Pasted nodes and a finite paste position are required.");
        }

        var minX = snapshot.Nodes.Min(node => node.X);
        var minY = snapshot.Nodes.Min(node => node.Y);
        var nodesByOldId = new Dictionary<Guid, GraphNodeViewModel>();
        var commands = new List<IUndoableCommand>();
        foreach (var specification in snapshot.Nodes)
        {
            var newX = x + (specification.X - minX);
            var newY = y + (specification.Y - minY);
            if (!double.IsFinite(newX) || !double.IsFinite(newY))
            {
                throw new ArgumentException("Pasted node coordinates are invalid.");
            }

            var definition = GraphNodeDefinition.All.Single(item => item.Kind == specification.Kind);
            var node = new GraphNodeViewModel(this, definition, specification.Name, newX, newY,
                curve: specification.Curve, childGraph: specification.ChildGraph is null ? null : CloneGraph(specification.ChildGraph));
            nodesByOldId.Add(specification.Id, node);
            commands.Add(new AddGraphNodeCommand(this, node, Nodes.Count + commands.Count));
        }

        foreach (var connection in snapshot.Connections)
        {
            commands.Add(new AddGraphConnectionCommand(
                this, nodesByOldId[connection.SourceNodeId], nodesByOldId[connection.TargetNodeId]));
        }

        _history.Execute(new CompositeUndoableCommand("Paste nodes", commands));
        _nextNodeIndex += snapshot.Nodes.Count;
        SetSelection(snapshot.Nodes.Select(node => nodesByOldId[node.Id]));
        ConnectionStatus = $"Pasted {snapshot.Nodes.Count} node(s).";
    }

    public void DeleteNodes(IEnumerable<GraphNodeViewModel> nodes)
    {
        var selected = nodes.Where(Nodes.Contains).Distinct().ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        _history.Execute(new CompositeUndoableCommand(
            "Delete nodes",
            selected.Select(node => new DeleteGraphNodeCommand(this, node))));
    }

    public void ReportStatus(string status)
    {
        ConnectionStatus = status;
    }

    public GraphSnapshot CreateSnapshot()
    {
        var current = CreateCurrentSnapshot();
        _rootSnapshot = ReplaceGraph(_rootSnapshot, _graphPath, 0, current);
        return _rootSnapshot;
    }

    public GraphSnapshot CreateCurrentSnapshot()
    {
        var nodes = Nodes
            .Select(node => new GraphNodeSnapshot(
                node.Id,
                node.Definition.Kind,
                node.Name,
                node.X,
                node.Y,
                node.Definition.PortType,
                node.Definition.PortCategory,
                node.Curve,
                node.ChildGraph))
            .ToArray();
        var connections = Connections
            .Select(connection => new GraphConnectionSnapshot(
                connection.Source.Id,
                connection.Target.Id))
            .ToArray();

        return new GraphSnapshot(nodes, connections);
    }

    private static GraphSnapshot ReplaceGraph(GraphSnapshot root, IReadOnlyList<Guid> path, int depth, GraphSnapshot replacement)
    {
        if (depth == path.Count) return replacement;
        var found = false;
        var nodes = root.Nodes.Select(node =>
        {
            if (node.Id != path[depth] || node.ChildGraph is null) return node;
            found = true;
            return node with { ChildGraph = ReplaceGraph(node.ChildGraph, path, depth + 1, replacement) };
        }).ToArray();
        if (!found) throw new InvalidOperationException("The graph hierarchy no longer exists.");
        return root with { Nodes = nodes };
    }

    private static GraphSnapshot CloneGraph(GraphSnapshot source)
    {
        var ids = source.Nodes.ToDictionary(node => node.Id, _ => Guid.NewGuid());
        return new GraphSnapshot(
            source.Nodes.Select(node => node with
            {
                Id = ids[node.Id],
                ChildGraph = node.ChildGraph is null ? null : CloneGraph(node.ChildGraph)
            }).ToArray(),
            source.Connections.Select(connection => new GraphConnectionSnapshot(
                ids[connection.SourceNodeId], ids[connection.TargetNodeId])).ToArray());
    }

    public bool EnterGraph(GraphNodeViewModel node)
    {
        if (!Nodes.Contains(node) || node.ChildGraph is null || _graphPath.Count >= MaxGraphDepth) return false;
        CreateSnapshot();
        _graphPath.Add(node.Id);
        RestoreCurrentGraph(node.ChildGraph);
        RefreshBreadcrumbs();
        ConnectionStatus = "Editing nested graph.";
        return true;
    }

    public void NavigateToGraph(int depth)
    {
        if (depth < 0 || depth > _graphPath.Count || depth == _graphPath.Count) return;
        CreateSnapshot();
        _graphPath.RemoveRange(depth, _graphPath.Count - depth);
        RestoreCurrentGraph(GetCurrentGraph());
        RefreshBreadcrumbs();
        ConnectionStatus = "Graph level changed.";
    }

    public bool GroupSelection()
    {
        if (_selectedNodes.Count == 0) return false;
        if (_graphPath.Count >= MaxGraphDepth)
        {
            ConnectionStatus = "The graph hierarchy has reached its maximum depth.";
            return false;
        }
        var selected = _selectedNodes.ToArray();
        var ids = selected.Select(node => node.Id).ToHashSet();
        if (Connections.Any(connection => ids.Contains(connection.Source.Id) != ids.Contains(connection.Target.Id)))
        {
            ConnectionStatus = "Cannot group nodes with connections to unselected nodes yet.";
            return false;
        }
        var selection = CreateSelectionSnapshot();
        var minX = selection.Nodes.Min(node => node.X);
        var minY = selection.Nodes.Min(node => node.Y);
        var child = selection with
        {
            Nodes = selection.Nodes.Select(node => node with { X = node.X - minX + 80, Y = node.Y - minY + 80 }).ToArray()
        };
        try { ValidateSnapshot(child, new HashSet<Guid>(), _graphPath.Count + 1); }
        catch (InvalidDataException ex)
        {
            ConnectionStatus = ex.Message;
            return false;
        }
        var definition = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.GraphGroup);
        var group = new GraphNodeViewModel(this, definition, definition.Name, minX, minY, childGraph: child);
        var commands = selected.Select(node => (IUndoableCommand)new DeleteGraphNodeCommand(this, node)).ToList();
        commands.Add(new AddGraphNodeCommand(this, group, Nodes.Count));
        _history.Execute(new CompositeUndoableCommand("Group nodes", commands));
        ConnectionStatus = $"Grouped {selected.Length} node(s).";
        return true;
    }

    private GraphSnapshot GetCurrentGraph()
    {
        var graph = _rootSnapshot;
        foreach (var id in _graphPath)
            graph = graph.Nodes.First(node => node.Id == id).ChildGraph!;
        return graph;
    }

    private void RefreshBreadcrumbs()
    {
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new GraphBreadcrumb("Top", 0));
        var graph = _rootSnapshot;
        foreach (var id in _graphPath)
        {
            var node = graph.Nodes.First(item => item.Id == id);
            Breadcrumbs.Add(new GraphBreadcrumb(node.Name, Breadcrumbs.Count));
            graph = node.ChildGraph!;
        }
    }

    public void InitializeFromSnapshot(GraphSnapshot snapshot)
    {
        if (Nodes.Count != 0 || Connections.Count != 0 || _history.CanUndo || _history.CanRedo)
        {
            throw new InvalidOperationException("A graph can only be initialized once while empty.");
        }

        ValidateSnapshot(snapshot);
        _rootSnapshot = snapshot;
        _graphPath.Clear();
        RestoreCurrentGraph(snapshot);
        RefreshBreadcrumbs();
        _history.Clear();
    }

    public static void ValidateSnapshot(GraphSnapshot snapshot) =>
        ValidateSnapshot(snapshot, new HashSet<Guid>(), 0);

    private static void ValidateSnapshot(GraphSnapshot snapshot, HashSet<Guid> allIds, int depth)
    {
        if (depth > MaxGraphDepth) throw new InvalidDataException("The graph hierarchy is too deep.");
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Nodes);
        ArgumentNullException.ThrowIfNull(snapshot.Connections);
        var definitions = GraphNodeDefinition.All.ToDictionary(definition => definition.Kind);
        var nodeSpecifications = new Dictionary<Guid, GraphNodeSnapshot>();
        foreach (var specification in snapshot.Nodes)
        {
            if (specification is null ||
                specification.Id == Guid.Empty ||
                !allIds.Add(specification.Id) ||
                string.IsNullOrWhiteSpace(specification.Name) ||
                !double.IsFinite(specification.X) ||
                !double.IsFinite(specification.Y) ||
                !definitions.TryGetValue(specification.Kind, out var definition) ||
                specification.PortType != definition.PortType ||
                specification.PortCategory != definition.PortCategory ||
                (specification.Kind == GraphNodeKind.FloatFCurve) != (specification.Curve is not null) ||
                (specification.Kind == GraphNodeKind.GraphGroup) != (specification.ChildGraph is not null) ||
                !nodeSpecifications.TryAdd(specification.Id, specification))
            {
                throw new InvalidDataException("The project contains an invalid graph node.");
            }

            if (specification.Curve is not null)
            {
                FCurve.Validate(specification.Curve);
            }
            if (specification.ChildGraph is not null)
                ValidateSnapshot(specification.ChildGraph, allIds, depth + 1);
        }

        var connectionKeys = new HashSet<(Guid SourceId, Guid TargetId)>();
        foreach (var specification in snapshot.Connections)
        {
            if (specification is null ||
                !nodeSpecifications.TryGetValue(specification.SourceNodeId, out var source) ||
                !nodeSpecifications.TryGetValue(specification.TargetNodeId, out var target) ||
                source.Id == target.Id ||
                source.Kind == GraphNodeKind.GraphGroup || target.Kind == GraphNodeKind.GraphGroup ||
                source.PortType != target.PortType ||
                source.PortCategory != target.PortCategory ||
                !connectionKeys.Add((source.Id, target.Id)))
            {
                throw new InvalidDataException("The project contains an invalid graph connection.");
            }
        }
    }

    private void RestoreFromHistory(GraphSnapshot snapshot)
    {
        ValidateSnapshot(snapshot);
        _rootSnapshot = snapshot;
        var graph = snapshot;
        var validDepth = 0;
        foreach (var id in _graphPath)
        {
            var child = graph.Nodes.FirstOrDefault(node => node.Id == id)?.ChildGraph;
            if (child is null) break;
            graph = child;
            validDepth++;
        }
        if (validDepth < _graphPath.Count) _graphPath.RemoveRange(validDepth, _graphPath.Count - validDepth);
        RestoreCurrentGraph(graph);
        RefreshBreadcrumbs();
        ConnectionStatus = "History state restored.";
    }

    private void RestoreCurrentGraph(GraphSnapshot snapshot)
    {
        var selectedIds = SelectedNodes.Select(node => node.Id).ToArray();
        var definitions = GraphNodeDefinition.All.ToDictionary(definition => definition.Kind);
        var nodesById = new Dictionary<Guid, GraphNodeViewModel>();
        foreach (var specification in snapshot.Nodes)
        {
            var definition = definitions[specification.Kind];
            var node = new GraphNodeViewModel(
                this,
                definition,
                specification.Name,
                specification.X,
                specification.Y,
                specification.Id,
                specification.Curve,
                specification.ChildGraph);
            nodesById.Add(node.Id, node);
        }

        var connections = new List<GraphConnectionViewModel>(snapshot.Connections.Count);
        foreach (var specification in snapshot.Connections)
        {
            var source = nodesById[specification.SourceNodeId];
            var target = nodesById[specification.TargetNodeId];
            connections.Add(new GraphConnectionViewModel(source, target));
        }

        SelectNode(null);
        Connections.Clear();
        Nodes.Clear();
        foreach (var specification in snapshot.Nodes)
        {
            Nodes.Add(nodesById[specification.Id]);
        }

        foreach (var connection in connections)
        {
            Connections.Add(connection);
        }

        SetSelection(selectedIds.Where(nodesById.ContainsKey).Select(id => nodesById[id]));
        _nextNodeIndex = Nodes.Count;
        _pendingOutputNode = null;
        ConnectionPreviewPoint = null;
        OnPropertyChanged(nameof(PendingOutputNode));
    }

    public GraphNodeViewModel GetNode(Guid id)
    {
        return Nodes.FirstOrDefault(node => node.Id == id)
            ?? throw new KeyNotFoundException($"Graph node '{id}' was not found.");
    }

    public GraphSnapshot ApplyPlan(LiberaGraphEditPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plan.Nodes);
        ArgumentNullException.ThrowIfNull(plan.Connections);
        if (plan.Nodes.Count == 0)
        {
            throw new ArgumentException("A graph plan must contain at least one node.", nameof(plan));
        }

        var commands = new List<IUndoableCommand>();
        var nodesByKey = new Dictionary<string, GraphNodeViewModel>(StringComparer.Ordinal);
        for (var index = 0; index < plan.Nodes.Count; index++)
        {
            var specification = plan.Nodes[index];
            if (specification is null || string.IsNullOrWhiteSpace(specification.Key))
            {
                throw new ArgumentException("Node keys must not be empty.", nameof(plan));
            }

            var node = CreatePlannedNode(specification);
            if (!nodesByKey.TryAdd(specification.Key, node))
            {
                throw new ArgumentException("Node keys must be unique.", nameof(plan));
            }

            commands.Add(new AddGraphNodeCommand(this, node, Nodes.Count + index));
        }

        var connectionKeys = Connections
            .Select(connection => (connection.Source.Id, connection.Target.Id))
            .ToHashSet();
        for (var index = 0; index < plan.Connections.Count; index++)
        {
            var specification = plan.Connections[index];
            if (specification is null ||
                string.IsNullOrWhiteSpace(specification.SourceKey) ||
                string.IsNullOrWhiteSpace(specification.TargetKey))
            {
                throw new ArgumentException("Connection node keys must not be empty.", nameof(plan));
            }

            if (!nodesByKey.TryGetValue(specification.SourceKey, out var source) ||
                !nodesByKey.TryGetValue(specification.TargetKey, out var target))
            {
                throw new ArgumentException(
                    "Each connection must reference node keys in the same graph plan.",
                    nameof(plan));
            }

            if (source == target)
            {
                throw new ArgumentException("A node cannot connect to itself.", nameof(plan));
            }

            if (source.IsGraphGroup || target.IsGraphGroup)
                throw new ArgumentException("Group boundary ports are not implemented yet.", nameof(plan));

            if (source.Definition.PortCategory != target.Definition.PortCategory ||
                source.Definition.PortType != target.Definition.PortType)
            {
                throw new ArgumentException(
                    "Port categories and data types must match.",
                    nameof(plan));
            }

            if (!connectionKeys.Add((source.Id, target.Id)))
            {
                throw new ArgumentException("The graph plan contains a duplicate connection.", nameof(plan));
            }

            if (WouldCreateCycle(source.Id, target.Id, connectionKeys.Where(key => key != (source.Id, target.Id))))
            {
                throw new ArgumentException("Connections cannot create a cycle.", nameof(plan));
            }

            commands.Add(new AddGraphConnectionCommand(
                this,
                source,
                target,
                Connections.Count + index));
        }

        _history.Execute(new CompositeUndoableCommand("Apply graph plan", commands));
        _nextNodeIndex += plan.Nodes.Count;
        return CreateCurrentSnapshot();
    }

    public void BeginConnection(GraphNodeViewModel source)
    {
        SelectNode(source);
        _pendingOutputNode = source;
        OnPropertyChanged(nameof(PendingOutputNode));
        ConnectionStatus = $"Choose an input for {source.Name} ({source.PortType}).";
    }

    public void PreviewConnection(Point point)
    {
        if (_pendingOutputNode is not null)
        {
            ConnectionPreviewPoint = point;
        }
    }

    public void CancelConnection()
    {
        _pendingOutputNode = null;
        ConnectionPreviewPoint = null;
        OnPropertyChanged(nameof(PendingOutputNode));
        ConnectionStatus = "Choose a node output, then a compatible input.";
    }

    public void CompleteConnection(GraphNodeViewModel target)
    {
        SelectNode(target);

        if (_pendingOutputNode is null)
        {
            ConnectionStatus = "Choose an output port first.";
            return;
        }

        var source = _pendingOutputNode;
        _pendingOutputNode = null;
        ConnectionPreviewPoint = null;
        OnPropertyChanged(nameof(PendingOutputNode));
        if (TryConnectNodes(source, target, out var error))
        {
            ConnectionStatus = $"Connected {source.Name} to {target.Name}.";
        }
        else
        {
            ConnectionStatus = error ?? "The connection could not be created.";
        }
    }

    public bool TryConnectNodes(
        GraphNodeViewModel source,
        GraphNodeViewModel target,
        out string? error)
    {
        if (!Nodes.Contains(source) || !Nodes.Contains(target))
        {
            error = "Both nodes must belong to this graph.";
            return false;
        }

        if (source == target)
        {
            error = "A node cannot connect to itself.";
            return false;
        }

        if (source.IsGraphGroup || target.IsGraphGroup)
        {
            error = "Group boundary ports are not implemented yet.";
            return false;
        }

        if (source.Definition.PortCategory != target.Definition.PortCategory ||
            source.Definition.PortType != target.Definition.PortType)
        {
            error = "Port categories and data types must match.";
            return false;
        }

        if (Connections.Any(connection =>
                connection.Source == source && connection.Target == target))
        {
            error = "These ports are already connected.";
            return false;
        }

        if (WouldCreateCycle(source.Id, target.Id,
                Connections.Select(connection => (connection.Source.Id, connection.Target.Id))))
        {
            error = "Connections cannot create a cycle.";
            return false;
        }

        _history.Execute(new AddGraphConnectionCommand(this, source, target));
        error = null;
        return true;
    }

    public void DisconnectConnection(GraphConnectionViewModel connection)
    {
        if (Connections.Contains(connection))
        {
            _history.Execute(new RemoveGraphConnectionCommand(this, connection));
            ConnectionStatus = "Connection removed.";
        }
    }

    private void RefreshSelectedConnections()
    {
        SelectedConnections.Clear();
        if (SelectedNode is null)
        {
            return;
        }

        foreach (var connection in Connections.Where(connection =>
                     connection.Source == SelectedNode || connection.Target == SelectedNode))
        {
            SelectedConnections.Add(connection);
        }
    }

    private static bool WouldCreateCycle(
        Guid sourceId,
        Guid targetId,
        IEnumerable<(Guid SourceId, Guid TargetId)> connections)
    {
        var outgoing = connections.ToLookup(connection => connection.SourceId, connection => connection.TargetId);
        var visited = new HashSet<Guid>();
        var pending = new Stack<Guid>();
        pending.Push(targetId);
        while (pending.TryPop(out var node))
        {
            if (node == sourceId)
            {
                return true;
            }

            if (visited.Add(node))
            {
                foreach (var next in outgoing[node])
                {
                    pending.Push(next);
                }
            }
        }

        return false;
    }

    public void MoveNode(GraphNodeViewModel node, double x, double y)
    {
        if (node.X == x && node.Y == y)
        {
            return;
        }

        _history.Execute(new MoveGraphNodeCommand(node, node.X, node.Y, x, y));
    }

    public void MoveNodes(IEnumerable<(GraphNodeViewModel Node, double X, double Y)> positions)
    {
        var commands = positions
            .Where(item => Nodes.Contains(item.Node) &&
                           double.IsFinite(item.X) && double.IsFinite(item.Y) &&
                           (item.Node.X != item.X || item.Node.Y != item.Y))
            .Select(item => new MoveGraphNodeCommand(item.Node, item.Node.X, item.Node.Y, item.X, item.Y))
            .ToArray();
        if (commands.Length > 0)
        {
            _history.Execute(new CompositeUndoableCommand("Move nodes", commands));
        }
    }

    public void RenameNode(GraphNodeViewModel node, string name)
    {
        if (node.Name == name)
        {
            return;
        }

        _history.Execute(new RenameGraphNodeCommand(node, node.Name, name));
    }

    public void SetCurve(GraphNodeViewModel node, FCurveSnapshot curve, string description)
    {
        if (!Nodes.Contains(node) || node.Definition.Kind != GraphNodeKind.FloatFCurve || node.Curve is null)
        {
            throw new InvalidOperationException("Select an F-Curve node in this graph.");
        }

        FCurve.Validate(curve);
        if (node.Curve.Keys.SequenceEqual(curve.Keys))
        {
            return;
        }

        _history.Execute(new SetFCurveCommand(node, node.Curve, curve, description));
    }

    public GraphNodeViewModel CreateNode(GraphNodeKind kind, double? x = null, double? y = null)
    {
        if (kind == GraphNodeKind.GraphGroup && _graphPath.Count >= MaxGraphDepth)
            throw new InvalidOperationException("The graph hierarchy has reached its maximum depth.");
        var definition = GraphNodeDefinition.All.Single(item => item.Kind == kind);
        var index = _nextNodeIndex++;
        var node = new GraphNodeViewModel(
            this,
            definition,
            definition.Name,
            x ?? 80 + (index % 4) * 280,
            y ?? 80 + (index / 4) * 160);
        _history.Execute(new AddGraphNodeCommand(this, node, Nodes.Count));
        return node;
    }

    public void DeleteNode(GraphNodeViewModel node)
    {
        _history.Execute(new DeleteGraphNodeCommand(this, node));
    }

    internal void AddNode(GraphNodeViewModel node, int index)
    {
        Nodes.Insert(Math.Clamp(index, 0, Nodes.Count), node);
        SelectNode(node);
    }

    internal int RemoveNode(GraphNodeViewModel node)
    {
        var index = Nodes.IndexOf(node);
        if (index >= 0)
        {
            Nodes.RemoveAt(index);
        }

        if (_selectedNodes.Contains(node))
        {
            SetSelection(_selectedNodes.Where(selected => selected != node).ToArray());
        }

        if (_pendingOutputNode == node)
        {
            CancelConnection();
            ConnectionStatus = "The pending connection was cancelled because its node was deleted.";
        }

        return index;
    }

    internal int AddConnection(GraphConnectionViewModel connection, int index)
    {
        var insertIndex = Math.Clamp(index, 0, Connections.Count);
        Connections.Insert(insertIndex, connection);
        return insertIndex;
    }

    internal int RemoveConnection(GraphConnectionViewModel connection)
    {
        var index = Connections.IndexOf(connection);
        if (index >= 0)
        {
            Connections.RemoveAt(index);
        }

        return index;
    }

    private GraphNodeViewModel CreatePlannedNode(LiberaGraphNodeSpec specification)
    {
        if (specification.Kind == GraphNodeKind.GraphGroup && _graphPath.Count >= MaxGraphDepth)
            throw new ArgumentException("The graph hierarchy has reached its maximum depth.");
        if (!GraphNodeDefinition.All.Any(item => item.Kind == specification.Kind))
        {
            throw new ArgumentException($"Unsupported node kind '{specification.Kind}'.");
        }

        if (string.IsNullOrWhiteSpace(specification.Name))
        {
            throw new ArgumentException("Node names must not be empty.");
        }

        if (!double.IsFinite(specification.X) || !double.IsFinite(specification.Y))
        {
            throw new ArgumentException("Node positions must be finite numbers.");
        }

        var definition = GraphNodeDefinition.All.Single(item => item.Kind == specification.Kind);
        return new GraphNodeViewModel(
            this,
            definition,
            specification.Name,
            specification.X,
            specification.Y);
    }

    private void DeleteSelectedNode()
    {
        DeleteNodes(_selectedNodes.ToArray());
    }

    private void OnSelectedNodePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphNodeViewModel.Name))
        {
            OnPropertyChanged(nameof(SelectedNodeName));
        }
    }

}

public sealed record GraphBreadcrumb(string Name, int Depth);
