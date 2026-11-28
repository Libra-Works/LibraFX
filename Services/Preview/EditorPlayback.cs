using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiberaFx.Models.Appearance;
using LiberaFx.Services.Settings;

namespace LiberaFx.Services.Preview;

public sealed record PreviewPlaybackState(bool Playing, double Frame, double FramesPerSecond,
    double RangeStart, double RangeEnd, bool Loop)
{
    public long SignalEpoch { get; init; }
    public double ForwardSeconds { get; init; }
    public double SeekTimeSeconds { get; init; }
    public double? SignalStartSeconds { get; init; }
    public long LoopCount { get; init; }
    public bool IncludeSignalStart { get; init; }
    public bool DebugDrawStopped { get; init; }
    public double TimeScale { get; init; } = 1;
    public int Motion { get; init; }
    public double MotionRadius { get; init; } = 2;
    public double MotionSpeed { get; init; } = 1;
}

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
    private bool _showSeconds;
    private string? _statusKey;
    private long _signalEpoch;
    private double _forwardSeconds;
    private double _seekTime;
    private double? _signalStart;
    private long _loopCount;
    private bool _includeSignalStart = true;
    private bool _hasStarted;
    private bool _debugDrawStopped;
    private double _timeScale = 1;
    private int _motion;
    private double _motionRadius = 2, _motionSpeed = 1;
    public IReadOnlyList<string> MotionOptions => new[] { "Static", "Circle", "Sphere" }
        .Select(name => LocalizationService.Shared.Get("Timeline.Motion" + name)).ToArray();
    public int Motion
    {
        get => _motion;
        set { if (value is >= 0 and <= 2 && SetProperty(ref _motion, value)) StateChanged?.Invoke(this, EventArgs.Empty); }
    }
    public string MotionRadiusText
    {
        get => _motionRadius.ToString("G", CultureInfo.CurrentCulture);
        set
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var radius) &&
                double.IsFinite(radius) && radius is >= 0 and <= 1000 && SetProperty(ref _motionRadius, radius))
                StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public string MotionSpeedText
    {
        get => _motionSpeed.ToString("G", CultureInfo.CurrentCulture);
        set
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var speed) &&
                double.IsFinite(speed) && speed is >= 0 and <= 100 && SetProperty(ref _motionSpeed, speed))
                StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public static double[] PreviewOffset(PreviewPlaybackState state)
    {
        if (state.Motion == 0) return [0, 0, 0];
        var angle = state.Frame / state.FramesPerSecond * state.MotionSpeed;
        var elevation = state.Motion == 2 ? Math.Sin(angle * .7) * Math.PI * .5 : 0;
        var horizontal = Math.Cos(elevation) * state.MotionRadius;
        return [Math.Cos(angle) * horizontal, Math.Sin(elevation) * state.MotionRadius, Math.Sin(angle) * horizontal];
    }
    public double TimeScale
    {
        get => _timeScale;
        set
        {
            if (!double.IsFinite(value) || value < 0 || value > 100 || !SetProperty(ref _timeScale, value)) return;
            OnPropertyChanged(nameof(TimeScaleText));
            _lastTick = Stopwatch.GetTimestamp();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public string TimeScaleText
    {
        get => TimeScale.ToString("G", CultureInfo.CurrentCulture);
        set { if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var ratio)) TimeScale = ratio; }
    }

    public event EventHandler? StateChanged;
    public IRelayCommand PlayPauseCommand { get; }
    public IRelayCommand StopCommand { get; }
    public IRelayCommand StepBackCommand { get; }
    public IRelayCommand StepForwardCommand { get; }
    public bool IsPlaying => _playing;
    public EditorIcon PlayIcon => _playing ? EditorIcon.Pause : EditorIcon.Play;
    public string Status => _statusKey is null ? "" : LocalizationService.Shared.Get(_statusKey);
    public double FramesPerSecond => _fps;
    public double TimeSeconds
    {
        get => _frame / _fps;
        set
        {
            if (!double.IsFinite(value)) return;
            Frame = value * _fps;
        }
    }
    public double Frame
    {
        get => _frame;
        set
        {
            if (!double.IsFinite(value)) return;
            SetFrame(Math.Clamp(value, _start, _end));
            MarkSeek();
            _lastTick = Stopwatch.GetTimestamp();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public bool ShowSeconds
    {
        get => _showSeconds;
        set
        {
            if (!SetProperty(ref _showSeconds, value)) return;
            OnPropertyChanged(nameof(TimeUnitLabel));
            OnPropertyChanged(nameof(FrameText));
            OnPropertyChanged(nameof(StartText));
            OnPropertyChanged(nameof(EndText));
        }
    }
    public string TimeUnitLabel => LocalizationService.Shared.Get(_showSeconds ? "Timeline.Seconds" : "Timeline.Frames");
    public double RangeStart => _start;
    public double RangeEnd => _end;
    public double RangeStartSeconds => _start / _fps;
    public double RangeEndSeconds => _end / _fps;
    public string FrameText { get => FormatTime(TimeSeconds); set => ParseTime(value, time => TimeSeconds = time); }
    public string StartText { get => FormatTime(_start / _fps); set => ParseTime(value, start => SetRange(start * _fps, _end)); }
    public string EndText { get => FormatTime(_end / _fps); set => ParseTime(value, end => SetRange(_start, end * _fps)); }
    public string FpsText { get => Number(_fps); set => Parse(value, SetFps); }
    public bool Loop
    {
        get => _loop;
        set { if (SetProperty(ref _loop, value)) StateChanged?.Invoke(this, EventArgs.Empty); }
    }
    public PreviewPlaybackState Snapshot => new(IsPlaying, Frame, _fps, _start, _end, Loop)
    {
        SignalEpoch = _signalEpoch, ForwardSeconds = _forwardSeconds,
        SeekTimeSeconds = _seekTime, SignalStartSeconds = _signalStart, LoopCount = _loopCount,
        IncludeSignalStart = _includeSignalStart, DebugDrawStopped = _debugDrawStopped, TimeScale = _timeScale,
        Motion = _motion, MotionRadius = _motionRadius, MotionSpeed = _motionSpeed
    };

    public double DisplayUnits(double timeSeconds) => _showSeconds ? timeSeconds : timeSeconds * _fps;
    public double TimeSecondsFromDisplay(double value) => _showSeconds ? value : value / _fps;
    public double SecondsFromDisplayDelta(double delta) => _showSeconds ? delta : delta / _fps;
    public double DisplaySlope(double slopePerSecond) => _showSeconds ? slopePerSecond : slopePerSecond / _fps;
    public double SlopePerSecond(double displayedSlope) => _showSeconds ? displayedSlope : displayedSlope * _fps;

    public bool TryParseTime(string text, out double timeSeconds)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value))
        {
            timeSeconds = TimeSecondsFromDisplay(value);
            return double.IsFinite(timeSeconds);
        }
        timeSeconds = 0;
        return false;
    }

    public string FormatTime(double timeSeconds) => Number(DisplayUnits(timeSeconds));

    public EditorPlayback()
    {
        PlayPauseCommand = new RelayCommand(() => { if (_playing) Pause(); else Play(); });
        StopCommand = new RelayCommand(() =>
        {
            _debugDrawStopped = true;
            Pause();
            SetFrame(_start);
            MarkSeek(restart: true);
            StateChanged?.Invoke(this, EventArgs.Empty);
        });
        StepBackCommand = new RelayCommand(() => { Pause(); Frame = Math.Ceiling(Frame) - 1; });
        StepForwardCommand = new RelayCommand(StepForward);
        _timer.Tick += OnTick;
        MessageLocalization.Observe(this, static playback =>
        {
            playback.OnPropertyChanged(nameof(Status));
            playback.OnPropertyChanged(nameof(TimeUnitLabel));
            playback.OnPropertyChanged(nameof(MotionOptions));
        });
    }

    public void Play()
    {
        if (_playing) return;
        _debugDrawStopped = false;
        if (Frame >= _end) { SetFrame(_start); MarkSeek(restart: true); }
        _signalStart ??= TimeSeconds;
        _hasStarted = true;
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
        seconds *= _timeScale;
        var next = Frame + seconds * _fps;
        if (!double.IsFinite(next)) return;
        var progress = Loop ? seconds : Math.Min(seconds, (_end - Frame) / _fps);
        if (!double.IsFinite(_forwardSeconds + progress)) return;
        var wraps = Loop && next >= _end ? Math.Floor((next - _start) / (_end - _start)) : 0;
        if (wraps >= long.MaxValue - _loopCount) return;
        _forwardSeconds += progress;
        if (next >= _end)
        {
            if (Loop)
            {
                _loopCount += (long)wraps;
                next = _start + (next - _start) % (_end - _start);
            }
            else { SetFrame(_end); Pause(); return; }
        }
        var previousFrame = Frame;
        SetFrame(next);
        if (Frame != previousFrame || progress > 0) StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StepForward()
    {
        _debugDrawStopped = false;
        Pause();
        var next = Math.Min(_end, Math.Floor(Frame) + 1);
        if (next <= Frame) return;
        _signalStart ??= TimeSeconds;
        _hasStarted = true;
        _forwardSeconds += (next - Frame) / _fps;
        SetFrame(next);
        _lastTick = Stopwatch.GetTimestamp();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MarkSeek(bool restart = false)
    {
        if (!restart) _debugDrawStopped = false;
        ++_signalEpoch;
        _forwardSeconds = 0;
        _seekTime = TimeSeconds;
        _signalStart = null;
        _loopCount = 0;
        _includeSignalStart = restart || !_hasStarted;
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
        MarkSeek();
        OnPropertyChanged(nameof(RangeStart));
        OnPropertyChanged(nameof(RangeEnd));
        OnPropertyChanged(nameof(RangeStartSeconds));
        OnPropertyChanged(nameof(RangeEndSeconds));
        OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(EndText));
        _lastTick = Stopwatch.GetTimestamp();
        SetStatus(null);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetFps(double fps)
    {
        if (fps is < 1 or > 240) { SetStatus("Timeline.InvalidFps"); return; }
        if (_fps == fps) return;
        _fps = fps;
        MarkSeek();
        _lastTick = Stopwatch.GetTimestamp();
        OnPropertyChanged(nameof(FramesPerSecond));
        OnPropertyChanged(nameof(TimeSeconds));
        OnPropertyChanged(nameof(Frame));
        OnPropertyChanged(nameof(FrameText));
        OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(EndText));
        OnPropertyChanged(nameof(RangeStartSeconds));
        OnPropertyChanged(nameof(RangeEndSeconds));
        OnPropertyChanged(nameof(FpsText));
        SetStatus(null);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetFrame(double frame)
    {
        if (SetProperty(ref _frame, frame, nameof(Frame)))
        {
            OnPropertyChanged(nameof(TimeSeconds));
            OnPropertyChanged(nameof(FrameText));
        }
    }

    private void NotifyPlaying()
    {
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(PlayIcon));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ParseTime(string text, Action<double> apply)
    {
        if (TryParseTime(text, out var timeSeconds))
        {
            SetStatus(null);
            apply(timeSeconds);
        }
        else SetStatus("Timeline.InvalidNumber");
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
