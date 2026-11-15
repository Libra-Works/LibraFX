using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LiberaFx.Views;

public sealed class GpuConnectionCurve : Control
{
    private IBrush? _stroke;
    private double _thickness = 2;
    private IList<double>? _dashes;
    private Pen? _pen;
    public StreamGeometry? Data { get; private set; }
    public IBrush? Stroke
    {
        get => _stroke;
        set { if (ReferenceEquals(_stroke, value)) return; _stroke = value; _pen = null; InvalidateVisual(); }
    }
    public double StrokeThickness
    {
        get => _thickness;
        set { _thickness = value; _pen = null; InvalidateVisual(); }
    }
    public IList<double>? StrokeDashArray
    {
        get => _dashes;
        set { _dashes = value; _pen = null; InvalidateVisual(); }
    }
    public Point StartPoint { get; private set; }
    public Point EndPoint { get; private set; }
    public void SetEndpoints(Point start, Point end)
    {
        if (Data is not null && start == StartPoint && end == EndPoint) return;
        StartPoint = start;
        EndPoint = end;
        var tangent = Math.Max(48, Math.Abs(end.X - start.X) * 0.5);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, isFilled: false);
            context.CubicBezierTo(start + new Vector(tangent, 0), end - new Vector(tangent, 0), end);
            context.EndFigure(isClosed: false);
        }
        Data = geometry;
        InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        if (Data is null || Stroke is null) return;
        _pen ??= new Pen(Stroke, StrokeThickness, StrokeDashArray is { Count: > 0 } dashes ? new DashStyle(dashes, 0) : null);
        context.DrawGeometry(null, _pen, Data);
    }
}
