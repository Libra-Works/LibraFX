using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public sealed class TimelineCanvas : Control
{
    private const double Header = 28;
    private const double NameWidth = 160;
    public static readonly StyledProperty<IBrush?> HeaderBackgroundProperty =
        AvaloniaProperty.Register<TimelineCanvas, IBrush?>(nameof(HeaderBackground));
    private TimelineViewModel? _model;
    private double _start;
    private double _end = 120;
    private TimelineTrackViewModel? _drag;
    private bool _scrubbing;
    private bool _panning;
    private bool _stretching;
    private Point _pointerStart;
    private double _panStartValue;
    private double _panEndValue;
    private double _previewDelta;
    private Guid _revision;
    private Guid? _dragKey;

    static TimelineCanvas() => AffectsRender<TimelineCanvas>(HeaderBackgroundProperty);

    public IBrush? HeaderBackground
    {
        get => GetValue(HeaderBackgroundProperty);
        set => SetValue(HeaderBackgroundProperty, value);
    }

    public TimelineCanvas() { ClipToBounds = true; Focusable = true; }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeSettingsService.EditorColorsChanged += ColorsChanged;
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ThemeSettingsService.EditorColorsChanged -= ColorsChanged;
        base.OnDetachedFromVisualTree(e);
    }
    private void ColorsChanged(object? sender, EventArgs e) => InvalidateVisual();
    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_model is not null) { _model.PropertyChanged -= Changed; _model.Playback.PropertyChanged -= Changed; }
        _model = DataContext as TimelineViewModel;
        if (_model is not null) { _model.PropertyChanged += Changed; _model.Playback.PropertyChanged += Changed; }
        FrameAll();
        base.OnDataContextChanged(e);
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();
    private double Scale => Math.Max(1, Bounds.Width - NameWidth) / Math.Max(1, _end - _start);
    private double X(double displayTime) => NameWidth + (displayTime - _start) * Scale;
    private double DisplayTime(double timeSeconds) => _model?.Playback.DisplayUnits(timeSeconds) ?? timeSeconds;
    private double TimeSeconds(double displayTime) => _model?.Playback.TimeSecondsFromDisplay(displayTime) ?? displayTime;
    private double DisplayPosition(double x) => _start + (x - NameWidth) / Scale;
    private double SnapDisplayTime(double displayTime) =>
        _model?.Playback.ShowSeconds == true ? displayTime : Math.Round(displayTime);

    public void ZoomBy(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0) return;
        var center = (_start + _end) * 0.5;
        var duration = Math.Clamp((_end - _start) / factor, 1, 2_000_000);
        _start = Math.Clamp(center - duration * 0.5, -1_000_000, 1_000_000 - duration);
        _end = _start + duration;
        InvalidateVisual();
    }

    public void FrameAll()
    {
        _start = Math.Min(_model is null ? 0 : DisplayTime(_model.Playback.RangeStartSeconds),
            _model?.Tracks.Select(track => DisplayTime(track.Start)).DefaultIfEmpty(0).Min() ?? 0);
        _end = Math.Max(_model is null ? 2 : DisplayTime(_model.Playback.RangeEndSeconds),
            _model?.Tracks.Select(track => DisplayTime(track.Start + track.Duration)).DefaultIfEmpty(2).Max() ?? 2);
        if (_end <= _start) _end = _start + 1;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var background = ThemeSettingsService.EditorRenderBrush("CurveBackground");
        var surface = HeaderBackground is ISolidColorBrush solid
            ? new ImmutableSolidColorBrush(solid.Color, solid.Opacity) : HeaderBackground ?? background;
        context.DrawRectangle(background, null, new Rect(Bounds.Size));
        context.DrawRectangle(surface, null, new Rect(0, 0, Math.Min(NameWidth, Bounds.Width), Bounds.Height));
        context.DrawRectangle(surface, null, new Rect(0, 0, Bounds.Width, Math.Min(Header, Bounds.Height)));
        if (_model is null) return;
        var grid = new Pen(ThemeSettingsService.EditorRenderBrush("CurveGrid"));
        var label = ThemeSettingsService.EditorRenderBrush("CurveLabel");
        var line = ThemeSettingsService.EditorRenderBrush("CurveLine");
        var selected = ThemeSettingsService.EditorRenderBrush("CurveSelected");
        var handle = ThemeSettingsService.EditorRenderBrush("CurveHandle");
        context.DrawLine(grid, new(NameWidth, 0), new(NameWidth, Bounds.Height));
        context.DrawLine(grid, new(0, Header), new(Bounds.Width, Header));
        var rawStep = 75 / Scale;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
        var step = Math.Ceiling(rawStep / magnitude) * magnitude;
        for (var frame = Math.Ceiling(_start / step) * step; frame <= _end; frame += step)
        {
            context.DrawLine(grid, new(X(frame), 0), new(X(frame), Bounds.Height));
            DrawText(context, frame.ToString("0.##", CultureInfo.CurrentCulture), new(X(frame) + 3, 5), label);
        }
        for (var index = 0; index < _model.Tracks.Count; index++)
        {
            var track = _model.Tracks[index];
            var y = Header + index * 32;
            if (track == _model.SelectedTrack)
                context.DrawRectangle(new ImmutableSolidColorBrush(Colors.White, 0.04), null,
                    new Rect(0, y, Bounds.Width, 31));
            context.DrawLine(grid, new(0, y + 31), new(Bounds.Width, y + 31));
            using (context.PushClip(new Rect(4, y, NameWidth - 8, 31)))
                DrawText(context, track.Name, new(6, y + 8), label);
            if (track.IsSignal)
            {
                using var clip = context.PushClip(new Rect(NameWidth, Header,
                    Math.Max(0, Bounds.Width - NameWidth), Math.Max(0, Bounds.Height - Header)));
                foreach (var key in track.SignalKeys)
                {
                    var time = DisplayTime(key.TimeSeconds) +
                        (ReferenceEquals(track, _drag) && _dragKey == key.Id ? _previewDelta : 0);
                    var center = new Point(X(time), y + 16);
                    var shape = new StreamGeometry();
                    using (var geometry = shape.Open())
                    {
                        geometry.BeginFigure(center + new Vector(0, -6), true);
                        geometry.LineTo(center + new Vector(6, 0));
                        geometry.LineTo(center + new Vector(0, 6));
                        geometry.LineTo(center + new Vector(-6, 0));
                        geometry.EndFigure(true);
                    }
                    context.DrawGeometry(key.Id == _model.SelectedSignalKey?.Id && track == _model.SelectedTrack
                        ? selected : line, null, shape);
                }
                continue;
            }
            var start = DisplayTime(track.Start) +
                (ReferenceEquals(track, _drag) && !_stretching ? _previewDelta : 0);
            var end = DisplayTime(track.Start + track.Duration) +
                (ReferenceEquals(track, _drag) && _stretching ? _previewDelta : 0);
            using (context.PushClip(new Rect(NameWidth, Header, Math.Max(0, Bounds.Width - NameWidth), Math.Max(0, Bounds.Height - Header))))
            {
                context.DrawRectangle(line, new Pen(track == _model.SelectedTrack ? selected : handle),
                    new Rect(X(start), y + 5, Math.Max(6, (end - start) * Scale), 21), 3, 3);
                if (track.Node.Curve is { Keys.Count: > 0 } curve)
                {
                    var offset = ReferenceEquals(track, _drag) && !_stretching ? _previewDelta : 0;
                    var stretchScale = ReferenceEquals(track, _drag) && _stretching && track.Duration > 0
                        ? Math.Max(0.0001, (DisplayTime(track.Start + track.Duration) + _previewDelta - DisplayTime(track.Start)) / (DisplayTime(track.Start + track.Duration) - DisplayTime(track.Start)))
                        : 1;
                    var trackStartDisplay = DisplayTime(track.Start);
                    foreach (var key in curve.Keys)
                    {
                        var keyDisplay = trackStartDisplay + (DisplayTime(key.TimeSeconds) - trackStartDisplay) * stretchScale + offset;
                        var center = new Point(X(keyDisplay), y + 15.5);
                        var diamond = new StreamGeometry();
                        using (var g = diamond.Open())
                        {
                            g.BeginFigure(center + new Vector(0, -4), true);
                            g.LineTo(center + new Vector(4, 0));
                            g.LineTo(center + new Vector(0, 4));
                            g.LineTo(center + new Vector(-4, 0));
                            g.EndFigure(true);
                        }
                        context.DrawGeometry(handle, null, diamond);
                    }
                }
            }
        }
        var playhead = X(DisplayTime(_model.Playback.TimeSeconds));
        if (playhead >= NameWidth)
        {
            context.DrawLine(new Pen(selected, 2), new(playhead, 0), new(playhead, Bounds.Height));
            var marker = new StreamGeometry();
            using (var g = marker.Open())
            {
                g.BeginFigure(new Point(playhead - 6, 0), true);
                g.LineTo(new Point(playhead + 6, 0));
                g.LineTo(new Point(playhead, 9));
                g.EndFigure(true);
            }
            context.DrawGeometry(selected, null, marker);
        }
    }

    private static void DrawText(DrawingContext context, string value, Point point, IBrush brush) =>
        context.DrawText(new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Arial"), 11, brush), point);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_model is null) return;
        var point = e.GetCurrentPoint(this);
        var position = e.GetPosition(this);
        if (point.Properties.IsMiddleButtonPressed || point.Properties.IsRightButtonPressed)
        {
            if (position.X < NameWidth) return;
            _panning = true;
            _pointerStart = position;
            _panStartValue = _start;
            _panEndValue = _end;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;
        if (position.X >= NameWidth && (position.Y < Header || e.KeyModifiers.HasFlag(KeyModifiers.Alt)))
        {
            _scrubbing = true;
        }
        else
        {
            var index = (int)Math.Floor((position.Y - Header) / 32);
            if (index < 0 || index >= _model.Tracks.Count)
            {
                if (position.X < NameWidth) return;
                _scrubbing = true;
            }
            else
            {
                var track = _model.Tracks[index];
                _model.Select(track);
                Focus();
                if (track.IsSignal)
                {
                    var key = track.SignalKeys.OrderBy(key => Math.Abs(X(DisplayTime(key.TimeSeconds)) - position.X))
                        .FirstOrDefault();
                    if (position.X < NameWidth || key is null ||
                        Math.Abs(X(DisplayTime(key.TimeSeconds)) - position.X) > 8)
                    {
                        if (position.X >= NameWidth && e.ClickCount == 2)
                            _model.AddSignalKey(track.SignalMode!.Value, track,
                                TimeSeconds(SnapDisplayTime(DisplayPosition(position.X))));
                        return;
                    }
                    _model.SelectSignalKey(track, key.Id);
                    _drag = track;
                    _dragKey = key.Id;
                    _revision = _model.Revision;
                }
                else
                {
                    if (position.X < NameWidth || track.Node.Curve?.Keys.Count == 0) return;
                    if (position.X < X(DisplayTime(track.Start)) - 4 ||
                        position.X > X(DisplayTime(track.Start + track.Duration)) + 7)
                    {
                        _scrubbing = true;
                    }
                    else
                    {
                        _drag = track;
                        _stretching = track.CanStretch &&
                            Math.Abs(position.X - X(DisplayTime(track.Start + track.Duration))) <= 7;
                        _revision = _model.Revision;
                    }
                }
            }
        }
        _pointerStart = position;
        _previewDelta = 0;
        e.Pointer.Capture(this);
        if (_scrubbing) _model.Playback.TimeSeconds = TimeSeconds(SnapDisplayTime(DisplayPosition(position.X)));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_model is null) return;
        var position = e.GetPosition(this);
        if (_panning)
        {
            var delta = (position.X - _pointerStart.X) / Scale;
            var duration = _panEndValue - _panStartValue;
            _start = Math.Clamp(_panStartValue - delta, -1_000_000, 1_000_000 - duration);
            _end = _start + duration;
            InvalidateVisual();
            return;
        }
        if (_scrubbing)
            _model.Playback.TimeSeconds = TimeSeconds(SnapDisplayTime(DisplayPosition(position.X)));
        else if (_drag is not null)
        {
            _previewDelta = (position.X - _pointerStart.X) / Scale;
            if (_model.Playback.ShowSeconds == false) _previewDelta = Math.Round(_previewDelta);
        }
        else
        {
            var index = (int)Math.Floor((position.Y - Header) / 32);
            if (index >= 0 && index < _model.Tracks.Count && _model.Tracks[index] is { IsSignal: false, CanStretch: true } hoverTrack &&
                Math.Abs(position.X - X(DisplayTime(hoverTrack.Start + hoverTrack.Duration))) <= 7)
                Cursor = new Cursor(StandardCursorType.SizeWestEast);
            else
                Cursor = Cursor.Default;
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is { } track && _model?.Revision == _revision)
        {
            var deltaSeconds = _model.Playback.SecondsFromDisplayDelta(_previewDelta);
            if (_dragKey is { } keyId)
            {
                var key = track.SignalKeys.Single(item => item.Id == keyId);
                _model.MoveSignalKey(track, keyId, key.TimeSeconds + deltaSeconds);
            }
            else
                _model.Retime(track, track.Start + (_stretching ? 0 : deltaSeconds),
                    track.Duration + (_stretching ? deltaSeconds : 0));
        }
        _drag = null;
        _dragKey = null;
        _scrubbing = false;
        _panning = false;
        _previewDelta = 0;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _drag = null;
        _dragKey = null;
        _scrubbing = false;
        _panning = false;
        _previewDelta = 0;
        InvalidateVisual();
        base.OnPointerCaptureLost(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var point = e.GetPosition(this);
        if (point.X < NameWidth) return;
        var anchor = DisplayPosition(point.X);
        var ratio = (anchor - _start) / (_end - _start);
        var duration = Math.Clamp((_end - _start) / Math.Pow(1.15, e.Delta.Y), 1, 2_000_000);
        _start = Math.Clamp(anchor - duration * ratio, -1_000_000, 1_000_000 - duration);
        _end = _start + duration;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Delete || _model?.DeleteSignalKeyCommand.CanExecute(null) != true) return;
        _model.DeleteSignalKeyCommand.Execute(null);
        e.Handled = true;
    }
}
