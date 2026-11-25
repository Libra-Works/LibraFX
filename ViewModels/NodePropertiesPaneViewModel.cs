using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using LiberaFx.Models.Appearance;
using LiberaFx.Models.Graph;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed class NodePropertiesPaneViewModel : Tool, IDisposable
{
    private readonly ActiveGraphProvider _activeGraph;
    private readonly EditorPlayback? _playback;
    private readonly AssetBrowserCoordinator? _assetBrowser;
    private readonly ExecutionDebugPaneViewModel? _debugger;
    private GraphWorkspaceViewModel _graph;
    private GraphNodeViewModel? _node;
    private GraphNodeViewModel? _pinnedNode;
    private long _selectionGeneration;
    private readonly List<string> _unavailableExposedItems = [];

    public GraphWorkspaceViewModel Graph => _graph;
    public GraphNodeViewModel? InspectorNode => _node;
    public bool HasInspectorTarget => _node is not null;
    public bool ShowRootEffectSettings => _graph.IsRootGraph && !HasInspectorTarget;
    public bool IsPinned => _pinnedNode is not null;
    public bool CanTogglePin => IsPinned || (_node is not null && _graph.Nodes.Contains(_node));
    public EditorIcon InspectorPinIcon => IsPinned ? EditorIcon.Lock : EditorIcon.Unlock;
    public string InspectorPinHint => LocalizationService.Shared.Get(IsPinned ? "Panels.InspectorUnlock" : "Panels.InspectorLock");
    public bool IsNodeLocked => _node?.IsLocked == true;
    public bool CanToggleNodeLock => _node is not null && _graph.Nodes.Contains(_node) &&
        !_graph.IsReadOnly && !IsInspectingContainingSpawnable;
    public EditorIcon NodeLockIcon => IsNodeLocked ? EditorIcon.Lock : EditorIcon.Unlock;
    public string NodeLockHint => LocalizationService.Shared.Get(
        IsNodeLocked ? "Panels.UnlockNodeParameters" : "Panels.LockNodeParameters");
    public IRelayCommand ToggleNodeLockCommand { get; }
    public bool IsInspectingContainingSpawnable =>
        _node is not null && ReferenceEquals(_node, _graph.ContainingSpawnable) && _graph.SelectedNode is null;
    public bool ShowSelectedConnections => !IsPinned && !IsInspectingContainingSpawnable && _graph.IncidentConnections.Count > 0;
    public string InspectorTitle => LocalizationService.Shared.Get(
        IsInspectingContainingSpawnable ? "Panels.ContainingSpawnable" : "Editor.SelectedNode");
    public string InspectorNodeName
    {
        get => _node?.Name ?? string.Empty;
        set { if (_node is not null && !IsInspectingContainingSpawnable) _node.Name = value; }
    }
    public string InspectorNodeType => _node is { } node
        ? LocalizationService.Shared.GetNodeLabel(node.Definition.DisplayNameKey, node.Definition.Name) : string.Empty;
    public bool CanManageGroup => IsGroup && !IsInspectingContainingSpawnable && !IsPinned;
    public bool IsStandardGroup => CurrentAsset?.Origin == GroupAssetOrigin.Standard;
    public bool IsProjectLinkedGroup => IsLinkedGroup && !IsStandardGroup;
    public bool CanCloneGroup => CanManageGroup && IsStandardGroup && _graph.GroupAssets?.HasLibraryDirectory == true;
    public bool CanConfigureGroupItems => HasExposedItems && !IsInspectingContainingSpawnable && !IsPinned;
    public ObservableCollection<NodeParameterEditorViewModel> Parameters { get; } = [];
    public string GroupExposedDiagnostics => string.Join(Environment.NewLine,
        _unavailableExposedItems.Select(name => LocalizationService.Shared.Get("Panels.GroupExposedUnavailable", name)));
    public IRelayCommand SaveGroupCommand { get; }
    public IRelayCommand ToggleGroupLockCommand { get; }
    public IRelayCommand CloneGroupCommand { get; }
    public IRelayCommand PublishCurvesCommand { get; }
    public bool CanPublishCurves => !IsPinned && _graph.CanPublishGroupCurves();
    public bool CanEditGroupCurves => HasExposedCurves && !IsPinned;
    public bool CanEditColorCurves => CanEditGroupCurves &&
        _node?.ChildGraph?.ExposedItems?.Any(item => item.Editor == "colorCurve") == true;
    public bool CanEditVectorCurves => CanEditGroupCurves && !CanEditColorCurves;
    public bool HasExposedCurves => _node?.ChildGraph?.ExposedItems?.Any(item =>
        GroupExposedItems.CurveChannels(item.Editor).Length != 0) == true;
    public bool HasExposedItems => _node?.ChildGraph?.ExposedItems is { Count: > 0 };
    public bool IsGroup => _node?.IsGraphGroup == true;
    public bool IsLinkedGroup => _node?.GroupAsset is not null;
    private GroupAsset? CurrentAsset => _node?.GroupAsset is { } reference
        ? _graph.GroupAssets?.Assets.FirstOrDefault(asset => asset.Id == reference.Id) : null;
    public bool IsGroupLocked => CurrentAsset?.Locked == true;
    public EditorIcon GroupLockIcon => IsGroupLocked ? EditorIcon.Lock : EditorIcon.Unlock;
    public string GroupLockHint => LocalizationService.Shared.Get(IsGroupLocked ? "Editor.UnlockGroup" : "Editor.LockGroup");
    public string GroupRevision => _node?.GroupAsset is { } reference
        ? LocalizationService.Shared.Get("Editor.GroupRevision", reference.Revision) : string.Empty;
    public string GroupStatus => LocalizationService.Shared.Get(
        IsStandardGroup ? "Modules.StandardReadOnly" :
        _graph.GroupAssets?.HasLibraryDirectory != true ? "Editor.GroupNoLibrary" :
        !IsLinkedGroup ? "Editor.GroupUnlinked" :
        CurrentAsset is null ? "Editor.GroupMissing" :
        IsGroupLocked ? "Editor.GroupLocked" : "Editor.GroupUnlocked");
    public string StandardGroupDetails
    {
        get
        {
            if (_node?.GroupAsset is not { } reference ||
                _graph.GroupAssets?.Metadata(reference.Id) is not { } metadata) return string.Empty;
            var localization = LocalizationService.Shared;
            return localization.Get("Modules.Details",
                localization.GetOrDefault(metadata.DescriptionKey, string.Empty),
                localization.GetOrDefault(metadata.PhaseKey, metadata.PhaseKey),
                localization.GetOrDefault(metadata.InputsKey, metadata.InputsKey),
                localization.GetOrDefault(metadata.OutputsKey, metadata.OutputsKey),
                localization.GetOrDefault(metadata.UnitsKey, metadata.UnitsKey),
                localization.GetOrDefault(metadata.StateKey, metadata.StateKey),
                localization.GetOrDefault(metadata.BackendKey, metadata.BackendKey));
        }
    }
    public string PluginStatus
    {
        get
        {
            var status = InspectorPluginService.Shared.Status;
            if (Environment.GetEnvironmentVariable("LIBERAFX_INSPECTOR_PLUGINS") != "1") return status;
            var legacy = InspectorEditors.Warnings;
            return legacy.Count == 0 ? status : string.Join(Environment.NewLine, new[] { status }.Concat(legacy));
        }
    }

    public NodePropertiesPaneViewModel(ActiveGraphProvider activeGraph, EditorPlayback? playback = null,
        AssetBrowserCoordinator? assetBrowser = null, ExecutionDebugPaneViewModel? debugger = null)
    {
        _activeGraph = activeGraph;
        _playback = playback;
        _assetBrowser = assetBrowser;
        _debugger = debugger;
        _graph = activeGraph.Graph;
        SaveGroupCommand = new RelayCommand(() => RunGroupAction(_graph.SaveSelectedGroupAsset),
            () => CanManageGroup && _graph.GroupAssets?.HasLibraryDirectory == true &&
                  (!IsLinkedGroup || CurrentAsset is { Locked: false, Origin: GroupAssetOrigin.Project }));
        ToggleGroupLockCommand = new RelayCommand(() => RunGroupAction(_graph.ToggleSelectedGroupAssetLock),
            () => CanManageGroup && IsLinkedGroup &&
                  CurrentAsset is { Origin: GroupAssetOrigin.Project });
        CloneGroupCommand = new RelayCommand(() => RunGroupAction(() => _graph.CloneSelectedStandardGroupToProject()),
            () => CanCloneGroup);
        PublishCurvesCommand = new RelayCommand(() => RunGroupAction(() => _graph.PublishGroupCurves()),
            () => CanPublishCurves);
        ToggleNodeLockCommand = new RelayCommand(() =>
        {
            if (_node is not null && CanToggleNodeLock)
                _graph.SetNodeLocked(_node, !IsNodeLocked);
        }, () => CanToggleNodeLock);
        _activeGraph.PropertyChanged += OnActiveGraphChanged;
        InspectorPluginService.Shared.DefaultsChanged += OnInspectorDefaultsChanged;
        InspectorPluginService.Shared.PropertyChanged += OnInspectorPluginServiceChanged;
        _graph.PropertyChanged += OnGraphChanged;
        _graph.Nodes.CollectionChanged += OnNodesChanged;
        _graph.Connections.CollectionChanged += OnConnectionsChanged;
        if (_graph.GroupAssets is { } catalog) catalog.Changed += OnGroupAssetsChanged;
        if (_debugger is not null) _debugger.PropertyChanged += OnDebuggerChanged;
        RefreshNode();
    }

    public bool HasLiveDebugValues =>
        _debugger is { IsDebuggingPaused: true, CurrentFrame: { } frame } &&
        InspectorNode is { } node && frame.Node == node.Id;
    public string LiveDebugNodeHeader => _debugger?.CurrentNode ?? string.Empty;
    public string LiveDebugSignalSource => _debugger?.CurrentSignalSource ?? string.Empty;
    public IReadOnlyList<string> LiveDebugInputs => HasLiveDebugValues ? _debugger!.Inputs : [];
    public IReadOnlyList<string> LiveDebugAuthoredParameters => HasLiveDebugValues ? _debugger!.AuthoredParameters : [];
    public IRelayCommand? DebugPauseCommand => _debugger?.PauseCommand;
    public IRelayCommand? DebugStepCommand => _debugger?.StepCommand;
    public IRelayCommand? DebugResumeCommand => _debugger?.ResumeCommand;

    private void OnDebuggerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ExecutionDebugPaneViewModel.Status) or
            nameof(ExecutionDebugPaneViewModel.CurrentNode) or
            nameof(ExecutionDebugPaneViewModel.Inputs) or
            nameof(ExecutionDebugPaneViewModel.AuthoredParameters))
        {
            OnPropertyChanged(nameof(HasLiveDebugValues));
            OnPropertyChanged(nameof(LiveDebugNodeHeader));
            OnPropertyChanged(nameof(LiveDebugSignalSource));
            OnPropertyChanged(nameof(LiveDebugInputs));
            OnPropertyChanged(nameof(LiveDebugAuthoredParameters));
        }
    }

    private void OnActiveGraphChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActiveGraphProvider.Graph)) return;
        _graph.PropertyChanged -= OnGraphChanged;
        _graph.Nodes.CollectionChanged -= OnNodesChanged;
        _graph.Connections.CollectionChanged -= OnConnectionsChanged;
        if (_graph.GroupAssets is { } previous) previous.Changed -= OnGroupAssetsChanged;
        _graph = _activeGraph.Graph;
        _pinnedNode = null;
        _graph.PropertyChanged += OnGraphChanged;
        _graph.Nodes.CollectionChanged += OnNodesChanged;
        _graph.Connections.CollectionChanged += OnConnectionsChanged;
        if (_graph.GroupAssets is { } current) current.Changed += OnGroupAssetsChanged;
        OnPropertyChanged(nameof(Graph));
        RefreshNode();
    }

    private void OnInspectorDefaultsChanged(object? sender, EventArgs e) => RefreshNode();

    private void OnInspectorPluginServiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InspectorPluginService.Status)) OnPropertyChanged(nameof(PluginStatus));
    }

    private void OnGraphChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphWorkspaceViewModel.InspectorNode) && !IsPinned) RefreshNode();
        if (e.PropertyName == nameof(GraphWorkspaceViewModel.IsRootGraph)) OnPropertyChanged(nameof(ShowRootEffectSettings));
        if (e.PropertyName == nameof(GraphWorkspaceViewModel.SelectionCount))
        {
            OnPropertyChanged(nameof(CanPublishCurves));
            PublishCurvesCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_pinnedNode is { } pinned && !_graph.Nodes.Contains(pinned))
        {
            _pinnedNode = null;
            RefreshNode();
        }
    }

    public void TogglePin()
    {
        if (!CanTogglePin) return;
        _pinnedNode = IsPinned ? null : _node;
        RefreshNode();
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphNodeViewModel.Definition))
        {
            RefreshNode();
            return;
        }
        if (e.PropertyName is nameof(GraphNodeViewModel.Parameters) or nameof(GraphNodeViewModel.GroupOverrides))
        {
            RefreshNodeLockState();
            foreach (var parameter in Parameters) parameter.Refresh();
        }
        if (e.PropertyName == nameof(GraphNodeViewModel.IsLocked)) RefreshNodeLockState();
        if (e.PropertyName == nameof(GraphNodeViewModel.GroupAsset)) RefreshGroupState();
        if (e.PropertyName == nameof(GraphNodeViewModel.Name)) OnPropertyChanged(nameof(InspectorNodeName));
    }

    private void OnConnectionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ShowSelectedConnections));
        foreach (var parameter in Parameters) parameter.Refresh();
    }

    private void OnGroupAssetsChanged(object? sender, EventArgs e) => RefreshGroupState();

    public void RefreshLocalizedText()
    {
        InspectorPluginService.Shared.RefreshLocalizedText();
        OnPropertyChanged(nameof(PluginStatus));
        OnPropertyChanged(nameof(GroupExposedDiagnostics));
        OnPropertyChanged(nameof(InspectorTitle));
        OnPropertyChanged(nameof(InspectorNodeType));
        OnPropertyChanged(nameof(InspectorPinHint));
        RefreshNodeLockState();
        RefreshGroupState();
    }

    private void RefreshNodeLockState()
    {
        OnPropertyChanged(nameof(IsNodeLocked));
        OnPropertyChanged(nameof(CanToggleNodeLock));
        OnPropertyChanged(nameof(NodeLockIcon));
        OnPropertyChanged(nameof(NodeLockHint));
        ToggleNodeLockCommand.NotifyCanExecuteChanged();
    }

    private void RefreshGroupState()
    {
        OnPropertyChanged(nameof(IsGroup));
        OnPropertyChanged(nameof(IsStandardGroup));
        OnPropertyChanged(nameof(IsProjectLinkedGroup));
        OnPropertyChanged(nameof(CanCloneGroup));
        OnPropertyChanged(nameof(CanManageGroup));
        OnPropertyChanged(nameof(CanConfigureGroupItems));
        OnPropertyChanged(nameof(HasExposedCurves));
        OnPropertyChanged(nameof(CanEditGroupCurves));
        OnPropertyChanged(nameof(CanEditColorCurves));
        OnPropertyChanged(nameof(CanEditVectorCurves));
        OnPropertyChanged(nameof(HasExposedItems));
        OnPropertyChanged(nameof(CanPublishCurves));
        OnPropertyChanged(nameof(IsLinkedGroup));
        OnPropertyChanged(nameof(IsGroupLocked));
        OnPropertyChanged(nameof(GroupLockIcon));
        OnPropertyChanged(nameof(GroupLockHint));
        OnPropertyChanged(nameof(GroupRevision));
        OnPropertyChanged(nameof(GroupStatus));
        OnPropertyChanged(nameof(StandardGroupDetails));
        SaveGroupCommand.NotifyCanExecuteChanged();
        ToggleGroupLockCommand.NotifyCanExecuteChanged();
        CloneGroupCommand.NotifyCanExecuteChanged();
        PublishCurvesCommand.NotifyCanExecuteChanged();
    }

    private void RunGroupAction(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   InvalidOperationException or ArgumentException)
        {
            _graph.ReportStatus(ex.Message);
        }
        RefreshGroupState();
    }

    private void RefreshNode()
    {
        var selectionGeneration = ++_selectionGeneration;
        if (_node is not null) _node.PropertyChanged -= OnNodeChanged;
        _node = _pinnedNode ?? _graph.InspectorNode;
        Parameters.Clear();
        _unavailableExposedItems.Clear();
        OnPropertyChanged(nameof(GroupExposedDiagnostics));
        OnPropertyChanged(nameof(InspectorNode));
        OnPropertyChanged(nameof(HasInspectorTarget));
        OnPropertyChanged(nameof(ShowRootEffectSettings));
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(CanTogglePin));
        OnPropertyChanged(nameof(InspectorPinIcon));
        OnPropertyChanged(nameof(InspectorPinHint));
        OnPropertyChanged(nameof(IsInspectingContainingSpawnable));
        OnPropertyChanged(nameof(ShowSelectedConnections));
        OnPropertyChanged(nameof(InspectorTitle));
        OnPropertyChanged(nameof(InspectorNodeName));
        OnPropertyChanged(nameof(InspectorNodeType));
        OnPropertyChanged(nameof(HasLiveDebugValues));
        OnPropertyChanged(nameof(LiveDebugNodeHeader));
        OnPropertyChanged(nameof(LiveDebugSignalSource));
        OnPropertyChanged(nameof(LiveDebugInputs));
        OnPropertyChanged(nameof(LiveDebugAuthoredParameters));
        RefreshNodeLockState();
        RefreshGroupState();
        if (_node is null) return;
        _node.PropertyChanged += OnNodeChanged;
        var graph = _graph;
        var selectedNode = _node;
        bool IsCurrentTarget() => selectionGeneration == _selectionGeneration &&
            ReferenceEquals(_activeGraph.Graph, graph) && (IsPinned
                ? ReferenceEquals(_pinnedNode, selectedNode) && graph.Nodes.Contains(selectedNode)
                : graph.IsCurrentInspectorTarget(selectedNode));
        foreach (var definition in _node.Definition.Parameters.Where(parameter =>
                     !parameter.Hidden &&
                     !_node.IsGpuParticle &&
                     (!RenderNodeSchema.IsRenderNode(_node.Definition) ||
                         parameter.Id is not ("materialSchema" or "spriteData" or "compositionData"))))
        {
            Parameters.Add(new NodeParameterEditorViewModel(graph, selectedNode, definition, isCurrentInspectorTarget: IsCurrentTarget,
                assetBrowser: _assetBrowser));
        }
        if (_node.ChildGraph is not { } child) return;
        foreach (var item in (child.ExposedItems ?? []).OrderBy(item => item.Order))
        {
            var itemMetadata = _node.GroupAsset is { } groupReference
                ? _graph.GroupAssets?.Metadata(groupReference.Id)?.Parameters
                    .FirstOrDefault(parameter => parameter.ItemId == item.Id)
                : null;
            var displayName = LocalizationService.Shared.GetOrDefault(itemMetadata?.NameKey, item.Name);
            if (GroupExposedItems.CurveChannels(item.Editor).Length != 0)
            {
                if (item.Targets.Any(target => !child.Nodes.Any(node => node.Id == target.NodeId &&
                        node.Kind == GraphNodeKind.FloatFCurve && node.Curve is not null)))
                    _unavailableExposedItems.Add(displayName);
                continue;
            }
            if (item.Editor != "parameter" || item.Targets.Count != 1)
            {
                _unavailableExposedItems.Add(displayName);
                continue;
            }
            var target = item.Targets[0];
            var internalNode = child.Nodes.FirstOrDefault(candidate => candidate.Id == target.NodeId);
            var baseDefinition = internalNode is null ? null :
                GraphNodeDefinition.All.FirstOrDefault(definition => definition.Kind == internalNode.Kind);
            var definition = baseDefinition is null ? null :
                GameIoSchema.Effective(RenderNodeSchema.Effective(baseDefinition, internalNode!.Parameters),
                    internalNode.Parameters).Parameters.FirstOrDefault(parameter => parameter.Id == target.ParameterId);
            var initial = definition is null ? default :
                internalNode!.Parameters?.FirstOrDefault(parameter =>
                    parameter.Id == definition.Id && parameter.Type == definition.Type)?.Value ?? definition.DefaultValue;
            var stored = _node.GroupOverrides?.FirstOrDefault(value => value.ItemId == item.Id);
            if (definition is null || !GroupExposedItems.SupportsParameter(baseDefinition!, definition) ||
                item.Presentation == "slider" && definition.Type is not ("float" or "int") ||
                !NodeParameter.IsValidValue(definition.Type, initial) ||
                stored is not null && (stored.Components.Count != 1 || stored.Components[0].Value is not { } storedValue ||
                    !NodeParameter.IsValidValue(definition.Type, storedValue)))
            {
                _unavailableExposedItems.Add(displayName);
                continue;
            }
            var editor = item.Presentation switch
            {
                "slider" => new NodeParameterEditorDefinition("slider", item.Minimum, item.Maximum, item.Step),
                "text" => new NodeParameterEditorDefinition("text", null, null, null),
                _ => definition.Editor
            };
            var display = definition with { Name = displayName, DisplayNameKey = null, DefaultValue = initial.Clone(), Editor = editor };
            Parameters.Add(new NodeParameterEditorViewModel(graph, selectedNode, display, item, IsCurrentTarget,
                itemMetadata?.DescriptionKey, _assetBrowser));
        }
        OnPropertyChanged(nameof(GroupExposedDiagnostics));
    }

    public CurveEditorViewModel CreateGroupCurveEditor() => new(_activeGraph, _playback);

    public void Dispose()
    {
        _activeGraph.PropertyChanged -= OnActiveGraphChanged;
        InspectorPluginService.Shared.DefaultsChanged -= OnInspectorDefaultsChanged;
        InspectorPluginService.Shared.PropertyChanged -= OnInspectorPluginServiceChanged;
        _graph.PropertyChanged -= OnGraphChanged;
        _graph.Nodes.CollectionChanged -= OnNodesChanged;
        _graph.Connections.CollectionChanged -= OnConnectionsChanged;
        if (_graph.GroupAssets is { } catalog) catalog.Changed -= OnGroupAssetsChanged;
        if (_node is { } node) node.PropertyChanged -= OnNodeChanged;
        _selectionGeneration++;
    }
}
