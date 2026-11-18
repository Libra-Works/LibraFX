using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Resources;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed partial class GraphWorkspaceViewModel : ObservableObject
{
    public event EventHandler<Guid>? GpuEditorRequested;
    public event EventHandler<Guid>? GpuProgramChanged;
    // Transient preview intent only: never a graph edit or an undoable command.
    public event EventHandler<Guid>? PreviewSignalRequested;

    public bool CanSendPreviewSignal(GraphNodeViewModel node) =>
        Nodes.Contains(node) && SignalTimeline.Supports(node.BaseDefinition.Kind) &&
        node.BaseDefinition.Kind.Value != "SpawnInputSignal" && !InsideSpawnable();

    internal void RequestPreviewSignal(GraphNodeViewModel node)
    {
        if (!CanSendPreviewSignal(node))
        {
            SetConnectionStatus("Editor.SignalSend.InstanceTargetRequired");
            return;
        }
        if (_document.PreviewSignalRequested is not { } requested)
        {
            SetConnectionStatus("Editor.SignalSend.CompiledClientRequired");
            return;
        }
        requested.Invoke(this, node.Id);
    }

    private Dictionary<Guid, string> _publishedGpuStates = [];
    public void OpenGpuEditor(GraphNodeViewModel node)
    {
        if (!Nodes.Contains(node) || !node.IsGpuParticle)
        {
            return;
        }

        if (_document.GpuEditorRequested is { } requested)
        {
            requested.Invoke(this, node.Id);
        }
        else
        {
            SetConnectionStatus("Inline.ExternalEditorUnavailable");
        }
    }
    internal void NotifyGpuProgramChanged(Guid nodeId) => _document.GpuProgramChanged?.Invoke(this, nodeId);
    private const int MaxGraphDepth = 12;
    private readonly UndoRedoManager _history;
    public GroupAssetCatalog? GroupAssets { get; }
    public NodeDefinitionCatalog? NodeDefinitions { get; }
    public IReadOnlyList<GraphNodeDefinition> AvailableNodeDefinitions =>
        NodeDefinitions?.AvailableDefinitions ?? GraphNodeDefinition.AuthoringCore;
    private int _nextNodeIndex;
    private GraphNodeViewModel? _selectedNode;
    private readonly List<GraphNodeViewModel> _selectedNodes = [];
    private GraphNodeViewModel? _pendingOutputNode;
    private string? _pendingOutputPortId;
    private Point? _connectionPreviewPoint;
    private string _connectionStatus = LocalizationService.Shared.Get("Messages.ChooseConnection");
    private string? _connectionStatusKey = "Messages.ChooseConnection";
    private object?[] _connectionStatusArgs = [];
    private GraphSnapshot _rootSnapshot = new([], []);
    private readonly List<Guid> _graphPath = [];
    public event EventHandler? FrameSelectionRequested;
    private GraphNodeViewModel? _containingSpawnable;

    public ObservableCollection<GraphNodeViewModel> Nodes { get; } = [];
    public ObservableCollection<GraphNodeViewModel> CommentNodes { get; } = [];
    public ObservableCollection<GraphNodeViewModel> ForegroundNodes { get; } = [];
    public ObservableCollection<GraphBreadcrumb> Breadcrumbs { get; } = [new(LocalizationService.Shared.Get("Messages.Top"), 0)];

    public ObservableCollection<GraphConnectionViewModel> Connections { get; } = [];

    public ObservableCollection<GraphConnectionViewModel> SelectedConnections { get; } = [];
    public ObservableCollection<GraphProblemViewModel> TypeProblems { get; } = [];

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
            OnPropertyChanged(nameof(InspectorNode));
            RefreshSelectedConnections();
            DeleteSelectedNodeCommand.NotifyCanExecuteChanged();
        }
    }

    public bool HasSelection => SelectedNode is not null;

    public bool HasNoSelection => SelectedNode is null;

    public GraphNodeViewModel? ContainingSpawnable => _containingSpawnable;
    public GraphNodeViewModel? InspectorNode => SelectedNode ?? ContainingSpawnable;

    internal bool IsCurrentInspectorTarget(GraphNodeViewModel node) =>
        ReferenceEquals(InspectorNode, node) &&
        (Nodes.Contains(node) || ReferenceEquals(ContainingSpawnable, node));

    public bool IsRootGraph => _graphPath.Count == 0;
    public IReadOnlyList<Guid> CurrentGraphPath => _graphPath.ToArray();

    public bool AutoDestroyWhenIdle
    {
        get => _rootSnapshot.AutoDestroyWhenIdle;
        set
        {
            var before = CreateSnapshot();
            if (before.AutoDestroyWhenIdle == value)
            {
                return;
            }

            ExecuteEdit(new ReplaceGraphSnapshotCommand(this, before,
                before with { AutoDestroyWhenIdle = value },
                LocalizationService.Shared.Get("Messages.History.AutoDestroyWhenIdle")));
        }
    }

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

    public string SelectedNodeType => SelectedNode is { } node
        ? LocalizationService.Shared.GetOrDefault(node.Definition.DisplayNameKey, node.Definition.Name) : string.Empty;

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set
        {
            _connectionStatusKey = null;
            _connectionStatusArgs = [];
            SetProperty(ref _connectionStatus, value);
        }
    }

    internal bool IsRefreshingLocalizedText { get; private set; }

    internal void SetConnectionStatus(string key, params object?[] args)
    {
        ConnectionStatus = LocalizationService.Shared.Get(key, args);
        _connectionStatusKey = key;
        _connectionStatusArgs = args;
    }

    private void RefreshLocalizedText()
    {
        RefreshBreadcrumbs();
        OnPropertyChanged(nameof(SelectedNodeType));
        RefreshTypeDiagnostics();
        foreach (var node in Nodes)
        {
            node.RefreshLocalizedText();
        }

        foreach (var connection in Connections)
        {
            connection.RefreshLocalizedText();
        }

        if (_connectionStatusKey is not { } key)
        {
            return;
        }

        IsRefreshingLocalizedText = true;
        try
        {
            SetProperty(ref _connectionStatus, LocalizationService.Shared.Get(key, _connectionStatusArgs), nameof(ConnectionStatus));
        }
        finally
        {
            IsRefreshingLocalizedText = false;
        }
    }

    public GraphNodeViewModel? PendingOutputNode => _pendingOutputNode;
    public string? PendingOutputPortId => _pendingOutputPortId;

    public Point? ConnectionPreviewPoint
    {
        get => _connectionPreviewPoint;
        private set => SetProperty(ref _connectionPreviewPoint, value);
    }

    public IRelayCommand DeleteSelectedNodeCommand { get; }

    public GraphWorkspaceViewModel(UndoRedoManager history, GroupAssetCatalog? groupAssets = null,
        NodeDefinitionCatalog? nodeDefinitions = null)
        : this(history, groupAssets, nodeDefinitions, null, [])
    {
    }

    private GraphWorkspaceViewModel(UndoRedoManager history, GroupAssetCatalog? groupAssets,
        NodeDefinitionCatalog? nodeDefinitions, GraphWorkspaceViewModel? document, IReadOnlyList<Guid> path)
    {
        _history = history;
        _document = document ?? this;
        _graphPath.AddRange(path);
        if (document is not null)
        {
            _rootSnapshot = document._rootSnapshot;
        }

        if (document is null)
        {
            _history.StateChanged += (_, _) =>
        {
            PublishGpuChanges();
            RefreshContainingSpawnable();
            RefreshInlineReadOnlyState();
        };
        }

        GroupAssets = groupAssets;
        if (document is null && groupAssets is not null)
        {
            var weakDocument = new WeakReference<GraphWorkspaceViewModel>(this);
            EventHandler? changed = null;
            changed = (_, _) =>
            {
                if (weakDocument.TryGetTarget(out var graph))
                {
                    if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                    {
                        graph.RefreshInlineReadOnlyState();
                    }
                    else
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(graph.RefreshInlineReadOnlyState);
                    }
                }
                else
                {
                    groupAssets.Changed -= changed;
                }
            };
            groupAssets.Changed += changed;
        }
        NodeDefinitions = nodeDefinitions;
        if (nodeDefinitions is not null)
        {
            var weakGraph = new WeakReference<GraphWorkspaceViewModel>(this);
            EventHandler? changed = null;
            changed = (_, _) =>
            {
                if (weakGraph.TryGetTarget(out var graph))
                {
                    graph.RefreshTypeDiagnostics();
                }
                else
                {
                    nodeDefinitions.Changed -= changed;
                }
            };
            nodeDefinitions.Changed += changed;
        }
        Nodes.CollectionChanged += OnNodesChanged;
        DeleteSelectedNodeCommand = new RelayCommand(DeleteSelectedNode, () => HasSelection);
        Connections.CollectionChanged += (_, _) =>
        {
            RefreshSelectedConnections();
            RefreshTypeDiagnostics();
            RefreshSignalOrder();
        };
        if (document is null)
        {
            _history.Configure(CreateSnapshot, RestoreFromHistory, ValidateSnapshot);
        }

        MessageLocalization.Observe(this, static graph => graph.RefreshLocalizedText());
    }

    private void RefreshSignalOrder()
    {
        foreach (var group in Connections.GroupBy(connection => (connection.Source.Id, connection.SourcePortId)))
        {
            var connections = group.ToArray();
            var fanout = connections.Length > 1 &&
                connections[0].Source.Definition.GetOutputPortCategory(connections[0].SourcePortId) == GraphPortCategory.Signal &&
                !(connections[0].Source.Definition.Kind == GraphNodeKind.SpawnableGroup &&
                    connections[0].SourcePortId == "destroy");
            for (var index = 0; index < connections.Length; index++)
            {
                connections[index].SetSignalOrder(fanout ? index + 1 : 0, fanout ? connections.Length : 0);
            }
        }
    }

    internal void RefreshTypeDiagnostics()
    {
        var snapshot = CreateCurrentSnapshot();
        var effective = GraphPolymorphism.Resolve(snapshot);
        foreach (var node in Nodes)
        {
            node.ApplyResolvedDefinition(effective[node.Id]);
        }

        var byId = Nodes.ToDictionary(node => node.Id);
        var problems = new List<GraphProblemViewModel>();
        foreach (var diagnostic in GraphTypeDiagnostics.Analyze(snapshot, AvailableNodeDefinitions))
        {
            if (!byId.TryGetValue(diagnostic.NodeId, out var target) ||
                !byId.TryGetValue(diagnostic.SourceNodeId, out var source))
            {
                continue;
            }

            var text = LocalizationService.Shared.Get("Messages.GraphTypeMismatch",
                target.Name, target.Id, diagnostic.InputPortId ?? "input",
                source.Name, source.Id, GraphNodeDefinition.DisplayPortType(diagnostic.SourceType),
                GraphNodeDefinition.DisplayPortType(diagnostic.ExpectedType)) +
                " " + GraphConversionPresentation.Describe(diagnostic.Conversion);
            problems.Add(new GraphProblemViewModel(target, text));
        }
        if (!IsCurrentGraphSpawnable)
        {
            foreach (var node in Nodes.Where(node => node.Definition.Kind.Value == "LifeTime"))
            {
                problems.Add(new GraphProblemViewModel(node,
                    LocalizationService.Shared.Get("Messages.LifeTimeSpawnableOnly")));
            }
        }

        var byTarget = problems.ToLookup(problem => problem.Node);
        foreach (var node in Nodes.Where(n => GpuParticleEditorService.IsGpu(n.Definition.Kind)))
        {
            foreach (var diagnostic in GpuParticleEditorService.Diagnostics(GpuParticleEditorService.ReadState(node)))
            {
                problems.Add(new GraphProblemViewModel(node,
                    $"GPU {node.Id} / {diagnostic.KernelId} / {diagnostic.NodeId} / {diagnostic.PinId}: " +
                    $"{diagnostic.Code}: {diagnostic.Message}" +
                    (diagnostic.Span is { } span ? $" [text {span.Start}:{span.Length}]" : "")));
            }
        }

        byTarget = problems.ToLookup(problem => problem.Node);
        foreach (var node in Nodes)
        {
            node.SetDiagnostics(string.Join(Environment.NewLine,
                byTarget[node].Select(problem => problem.Message)));
        }

        TypeProblems.Clear();
        foreach (var problem in problems)
        {
            TypeProblems.Add(problem);
        }
    }

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Reset or NotifyCollectionChangedAction.Move)
        {
            CommentNodes.Clear();
            ForegroundNodes.Clear();
            foreach (var node in Nodes)
            {
                (node.IsComment ? CommentNodes : ForegroundNodes).Add(node);
            }

            return;
        }
        if (e.OldItems is not null)
        {
            foreach (GraphNodeViewModel node in e.OldItems)
            {
                (node.IsComment ? CommentNodes : ForegroundNodes).Remove(node);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (GraphNodeViewModel node in e.NewItems)
            {
                var target = node.IsComment ? CommentNodes : ForegroundNodes;
                var index = Nodes.TakeWhile(item => item != node).Count(item => item.IsComment == node.IsComment);
                target.Insert(index, node);
            }
        }
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
        ValidateSpawnPlacement(snapshot.Nodes.Select(node => node.Kind));
        if (InsideSpawnable() && ContainsSpawnable(snapshot))
        {
            throw new InvalidOperationException("Nested Spawnable Groups are not supported.");
        }

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
                curve: specification.Curve, childGraph: specification.ChildGraph is null ? null : CloneGraph(specification.ChildGraph),
                parameters: specification.Parameters, groupAsset: specification.GroupAsset,
                isPolymorphic: specification.IsPolymorphic, groupOverrides: specification.GroupOverrides,
                signalKeys: specification.SignalKeys);
            nodesByOldId.Add(specification.Id, node);
            commands.Add(new AddGraphNodeCommand(this, node, Nodes.Count + commands.Count));
        }

        var connectionIndex = Connections.Count;
        foreach (var connection in snapshot.Connections)
        {
            var oldTarget = snapshot.Nodes.Single(node => node.Id == connection.TargetNodeId);
            var newTarget = nodesByOldId[connection.TargetNodeId];
            commands.Add(new AddGraphConnectionCommand(
                this, nodesByOldId[connection.SourceNodeId], newTarget, connectionIndex++,
                targetPortId: SpawnableGroupSchema.RemapPort(oldTarget, newTarget.ChildGraph, connection.TargetPortId),
                sourcePortId: SpawnableGroupSchema.RemapPort(snapshot.Nodes.Single(node => node.Id == connection.SourceNodeId),
                    nodesByOldId[connection.SourceNodeId].ChildGraph, connection.SourcePortId)));
        }

        ExecuteEdit(new CompositeUndoableCommand(LocalizationService.Shared.Get("Messages.History.PasteNodes"), commands));
        _nextNodeIndex += snapshot.Nodes.Count;
        SetSelection(snapshot.Nodes.Select(node => nodesByOldId[node.Id]));
        SetConnectionStatus("Messages.NodesPasted", snapshot.Nodes.Count);
    }

    public void DeleteNodes(IEnumerable<GraphNodeViewModel> nodes)
    {
        var selected = nodes.Where(Nodes.Contains).Distinct().ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        ExecuteEdit(new CompositeUndoableCommand(
            LocalizationService.Shared.Get("Messages.History.DeleteNodes"),
            selected.Select(node => new DeleteGraphNodeCommand(this, node))));
    }

    public void ReportStatus(string status)
    {
        if (MessageLocalization.ErrorKey(status) is { } key)
        {
            SetConnectionStatus(key);
        }
        else
        {
            ConnectionStatus = status;
        }
    }

    public bool EnterGraph(GraphNodeViewModel node)
    {
        if (!ReferenceEquals(_document, this))
        {
            return MaximizeInlineGraph(node);
        }

        return EnterGraphCore(node, inspectLocked: false);
    }

    private bool EnterGraphCore(GraphNodeViewModel node, bool inspectLocked)
    {
        if (!Nodes.Contains(node) || node.ChildGraph is null || _graphPath.Count >= MaxGraphDepth)
        {
            return false;
        }

        if (node.GroupAsset is { } reference)
        {
            try
            {
                var asset = GroupAssets?.Load(reference.Id)
                    ?? throw new InvalidOperationException("The group library is unavailable.");
                if (asset.Locked && !inspectLocked)
                {
                    SetConnectionStatus("Messages.GroupLocked");
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                       InvalidOperationException or ArgumentException)
            {
                SetConnectionStatus("Messages.EditLinkedGroupFailed", ex.Message);
                return false;
            }
        }
        CreateSnapshot();
        _graphPath.Add(node.Id);
        RestoreCurrentGraph(node.ChildGraph);
        RefreshBreadcrumbs();
        SetConnectionStatus("Messages.EditingNestedGraph");
        return true;
    }

    public void NavigateToGraph(int depth)
    {
        if (depth < 0 || depth > _graphPath.Count || depth == _graphPath.Count)
        {
            return;
        }

        CreateSnapshot();
        _graphPath.RemoveRange(depth, _graphPath.Count - depth);
        RestoreCurrentGraph(GetCurrentGraph());
        RefreshBreadcrumbs();
        SetConnectionStatus("Messages.GraphLevelChanged");
    }

    public void NavigateToNode(IReadOnlyList<Guid> parentPath, Guid nodeId)
    {
        if (!_graphPath.SequenceEqual(parentPath))
        {
            NavigateToGraph(0);
            foreach (var id in parentPath)
            {
                var group = Nodes.FirstOrDefault(node => node.Id == id);
                if (group is null || !EnterGraph(group))
                {
                    return;
                }
            }
        }
        var target = Nodes.FirstOrDefault(node => node.Id == nodeId);
        if (target is not null)
        {
            SelectNode(target);
            FrameSelectionRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool GroupSelection()
    {
        if (_selectedNodes.Count == 0)
        {
            return false;
        }

        if (_graphPath.Count >= MaxGraphDepth)
        {
            SetConnectionStatus("Messages.MaxGraphDepth");
            return false;
        }
        var selected = _selectedNodes.ToArray();
        var ids = selected.Select(node => node.Id).ToHashSet();
        if (Connections.Any(connection => ids.Contains(connection.Source.Id) != ids.Contains(connection.Target.Id)))
        {
            SetConnectionStatus("Messages.CannotGroupExternalConnections");
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
            ReportStatus(ex.Message);
            return false;
        }
        var definition = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.GraphGroup);
        var group = new GraphNodeViewModel(this, definition, definition.Name, minX, minY, childGraph: child);
        var commands = selected.Select(node => (IUndoableCommand)new DeleteGraphNodeCommand(this, node)).ToList();
        commands.Add(new AddGraphNodeCommand(this, group, Nodes.Count));
        ExecuteEdit(new CompositeUndoableCommand(LocalizationService.Shared.Get("Messages.History.GroupNodes"), commands));
        SetConnectionStatus("Messages.NodesGrouped", selected.Length);
        return true;
    }

    public GraphSnapshot ApplyPlan(LiberaGraphEditPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plan.Nodes);
        ArgumentNullException.ThrowIfNull(plan.Connections);
        if (!Enum.IsDefined(plan.Layout))
        {
            throw new ArgumentException("Unknown graph layout mode.");
        }

        if (plan.Nodes.Count == 0)
        {
            throw new ArgumentException("A graph plan must contain at least one node.", nameof(plan));
        }
        ValidateSpawnPlacement(plan.Nodes.Where(node => node is not null).Select(node => node.Kind));

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
            .Select(connection => (Source: connection.Source.Id, Target: connection.Target.Id,
                TargetPort: connection.TargetPortId, SourcePort: connection.SourcePortId))
            .ToHashSet();
        var plannedConnections = new List<GraphConnectionSnapshot>(plan.Connections.Count);
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

            if (source.IsComment || target.IsComment)
            {
                throw new ArgumentException("Comment nodes do not have ports.", nameof(plan));
            }

            var targetPortId = specification.TargetPortId;
            if (target.Definition.InputPorts is { } ports)
            {
                targetPortId ??= ports.FirstOrDefault()?.Id;
                if (targetPortId is null || !ports.Any(port => port.Id == targetPortId))
                {
                    throw new ArgumentException("The target input port does not exist.", nameof(plan));
                }

                if (target.Definition.GetInputPortCategory(targetPortId) != GraphPortCategory.Signal &&
                    connectionKeys.Any(key => key.Target == target.Id && key.TargetPort == targetPortId))
                {
                    throw new ArgumentException("The target input is already connected.", nameof(plan));
                }
            }
            else if (targetPortId is not null)
            {
                throw new ArgumentException("The target input port does not exist.", nameof(plan));
            }

            var sourcePortId = specification.SourcePortId;
            if (source.Definition.OutputPorts is { } outputPorts)
            {
                if (sourcePortId is null || !outputPorts.Any(port => port.Id == sourcePortId))
                {
                    throw new ArgumentException("The source output port does not exist.", nameof(plan));
                }
            }
            else if (sourcePortId is not null)
            {
                throw new ArgumentException("The source output port does not exist.", nameof(plan));
            }

            if (source.Definition.GetOutputPortCategory(sourcePortId) == GraphPortCategory.Signal &&
                connectionKeys.Any(key => key.Source == source.Id && key.SourcePort == sourcePortId))
            {
                throw new ArgumentException("A Signal output can have only one destination. Use a Sequence node to branch.", nameof(plan));
            }

            if (source.Definition.GetOutputPortCategory(sourcePortId) != target.Definition.GetInputPortCategory(targetPortId) ||
                (!source.IsPolymorphic && !target.IsPolymorphic &&
                 source.Definition.GetOutputPortType(sourcePortId) != target.Definition.GetInputPortType(targetPortId)))
            {
                throw new ArgumentException(
                    "Port categories and data types must match. " +
                    new GraphConversionRegistry(AvailableNodeDefinitions).Resolve(
                        source.Definition.GetOutputPortType(sourcePortId)!,
                        target.Definition.GetInputPortType(targetPortId)!).Describe(),
                    nameof(plan));
            }

            if (!connectionKeys.Add((source.Id, target.Id, targetPortId, sourcePortId)))
            {
                throw new ArgumentException("The graph plan contains a duplicate connection.", nameof(plan));
            }

            if (WouldCreateCycle(source.Id, target.Id,
                    connectionKeys.Where(key => key != (source.Id, target.Id, targetPortId, sourcePortId))
                        .Select(key => (key.Source, key.Target))))
            {
                throw new ArgumentException("Connections cannot create a cycle.", nameof(plan));
            }

            commands.Add(new AddGraphConnectionCommand(
                this,
                source,
                target,
                Connections.Count + index,
                targetPortId,
                sourcePortId));
            plannedConnections.Add(new GraphConnectionSnapshot(
                source.Id, target.Id, targetPortId, sourcePortId));
        }

        if (plan.Layout != GraphLayoutMode.Manual)
        {
            var additions = nodesByKey.Values.ToArray();
            var pinned = plan.Nodes.Where(spec => spec.IsPositionFixed).Select(spec => nodesByKey[spec.Key].Id).ToHashSet();
            var top = Nodes.Count == 0 ? 80 : Nodes.Max(node => GraphFlowLayout.Bounds(node).Bottom) + 80;
            var moves = GraphFlowLayout.Arrange(additions, plannedConnections, Nodes.ToArray(), pinned,
                plan.Layout, new Point(80, top));
            foreach (var move in moves)
            {
                nodesByKey.Values.First(node => node.Id == move.NodeId).SetPosition(move.X, move.Y);
            }
        }
        var current = CreateCurrentSnapshot();
        var plannedNodes = nodesByKey.Values.Select(node => new GraphNodeSnapshot(
            node.Id,
            node.BaseDefinition.Kind,
            node.Name,
            node.X,
            node.Y,
            node.BaseDefinition.PortType,
            node.BaseDefinition.PortCategory,
            node.Curve,
            node.ChildGraph,
            node.Parameters,
            node.GroupAsset,
            node.IsPolymorphic,
            node.GroupOverrides));
        GraphValidator.Validate(new GraphSnapshot(
            current.Nodes.Concat(plannedNodes).ToArray(),
            current.Connections.Concat(plannedConnections).ToArray()));
        ExecuteEdit(new CompositeUndoableCommand("Apply graph plan", commands));
        _nextNodeIndex += plan.Nodes.Count;
        return CreateCurrentSnapshot();
    }

    public void MoveNode(GraphNodeViewModel node, double x, double y)
    {
        if (!Nodes.Contains(node))
        {
            throw new InvalidOperationException("The node is not in this graph.");
        }

        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentException("A finite position is required.");
        }

        if (node.X == x && node.Y == y)
        {
            return;
        }

        if (node.IsComment)
        {
            var dx = x - node.X;
            var dy = y - node.Y;
            MoveNodes(GetMoveTargets([node]).Select(item => (item, item.X + dx, item.Y + dy)));
            return;
        }
        ExecuteEdit(new MoveGraphNodeCommand(node, node.X, node.Y, x, y));
    }

    public IReadOnlyList<GraphNodeViewModel> GetMoveTargets(IEnumerable<GraphNodeViewModel> selection)
    {
        var selected = selection.Where(Nodes.Contains).Distinct().ToArray();
        var result = selected.ToHashSet();
        foreach (var comment in selected.Where(node => node.IsComment))
        {
            var right = comment.X + comment.CommentWidth;
            var bottom = comment.Y + comment.CommentHeight;
            foreach (var node in Nodes)
            {
                if (ReferenceEquals(node, comment))
                {
                    continue;
                }

                var height = node.IsComment ? node.CommentHeight : node.RenderedHeight;
                if (node.X >= comment.X && node.Y >= comment.Y &&
                    node.X + node.RenderedWidth <= right && node.Y + height <= bottom)
                {
                    result.Add(node);
                }
            }
        }
        return Nodes.Where(result.Contains).ToArray();
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
            ExecuteEdit(new CompositeUndoableCommand(LocalizationService.Shared.Get("Messages.History.MoveNodes"), commands));
        }
    }

    public void RenameNode(GraphNodeViewModel node, string name)
    {
        if (!Nodes.Contains(node))
        {
            throw new InvalidOperationException("The node is not in this graph.");
        }

        if (node.Name == name)
        {
            return;
        }

        ExecuteEdit(new RenameGraphNodeCommand(node, node.Name, name));
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

        ExecuteEdit(new SetFCurveCommand(node, node.Curve, curve, description));
    }

    public void SetSignalKeys(GraphNodeViewModel node, IReadOnlyList<SignalTimelineKey> keys, string description)
    {
        if (!Nodes.Contains(node))
        {
            throw new InvalidOperationException("Select a Signal entry in this graph.");
        }

        SignalTimeline.Validate(node.BaseDefinition.Kind, keys);
        if (!SignalTimeline.Equal(node.SignalKeys, keys))
        {
            ExecuteEdit(new SetSignalKeysCommand(node, node.SignalKeys, keys.ToArray(), description));
        }
    }

    public void SetParameter(GraphNodeViewModel node, string id, JsonElement value)
    {
        if (!Nodes.Contains(node))
        {
            throw new InvalidOperationException("The node is not in this graph.");
        }

        if (RenderNodeSchema.IsRenderNode(node.Definition) && id == "materialSchema")
        {
            throw new InvalidOperationException("Assign an engine asset to update the material schema.");
        }

        if (RenderNodeSchema.IsRenderNode(node.Definition) && RenderNodeSchema.TryGetMaterialSlot(id, out _))
        {
            throw new InvalidOperationException(LocalizationService.Shared.Get("Resources.MaterialBrowseRequired"));
        }

        var definition = node.Definition.Parameters.FirstOrDefault(parameter => parameter.Id == id)
            ?? throw new InvalidOperationException("The node parameter no longer exists.");
        if (!NodeParameter.IsValidValue(definition.Type, value))
        {
            throw new ArgumentException("The value is invalid for this parameter.", nameof(value));
        }

        if (RenderNodeSchema.IsRenderNode(node.Definition) && id == "assetPath" &&
            value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } assetPath &&
            !RenderNodeSchema.IsSafeAssetPath(assetPath))
        {
            throw new ArgumentException("The engine asset path is invalid.", nameof(value));
        }

        if (RenderNodeSchema.IsRenderNode(node.Definition) &&
            RenderNodeSchema.Read(GraphNodeDefinition.All.Single(item => item.Kind == node.Definition.Kind), node.Parameters)
                .FirstOrDefault(parameter => parameter.Id == id && parameter.Kind == "texture") is not null &&
            value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: > 0 } texturePath &&
            !RenderNodeSchema.IsSafeAssetPath(texturePath))
        {
            throw new ArgumentException("The texture parameter must use a safe engine asset path.", nameof(value));
        }

        if (NodeParameter.ValuesEqual(node.GetParameterValue(definition), value))
        {
            return;
        }

        var parameters = node.Parameters?.ToList() ?? [];
        var index = parameters.FindIndex(parameter => parameter.Id == id);
        var updated = new NodeParameterSnapshot(id, definition.Type, value.Clone());
        if (index < 0)
        {
            parameters.Add(updated);
        }
        else
        {
            parameters[index] = updated;
        }

        var commands = new List<IUndoableCommand>();
        if (node.IsSequence)
        {
            var count = SequenceSchema.OutputCount(parameters);
            var valid = Enumerable.Range(0, count).Select(SequenceSchema.OutputId).ToHashSet(StringComparer.Ordinal);
            commands.AddRange(Connections.Where(connection => connection.Source == node &&
                    !valid.Contains(connection.SourcePortId ?? string.Empty)).Select(connection =>
                (IUndoableCommand)new RemoveGraphConnectionCommand(this, connection)));
        }
        commands.Add(new SetNodeParametersCommand(node, node.Parameters, parameters.AsReadOnly(), definition.Name));
        ExecuteEdit(commands.Count == 1 ? commands[0] :
            new CompositeUndoableCommand(LocalizationService.Shared.Get("Editor.Sequence.EditOutputs"), commands));
    }

    public void AssignRenderAsset(GraphNodeViewModel node, EngineAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!Nodes.Contains(node) || !RenderNodeSchema.IsRenderNode(node.Definition))
        {
            throw new InvalidOperationException("Select a model or sprite render node.");
        }

        RenderNodeAuthoring.ValidateAssetClass(asset, node.Definition.TypeName == "ModelDraw" ? "model" : "texture");
        var assetPath = RenderNodeSchema.IsSafeAssetPath(asset.Path)
            ? asset.Path
            : throw new ArgumentException("The engine asset path is invalid.", nameof(asset));
        ApplyRenderSchema(node, RenderNodeSchema.Encode(asset), assetPath, "Assign engine asset");
    }

    public void AssignRenderMaterial(GraphNodeViewModel node, EngineAsset asset, int slot)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!Nodes.Contains(node) || !RenderNodeSchema.IsRenderNode(node.Definition))
        {
            throw new InvalidOperationException("Select a model or sprite render node.");
        }

        RenderNodeAuthoring.ValidateAssetClass(asset, "material");
        if (!RenderNodeSchema.ReadMaterials(node.Definition, node.Parameters).Any(material => material.Slot == slot))
        {
            throw new ArgumentException("The selected material slot no longer exists.", nameof(slot));
        }

        if (asset.Materials is not { Count: 1 } || asset.Materials[0].Path != asset.Path)
        {
            throw new InvalidDataException("Load the material's parameter metadata before assigning it.");
        }

        var oldIds = RenderNodeSchema.Read(node.Definition, node.Parameters)
            .Where(parameter => parameter.Slot == slot).Select(parameter => parameter.Id).ToHashSet(StringComparer.Ordinal);
        var identities = CreateSnapshot().ResourceIdentities;
        if (identities is not null &&
            (identities.Uses.Any(use => use.NodeId == node.Id &&
                (use.Location == $"material:{slot}" ||
                 use.Location.StartsWith("texture:", StringComparison.Ordinal) && oldIds.Contains(use.Location["texture:".Length..])) &&
                ResourceIdentityScope.Equal(use.InstancePath, _graphPath)) ||
             identities.ParameterUses.Any(use => use.NodeId == node.Id && oldIds.Contains(use.PinId) &&
                ResourceIdentityScope.Equal(use.InstancePath, _graphPath))))
        {
            throw new InvalidOperationException(LocalizationService.Shared.Get("Resources.MaterialIdentityManaged"));
        }

        var materials = RenderNodeSchema.ReadEngineMaterials(node.Definition, node.Parameters)
            .Where(material => material.Slot != slot).Append(
                new EngineMaterial(slot, asset.Path, asset.Name, asset.Materials[0].Parameters))
            .OrderBy(material => material.Slot).ToArray();
        ApplyRenderSchema(node, RenderNodeSchema.EncodeMaterials(materials), null,
            LocalizationService.Shared.Get("Resources.AssignMaterialHistory", asset.Name, slot));
    }

    private void ApplyRenderSchema(GraphNodeViewModel node, string schema, string? assetPath, string description)
    {
        var schemaDefinition = node.Definition.Parameters.FirstOrDefault(parameter => parameter.Id == "materialSchema")
            ?? throw new InvalidOperationException("The render node does not define a material schema.");

        var values = node.Parameters?.ToList() ?? [];
        var oldSchema = node.Parameters?.FirstOrDefault(parameter =>
            parameter.Id == schemaDefinition.Id && parameter.Type == schemaDefinition.Type)?.Value
            ?? schemaDefinition.DefaultValue;
        var oldIds = RenderNodeSchema.Describe(oldSchema.ValueKind == JsonValueKind.String
            ? oldSchema.GetString() : "[]").Select(parameter => parameter.Id).ToHashSet(StringComparer.Ordinal);
        var newIds = RenderNodeSchema.Describe(schema).Select(parameter => parameter.Id).ToHashSet(StringComparer.Ordinal);
        values.RemoveAll(parameter => RenderNodeSchema.TryGetMaterialSlot(parameter.Id, out _) ||
            oldIds.Contains(parameter.Id) && !newIds.Contains(parameter.Id));
        if (assetPath is not null)
        {
            SetValue(values, "assetPath", "string", JsonSerializer.SerializeToElement(assetPath));
        }

        SetValue(values, schemaDefinition.Id, schemaDefinition.Type, JsonSerializer.SerializeToElement(schema));

        var updated = values.AsReadOnly();
        var newDefinition = RenderNodeSchema.Effective(
            GraphNodeDefinition.All.Single(definition => definition.Kind == node.Definition.Kind), updated);
        var commands = new List<IUndoableCommand>();
        if (!ParameterSnapshotsEqual(node.Parameters, updated))
        {
            commands.Add(new SetNodeParametersCommand(node, node.Parameters, updated, "engine asset"));
        }

        foreach (var connection in Connections.Where(connection => connection.Target == node).ToArray())
        {
            var sourceCategory = connection.Source.Definition.GetOutputPortCategory(connection.SourcePortId);
            var targetCategory = newDefinition.GetInputPortCategory(connection.TargetPortId);
            var validPort = newDefinition.InputPorts?.Any(port => port.Id == connection.TargetPortId) == true;
            if (!validPort || sourceCategory != targetCategory)
            {
                commands.Add(new RemoveGraphConnectionCommand(this, connection));
            }
        }
        if (commands.Count != 0)
        {
            ExecuteEdit(new CompositeUndoableCommand(description, commands));
        }

        static void SetValue(List<NodeParameterSnapshot> parameters, string id, string type, JsonElement value)
        {
            var index = parameters.FindIndex(parameter => parameter.Id == id);
            var item = new NodeParameterSnapshot(id, type, value.Clone());
            if (index < 0)
            {
                parameters.Add(item);
            }
            else
            {
                parameters[index] = item;
            }
        }

        static bool ParameterSnapshotsEqual(
            IReadOnlyList<NodeParameterSnapshot>? left,
            IReadOnlyList<NodeParameterSnapshot> right) =>
            left is not null && left.Count == right.Count &&
            left.Zip(right).All(pair => pair.First.Id == pair.Second.Id && pair.First.Type == pair.Second.Type &&
                NodeParameter.ValuesEqual(pair.First.Value, pair.Second.Value)) ||
            left is null && right.Count == 0;
    }

    internal void SetCommentSize(GraphNodeViewModel node, double width, double height)
    {
        if (!Nodes.Contains(node) || !node.IsComment || !double.IsFinite(width) || !double.IsFinite(height))
        {
            throw new InvalidOperationException("The comment size is invalid.");
        }

        width = Math.Clamp(width, 180, 4000);
        height = Math.Clamp(height, 80, 4000);
        var values = node.Parameters?.ToList() ?? [];
        foreach (var (id, value) in new[] { ("width", width), ("height", height) })
        {
            var item = new NodeParameterSnapshot(id, "float", JsonSerializer.SerializeToElement(value));
            var index = values.FindIndex(existing => existing.Id == id);
            if (index < 0)
            {
                values.Add(item);
            }
            else
            {
                values[index] = item;
            }
        }
        var oldWidth = node.GetParameterValue(node.Definition.Parameters.Single(item => item.Id == "width")).GetDouble();
        var oldHeight = node.GetParameterValue(node.Definition.Parameters.Single(item => item.Id == "height")).GetDouble();
        if (oldWidth == width && oldHeight == height)
        {
            return;
        }

        ExecuteEdit(new SetNodeParametersCommand(node, node.Parameters, values.AsReadOnly(), LocalizationService.Shared.Get("Messages.History.CommentSize")));
    }

    public GraphNodeViewModel CreateNode(GraphNodeKind kind, double? x = null, double? y = null, bool isPolymorphic = false)
    {
        if ((kind == GraphNodeKind.GraphGroup || kind == GraphNodeKind.SpawnableGroup) && _graphPath.Count >= MaxGraphDepth)
        {
            throw new InvalidOperationException("The graph hierarchy has reached its maximum depth.");
        }

        ValidateSpawnPlacement([kind]);
        var definition = AvailableNodeDefinitions.FirstOrDefault(item => item.Kind == kind)
            ?? throw new ArgumentException(LocalizationService.Shared.Get("Editor.NodeNotInProject"));
        var family = GraphPolymorphism.TryFamily(kind, out var selectedFamily) ? selectedFamily : null;
        if (isPolymorphic && family is null)
        {
            throw new ArgumentException("The node has no polymorphic family.", nameof(kind));
        }

        var index = _nextNodeIndex++;
        var nodeId = Guid.NewGuid();
        var node = new GraphNodeViewModel(
            this,
            definition,
            isPolymorphic ? LocalizationService.Shared.GetOrDefault(family?.DisplayNameKey ?? definition.DisplayNameKey, definition.Name) : definition.Name,
            x ?? 80 + (index % 4) * 280,
            y ?? 80 + (index / 4) * 160,
            id: nodeId,
            parameters: GpuParticleEditorService.IsGpu(kind) ? GpuParticleEditorService.InitialParameters(nodeId) : null,
            isPolymorphic: isPolymorphic);
        ExecuteEdit(new AddGraphNodeCommand(this, node, Nodes.Count));
        return node;
    }

    private void ValidateSpawnPlacement(IEnumerable<GraphNodeKind> kinds)
    {
        var additions = kinds.ToArray();
        var parent = _rootSnapshot;
        GraphNodeSnapshot? owner = null;
        var insideSpawnable = false;
        foreach (var id in _graphPath)
        {
            owner = parent.Nodes.First(node => node.Id == id);
            insideSpawnable |= owner.Kind == GraphNodeKind.SpawnableGroup;
            parent = owner.ChildGraph!;
        }

        if (insideSpawnable && additions.Any(kind => kind == GraphNodeKind.SpawnableGroup))
        {
            throw new InvalidOperationException("Nested Spawnable Groups are not supported.");
        }

        var ownerDestroyCount = additions.Count(kind => kind.Value == "OwnerDestroy");
        if (ownerDestroyCount != 0 && (owner?.Kind != GraphNodeKind.SpawnableGroup ||
            Nodes.Count(node => node.Definition.Kind.Value == "OwnerDestroy") + ownerDestroyCount > 1))
        {
            throw new InvalidOperationException(LocalizationService.Shared.Get("Messages.OwnerDestroyPlacement"));
        }

        var rootDestroyCount = additions.Count(kind => kind.Value == "OnDestroy");
        if (rootDestroyCount != 0 && (owner is not null ||
            Nodes.Count(node => node.Definition.Kind.Value == "OnDestroy") + rootDestroyCount > 1))
        {
            throw new InvalidOperationException(LocalizationService.Shared.Get("Messages.OnDestroyPlacement"));
        }

        if (additions.Any(kind => GameIoSchema.IsNode(kind) && !GameIoSchema.IsInstanceNode(kind)) && owner is not null)
        {
            throw new InvalidOperationException("Game I/O nodes must be placed directly in the root graph.");
        }

        if (additions.Any(GameIoSchema.IsInstanceNode) && owner?.Kind != GraphNodeKind.SpawnableGroup)
        {
            throw new InvalidOperationException("Instance Game I/O nodes must be placed directly inside a Spawnable Group.");
        }

        if (additions.Any(kind => GroupBoundarySchema.IsInput(kind) || GroupBoundarySchema.IsOutput(kind)) &&
            owner?.Kind != GraphNodeKind.GraphGroup)
        {
            throw new InvalidOperationException("Group boundary nodes must be placed directly inside a Graph Group.");
        }

        var newInputLanes = additions.Where(SpawnableGroupSchema.IsInput)
            .Sum(kind => SpawnableGroupSchema.InputWidth(GraphNodeDefinition.All.Single(item => item.Kind == kind).PortType));
        if (newInputLanes != 0)
        {
            if (owner?.Kind != GraphNodeKind.SpawnableGroup)
            {
                throw new InvalidOperationException("Spawn inputs must be placed directly inside a Spawnable Group.");
            }

            var existingInputLanes = Nodes.Where(node => SpawnableGroupSchema.IsInput(node.Definition.Kind))
                .Sum(node => SpawnableGroupSchema.InputWidth(node.Definition.PortType));
            if (existingInputLanes + newInputLanes > SpawnableGroupSchema.MaxInputLanes)
            {
                throw new InvalidOperationException($"A Spawnable Group supports at most {SpawnableGroupSchema.MaxInputLanes} input value lanes.");
            }
        }
    }

    private bool InsideSpawnable()
    {
        var graph = _rootSnapshot;
        foreach (var id in _graphPath)
        {
            var node = graph.Nodes.First(item => item.Id == id);
            if (node.Kind == GraphNodeKind.SpawnableGroup)
            {
                return true;
            }

            graph = node.ChildGraph!;
        }
        return false;
    }

    private static bool ContainsSpawnable(GraphSnapshot graph) => graph.Nodes.Any(node =>
        node.Kind == GraphNodeKind.SpawnableGroup ||
        node.ChildGraph is { } child && ContainsSpawnable(child));

    public void DeleteNode(GraphNodeViewModel node)
    {
        if (!Nodes.Contains(node))
        {
            throw new InvalidOperationException("The node is not in this graph.");
        }

        ExecuteEdit(new DeleteGraphNodeCommand(this, node));
    }

    internal void AddNode(GraphNodeViewModel node, int index)
    {
        Nodes.Insert(Math.Clamp(index, 0, Nodes.Count), node);
        SelectNode(node);
        RefreshTypeDiagnostics();
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
            SetConnectionStatus("Messages.PendingConnectionDeleted");
        }

        RefreshTypeDiagnostics();
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
        if ((specification.Kind == GraphNodeKind.GraphGroup || specification.Kind == GraphNodeKind.SpawnableGroup) && _graphPath.Count >= MaxGraphDepth)
        {
            throw new ArgumentException("The graph hierarchy has reached its maximum depth.");
        }

        var definition = AvailableNodeDefinitions.FirstOrDefault(item => item.Kind == specification.Kind)
            ?? throw new ArgumentException($"Unsupported node kind '{specification.Kind}'.");
        if (specification.IsPolymorphic && !GraphPolymorphism.TryFamily(specification.Kind, out _))
        {
            throw new ArgumentException("The node has no polymorphic family.");
        }

        if (string.IsNullOrWhiteSpace(specification.Name))
        {
            throw new ArgumentException("Node names must not be empty.");
        }

        if (!double.IsFinite(specification.X) || !double.IsFinite(specification.Y))
        {
            throw new ArgumentException("Node positions must be finite numbers.");
        }

        var plannedId = Guid.NewGuid();
        return new GraphNodeViewModel(
            this,
            definition,
            specification.Name,
            specification.X,
            specification.Y,
            id: plannedId,
            parameters: GpuParticleEditorService.IsGpu(specification.Kind) &&
                specification.Parameters?.Any(p => p.Id == GpuParticleEditorService.ParameterId) != true
                ? [.. specification.Parameters ?? [], .. GpuParticleEditorService.InitialParameters(plannedId)]
                : specification.Parameters,
            isPolymorphic: specification.IsPolymorphic);
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

public sealed record GraphBreadcrumb(string Name, int Depth)
{
    public bool HasSeparator => Depth > 0;
}
