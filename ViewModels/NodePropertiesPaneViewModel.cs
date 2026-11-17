using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using LiberaFx.Models.Appearance;
using LiberaFx.Models.Graph;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed class NodePropertiesPaneViewModel : Tool
{
    private readonly ActiveGraphProvider _activeGraph;
    private readonly EditorPlayback? _playback;
    private GraphWorkspaceViewModel _graph;
    private GraphNodeViewModel? _node;
    private long _selectionGeneration;
    private readonly List<string> _unavailableExposedItems = [];

    public GraphWorkspaceViewModel Graph => _graph;
    public ObservableCollection<NodeParameterEditorViewModel> Parameters { get; } = [];
    public string GroupExposedDiagnostics => string.Join(Environment.NewLine,
        _unavailableExposedItems.Select(name => LocalizationService.Shared.Get("Panels.GroupExposedUnavailable", name)));
    public IRelayCommand SaveGroupCommand { get; }
    public IRelayCommand ToggleGroupLockCommand { get; }
    public IRelayCommand PublishCurvesCommand { get; }
    public bool CanPublishCurves => _graph.CanPublishGroupCurves();
    public bool HasExposedCurves => _node?.ChildGraph?.ExposedItems?.Any(item =>
        item.Editor is "curve" or "vector3Curve") == true;
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
        _graph.GroupAssets?.HasLibraryDirectory != true ? "Editor.GroupNoLibrary" :
        !IsLinkedGroup ? "Editor.GroupUnlinked" :
        CurrentAsset is null ? "Editor.GroupMissing" :
        IsGroupLocked ? "Editor.GroupLocked" : "Editor.GroupUnlocked");
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

    public NodePropertiesPaneViewModel(ActiveGraphProvider activeGraph, EditorPlayback? playback = null)
    {
        _activeGraph = activeGraph;
        _playback = playback;
        _graph = activeGraph.Graph;
        SaveGroupCommand = new RelayCommand(() => RunGroupAction(_graph.SaveSelectedGroupAsset),
            () => IsGroup && _graph.GroupAssets?.HasLibraryDirectory == true &&
                  (!IsLinkedGroup || CurrentAsset is { Locked: false }));
        ToggleGroupLockCommand = new RelayCommand(() => RunGroupAction(_graph.ToggleSelectedGroupAssetLock),
            () => IsLinkedGroup && CurrentAsset is not null);
        PublishCurvesCommand = new RelayCommand(() => RunGroupAction(() => _graph.PublishGroupCurves()),
            () => CanPublishCurves);
        _activeGraph.PropertyChanged += OnActiveGraphChanged;
        InspectorPluginService.Shared.DefaultsChanged += OnInspectorDefaultsChanged;
        InspectorPluginService.Shared.PropertyChanged += OnInspectorPluginServiceChanged;
        _graph.PropertyChanged += OnGraphChanged;
        _graph.Connections.CollectionChanged += OnConnectionsChanged;
        if (_graph.GroupAssets is { } catalog) catalog.Changed += OnGroupAssetsChanged;
        RefreshNode();
    }

    private void OnActiveGraphChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActiveGraphProvider.Graph)) return;
        _graph.PropertyChanged -= OnGraphChanged;
        _graph.Connections.CollectionChanged -= OnConnectionsChanged;
        if (_graph.GroupAssets is { } previous) previous.Changed -= OnGroupAssetsChanged;
        _graph = _activeGraph.Graph;
        _graph.PropertyChanged += OnGraphChanged;
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
        if (e.PropertyName == nameof(GraphWorkspaceViewModel.SelectedNode)) RefreshNode();
        if (e.PropertyName == nameof(GraphWorkspaceViewModel.SelectionCount))
        {
            OnPropertyChanged(nameof(CanPublishCurves));
            PublishCurvesCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphNodeViewModel.Definition))
        {
            RefreshNode();
            return;
        }
        if (e.PropertyName is nameof(GraphNodeViewModel.Parameters) or nameof(GraphNodeViewModel.GroupOverrides))
            foreach (var parameter in Parameters) parameter.Refresh();
        if (e.PropertyName == nameof(GraphNodeViewModel.GroupAsset)) RefreshGroupState();
    }

    private void OnConnectionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var parameter in Parameters) parameter.Refresh();
    }

    private void OnGroupAssetsChanged(object? sender, EventArgs e) => RefreshGroupState();

    public void RefreshLocalizedText()
    {
        InspectorPluginService.Shared.RefreshLocalizedText();
        OnPropertyChanged(nameof(PluginStatus));
        OnPropertyChanged(nameof(GroupExposedDiagnostics));
        RefreshGroupState();
    }

    private void RefreshGroupState()
    {
        OnPropertyChanged(nameof(IsGroup));
        OnPropertyChanged(nameof(HasExposedCurves));
        OnPropertyChanged(nameof(HasExposedItems));
        OnPropertyChanged(nameof(CanPublishCurves));
        OnPropertyChanged(nameof(IsLinkedGroup));
        OnPropertyChanged(nameof(IsGroupLocked));
        OnPropertyChanged(nameof(GroupLockIcon));
        OnPropertyChanged(nameof(GroupLockHint));
        OnPropertyChanged(nameof(GroupRevision));
        OnPropertyChanged(nameof(GroupStatus));
        SaveGroupCommand.NotifyCanExecuteChanged();
        ToggleGroupLockCommand.NotifyCanExecuteChanged();
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
        _node = _graph.SelectedNode;
        Parameters.Clear();
        _unavailableExposedItems.Clear();
        OnPropertyChanged(nameof(GroupExposedDiagnostics));
        RefreshGroupState();
        if (_node is null) return;
        _node.PropertyChanged += OnNodeChanged;
        var graph = _graph;
        var selectedNode = _node;
        foreach (var definition in _node.Definition.Parameters.Where(parameter =>
                     !_node.IsGpuParticle &&
                     (!RenderNodeSchema.IsRenderNode(_node.Definition) || parameter.Id != "materialSchema")))
        {
            Parameters.Add(new NodeParameterEditorViewModel(graph, selectedNode, definition, isCurrentInspectorTarget: () =>
                selectionGeneration == _selectionGeneration &&
                ReferenceEquals(_activeGraph.Graph, graph) && ReferenceEquals(graph.SelectedNode, selectedNode)));
        }
        if (_node.ChildGraph is not { } child) return;
        foreach (var item in (child.ExposedItems ?? []).OrderBy(item => item.Order))
        {
            if (item.Editor is "curve" or "vector3Curve")
            {
                if (item.Targets.Any(target => !child.Nodes.Any(node => node.Id == target.NodeId &&
                        node.Kind == GraphNodeKind.FloatFCurve && node.Curve is not null)))
                    _unavailableExposedItems.Add(item.Name);
                continue;
            }
            if (item.Editor != "parameter" || item.Targets.Count != 1)
            {
                _unavailableExposedItems.Add(item.Name);
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
                _unavailableExposedItems.Add(item.Name);
                continue;
            }
            var editor = item.Presentation switch
            {
                "slider" => new NodeParameterEditorDefinition("slider", item.Minimum, item.Maximum, item.Step),
                "text" => new NodeParameterEditorDefinition("text", null, null, null),
                _ => definition.Editor
            };
            var display = definition with { Name = item.Name, DisplayNameKey = null, DefaultValue = initial.Clone(), Editor = editor };
            Parameters.Add(new NodeParameterEditorViewModel(graph, selectedNode, display, item, () =>
                selectionGeneration == _selectionGeneration &&
                ReferenceEquals(_activeGraph.Graph, graph) && ReferenceEquals(graph.SelectedNode, selectedNode)));
        }
        OnPropertyChanged(nameof(GroupExposedDiagnostics));
    }

    public CurveEditorViewModel CreateGroupCurveEditor() => new(_activeGraph, _playback);
}
