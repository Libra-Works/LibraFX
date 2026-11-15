using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public sealed class TimelineCanvas : Control
{
    private const double Header = 28;
    private const double NameWidth = 160;
    private TimelineViewModel? _model;
    private double _start;
    private double _end = 120;
    private TimelineTrackViewModel? _drag;
    private bool _scrubbing;
    private bool _stretching;
    private Point _pointerStart;
    private double _previewDelta;
    private Guid _revision;

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
    private double X(double frame) => NameWidth + (frame - _start) * Scale;
    private double Frame(double x) => _start + (x - NameWidth) / Scale;

    public void FrameAll()
    {
        _start = Math.Min(_model?.Playback.RangeStart ?? 0, _model?.Tracks.Select(track => track.Start).DefaultIfEmpty(0).Min() ?? 0);
        _end = Math.Max(_model?.Playback.RangeEnd ?? 120, _model?.Tracks.Select(track => track.Start + track.Duration).DefaultIfEmpty(120).Max() ?? 120);
        if (_end <= _start) _end = _start + 1;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(ThemeSettingsService.EditorBrush("CurveBackground"), null, new Rect(Bounds.Size));
        if (_model is null) return;
        var grid = new Pen(ThemeSettingsService.EditorBrush("CurveGrid"));
        var label = ThemeSettingsService.EditorBrush("CurveLabel");
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
            context.DrawLine(grid, new(0, y + 31), new(Bounds.Width, y + 31));
            using (context.PushClip(new Rect(4, y, NameWidth - 8, 31)))
                DrawText(context, track.Name, new(6, y + 8), label);
            var start = track.Start + (ReferenceEquals(track, _drag) && !_stretching ? _previewDelta : 0);
            var duration = track.Duration + (ReferenceEquals(track, _drag) && _stretching ? _previewDelta : 0);
            using (context.PushClip(new Rect(NameWidth, Header, Math.Max(0, Bounds.Width - NameWidth), Math.Max(0, Bounds.Height - Header))))
                context.DrawRectangle(ThemeSettingsService.EditorBrush("CurveLine"),
                    new Pen(ThemeSettingsService.EditorBrush(track == _model.SelectedTrack ? "CurveSelected" : "CurveHandle")),
                    new Rect(X(start), y + 5, Math.Max(6, duration * Scale), 21), 3, 3);
        }
        var playhead = X(_model.Playback.Frame);
        if (playhead >= NameWidth)
            context.DrawLine(new Pen(ThemeSettingsService.EditorBrush("CurveSelected"), 2), new(playhead, 0), new(playhead, Bounds.Height));
    }

    private static void DrawText(DrawingContext context, string value, Point point, IBrush brush) =>
        context.DrawText(new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Arial"), 11, brush), point);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_model is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var position = e.GetPosition(this);
        if (position.X >= NameWidth && position.Y < Header) _scrubbing = true;
        else
        {
            var index = (int)Math.Floor((position.Y - Header) / 32);
            if (index < 0 || index >= _model.Tracks.Count) return;
            var track = _model.Tracks[index];
            _model.Select(track);
            if (position.X < NameWidth || track.Node.Curve?.Keys.Count == 0) return;
            if (position.X < X(track.Start) - 4 || position.X > X(track.Start + track.Duration) + 7) return;
            _drag = track;
            _stretching = track.CanStretch && Math.Abs(position.X - X(track.Start + track.Duration)) <= 7;
            _revision = _model.Revision;
        }
        _pointerStart = position;
        _previewDelta = 0;
        e.Pointer.Capture(this);
        if (_scrubbing) _model.Playback.Frame = Math.Round(Frame(position.X));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_model is null) return;
        if (_scrubbing) _model.Playback.Frame = Math.Round(Frame(e.GetPosition(this).X));
        else if (_drag is not null) _previewDelta = Math.Round((e.GetPosition(this).X - _pointerStart.X) / Scale);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is { } track && _model?.Revision == _revision)
            _model.Retime(track, track.Start + (_stretching ? 0 : _previewDelta),
                track.Duration + (_stretching ? _previewDelta : 0));
        _drag = null;
        _scrubbing = false;
        _previewDelta = 0;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _drag = null;
        _scrubbing = false;
        _previewDelta = 0;
        InvalidateVisual();
        base.OnPointerCaptureLost(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var point = e.GetPosition(this);
        if (point.X < NameWidth) return;
        var anchor = Frame(point.X);
        var ratio = (anchor - _start) / (_end - _start);
        var duration = Math.Clamp((_end - _start) / Math.Pow(1.15, e.Delta.Y), 1, 2_000_000);
        _start = Math.Clamp(anchor - duration * ratio, -1_000_000, 1_000_000 - duration);
        _end = _start + duration;
        InvalidateVisual();
        e.Handled = true;
    }
}
