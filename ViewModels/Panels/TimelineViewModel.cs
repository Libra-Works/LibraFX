using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Globalization;
using Dock.Model.Mvvm.Controls;
using CommunityToolkit.Mvvm.Input;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Preview;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;

namespace LiberaFx.ViewModels.Panels;

public sealed class TimelineViewModel : Tool
{
    private readonly ActiveGraphProvider _active;
    private GraphWorkspaceViewModel _graph;
    private string? _statusKey;
    private bool _refreshQueued;
    private SignalTimelineMode _signalMode;
    private TimelineSignalKeyViewModel? _selectedSignalKey;
    public EditorPlayback Playback { get; }
    public ObservableCollection<TimelineTrackViewModel> Tracks { get; } = [];
    public TimelineTrackViewModel? SelectedTrack => Tracks.FirstOrDefault(track =>
        track.Node == _graph.SelectedNode && (!track.IsSignal || track.SignalMode == _signalMode)) ??
        Tracks.FirstOrDefault(track => track.Node == _graph.SelectedNode);
    public IRelayCommand AddPreviewSignalKeyCommand { get; }
    public IRelayCommand AddRuntimeSignalKeyCommand { get; }
    public IRelayCommand DeleteSignalKeyCommand { get; }
    public TimelineSignalKeyViewModel? SelectedSignalKey
    {
        get => _selectedSignalKey;
        set
        {
            if (!SetProperty(ref _selectedSignalKey, value)) return;
            OnPropertyChanged(nameof(SignalTimeText));
            DeleteSignalKeyCommand.NotifyCanExecuteChanged();
        }
    }
    public string SignalTimeText
    {
        get => SelectedSignalKey is { } key ? Playback.FormatTime(key.TimeSeconds) : "";
        set
        {
            if (SelectedSignalKey is not { } key || SelectedTrack is not { } track) return;
            if (Playback.TryParseTime(value, out var time)) MoveSignalKey(track, key.Id, time);
            else RejectNumber();
        }
    }
    public double CanvasHeight => Math.Max(150, 28 + Tracks.Count * 32);
    public string Status => LocalizationService.Shared.Get(_statusKey ?? "Timeline.Hint");
    public Guid Revision => _graph.History.CurrentNodeId;

    public TimelineViewModel(ActiveGraphProvider active, EditorPlayback playback)
    {
        _active = active;
        _graph = active.Graph;
        Playback = playback;
        AddPreviewSignalKeyCommand = new RelayCommand(() => AddSignalKey(SignalTimelineMode.Preview),
            () => _graph.SelectedNode is { } node && SignalTimeline.Supports(node.BaseDefinition.Kind));
        AddRuntimeSignalKeyCommand = new RelayCommand(() => AddSignalKey(SignalTimelineMode.Runtime),
            () => _graph.SelectedNode is { } node && SignalTimeline.Supports(node.BaseDefinition.Kind));
        DeleteSignalKeyCommand = new RelayCommand(DeleteSignalKey, () => SelectedSignalKey is not null);
        Playback.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EditorPlayback.ShowSeconds) or nameof(EditorPlayback.FramesPerSecond))
                Refresh();
        };
        _active.PropertyChanged += OnActiveChanged;
        Attach();
        Refresh();
        MessageLocalization.Observe(this, static timeline => timeline.OnPropertyChanged(nameof(Status)));
    }

    private void Attach()
    {
        _graph.History.StateChanged += OnHistoryChanged;
        _graph.PropertyChanged += OnGraphChanged;
        _graph.Nodes.CollectionChanged += OnNodesChanged;
    }

    private void OnActiveChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActiveGraphProvider.Graph)) return;
        _graph.History.StateChanged -= OnHistoryChanged;
        _graph.PropertyChanged -= OnGraphChanged;
        _graph.Nodes.CollectionChanged -= OnNodesChanged;
        _graph = _active.Graph;
        Attach();
        Refresh();
        SetStatus(null);
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => Refresh();
    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { _refreshQueued = false; Refresh(); });
    }
    private void OnGraphChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphWorkspaceViewModel.SelectedNode))
        {
            SelectedSignalKey = null;
            OnPropertyChanged(nameof(SelectedTrack));
            AddPreviewSignalKeyCommand.NotifyCanExecuteChanged();
            AddRuntimeSignalKeyCommand.NotifyCanExecuteChanged();
        }
    }

    private void Refresh()
    {
        var selectedKeyId = SelectedSignalKey?.Id;
        Tracks.Clear();
        foreach (var node in _graph.Nodes)
        {
            if (node.Curve is not null) Tracks.Add(new TimelineTrackViewModel(this, node));
            if (SignalTimeline.Supports(node.BaseDefinition.Kind))
            {
                Tracks.Add(new TimelineTrackViewModel(this, node, SignalTimelineMode.Preview));
                if (node.SignalKeys?.Any(key => key.Mode == SignalTimelineMode.Runtime) == true)
                    Tracks.Add(new TimelineTrackViewModel(this, node, SignalTimelineMode.Runtime));
            }
        }
        SelectedSignalKey = SelectedTrack?.SignalKeys.FirstOrDefault(key => key.Id == selectedKeyId);
        OnPropertyChanged(nameof(SelectedTrack));
        OnPropertyChanged(nameof(CanvasHeight));
    }

    public void Select(TimelineTrackViewModel track)
    {
        if (track.SignalMode is { } mode) _signalMode = mode;
        if (_graph.Nodes.Contains(track.Node)) _graph.SelectNode(track.Node);
        OnPropertyChanged(nameof(SelectedTrack));
    }

    public void AddSignalKey(SignalTimelineMode mode, TimelineTrackViewModel? track = null, double? timeSeconds = null)
    {
        var node = track?.Node ?? _graph.SelectedNode;
        if (node is null || !SignalTimeline.Supports(node.BaseDefinition.Kind)) return;
        var time = timeSeconds ?? Playback.TimeSeconds;
        if (!ValidSignalTime(time)) return;
        _signalMode = mode;
        var key = new SignalTimelineKey(Guid.NewGuid(), time, mode);
        _graph.SetSignalKeys(node, [.. node.SignalKeys ?? [], key],
            LocalizationService.Shared.Get("Timeline.AddSignalHistory"));
        SelectedSignalKey = SelectedTrack?.SignalKeys.FirstOrDefault(item => item.Id == key.Id);
    }

    public void SelectSignalKey(TimelineTrackViewModel track, Guid id)
    {
        Select(track);
        SelectedSignalKey = track.SignalKeys.FirstOrDefault(key => key.Id == id);
    }

    public void MoveSignalKey(TimelineTrackViewModel track, Guid id, double time)
    {
        if (!_graph.Nodes.Contains(track.Node) || !ValidSignalTime(time)) return;
        var key = track.Node.SignalKeys?.FirstOrDefault(item => item.Id == id);
        if (key is null || key.Mode != track.SignalMode) return;
        _graph.SetSignalKeys(track.Node, track.Node.SignalKeys!.Select(item =>
            item.Id == id ? item with { TimeSeconds = time } : item).ToArray(),
            LocalizationService.Shared.Get("Timeline.MoveSignalHistory"));
    }

    private void DeleteSignalKey()
    {
        if (SelectedTrack is not { } track || SelectedSignalKey is not { } key) return;
        _graph.SetSignalKeys(track.Node, track.Node.SignalKeys!.Where(item => item.Id != key.Id).ToArray(),
            LocalizationService.Shared.Get("Timeline.DeleteSignalHistory"));
    }

    private bool ValidSignalTime(double time)
    {
        if (double.IsFinite(time) && time is >= 0 and <= 1_000_000) return true;
        SetStatus("Timeline.InvalidSignalTime");
        return false;
    }

    public void Retime(TimelineTrackViewModel track, double start, double duration)
    {
        if (!_graph.Nodes.Contains(track.Node) || track.Node.Curve is not { Keys.Count: > 0 } curve) return;
        if (!double.IsFinite(start) || !double.IsFinite(duration) || Math.Abs(start) > 1_000_000 ||
            duration < 0 || duration > 2_000_000 || (curve.Keys.Count > 1 && duration < 0.000001))
        {
            SetStatus("Timeline.InvalidClip");
            return;
        }
        var oldStart = curve.Keys[0].TimeSeconds;
        var oldDuration = curve.Keys[^1].TimeSeconds - oldStart;
        var scale = oldDuration == 0 ? 1 : duration / oldDuration;
        try
        {
            var updated = new FCurveSnapshot(curve.Keys.Select(key => key with
            {
                TimeSeconds = start + (key.TimeSeconds - oldStart) * scale,
                InSlope = key.InSlope / scale,
                OutSlope = key.OutSlope / scale
            }).ToArray());
            FCurve.Validate(updated);
            _graph.SetCurve(track.Node, updated, LocalizationService.Shared.Get("Timeline.RetimeHistory"));
            SetStatus(null);
        }
        catch (InvalidDataException) { SetStatus("Timeline.InvalidClip"); }
    }

    internal void RejectNumber() => SetStatus("Timeline.InvalidNumber");
    private void SetStatus(string? key) { _statusKey = key; OnPropertyChanged(nameof(Status)); }
}

public sealed class TimelineTrackViewModel(TimelineViewModel owner, GraphNodeViewModel node,
    SignalTimelineMode? signalMode = null)
{
    public GraphNodeViewModel Node { get; } = node;
    public SignalTimelineMode? SignalMode { get; } = signalMode;
    public bool IsSignal => SignalMode is not null;
    public bool IsCurve => !IsSignal;
    public string Name => IsSignal ? $"{Node.Name} [{LocalizationService.Shared.Get(
        SignalMode == SignalTimelineMode.Preview ? "Timeline.SignalPreview" : "Timeline.SignalRuntime")}]" : Node.Name;
    public IReadOnlyList<TimelineSignalKeyViewModel> SignalKeys { get; } = (node.SignalKeys ?? [])
        .Where(key => key.Mode == signalMode).OrderBy(key => key.TimeSeconds)
        .Select(key => new TimelineSignalKeyViewModel(owner.Playback, key)).ToArray();
    public double Start => IsSignal ? SignalKeys.FirstOrDefault()?.TimeSeconds ?? 0 :
        Node.Curve?.Keys.FirstOrDefault()?.TimeSeconds ?? 0;
    public double Duration => (IsSignal ? SignalKeys.LastOrDefault()?.TimeSeconds :
        Node.Curve?.Keys.LastOrDefault()?.TimeSeconds) is { } end ? end - Start : 0;
    public bool CanStretch => IsCurve && Node.Curve?.Keys.Count > 1;
    public string StartText { get => owner.Playback.FormatTime(Start); set => Parse(value, start => owner.Retime(this, start, Duration)); }
    public string DurationText { get => owner.Playback.FormatTime(Duration); set => Parse(value, duration => owner.Retime(this, Start, duration)); }
    private void Parse(string text, Action<double> apply)
    {
        if (owner.Playback.TryParseTime(text, out var value)) apply(value);
        else owner.RejectNumber();
    }

}

public sealed class TimelineSignalKeyViewModel(EditorPlayback playback, SignalTimelineKey key)
{
    public Guid Id => key.Id;
    public double TimeSeconds => key.TimeSeconds;
    public string TimeText => playback.FormatTime(TimeSeconds);
}
