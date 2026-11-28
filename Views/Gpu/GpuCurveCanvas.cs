using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LibraGpu.Core;

namespace LiberaFx.Views;

/// <summary>
/// Interactive inline curve editor control for <see cref="GpuCurveData"/> in the GPU node inspector.
/// </summary>
public sealed class GpuCurveCanvas : Control
{
    private static readonly IBrush BgBrush = new SolidColorBrush(Color.Parse("#141821"));
    private static readonly Pen BorderPen = new(new SolidColorBrush(Color.Parse("#2A3140")), 1);
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.Parse("#222836")), 1);
    private static readonly Pen AxisPen = new(new SolidColorBrush(Color.Parse("#364154")), 1);
    private static readonly Pen CurvePen = new(new SolidColorBrush(Color.Parse("#38BDF8")), 2);
    private static readonly IBrush KeyFill = new SolidColorBrush(Color.Parse("#60A5FA"));
    private static readonly IBrush SelectedKeyFill = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly Pen KeyStroke = new(new SolidColorBrush(Color.Parse("#0F172A")), 1.5);
    private static readonly Pen SelectedKeyStroke = new(Brushes.White, 1.5);

    private List<GpuCurveKey> _keys = [new(0, 0), new(1, 1)];
    private int _selectedIndex;
    private int _dragIndex = -1;

    public GpuCurveCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        Height = 160;
    }

    public event Action<GpuCurveData>? CurveCommitted;
    public event Action? SelectionChanged;

    public GpuCurveData Curve
    {
        get => new() { Keys = _keys.OrderBy(k => k.Time).ToList() };
        set
        {
            _keys = (value?.Keys is { Count: > 0 } k ? k : [new(0, 0), new(1, 1)])
                .OrderBy(item => item.Time)
                .ToList();
            _selectedIndex = Math.Clamp(_selectedIndex, 0, _keys.Count - 1);
            InvalidateVisual();
        }
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var clamped = _keys.Count == 0 ? 0 : Math.Clamp(value, 0, _keys.Count - 1);
            if (_selectedIndex == clamped) return;
            _selectedIndex = clamped;
            InvalidateVisual();
            SelectionChanged?.Invoke();
        }
    }

    public GpuCurveKey? SelectedKey => _selectedIndex >= 0 && _selectedIndex < _keys.Count ? _keys[_selectedIndex] : null;

    public void UpdateSelectedKey(GpuCurveKey updated, bool commit = true)
    {
        if (_selectedIndex < 0 || _selectedIndex >= _keys.Count) return;
        _keys[_selectedIndex] = updated;
        _keys = _keys.OrderBy(k => k.Time).ToList();
        _selectedIndex = Math.Clamp(_keys.IndexOf(updated), 0, _keys.Count - 1);
        InvalidateVisual();
        if (commit)
            CurveCommitted?.Invoke(Curve);
    }

    public void SetPreset(IEnumerable<GpuCurveKey> keys)
    {
        _keys = keys.OrderBy(k => k.Time).ToList();
        _selectedIndex = 0;
        InvalidateVisual();
        SelectionChanged?.Invoke();
        CurveCommitted?.Invoke(Curve);
    }

    private (double MinT, double MaxT, double MinV, double MaxV, Rect Plot) GetBounds()
    {
        var plot = new Rect(12, 10, Math.Max(10, Bounds.Width - 24), Math.Max(10, Bounds.Height - 20));
        var minT = Math.Min(0.0, _keys.Min(k => k.Time));
        var maxT = Math.Max(1.0, _keys.Max(k => k.Time));
        if (maxT - minT < 1e-4) maxT = minT + 1.0;

        var minV = Math.Min(0.0, _keys.Min(k => k.Value));
        var maxV = Math.Max(1.0, _keys.Max(k => k.Value));
        if (maxV - minV < 1e-4) maxV = minV + 1.0;
        var padV = (maxV - minV) * 0.08;
        return (minT, maxT, minV - padV, maxV + padV, plot);
    }

    private Point ToScreen(double t, double v, (double MinT, double MaxT, double MinV, double MaxV, Rect Plot) b)
    {
        var x = b.Plot.X + (t - b.MinT) / (b.MaxT - b.MinT) * b.Plot.Width;
        var y = b.Plot.Bottom - (v - b.MinV) / (b.MaxV - b.MinV) * b.Plot.Height;
        return new Point(x, y);
    }

    private (double Time, double Value) FromScreen(Point p, (double MinT, double MaxT, double MinV, double MaxV, Rect Plot) b)
    {
        var tx = Math.Clamp((p.X - b.Plot.X) / b.Plot.Width, 0, 1);
        var ty = (b.Plot.Bottom - p.Y) / b.Plot.Height;
        var t = b.MinT + tx * (b.MaxT - b.MinT);
        var v = b.MinV + ty * (b.MaxV - b.MinV);
        return (Math.Round(t, 4), Math.Round(v, 4));
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        context.DrawRectangle(BgBrush, BorderPen, rect, 4, 4);
        if (Bounds.Width < 24 || Bounds.Height < 24) return;

        var b = GetBounds();
        for (var i = 0; i <= 4; i++)
        {
            var frac = i * 0.25;
            var gx = b.Plot.X + frac * b.Plot.Width;
            context.DrawLine(i is 0 or 4 ? AxisPen : GridPen, new Point(gx, b.Plot.Top), new Point(gx, b.Plot.Bottom));
            var gy = ToScreen(0, frac, b).Y;
            if (gy >= b.Plot.Top && gy <= b.Plot.Bottom)
                context.DrawLine(i is 0 or 4 ? AxisPen : GridPen, new Point(b.Plot.Left, gy), new Point(b.Plot.Right, gy));
        }

        var curve = Curve;
        const int steps = 96;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (var i = 0; i <= steps; i++)
            {
                var t = b.MinT + (b.MaxT - b.MinT) * i / steps;
                var pt = ToScreen(t, curve.Evaluate(t), b);
                if (i == 0) ctx.BeginFigure(pt, false);
                else ctx.LineTo(pt);
            }
        }
        context.DrawGeometry(null, CurvePen, geo);

        for (var i = 0; i < _keys.Count; i++)
        {
            var pt = ToScreen(_keys[i].Time, _keys[i].Value, b);
            var selected = i == _selectedIndex;
            context.DrawEllipse(selected ? SelectedKeyFill : KeyFill, selected ? SelectedKeyStroke : KeyStroke, pt, selected ? 5.5 : 4.5, selected ? 5.5 : 4.5);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var props = e.GetCurrentPoint(this).Properties;
        var pos = e.GetPosition(this);
        var b = GetBounds();

        var hit = -1;
        var bestDist = 11.0;
        for (var i = 0; i < _keys.Count; i++)
        {
            var pt = ToScreen(_keys[i].Time, _keys[i].Value, b);
            var d = Math.Sqrt(Math.Pow(pt.X - pos.X, 2) + Math.Pow(pt.Y - pos.Y, 2));
            if (d < bestDist)
            {
                bestDist = d;
                hit = i;
            }
        }

        if (props.IsRightButtonPressed && hit >= 0 && _keys.Count > 1)
        {
            _keys.RemoveAt(hit);
            _selectedIndex = Math.Clamp(_selectedIndex, 0, _keys.Count - 1);
            InvalidateVisual();
            SelectionChanged?.Invoke();
            CurveCommitted?.Invoke(Curve);
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed && e.ClickCount == 2 && hit < 0)
        {
            var (t, v) = FromScreen(pos, b);
            var added = new GpuCurveKey(Math.Clamp(t, 0, 1), v);
            _keys.Add(added);
            _keys = _keys.OrderBy(k => k.Time).ToList();
            _selectedIndex = Math.Clamp(_keys.IndexOf(added), 0, _keys.Count - 1);
            _dragIndex = _selectedIndex;
            e.Pointer.Capture(this);
            InvalidateVisual();
            SelectionChanged?.Invoke();
            CurveCommitted?.Invoke(Curve);
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed && hit >= 0)
        {
            SelectedIndex = hit;
            _dragIndex = hit;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragIndex < 0 || _dragIndex >= _keys.Count) return;
        var b = GetBounds();
        var (t, v) = FromScreen(e.GetPosition(this), b);
        var cur = _keys[_dragIndex];
        var updated = cur with { Time = Math.Clamp(t, 0, 1), Value = v };
        _keys[_dragIndex] = updated;
        _keys = _keys.OrderBy(k => k.Time).ToList();
        _dragIndex = Math.Clamp(_keys.IndexOf(updated), 0, _keys.Count - 1);
        _selectedIndex = _dragIndex;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragIndex >= 0)
        {
            _dragIndex = -1;
            e.Pointer.Capture(null);
            CurveCommitted?.Invoke(Curve);
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if ((e.Key == Key.Delete || e.Key == Key.Back) && _keys.Count > 1 && _selectedIndex >= 0 && _selectedIndex < _keys.Count)
        {
            _keys.RemoveAt(_selectedIndex);
            _selectedIndex = Math.Clamp(_selectedIndex, 0, _keys.Count - 1);
            InvalidateVisual();
            SelectionChanged?.Invoke();
            CurveCommitted?.Invoke(Curve);
            e.Handled = true;
        }
    }
}
