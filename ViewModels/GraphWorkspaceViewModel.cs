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
    private bool _restoringGraph;
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

    public GraphItemCollection<GraphNodeViewModel> Nodes { get; } = [];
    public ObservableCollection<GraphNodeViewModel> CommentNodes { get; } = [];
    public ObservableCollection<GraphNodeViewModel> ForegroundNodes { get; } = [];
    public ObservableCollection<GraphBreadcrumb> Breadcrumbs { get; } = [new(LocalizationService.Shared.Get("Messages.Top"), 0)];

    public GraphItemCollection<GraphConnectionViewModel> Connections { get; } = [];

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
        ? LocalizationService.Shared.GetNodeLabel(node.Definition.DisplayNameKey, node.Definition.Name) : string.Empty;

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
            if (_restoringGraph) return;
            RefreshSelectedConnections();
            RefreshTypeDiagnostics();
            RefreshSignalOrder();
        };
        if (document is null)
        {
            _history.Configure(CreateSnapshot, RestoreFromHistory, ValidateSnapshot,
                action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
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
        foreach (var diagnostic in GraphTypeDiagnostics.Analyze(snapshot, AvailableNodeDefinitions,
                     includeChildren: false, resolvedDefinitions: effective))
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


}

public sealed record GraphBreadcrumb(string Name, int Depth)
{
    public bool HasSeparator => Depth > 0;
}
