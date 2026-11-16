using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Globalization;
using Dock.Model.Mvvm.Controls;
using LiberaFx.Models.Graph;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed class TimelineViewModel : Tool
{
    private readonly ActiveGraphProvider _active;
    private GraphWorkspaceViewModel _graph;
    private string? _statusKey;
    private bool _refreshQueued;
    public EditorPlayback Playback { get; }
    public ObservableCollection<TimelineTrackViewModel> Tracks { get; } = [];
    public TimelineTrackViewModel? SelectedTrack => Tracks.FirstOrDefault(track => track.Node == _graph.SelectedNode);
    public double CanvasHeight => Math.Max(150, 28 + Tracks.Count * 32);
    public string Status => LocalizationService.Shared.Get(_statusKey ?? "Timeline.Hint");
    public Guid Revision => _graph.History.CurrentNodeId;

    public TimelineViewModel(ActiveGraphProvider active, EditorPlayback playback)
    {
        _active = active;
        _graph = active.Graph;
        Playback = playback;
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
        if (e.PropertyName == nameof(GraphWorkspaceViewModel.SelectedNode)) OnPropertyChanged(nameof(SelectedTrack));
    }

    private void Refresh()
    {
        Tracks.Clear();
        foreach (var node in _graph.Nodes.Where(node => node.Curve is not null))
            Tracks.Add(new TimelineTrackViewModel(this, node));
        OnPropertyChanged(nameof(SelectedTrack));
        OnPropertyChanged(nameof(CanvasHeight));
    }

    public void Select(TimelineTrackViewModel track)
    {
        if (_graph.Nodes.Contains(track.Node)) _graph.SelectNode(track.Node);
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

public sealed class TimelineTrackViewModel(TimelineViewModel owner, GraphNodeViewModel node)
{
    public GraphNodeViewModel Node { get; } = node;
    public string Name => Node.Name;
    public double Start => Node.Curve?.Keys.FirstOrDefault()?.TimeSeconds ?? 0;
    public double Duration => (Node.Curve?.Keys.LastOrDefault()?.TimeSeconds ?? Start) - Start;
    public bool CanStretch => Node.Curve?.Keys.Count > 1;
    public string StartText { get => owner.Playback.FormatTime(Start); set => Parse(value, start => owner.Retime(this, start, Duration)); }
    public string DurationText { get => owner.Playback.FormatTime(Duration); set => Parse(value, duration => owner.Retime(this, Start, duration)); }
    private void Parse(string text, Action<double> apply)
    {
        if (owner.Playback.TryParseTime(text, out var value)) apply(value);
        else owner.RejectNumber();
    }
}
