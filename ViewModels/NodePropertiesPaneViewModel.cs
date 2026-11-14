using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using LiberaFx.Models.Appearance;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed class NodePropertiesPaneViewModel : Tool
{
    private readonly ActiveGraphProvider _activeGraph;
    private GraphWorkspaceViewModel _graph;
    private GraphNodeViewModel? _node;

    public GraphWorkspaceViewModel Graph => _graph;
    public ObservableCollection<NodeParameterEditorViewModel> Parameters { get; } = [];
    public IRelayCommand SaveGroupCommand { get; }
    public IRelayCommand ToggleGroupLockCommand { get; }
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
    public string PluginStatus => Environment.GetEnvironmentVariable("LIBERAFX_INSPECTOR_PLUGINS") == "1"
        ? string.Join(Environment.NewLine, InspectorEditors.Warnings)
        : LocalizationService.Shared.Get("Editor.PluginsDisabled");

    public NodePropertiesPaneViewModel(ActiveGraphProvider activeGraph)
    {
        _activeGraph = activeGraph;
        _graph = activeGraph.Graph;
        SaveGroupCommand = new RelayCommand(() => RunGroupAction(_graph.SaveSelectedGroupAsset),
            () => IsGroup && _graph.GroupAssets?.HasLibraryDirectory == true &&
                  (!IsLinkedGroup || CurrentAsset is { Locked: false }));
        ToggleGroupLockCommand = new RelayCommand(() => RunGroupAction(_graph.ToggleSelectedGroupAssetLock),
            () => IsLinkedGroup && CurrentAsset is not null);
        _activeGraph.PropertyChanged += OnActiveGraphChanged;
        _graph.PropertyChanged += OnGraphChanged;
        if (_graph.GroupAssets is { } catalog) catalog.Changed += OnGroupAssetsChanged;
        RefreshNode();
    }

    private void OnActiveGraphChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActiveGraphProvider.Graph)) return;
        _graph.PropertyChanged -= OnGraphChanged;
        if (_graph.GroupAssets is { } previous) previous.Changed -= OnGroupAssetsChanged;
        _graph = _activeGraph.Graph;
        _graph.PropertyChanged += OnGraphChanged;
        if (_graph.GroupAssets is { } current) current.Changed += OnGroupAssetsChanged;
        OnPropertyChanged(nameof(Graph));
        RefreshNode();
    }

    private void OnGraphChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphWorkspaceViewModel.SelectedNode)) RefreshNode();
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphNodeViewModel.Parameters))
            foreach (var parameter in Parameters) parameter.Refresh();
        if (e.PropertyName == nameof(GraphNodeViewModel.GroupAsset)) RefreshGroupState();
    }

    private void OnGroupAssetsChanged(object? sender, EventArgs e) => RefreshGroupState();

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(PluginStatus));
        RefreshGroupState();
    }

    private void RefreshGroupState()
    {
        OnPropertyChanged(nameof(IsGroup));
        OnPropertyChanged(nameof(IsLinkedGroup));
        OnPropertyChanged(nameof(IsGroupLocked));
        OnPropertyChanged(nameof(GroupLockIcon));
        OnPropertyChanged(nameof(GroupLockHint));
        OnPropertyChanged(nameof(GroupRevision));
        OnPropertyChanged(nameof(GroupStatus));
        SaveGroupCommand.NotifyCanExecuteChanged();
        ToggleGroupLockCommand.NotifyCanExecuteChanged();
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
        if (_node is not null) _node.PropertyChanged -= OnNodeChanged;
        _node = _graph.SelectedNode;
        Parameters.Clear();
        RefreshGroupState();
        if (_node is null) return;
        _node.PropertyChanged += OnNodeChanged;
        foreach (var definition in _node.Definition.Parameters)
            Parameters.Add(new NodeParameterEditorViewModel(_graph, _node, definition));
    }
}
