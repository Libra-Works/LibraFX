using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiberaFx.Models.Appearance;

namespace LiberaFx.Services;

public sealed record PreviewPlaybackState(bool Playing, double Frame, double FramesPerSecond,
    double RangeStart, double RangeEnd, bool Loop);

public sealed class EditorPlayback : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private long _lastTick;
    private double _frame;
    private double _start;
    private double _end = 120;
    private double _fps = 60;
    private bool _loop = true;
    private bool _playing;
    private string? _statusKey;

    public event EventHandler? StateChanged;
    public IRelayCommand PlayPauseCommand { get; }
    public IRelayCommand StopCommand { get; }
    public IRelayCommand StepBackCommand { get; }
    public IRelayCommand StepForwardCommand { get; }
    public bool IsPlaying => _playing;
    public EditorIcon PlayIcon => _playing ? EditorIcon.Pause : EditorIcon.Play;
    public string Status => _statusKey is null ? "" : LocalizationService.Shared.Get(_statusKey);
    public double Frame
    {
        get => _frame;
        set
        {
            if (!double.IsFinite(value)) return;
            SetFrame(Math.Clamp(value, _start, _end));
            _lastTick = Stopwatch.GetTimestamp();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public double RangeStart => _start;
    public double RangeEnd => _end;
    public string FrameText { get => Number(Frame); set => Parse(value, frame => Frame = frame); }
    public string StartText { get => Number(_start); set => Parse(value, start => SetRange(start, _end)); }
    public string EndText { get => Number(_end); set => Parse(value, end => SetRange(_start, end)); }
    public string FpsText { get => Number(_fps); set => Parse(value, SetFps); }
    public bool Loop
    {
        get => _loop;
        set { if (SetProperty(ref _loop, value)) StateChanged?.Invoke(this, EventArgs.Empty); }
    }
    public PreviewPlaybackState Snapshot => new(IsPlaying, Frame, _fps, _start, _end, Loop);

    public EditorPlayback()
    {
        PlayPauseCommand = new RelayCommand(() => { if (_playing) Pause(); else Play(); });
        StopCommand = new RelayCommand(() => { Pause(); Frame = _start; });
        StepBackCommand = new RelayCommand(() => { Pause(); Frame = Math.Ceiling(Frame) - 1; });
        StepForwardCommand = new RelayCommand(() => { Pause(); Frame = Math.Floor(Frame) + 1; });
        _timer.Tick += OnTick;
        MessageLocalization.Observe(this, static playback => playback.OnPropertyChanged(nameof(Status)));
    }

    public void Play()
    {
        if (_playing) return;
        if (Frame >= _end) SetFrame(_start);
        _playing = true;
        _lastTick = Stopwatch.GetTimestamp();
        _timer.Start();
        NotifyPlaying();
    }

    public void Pause()
    {
        if (!_playing) return;
        _timer.Stop();
        _playing = false;
        NotifyPlaying();
    }

    public void Advance(double seconds)
    {
        if (!_playing || !double.IsFinite(seconds) || seconds < 0) return;
        var next = Frame + seconds * _fps;
        if (!double.IsFinite(next)) return;
        if (next >= _end)
        {
            if (Loop) next = _start + (next - _start) % (_end - _start);
            else { SetFrame(_end); Pause(); return; }
        }
        var previousFrame = Frame;
        SetFrame(next);
        if (Frame != previousFrame) StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - _lastTick) / (double)Stopwatch.Frequency;
        _lastTick = now;
        Advance(elapsed);
    }

    private void SetRange(double start, double end)
    {
        if (start < -1_000_000 || end > 1_000_000 || end - start < 1)
        {
            SetStatus("Timeline.InvalidRange");
            return;
        }
        _start = start;
        _end = end;
        SetFrame(Math.Clamp(Frame, start, end));
        OnPropertyChanged(nameof(RangeStart));
        OnPropertyChanged(nameof(RangeEnd));
        OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(EndText));
        _lastTick = Stopwatch.GetTimestamp();
        SetStatus(null);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetFps(double fps)
    {
        if (fps is < 1 or > 240) { SetStatus("Timeline.InvalidFps"); return; }
        _fps = fps;
        _lastTick = Stopwatch.GetTimestamp();
        OnPropertyChanged(nameof(FpsText));
        SetStatus(null);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetFrame(double frame)
    {
        if (SetProperty(ref _frame, frame, nameof(Frame))) OnPropertyChanged(nameof(FrameText));
    }

    private void NotifyPlaying()
    {
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(PlayIcon));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Parse(string text, Action<double> apply)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value))
        {
            SetStatus(null);
            apply(value);
        }
        else SetStatus("Timeline.InvalidNumber");
    }

    private void SetStatus(string? key) { _statusKey = key; OnPropertyChanged(nameof(Status)); }
    private static string Number(double number) => number.ToString("0.###", CultureInfo.CurrentCulture);
    public void Dispose() { _timer.Stop(); _timer.Tick -= OnTick; }
}
