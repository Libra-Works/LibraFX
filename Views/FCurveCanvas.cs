using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using LiberaFx.Models.Graph;
using LiberaFx.ViewModels;
using LiberaFx.Services;

namespace LiberaFx.Views;

public sealed class FCurveCanvas : Control
{
    private CurveEditorViewModel? _model;
    private GraphNodeViewModel? _framedNode;
    private double _scaleX = 5;
    private double _scaleY = 80;
    private Point _origin = new(50, 150);
    private Point _pointerStart;
    private Point _originStart;
    private FCurveKey? _dragKey;
    private FCurveKey[] _dragKeys = [];
    private Point _selectionEnd;
    private bool _selectionAdditive;
    private DragMode _dragMode;

    public FCurveCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeSettingsService.EditorColorsChanged += OnEditorColorsChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ThemeSettingsService.EditorColorsChanged -= OnEditorColorsChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnEditorColorsChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        _model = DataContext as CurveEditorViewModel;
        if (_model is not null) _model.PropertyChanged += OnModelChanged;
        _framedNode = null;
        InvalidateVisual();
        base.OnDataContextChanged(e);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(ThemeSettingsService.EditorRenderBrush("CurveBackground"), null, new Rect(Bounds.Size));
        if (_model?.Node != _framedNode && Bounds.Width > 0 && Bounds.Height > 0)
        {
            _framedNode = _model?.Node;
            UpdateFrame();
        }

        using var clip = context.PushClip(new Rect(Bounds.Size));
        DrawGrid(context);
        if (_model is null) return;
        foreach (var display in _model.VisibleCurves.OrderBy(item => ReferenceEquals(item.Node, _model.Node)))
        {
            var brush = display.Brush is ISolidColorBrush solid
                ? new ImmutableSolidColorBrush(solid.Color, solid.Opacity) : display.Brush;
            DrawCurve(context, display.Curve, new Pen(brush, ReferenceEquals(display.Node, _model.Node) ? 2 : 1.5));
        }
        var selected = ThemeSettingsService.EditorRenderBrush("CurveSelected");
        if (_model.Curve is { } curve && _model.VisibleCurves.Any(item => ReferenceEquals(item.Node, _model.Node)))
            DrawKeys(context, curve, selected);
        if (_model.PlayheadFrame is { } frame)
        {
            var displayTime = _model.Playback?.DisplayUnits(_model.Playback.TimeSeconds) ?? frame;
            var x = _origin.X + displayTime * _scaleX;
            context.DrawLine(new Pen(selected, 1.5), new Point(x, 0), new Point(x, Bounds.Height));
        }
        if (_dragMode == DragMode.Select)
            context.DrawRectangle(ThemeSettingsService.EditorRenderBrush("SelectionFill"),
                new Pen(ThemeSettingsService.EditorRenderBrush("SelectionBorder")), SelectionRect(_pointerStart, _selectionEnd));
    }

    public void FrameAll()
    {
        UpdateFrame();
        InvalidateVisual();
    }

    public void FrameSelected()
    {
        if (_model?.SelectedKeys is { Count: > 0 } keys)
        {
            UpdateFrame(keys);
            InvalidateVisual();
        }
    }

    private void UpdateFrame(IReadOnlyList<FCurveKey>? selected = null)
    {
        var keys = selected ?? _model?.VisibleCurves.SelectMany(item => item.Curve.Keys).ToArray() ?? [];
        if (keys.Count == 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            _scaleX = 5;
            _scaleY = 80;
            _origin = new Point(50, Math.Max(50, Bounds.Height / 2));
            return;
        }

        var minX = keys.Min(key => DisplayTime(key.TimeSeconds));
        var maxX = keys.Max(key => DisplayTime(key.TimeSeconds));
        var minY = keys.Min(key => key.Value);
        var maxY = keys.Max(key => key.Value);
        var width = Math.Max(10, maxX - minX);
        var height = Math.Max(1, maxY - minY);
        _scaleX = Math.Clamp(Math.Max(1, Bounds.Width - 100) / width, 0.001, 2000);
        _scaleY = Math.Clamp(Math.Max(1, Bounds.Height - 80) / height, 0.001, 10000);
        _origin = new Point(Bounds.Width / 2 - (minX + maxX) / 2 * _scaleX,
                            Bounds.Height / 2 + (minY + maxY) / 2 * _scaleY);
    }

    private void DrawGrid(DrawingContext context)
    {
        var grid = new Pen(ThemeSettingsService.EditorRenderBrush("CurveGrid"));
        var axis = new Pen(ThemeSettingsService.EditorRenderBrush("CurveAxis"));
        var label = ThemeSettingsService.EditorRenderBrush("CurveLabel");
        var stepX = GridStep(_scaleX);
        var stepY = GridStep(_scaleY);
        var left = (0 - _origin.X) / _scaleX;
        var right = (Bounds.Width - _origin.X) / _scaleX;
        var bottom = (_origin.Y - Bounds.Height) / _scaleY;
        var top = _origin.Y / _scaleY;
        for (var value = Math.Ceiling(left / stepX) * stepX; value <= right; value += stepX)
        {
            var x = _origin.X + value * _scaleX;
            context.DrawLine(Math.Abs(value) < stepX * 0.001 ? axis : grid, new Point(x, 0), new Point(x, Bounds.Height));
            DrawLabel(context, value, new Point(x + 3, 2), label);
        }
        for (var value = Math.Ceiling(bottom / stepY) * stepY; value <= top; value += stepY)
        {
            var y = _origin.Y - value * _scaleY;
            context.DrawLine(Math.Abs(value) < stepY * 0.001 ? axis : grid, new Point(0, y), new Point(Bounds.Width, y));
            DrawLabel(context, value, new Point(3, y + 2), label);
        }
    }

    private static double GridStep(double scale)
    {
        var raw = 75 / scale;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var unit = raw / magnitude;
        return (unit <= 1 ? 1 : unit <= 2 ? 2 : unit <= 5 ? 5 : 10) * magnitude;
    }

    private static void DrawLabel(DrawingContext context, double value, Point point, IBrush label)
    {
        var text = new FormattedText(value.ToString("0.###", CultureInfo.CurrentCulture),
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Arial"), 10, label);
        context.DrawText(text, point);
    }

    private void DrawCurve(DrawingContext context, FCurveSnapshot curve, Pen curvePen)
    {
        var keys = curve.Keys;
        if (keys.Count == 0) return;
        var leftTime = -_origin.X / _scaleX;
        var rightTime = (Bounds.Width - _origin.X) / _scaleX;
        var firstKeyTime = DisplayTime(keys[0].TimeSeconds);
        if (leftTime < firstKeyTime)
            context.DrawLine(curvePen, ToPoint(leftTime, keys[0].Value), ToPoint(Math.Min(rightTime, firstKeyTime), keys[0].Value));
        for (var i = 0; i < keys.Count - 1; i++)
        {
            var left = keys[i];
            var right = keys[i + 1];
            var leftDisplayTime = DisplayTime(left.TimeSeconds);
            var rightDisplayTime = DisplayTime(right.TimeSeconds);
            if (rightDisplayTime < leftTime || leftDisplayTime > rightTime) continue;
            if (left.Interpolation == CurveInterpolation.Constant)
            {
                context.DrawLine(curvePen, ToPoint(leftDisplayTime, left.Value), ToPoint(rightDisplayTime, left.Value));
                context.DrawLine(curvePen, ToPoint(rightDisplayTime, left.Value), ToPoint(rightDisplayTime, right.Value));
                continue;
            }

            var start = Math.Max(leftDisplayTime, leftTime);
            var end = Math.Min(rightDisplayTime, rightTime);
            var count = left.Interpolation == CurveInterpolation.Linear ? 1 :
                Math.Clamp((int)Math.Ceiling((end - start) * _scaleX / 4), 1, 2000);
            var last = ToPoint(start, FCurve.Evaluate(curve, TimeSeconds(start)));
            for (var sample = 1; sample <= count; sample++)
            {
                var displayTime = start + (end - start) * sample / count;
                var next = ToPoint(displayTime, FCurve.Evaluate(curve, TimeSeconds(displayTime)));
                context.DrawLine(curvePen, last, next);
                last = next;
            }
        }
        var lastKeyTime = DisplayTime(keys[^1].TimeSeconds);
        if (rightTime > lastKeyTime)
            context.DrawLine(curvePen, ToPoint(Math.Max(leftTime, lastKeyTime), keys[^1].Value), ToPoint(rightTime, keys[^1].Value));
    }

    private void DrawKeys(DrawingContext context, FCurveSnapshot curve, IBrush selected)
    {
        var keyBrush = ThemeSettingsService.EditorRenderBrush("CurveKey");
        var handlePen = new Pen(ThemeSettingsService.EditorRenderBrush("CurveHandle"));
        foreach (var key in curve.Keys)
        {
            var point = ToPoint(DisplayTime(key.TimeSeconds), key.Value);
            if (point.X < -150 || point.X > Bounds.Width + 150) continue;
            if (_model?.SelectedKey?.Id == key.Id)
            {
                var index = curve.Keys.ToList().FindIndex(item => item.Id == key.Id);
                if (index > 0 && curve.Keys[index - 1].Interpolation == CurveInterpolation.Cubic)
                    DrawHandle(context, point, HandlePoint(key, incoming: true), handlePen, selected);
                if (index < curve.Keys.Count - 1 && key.Interpolation == CurveInterpolation.Cubic)
                    DrawHandle(context, point, HandlePoint(key, incoming: false), handlePen, selected);
            }
            context.DrawEllipse(_model?.IsKeySelected(key.Id) == true ? selected : keyBrush, null, point, 5, 5);
        }
    }

    private static void DrawHandle(DrawingContext context, Point key, Point handle, Pen handlePen, IBrush selected)
    {
        context.DrawLine(handlePen, key, handle);
        context.DrawEllipse(selected, null, handle, 4, 4);
    }

    private Point ToPoint(double displayTime, double value) =>
        new(Math.Clamp(_origin.X + displayTime * _scaleX, -1_000_000, 1_000_000),
            Math.Clamp(_origin.Y - value * _scaleY, -1_000_000, 1_000_000));

    private Point HandlePoint(FCurveKey key, bool incoming)
    {
        var delta = (incoming ? -40 : 40) / _scaleX;
        var slope = incoming ? key.InSlope : key.OutSlope;
        return ToPoint(DisplayTime(key.TimeSeconds) + delta,
            key.Value + delta * (_model?.Playback?.DisplaySlope(slope) ?? slope));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        var pointer = e.GetCurrentPoint(this).Properties;
        if (ShortcutService.Shared.CurvePanButton == "Middle"
            ? pointer.IsMiddleButtonPressed
            : pointer.IsRightButtonPressed)
        {
            _dragMode = DragMode.Pan;
            _pointerStart = point;
            _originStart = _origin;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _model?.Curve is not { } curve ||
            !_model.VisibleCurves.Any(item => ReferenceEquals(item.Node, _model.Node))) return;
        Focus();
        if (e.ClickCount >= 2)
        {
            var displayTime = (point.X - _origin.X) / _scaleX;
            displayTime = _model.SnapDisplayTime(displayTime);
            _model.AddKey(TimeSeconds(displayTime), (_origin.Y - point.Y) / _scaleY);
            e.Handled = true;
            return;
        }

        var selected = _model.SelectedKey;
        if (selected is not null)
        {
            var index = curve.Keys.ToList().FindIndex(key => key.Id == selected.Id);
            if (index > 0 && curve.Keys[index - 1].Interpolation == CurveInterpolation.Cubic &&
                Near(point, HandlePoint(selected, true)))
                _dragMode = DragMode.InSlope;
            else if (index >= 0 && index < curve.Keys.Count - 1 &&
                     selected.Interpolation == CurveInterpolation.Cubic && Near(point, HandlePoint(selected, false)))
                _dragMode = DragMode.OutSlope;
            if (_dragMode != DragMode.None) _dragKey = selected;
        }
        if (_dragMode == DragMode.None)
        {
            _dragKey = curve.Keys.Reverse().FirstOrDefault(key => Near(point, ToPoint(DisplayTime(key.TimeSeconds), key.Value)));
            var additive = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (_dragKey is not null)
            {
                _model.SelectKey(_dragKey.Id, toggle: additive, preserveSelection: !additive);
                if (_model.IsKeySelected(_dragKey.Id))
                {
                    _dragMode = DragMode.Key;
                    _dragKeys = _model.SelectedKeys.ToArray();
                }
            }
            else
            {
                _dragMode = DragMode.Select;
                _selectionAdditive = additive;
                _selectionEnd = point;
            }
        }
        if (_dragMode != DragMode.None)
        {
            _pointerStart = point;
            e.Pointer.Capture(this);
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_dragMode == DragMode.Pan)
        {
            _origin = new Point(_originStart.X + point.X - _pointerStart.X, _originStart.Y + point.Y - _pointerStart.Y);
            InvalidateVisual();
        }
        else if (_dragMode == DragMode.Select)
        {
            _selectionEnd = point;
            InvalidateVisual();
        }
        else if (_dragKey is { } key && _model is not null)
        {
            if (_dragMode == DragMode.Key)
            {
                var displayTime = DisplayTime(key.TimeSeconds) + (point.X - _pointerStart.X) / _scaleX;
                displayTime = _model.SnapDisplayTime(displayTime);
                _model.PreviewKeys(_dragKeys, TimeSeconds(displayTime) - key.TimeSeconds,
                    -(point.Y - _pointerStart.Y) / _scaleY);
            }
            else
            {
                var origin = ToPoint(DisplayTime(key.TimeSeconds), key.Value);
                var delta = (_dragMode == DragMode.InSlope ? -40 : 40) / _scaleX;
                var slope = Math.Clamp((origin.Y - point.Y) / _scaleY / delta, -1_000_000, 1_000_000);
                slope = _model.Playback?.SlopePerSecond(slope) ?? slope;
                _model.PreviewKey(_dragMode == DragMode.InSlope ? key with { InSlope = slope } : key with { OutSlope = slope });
            }
        }
        if (_dragMode != DragMode.None) e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragMode == DragMode.None) return;
        var mode = _dragMode;
        if (mode == DragMode.Select && _model?.Curve is { } curve)
        {
            var rect = SelectionRect(_pointerStart, e.GetPosition(this)).Inflate(2);
            _model.SelectKeys(curve.Keys.Where(key => rect.Contains(ToPoint(DisplayTime(key.TimeSeconds), key.Value)))
                .Select(key => key.Id), _selectionAdditive);
        }
        _dragMode = DragMode.None;
        _dragKey = null;
        _dragKeys = [];
        e.Pointer.Capture(null);
        if (mode is DragMode.Key or DragMode.InSlope or DragMode.OutSlope) _model?.CommitPreview();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_dragMode is DragMode.Key or DragMode.InSlope or DragMode.OutSlope) _model?.CancelPreview();
        _dragMode = DragMode.None;
        _dragKey = null;
        _dragKeys = [];
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (ShortcutService.Shared.CurveZoomModifier == "Control" &&
            !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        ZoomAt(Math.Pow(1.15, e.Delta.Y), e.GetPosition(this));
        e.Handled = true;
    }

    public void ZoomBy(double factor) => ZoomAt(factor, new Point(Bounds.Width / 2, Bounds.Height / 2));

    private void ZoomAt(double factor, Point point)
    {
        var frame = (point.X - _origin.X) / _scaleX;
        var value = (_origin.Y - point.Y) / _scaleY;
        _scaleX = Math.Clamp(_scaleX * factor, 0.001, 2000);
        _scaleY = Math.Clamp(_scaleY * factor, 0.001, 10000);
        _origin = new Point(point.X - frame * _scaleX, point.Y + value * _scaleY);
        InvalidateVisual();
    }

    private double DisplayTime(double timeSeconds) => _model?.Playback?.DisplayUnits(timeSeconds) ?? timeSeconds;
    private double TimeSeconds(double displayTime) => _model?.Playback?.TimeSecondsFromDisplay(displayTime) ?? displayTime;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_model is null) return;
        var shortcuts = ShortcutService.Shared;
        if (shortcuts.Matches("curve.delete", e) && _model.DeleteKeyCommand.CanExecute(null))
            _model.DeleteKeyCommand.Execute(null);
        else if (shortcuts.Matches("curve.add", e) && _model.AddKeyCommand.CanExecute(null))
            _model.AddKeyCommand.Execute(null);
        else if (shortcuts.Matches("curve.frame", e))
        {
            if (_model.HasSelectedKeys) FrameSelected();
            else FrameAll();
        }
        else return;
        e.Handled = true;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();
    private static bool Near(Point a, Point b) => Math.Abs(a.X - b.X) <= 8 && Math.Abs(a.Y - b.Y) <= 8;
    private static Rect SelectionRect(Point start, Point end) => new(
        new Point(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y)),
        new Point(Math.Max(start.X, end.X), Math.Max(start.Y, end.Y)));
    private enum DragMode { None, Pan, Select, Key, InSlope, OutSlope }
}
