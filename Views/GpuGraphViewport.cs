using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using LiberaFx.Services;

namespace LiberaFx.Views;

// Uses the normal graph's coordinate system, renderer and configurable input mappings.
internal sealed class GpuGraphViewport
{
    private const double OriginStep = 2400;
    private readonly ScrollViewer _scroll;
    private readonly Grid _host;
    private readonly Canvas _canvas;
    private readonly ScaleTransform _scale = new(1, 1);
    private GraphSceneLayer? _grid;
    private bool _initialized;
    private bool _panning;
    private bool _panMoved;
    private MouseButton _panButton;
    private Point _panStart;
    private Vector _panOffset;
    public Vector Origin { get; private set; } = new(OriginStep, OriginStep);
    public double Zoom { get; private set; } = 1;
    public event Action? Changed;
    public event Action<Point>? ContextRequested;
    public Point? LastPointer { get; private set; }
    public GpuGraphViewport(ScrollViewer scroll, Grid host, Canvas canvas)
    {
        _scroll = scroll; _host = host; _canvas = canvas;
        canvas.RenderTransform = _scale;
        scroll.AddHandler(InputElement.PointerPressedEvent, Press, RoutingStrategies.Tunnel);
        scroll.AddHandler(InputElement.PointerMovedEvent, Move, RoutingStrategies.Tunnel);
        scroll.AddHandler(InputElement.PointerReleasedEvent, Release, RoutingStrategies.Tunnel);
        scroll.AddHandler(InputElement.PointerWheelChangedEvent, Wheel, RoutingStrategies.Tunnel);
        scroll.PointerCaptureLost += (_, _) => _panning = false;
        scroll.ScrollChanged += (_, _) => { Initialize(); UpdateGrid(); };
        scroll.SizeChanged += (_, _) => { Initialize(); UpdateGrid(); };
    }
    public Point ToGraphPoint(Point canvasPoint) => canvasPoint - Origin;
    public void SetGrid(GraphSceneLayer grid) { _grid = grid; UpdateGrid(); }
    public void Initialize()
    {
        if (_initialized || _grid is null || _scroll.Viewport.Width <= 0 || _scroll.Viewport.Height <= 0) return;
        _initialized = true;
        EnsureWorldBounds(new Rect(0, 0, 1600, 1000));
        _scroll.UpdateLayout();
        _scroll.Offset = Origin * Zoom;
        UpdateGrid();
    }
    private void UpdateExtent()
    {
        _host.Width = _canvas.Width * Zoom;
        _host.Height = _canvas.Height * Zoom;
    }
    private void ShiftOrigin(double x, double y)
    {
        if (x == 0 && y == 0) return;
        Origin += new Vector(x, y);
        _canvas.Width += x; _canvas.Height += y;
        UpdateExtent();
        if (_initialized)
        {
            var delta = new Vector(x * Zoom, y * Zoom);
            _panOffset += delta;
            _scroll.UpdateLayout();
            _scroll.Offset += delta;
        }
        Changed?.Invoke();
        UpdateGrid();
    }
    public void EnsureWorldBounds(Rect bounds)
    {
        ShiftOrigin(Math.Max(0, Math.Ceiling((-bounds.Left - Origin.X + 200) / OriginStep) * OriginStep),
            Math.Max(0, Math.Ceiling((-bounds.Top - Origin.Y + 200) / OriginStep) * OriginStep));
        var width = Math.Ceiling((Origin.X + Math.Max(1600, bounds.Right + 200)) / OriginStep) * OriginStep;
        var height = Math.Ceiling((Origin.Y + Math.Max(1000, bounds.Bottom + 200)) / OriginStep) * OriginStep;
        if (width <= _canvas.Width && height <= _canvas.Height) return;
        _canvas.Width = Math.Max(_canvas.Width, width);
        _canvas.Height = Math.Max(_canvas.Height, height);
        UpdateExtent();
        Changed?.Invoke();
        UpdateGrid();
    }
    private void EnsureViewportExtent(Vector offset)
    {
        var width = Math.Ceiling(((offset.X + _scroll.Viewport.Width) / Zoom + OriginStep) / OriginStep) * OriginStep;
        var height = Math.Ceiling(((offset.Y + _scroll.Viewport.Height) / Zoom + OriginStep) / OriginStep) * OriginStep;
        if (width <= _canvas.Width && height <= _canvas.Height) return;
        _canvas.Width = Math.Max(_canvas.Width, width); _canvas.Height = Math.Max(_canvas.Height, height);
        UpdateExtent();
        _scroll.UpdateLayout();
        Changed?.Invoke();
    }
    public void SetZoom(double zoom, Point? anchor = null)
    {
        var point = anchor ?? new Point(_scroll.Viewport.Width / 2, _scroll.Viewport.Height / 2);
        var world = (_scroll.Offset + (Vector)point) / Zoom - Origin;
        Zoom = Math.Clamp(zoom, 0.05, 4);
        _scale.ScaleX = _scale.ScaleY = Zoom;
        UpdateExtent();
        _host.UpdateLayout(); _scroll.UpdateLayout();
        var offset = (world + Origin) * Zoom - (Vector)point;
        var x = offset.X < 0 ? Math.Ceiling(-offset.X / (OriginStep * Zoom)) * OriginStep : 0;
        var y = offset.Y < 0 ? Math.Ceiling(-offset.Y / (OriginStep * Zoom)) * OriginStep : 0;
        ShiftOrigin(x, y);
        offset += new Vector(x * Zoom, y * Zoom);
        EnsureViewportExtent(offset);
        _scroll.Offset = offset;
        UpdateGrid();
        Changed?.Invoke();
    }
    public void Frame(Rect bounds)
    {
        var size = _scroll.Viewport;
        if (size.Width <= 0 || size.Height <= 0) return;
        EnsureWorldBounds(bounds);
        SetZoom(Math.Min(Math.Max(1, size.Width - 80) / Math.Max(1, bounds.Width),
            Math.Max(1, size.Height - 80) / Math.Max(1, bounds.Height)));
        var offset = ((Vector)bounds.Center + Origin) * Zoom - new Vector(size.Width / 2, size.Height / 2);
        EnsureViewportExtent(offset);
        _scroll.Offset = offset;
        UpdateGrid();
    }
    private void UpdateGrid()
    {
        if (_grid is null) return;
        _grid.GraphOrigin = Origin;
        _grid.Width = _canvas.Width; _grid.Height = _canvas.Height;
        _grid.VisibleArea = new Rect(_scroll.Offset.X / Zoom, _scroll.Offset.Y / Zoom,
            _scroll.Viewport.Width / Zoom, _scroll.Viewport.Height / Zoom);
    }
    private void Press(object? sender, PointerPressedEventArgs e)
    {
        LastPointer = ToGraphPoint(e.GetPosition(_canvas));
        var p = e.GetCurrentPoint(_scroll).Properties;
        var setting = ShortcutService.Shared.GraphPanButton;
        var right = p.IsRightButtonPressed && setting is "Right" or "Right + Middle";
        var middle = p.IsMiddleButtonPressed && setting is "Middle" or "Right + Middle";
        if (!right && !middle) return;
        _panning = true; _panMoved = false;
        _panButton = right ? MouseButton.Right : MouseButton.Middle;
        _panStart = e.GetPosition(_scroll); _panOffset = _scroll.Offset;
        _scroll.Focus();
        e.Pointer.Capture(_scroll); e.Handled = true;
    }
    private void Move(object? sender, PointerEventArgs e)
    {
        LastPointer = ToGraphPoint(e.GetPosition(_canvas));
        if (!_panning) return;
        var point = e.GetPosition(_scroll);
        _panMoved |= Math.Abs(point.X - _panStart.X) >= 4 || Math.Abs(point.Y - _panStart.Y) >= 4;
        if (!_panMoved) return;
        var offset = _panOffset + (Vector)(_panStart - point);
        var x = offset.X < 200 ? OriginStep : 0;
        var y = offset.Y < 200 ? OriginStep : 0;
        ShiftOrigin(x, y);
        offset += new Vector(x * Zoom, y * Zoom);
        EnsureViewportExtent(offset);
        _scroll.Offset = offset; e.Handled = true;
    }
    private void Release(object? sender, PointerReleasedEventArgs e)
    {
        if (_panning)
        {
            var menu = _panButton == MouseButton.Right && !_panMoved;
            _panning = false; e.Pointer.Capture(null); e.Handled = true;
            if (menu) ContextRequested?.Invoke(ToGraphPoint(e.GetPosition(_canvas)));
        }
        else if (e.InitialPressMouseButton == MouseButton.Right)
        {
            ContextRequested?.Invoke(ToGraphPoint(e.GetPosition(_canvas))); e.Handled = true;
        }
    }
    private void Wheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0 || ShortcutService.Shared.GraphZoomModifier == "Control" &&
            !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        SetZoom(Zoom * Math.Pow(1.15, e.Delta.Y), e.GetPosition(_scroll));
        e.Handled = true;
    }
}
