using System.ComponentModel;
using System.Globalization;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Preview;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;

namespace LiberaFx.ViewModels.Panels;

public sealed class CurveEditorViewModel : Tool, IDisposable
{
    private readonly ActiveGraphProvider _activeGraph;
    private GraphWorkspaceViewModel _graph;
    private GraphNodeViewModel? _node;
    private GraphNodeViewModel? _group;
    private readonly Dictionary<Guid, (GroupExposedItem Item, int Index)> _groupChannels = [];
    private readonly List<GraphNodeViewModel> _groupNodes = [];
    private FCurveSnapshot? _preview;
    private readonly HashSet<Guid> _selectedKeyIds = [];
    private Guid? _selectionNodeId;
    private Guid? _previewRevision;
    private string _status = string.Empty;
    private bool _snapToFrames = true;
    private string _channelSearch = string.Empty;
    private readonly Dictionary<Guid, bool> _channelVisibility = [];
    private readonly HashSet<GraphNodeViewModel> _observedNodes = [];
    private static readonly string[] ChannelColors = ["CurveLine", "GraphParameter", "GraphSignal", "CurveSelected"];

    public GraphNodeViewModel? Node => _node;
    public EditorPlayback? Playback { get; }
    public double? PlayheadFrame => Playback?.Frame;
    public bool IsColorCurve => _node is not null &&
        _groupChannels.TryGetValue(_node.Id, out var channel) && channel.Item.Editor == "colorCurve";
    public IBrush? ColorCurvePreview
    {
        get
        {
            if (_node is null || !_groupChannels.TryGetValue(_node.Id, out var channel) ||
                channel.Item.Editor != "colorCurve")
            {
                return null;
            }
            var rgba = channel.Item.Targets.Select(target =>
            {
                var node = _groupNodes.FirstOrDefault(candidate => candidate.Id == target.NodeId);
                var curve = ReferenceEquals(node, _node) ? Curve : node?.Curve;
                var value = curve is null ? 0 : Models.Graph.FCurve.Evaluate(curve, Playback?.TimeSeconds ?? 0);
                return (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
            }).ToArray();
            return rgba.Length == 4
                ? new SolidColorBrush(Color.FromArgb(rgba[3], rgba[0], rgba[1], rgba[2]))
                : null;
        }
    }
    public ObservableCollection<CurveChannelViewModel> Channels { get; } = [];
    public IReadOnlyList<CurveChannelViewModel> FilteredChannels => Channels
        .Where(channel => channel.Name.Contains(ChannelSearch.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
    public IReadOnlyList<CurveChannelGroupViewModel> FilteredChannelGroups => BuildFilteredChannelGroups();
    public string ChannelSearch
    {
        get => _channelSearch;
        set
        {
            if (SetProperty(ref _channelSearch, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(FilteredChannels));
                OnPropertyChanged(nameof(FilteredChannelGroups));
            }
        }
    }
    public IReadOnlyList<CurveDisplay> VisibleCurves => Channels.Where(channel => channel.IsVisible)
        .Select(channel => new CurveDisplay(channel.Node,
            ReferenceEquals(channel.Node, _node) && _preview is not null ? _preview : channel.Node.Curve!,
            channel.Brush)).ToArray();
    public FCurveSnapshot? Curve => _preview ?? _node?.Curve;
    public bool HasCurve => _node?.Curve is not null;
    public bool HasKey => SelectedKey is not null;
    public IReadOnlyList<FCurveKey> SelectedKeys => Curve?.Keys.Where(key => _selectedKeyIds.Contains(key.Id)).ToArray() ?? [];
    public bool HasSelectedKeys => SelectedKeys.Count > 0;
    public double SnapDisplayTime(double displayTime)
    {
        if (!SnapToFrames) return displayTime;
        var framesPerUnit = Playback?.ShowSeconds == true ? Playback.FramesPerSecond : 1;
        return Math.Round(displayTime * framesPerUnit) / framesPerUnit;
    }
    public FCurveKey? SelectedKey => SelectedKeys.Count == 1 ? SelectedKeys[0] : null;
    public IReadOnlyList<CurveInterpolation> Interpolations { get; } = Enum.GetValues<CurveInterpolation>();
    public string Status { get => LocalizationService.Shared.Get(_status); private set => SetProperty(ref _status, value); }
    public bool SnapToFrames { get => _snapToFrames; set => SetProperty(ref _snapToFrames, value); }
    public string TimeUnitLabel => Playback?.TimeUnitLabel ?? LocalizationService.Shared.Get("Timeline.Seconds");
    public IRelayCommand AddKeyCommand { get; }
    public IRelayCommand SplitKeyCommand { get; }
    public IRelayCommand DeleteKeyCommand { get; }

    public string FrameText
    {
        get => SelectedKey is { } key ? Playback?.FormatTime(key.TimeSeconds) ?? Format(key.TimeSeconds) : "";
        set
        {
            if (Playback is null) ChangeNumeric(value, (k, n) => k with { TimeSeconds = n });
            else if (Playback.TryParseTime(value, out var timeSeconds))
                ChangeValue(timeSeconds, (k, n) => k with { TimeSeconds = n });
            else RejectNumber();
        }
    }
    public string ValueText { get => Format(SelectedKey?.Value); set => ChangeNumeric(value, (k, n) => k with { Value = n }); }
    public string InSlopeText
    {
        get => Format(SelectedKey is { } key ? Playback?.DisplaySlope(key.InSlope) ?? key.InSlope : null);
        set => ChangeSlope(value, incoming: true);
    }
    public string OutSlopeText
    {
        get => Format(SelectedKey is { } key ? Playback?.DisplaySlope(key.OutSlope) ?? key.OutSlope : null);
        set => ChangeSlope(value, incoming: false);
    }

    public CurveInterpolation SelectedInterpolation
    {
        get => SelectedKey?.Interpolation ?? CurveInterpolation.Linear;
        set
        {
            if (SelectedKey is { } key && Enum.IsDefined(value))
                CommitKey(key with { Interpolation = value }, LocalizationService.Shared.Get("Messages.History.CurveInterpolation"));
        }
    }

    public CurveEditorViewModel(ActiveGraphProvider activeGraph, EditorPlayback? playback = null)
    {
        Playback = playback;
        if (playback is not null) playback.PropertyChanged += OnPlaybackChanged;
        _activeGraph = activeGraph;
        _graph = activeGraph.Graph;
        _activeGraph.PropertyChanged += OnActiveGraphChanged;
        _graph.PropertyChanged += OnGraphChanged;
        _graph.Nodes.CollectionChanged += OnNodesChanged;
        AddKeyCommand = new RelayCommand(AddKeyAtEnd, () => HasCurve);
        SplitKeyCommand = new RelayCommand(SplitSelectedKey, () => SelectedKeys.Count == 1);
        DeleteKeyCommand = new RelayCommand(DeleteSelectedKeys, () => HasSelectedKeys);
        SynchronizeChannels();
        RefreshNode();
        MessageLocalization.Observe(this, static editor => editor.OnPropertyChanged(nameof(Status)));
    }

    public void SelectChannel(CurveChannelViewModel channel)
    {
        if (_graph.Nodes.Contains(channel.Node)) _graph.SelectNode(channel.Node);
        else if (_groupChannels.ContainsKey(channel.Node.Id))
            SetNode(channel.Node);
    }

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorPlayback.Frame)) OnPropertyChanged(nameof(PlayheadFrame));
        if (e.PropertyName == nameof(EditorPlayback.TimeSeconds) && IsColorCurve)
        {
            OnPropertyChanged(nameof(ColorCurvePreview));
        }
        if (e.PropertyName is nameof(EditorPlayback.FramesPerSecond) or nameof(EditorPlayback.ShowSeconds))
        {
            OnPropertyChanged(nameof(TimeUnitLabel));
            OnPropertyChanged(nameof(FrameText));
            OnPropertyChanged(nameof(InSlopeText));
            OnPropertyChanged(nameof(OutSlopeText));
        }
    }

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e) => SynchronizeChannels();

    private void SynchronizeChannels()
    {
        var channelNodes = _group is null ? _graph.Nodes.Where(node => node.Curve is not null).ToArray() : _groupNodes.ToArray();
        var current = channelNodes.ToHashSet();
        foreach (var node in _observedNodes.Except(current).ToArray())
        {
            node.PropertyChanged -= OnNodeChanged;
            _observedNodes.Remove(node);
        }
        foreach (var node in current.Except(_observedNodes))
        {
            node.PropertyChanged += OnNodeChanged;
            _observedNodes.Add(node);
        }
        Channels.Clear();
        foreach (var node in channelNodes)
        {
            var brush = ThemeSettingsService.EditorBrush(ChannelColors[Channels.Count % ChannelColors.Length]);
            var label = _groupChannels.TryGetValue(node.Id, out var meta) &&
                        GroupExposedItems.CurveChannels(meta.Item.Editor) is { Length: > 1 } names
                ? names[meta.Index].ToString()
                : LocalizationService.Shared?.Get("Panels.Value") ?? "Value";
            Channels.Add(new CurveChannelViewModel(node, label, brush, _channelVisibility.GetValueOrDefault(node.Id, true), visible =>
            {
                _channelVisibility[node.Id] = visible;
                OnPropertyChanged(nameof(VisibleCurves));
            }));
        }
        OnPropertyChanged(nameof(FilteredChannels));
        OnPropertyChanged(nameof(FilteredChannelGroups));
        OnPropertyChanged(nameof(VisibleCurves));
    }

    public bool IsKeySelected(Guid id) => _selectedKeyIds.Contains(id);

    public void SelectKey(Guid? id, bool toggle = false, bool preserveSelection = false)
    {
        if (!toggle && !(preserveSelection && id is { } selected && _selectedKeyIds.Contains(selected)))
            _selectedKeyIds.Clear();
        if (id is { } keyId && Curve?.Keys.Any(key => key.Id == keyId) == true)
        {
            if (toggle && !_selectedKeyIds.Add(keyId)) _selectedKeyIds.Remove(keyId);
            else if (!toggle) _selectedKeyIds.Add(keyId);
        }
        NotifyCurve();
    }

    public void SelectKeys(IEnumerable<Guid> ids, bool additive)
    {
        if (!additive) _selectedKeyIds.Clear();
        var available = Curve?.Keys.Select(key => key.Id).ToHashSet() ?? [];
        _selectedKeyIds.UnionWith(ids.Where(available.Contains));
        NotifyCurve();
    }

    public void AddKey(double timeSeconds, double value)
    {
        if (_node?.Curve is not { } curve || !double.IsFinite(timeSeconds) || !double.IsFinite(value)) return;
        var key = new FCurveKey(Guid.NewGuid(), timeSeconds, value, 0, 0, CurveInterpolation.Linear);
        try
        {
            var updated = new FCurveSnapshot(curve.Keys.Append(key).OrderBy(item => item.TimeSeconds).ToArray());
            SetCurve(_node, updated, LocalizationService.Shared.Get("Messages.History.AddCurveKey"));
            _selectedKeyIds.Clear();
            _selectedKeyIds.Add(key.Id);
            Status = "Messages.CurveKeyAdded";
        }
        catch (InvalidDataException ex) { Status = ex.Message; }
        NotifyCurve();
    }

    public void PreviewKey(FCurveKey key)
    {
        if (_node?.Curve is not { } curve) return;
        var keys = curve.Keys.Select(item => item.Id == key.Id ? key : item).OrderBy(item => item.TimeSeconds).ToArray();
        try
        {
            var preview = new FCurveSnapshot(keys);
            FCurve.Validate(preview);
            _previewRevision ??= _graph.History.CurrentNodeId;
            _preview = preview;
            NotifyCurve();
        }

        catch (InvalidDataException) { _preview = null; NotifyCurve(); }
    }

    public void PreviewKeys(IReadOnlyList<FCurveKey> selected, double timeDeltaSeconds, double valueDelta)
    {
        if (_node?.Curve is not { } curve || selected.Count == 0) return;
        var changed = selected.ToDictionary(key => key.Id,
            key => key with { TimeSeconds = key.TimeSeconds + timeDeltaSeconds, Value = key.Value + valueDelta });
        try
        {
            var preview = new FCurveSnapshot(curve.Keys.Select(key => changed.GetValueOrDefault(key.Id, key))
                .OrderBy(key => key.TimeSeconds).ToArray());
            FCurve.Validate(preview);
            _previewRevision ??= _graph.History.CurrentNodeId;
            _preview = preview;
            NotifyCurve();
        }
        catch (InvalidDataException) { _preview = null; NotifyCurve(); }
    }

    public void CommitPreview()
    {
        var preview = _preview;
        _preview = null;
        if (preview is not null && _node is not null && _previewRevision == _graph.History.CurrentNodeId)
            SetCurve(_node, preview, LocalizationService.Shared.Get(
                SelectedKeys.Count > 1 ? "Messages.History.MoveCurveKeys" : "Messages.History.MoveCurveKey"));
        _previewRevision = null;
        NotifyCurve();
    }

    public void CancelPreview()
    {
        _preview = null;
        _previewRevision = null;
        NotifyCurve();
    }

    public void SplitSelectedKey()
    {
        if (_node?.Curve is not { } curve || SelectedKeys.Count != 1 || SelectedKey is not { } key) return;
        const double step = 1e-4;
        var splitTime = key.TimeSeconds + step;
        while (curve.Keys.Any(existing => Math.Abs(existing.TimeSeconds - splitTime) < 1e-7))
            splitTime += step;
        var split = new FCurveKey(Guid.NewGuid(), splitTime, key.Value, 0, 0, key.Interpolation);
        try
        {
            var updated = new FCurveSnapshot(curve.Keys.Append(split).OrderBy(item => item.TimeSeconds).ToArray());
            SetCurve(_node, updated, LocalizationService.Shared.Get("Timeline.CurveSplit"));
            _selectedKeyIds.Clear();
            _selectedKeyIds.Add(split.Id);
            Status = "Messages.CurveKeyAdded";
        }
        catch (InvalidDataException ex) { Status = ex.Message; }
        NotifyCurve();
    }

    private void AddKeyAtEnd()
    {
        if (Curve is not { } curve) return;
        var last = curve.Keys.LastOrDefault();
        AddKey(last is null ? 0 : last.TimeSeconds + 10 / (Playback?.FramesPerSecond ?? 60), last?.Value ?? 0);
    }

    private void DeleteSelectedKeys()
    {
        if (_node?.Curve is not { } curve || _selectedKeyIds.Count == 0) return;
        SetCurve(_node, new FCurveSnapshot(curve.Keys.Where(key => !_selectedKeyIds.Contains(key.Id)).ToArray()),
            LocalizationService.Shared.Get(_selectedKeyIds.Count > 1 ? "Messages.History.DeleteCurveKeys" : "Messages.History.DeleteCurveKey"));
        _selectedKeyIds.Clear();
        NotifyCurve();
    }

    private void ChangeNumeric(string? text, Func<FCurveKey, double, FCurveKey> change)
    {
        if (SelectedKey is { } key &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) &&
            double.IsFinite(value))
            ChangeValue(value, change);
        else
            RejectNumber();
    }

    private void ChangeSlope(string? text, bool incoming)
    {
        if (SelectedKey is not { } key ||
            !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ||
            !double.IsFinite(value))
        {
            RejectNumber();
            return;
        }
        var slopePerSecond = Playback?.SlopePerSecond(value) ?? value;
        ChangeValue(slopePerSecond, (current, slope) => incoming
            ? current with { InSlope = slope } : current with { OutSlope = slope });
    }

    private void ChangeValue(double value, Func<FCurveKey, double, FCurveKey> change)
    {
        if (SelectedKey is { } key)
            CommitKey(change(key, value), LocalizationService.Shared.Get("Messages.History.EditCurveKey"));
    }

    private void RejectNumber()
    {
        Status = "Messages.EnterFiniteNumber";
        NotifyCurve();
    }

    private void CommitKey(FCurveKey key, string description)
    {
        if (_node?.Curve is not { } curve) return;
        try
        {
            var updated = new FCurveSnapshot(curve.Keys.Select(item => item.Id == key.Id ? key : item)
                .OrderBy(item => item.TimeSeconds).ToArray());
            SetCurve(_node, updated, description);
            Status = "Messages.CurveKeyUpdated";
        }
        catch (InvalidDataException ex) { Status = ex.Message; }
        NotifyCurve();
    }

    private void OnActiveGraphChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActiveGraphProvider.Graph)) return;
        _graph.PropertyChanged -= OnGraphChanged;
        _graph.Nodes.CollectionChanged -= OnNodesChanged;
        if (_group is not null) _group.PropertyChanged -= OnGroupChanged;
        _group = null;
        _groupNodes.Clear();
        _groupChannels.Clear();
        foreach (var node in _observedNodes) node.PropertyChanged -= OnNodeChanged;
        _observedNodes.Clear();
        _graph = _activeGraph.Graph;
        _selectedKeyIds.Clear();
        _selectionNodeId = null;
        _graph.PropertyChanged += OnGraphChanged;
        _graph.Nodes.CollectionChanged += OnNodesChanged;
        SynchronizeChannels();
        RefreshNode();
    }

    private void OnGraphChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphWorkspaceViewModel.InspectorNode)) RefreshNode();
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphNodeViewModel.Name))
        {
            var channel = Channels.FirstOrDefault(item => ReferenceEquals(item.Node, sender));
            channel?.NotifyNameChanged();
            OnPropertyChanged(nameof(FilteredChannels));
            OnPropertyChanged(nameof(FilteredChannelGroups));
        }
        if (e.PropertyName == nameof(GraphNodeViewModel.Curve))
        {
            OnPropertyChanged(nameof(VisibleCurves));
            if (ReferenceEquals(sender, _node)) NotifyCurve();
        }
    }

    private void RefreshNode()
    {
        var group = _graph.InspectorNode is { ChildGraph: not null } selected ? selected : null;
        if (!ReferenceEquals(group, _group))
        {
            if (_group is not null) _group.PropertyChanged -= OnGroupChanged;
            _group = group;
            _groupNodes.Clear();
            _groupChannels.Clear();
            if (group is not null)
            {
                group.PropertyChanged += OnGroupChanged;
                BuildGroupChannels(group);
            }
            SynchronizeChannels();
        }
        var node = group is null
            ? _graph.SelectedNode?.Curve is not null ? _graph.SelectedNode : null
            : _groupNodes.FirstOrDefault();
        SetNode(node);
    }

    private void SetNode(GraphNodeViewModel? node)
    {
        if (ReferenceEquals(node, _node)) return;
        _node = node;
        _preview = null;
        _previewRevision = null;
        if (_node?.Curve is { } curve)
        {
            if (_selectionNodeId != _node.Id) _selectedKeyIds.Clear();
            _selectionNodeId = _node.Id;
            _selectedKeyIds.IntersectWith(curve.Keys.Select(key => key.Id));
        }
        Status = _node is null ? string.Empty : "Messages.CurveEditingHint";
        OnPropertyChanged(nameof(Node));
        NotifyCurve();
    }

    private void BuildGroupChannels(GraphNodeViewModel group)
    {
        var child = group.ChildGraph!;
        foreach (var item in (child.ExposedItems ?? []).Where(item => GroupExposedItems.CurveChannels(item.Editor).Length != 0)
                     .OrderBy(item => item.Order))
        {
            var targets = item.Targets.Select(target => child.Nodes.FirstOrDefault(node =>
                node.Id == target.NodeId && node.Kind == GraphNodeKind.FloatFCurve && node.Curve is not null)).ToArray();
            var value = group.GroupOverrides?.FirstOrDefault(candidate => candidate.ItemId == item.Id);
            if (targets.Any(node => node is null) || value is not null &&
                (value.Components.Count != targets.Length || value.Components.Any(component => component.Curve is null)))
                continue;
            var channels = GroupExposedItems.CurveChannels(item.Editor);
            for (var index = 0; index < targets.Length; index++)
            {
                var target = targets[index]!;
                if (_groupChannels.ContainsKey(target.Id)) continue;
                var name = channels.Length > 1 ? $"{item.Name} / {channels[index]}" : item.Name;
                var definition = GraphNodeDefinition.All.Single(candidate => candidate.Kind == target.Kind);
                var curve = value?.Components[index].Curve ?? target.Curve!;
                var node = new GraphNodeViewModel(_graph, definition, name, 0, 0, target.Id, curve);
                _groupNodes.Add(node);
                _groupChannels.Add(node.Id, (item, index));
            }
        }
    }

    private void OnGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GraphNodeViewModel.GroupOverrides) || _group is null) return;
        foreach (var node in _groupNodes)
        {
            var (item, index) = _groupChannels[node.Id];
            var value = _group.GroupOverrides?.FirstOrDefault(candidate => candidate.ItemId == item.Id);
            var curve = value?.Components[index].Curve ??
                _group.ChildGraph!.Nodes.Single(target => target.Id == node.Id).Curve!;
            node.SetCurveValue(curve);
        }
        OnPropertyChanged(nameof(ColorCurvePreview));
    }

    private void SetCurve(GraphNodeViewModel node, FCurveSnapshot curve, string description)
    {
        if (_group is null || !_groupChannels.TryGetValue(node.Id, out var channel))
        {
            _graph.SetCurve(node, curve, description);
            return;
        }
        var previous = _group.GroupOverrides?.FirstOrDefault(value => value.ItemId == channel.Item.Id);
        var components = channel.Item.Targets.Select((target, index) =>
        {
            var current = previous?.Components[index].Curve ??
                _group.ChildGraph!.Nodes.Single(candidate => candidate.Id == target.NodeId).Curve!;
            return new GroupInstanceComponent(Curve: index == channel.Index ? curve : current);
        }).ToArray();
        _graph.SetGroupInstanceValue(_group, new(channel.Item.Id, components));
    }

    public void Dispose()
    {
        if (Playback is not null) Playback.PropertyChanged -= OnPlaybackChanged;
        _activeGraph.PropertyChanged -= OnActiveGraphChanged;
        _graph.PropertyChanged -= OnGraphChanged;
        _graph.Nodes.CollectionChanged -= OnNodesChanged;
        if (_group is not null) _group.PropertyChanged -= OnGroupChanged;
        foreach (var node in _observedNodes) node.PropertyChanged -= OnNodeChanged;
        _observedNodes.Clear();
    }

    private void NotifyCurve()
    {
        OnPropertyChanged(nameof(Curve));
        OnPropertyChanged(nameof(IsColorCurve));
        OnPropertyChanged(nameof(ColorCurvePreview));
        OnPropertyChanged(nameof(VisibleCurves));
        OnPropertyChanged(nameof(HasCurve));
        OnPropertyChanged(nameof(SelectedKey));
        OnPropertyChanged(nameof(SelectedKeys));
        OnPropertyChanged(nameof(HasSelectedKeys));
        OnPropertyChanged(nameof(HasKey));
        OnPropertyChanged(nameof(FrameText));
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(InSlopeText));
        OnPropertyChanged(nameof(OutSlopeText));
        OnPropertyChanged(nameof(SelectedInterpolation));
        AddKeyCommand.NotifyCanExecuteChanged();
        SplitKeyCommand.NotifyCanExecuteChanged();
        DeleteKeyCommand.NotifyCanExecuteChanged();
    }

    private IReadOnlyList<CurveChannelGroupViewModel> BuildFilteredChannelGroups()
    {
        var query = ChannelSearch.Trim();
        var groups = new List<CurveChannelGroupViewModel>();
        var groupedById = new Dictionary<Guid, List<CurveChannelViewModel>>();
        var groupNames = new Dictionary<Guid, string>();
        var order = new List<(bool IsGroup, Guid Id, CurveChannelViewModel? Single)>();

        foreach (var channel in Channels)
        {
            if (_groupChannels.TryGetValue(channel.Node.Id, out var meta))
            {
                if (!groupedById.TryGetValue(meta.Item.Id, out var list))
                {
                    list = [];
                    groupedById[meta.Item.Id] = list;
                    groupNames[meta.Item.Id] = meta.Item.Name;
                    order.Add((true, meta.Item.Id, null));
                }
                list.Add(channel);
            }
            else
            {
                order.Add((false, channel.Node.Id, channel));
            }
        }

        foreach (var entry in order)
        {
            if (entry.IsGroup)
            {
                var name = groupNames[entry.Id];
                var members = groupedById[entry.Id];
                var matching = string.IsNullOrEmpty(query) || name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    ? members
                    : members.Where(c => c.Label.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matching.Count > 0)
                    groups.Add(new CurveChannelGroupViewModel(name, matching));
            }
            else if (entry.Single is { } single)
            {
                if (string.IsNullOrEmpty(query) ||
                    single.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    single.Label.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    groups.Add(new CurveChannelGroupViewModel(single.Name, [single]));
                }
            }
        }

        return groups;
    }

    private static string Format(double? value) => value?.ToString("G8", CultureInfo.CurrentCulture) ?? "";
}

public sealed record CurveDisplay(GraphNodeViewModel Node, FCurveSnapshot Curve, IBrush Brush);

public sealed record CurveChannelGroupViewModel(string Name, IReadOnlyList<CurveChannelViewModel> Channels);

public sealed class CurveChannelViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private bool _isVisible;
    private readonly Action<bool> _visibilityChanged;
    public GraphNodeViewModel Node { get; }
    public string Name => Node.Name;
    public string Label { get; }
    public IBrush Brush { get; }
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (SetProperty(ref _isVisible, value)) _visibilityChanged(value);
        }
    }

    public CurveChannelViewModel(GraphNodeViewModel node, string label, IBrush brush, bool isVisible, Action<bool> visibilityChanged)
    {
        Node = node;
        Label = label;
        Brush = brush;
        _isVisible = isVisible;
        _visibilityChanged = visibilityChanged;
    }

    public void NotifyNameChanged() => OnPropertyChanged(nameof(Name));
}
