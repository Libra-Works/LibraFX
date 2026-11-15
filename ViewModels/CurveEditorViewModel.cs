using System.ComponentModel;
using System.Globalization;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using LiberaFx.Models.Graph;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed class CurveEditorViewModel : Tool
{
    private readonly ActiveGraphProvider _activeGraph;
    private GraphWorkspaceViewModel _graph;
    private GraphNodeViewModel? _node;
    private FCurveSnapshot? _preview;
    private readonly HashSet<Guid> _selectedKeyIds = [];
    private Guid? _selectionNodeId;
    private Guid? _previewRevision;
    private string _status = "Messages.SelectCurveNode";
    private bool _snapToFrames = true;
    private string _channelSearch = string.Empty;
    private readonly Dictionary<Guid, bool> _channelVisibility = [];
    private readonly HashSet<GraphNodeViewModel> _observedNodes = [];
    private static readonly string[] ChannelColors = ["CurveLine", "GraphParameter", "GraphSignal", "CurveSelected"];

    public GraphNodeViewModel? Node => _node;
    public EditorPlayback? Playback { get; }
    public double? PlayheadFrame => Playback?.Frame;
    public ObservableCollection<CurveChannelViewModel> Channels { get; } = [];
    public IReadOnlyList<CurveChannelViewModel> FilteredChannels => Channels
        .Where(channel => channel.Name.Contains(ChannelSearch.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
    public string ChannelSearch
    {
        get => _channelSearch;
        set
        {
            if (SetProperty(ref _channelSearch, value ?? string.Empty))
                OnPropertyChanged(nameof(FilteredChannels));
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
    public FCurveKey? SelectedKey => SelectedKeys.Count == 1 ? SelectedKeys[0] : null;
    public IReadOnlyList<CurveInterpolation> Interpolations { get; } = Enum.GetValues<CurveInterpolation>();
    public string Status { get => LocalizationService.Shared.Get(_status); private set => SetProperty(ref _status, value); }
    public bool SnapToFrames { get => _snapToFrames; set => SetProperty(ref _snapToFrames, value); }
    public IRelayCommand AddKeyCommand { get; }
    public IRelayCommand DeleteKeyCommand { get; }

    public string FrameText { get => Format(SelectedKey?.Frame); set => ChangeNumeric(value, (k, n) => k with { Frame = n }); }
    public string ValueText { get => Format(SelectedKey?.Value); set => ChangeNumeric(value, (k, n) => k with { Value = n }); }
    public string InSlopeText { get => Format(SelectedKey?.InSlope); set => ChangeNumeric(value, (k, n) => k with { InSlope = n }); }
    public string OutSlopeText { get => Format(SelectedKey?.OutSlope); set => ChangeNumeric(value, (k, n) => k with { OutSlope = n }); }

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
        if (playback is not null) playback.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorPlayback.Frame)) OnPropertyChanged(nameof(PlayheadFrame));
        };
        _activeGraph = activeGraph;
        _graph = activeGraph.Graph;
        _activeGraph.PropertyChanged += OnActiveGraphChanged;
        _graph.PropertyChanged += OnGraphChanged;
        _graph.Nodes.CollectionChanged += OnNodesChanged;
        AddKeyCommand = new RelayCommand(AddKeyAtEnd, () => HasCurve);
        DeleteKeyCommand = new RelayCommand(DeleteSelectedKeys, () => HasSelectedKeys);
        SynchronizeChannels();
        RefreshNode();
        MessageLocalization.Observe(this, static editor => editor.OnPropertyChanged(nameof(Status)));
    }

    public void SelectChannel(CurveChannelViewModel channel)
    {
        if (_graph.Nodes.Contains(channel.Node)) _graph.SelectNode(channel.Node);
    }

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e) => SynchronizeChannels();

    private void SynchronizeChannels()
    {
        var current = _graph.Nodes.Where(node => node.Curve is not null).ToHashSet();
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
        foreach (var node in _graph.Nodes.Where(node => node.Curve is not null))
        {
            var brush = ThemeSettingsService.EditorBrush(ChannelColors[Channels.Count % ChannelColors.Length]);
            Channels.Add(new CurveChannelViewModel(node, brush, _channelVisibility.GetValueOrDefault(node.Id, true), visible =>
            {
                _channelVisibility[node.Id] = visible;
                OnPropertyChanged(nameof(VisibleCurves));
            }));
        }
        OnPropertyChanged(nameof(FilteredChannels));
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

    public void AddKey(double frame, double value)
    {
        if (_node?.Curve is not { } curve || !double.IsFinite(frame) || !double.IsFinite(value)) return;
        var key = new FCurveKey(Guid.NewGuid(), frame, value, 0, 0, CurveInterpolation.Linear);
        try
        {
            var updated = new FCurveSnapshot(curve.Keys.Append(key).OrderBy(item => item.Frame).ToArray());
            _graph.SetCurve(_node, updated, LocalizationService.Shared.Get("Messages.History.AddCurveKey"));
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
        var keys = curve.Keys.Select(item => item.Id == key.Id ? key : item).OrderBy(item => item.Frame).ToArray();
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

    public void PreviewKeys(IReadOnlyList<FCurveKey> selected, double frameDelta, double valueDelta)
    {
        if (_node?.Curve is not { } curve || selected.Count == 0) return;
        var changed = selected.ToDictionary(key => key.Id,
            key => key with { Frame = key.Frame + frameDelta, Value = key.Value + valueDelta });
        try
        {
            var preview = new FCurveSnapshot(curve.Keys.Select(key => changed.GetValueOrDefault(key.Id, key))
                .OrderBy(key => key.Frame).ToArray());
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
            _graph.SetCurve(_node, preview, LocalizationService.Shared.Get(
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

    private void AddKeyAtEnd()
    {
        if (Curve is not { } curve) return;
        var last = curve.Keys.LastOrDefault();
        AddKey(last is null ? 0 : last.Frame + 10, last?.Value ?? 0);
    }

    private void DeleteSelectedKeys()
    {
        if (_node?.Curve is not { } curve || _selectedKeyIds.Count == 0) return;
        _graph.SetCurve(_node, new FCurveSnapshot(curve.Keys.Where(key => !_selectedKeyIds.Contains(key.Id)).ToArray()),
            LocalizationService.Shared.Get(_selectedKeyIds.Count > 1 ? "Messages.History.DeleteCurveKeys" : "Messages.History.DeleteCurveKey"));
        _selectedKeyIds.Clear();
        NotifyCurve();
    }

    private void ChangeNumeric(string? text, Func<FCurveKey, double, FCurveKey> change)
    {
        if (SelectedKey is { } key &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) &&
            double.IsFinite(value))
            CommitKey(change(key, value), LocalizationService.Shared.Get("Messages.History.EditCurveKey"));
        else
        {
            Status = "Messages.EnterFiniteNumber";
            NotifyCurve();
        }
    }

    private void CommitKey(FCurveKey key, string description)
    {
        if (_node?.Curve is not { } curve) return;
        try
        {
            var updated = new FCurveSnapshot(curve.Keys.Select(item => item.Id == key.Id ? key : item)
                .OrderBy(item => item.Frame).ToArray());
            _graph.SetCurve(_node, updated, description);
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
        if (e.PropertyName == nameof(GraphWorkspaceViewModel.SelectedNode)) RefreshNode();
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphNodeViewModel.Name))
        {
            var channel = Channels.FirstOrDefault(item => ReferenceEquals(item.Node, sender));
            channel?.NotifyNameChanged();
            OnPropertyChanged(nameof(FilteredChannels));
        }
        if (e.PropertyName == nameof(GraphNodeViewModel.Curve))
        {
            OnPropertyChanged(nameof(VisibleCurves));
            if (ReferenceEquals(sender, _node)) NotifyCurve();
        }
    }

    private void RefreshNode()
    {
        var node = _graph.SelectedNode?.Curve is not null ? _graph.SelectedNode : null;
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
        Status = _node is null ? "Messages.SelectCurveNode" : "Messages.CurveEditingHint";
        OnPropertyChanged(nameof(Node));
        NotifyCurve();
    }

    private void NotifyCurve()
    {
        OnPropertyChanged(nameof(Curve));
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
        DeleteKeyCommand.NotifyCanExecuteChanged();
    }

    private static string Format(double? value) => value?.ToString("G8", CultureInfo.CurrentCulture) ?? "";
}

public sealed record CurveDisplay(GraphNodeViewModel Node, FCurveSnapshot Curve, IBrush Brush);

public sealed class CurveChannelViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private bool _isVisible;
    private readonly Action<bool> _visibilityChanged;
    public GraphNodeViewModel Node { get; }
    public string Name => Node.Name;
    public IBrush Brush { get; }
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (SetProperty(ref _isVisible, value)) _visibilityChanged(value);
        }
    }

    public CurveChannelViewModel(GraphNodeViewModel node, IBrush brush, bool isVisible, Action<bool> visibilityChanged)
    {
        Node = node;
        Brush = brush;
        _isVisible = isVisible;
        _visibilityChanged = visibilityChanged;
    }

    public void NotifyNameChanged() => OnPropertyChanged(nameof(Name));
}
