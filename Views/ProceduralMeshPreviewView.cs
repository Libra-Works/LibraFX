using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LiberaFx.Services;

namespace LiberaFx.Views;

public sealed class ProceduralMeshPreviewView : Control
{
    private ProceduralMeshPreviewFrame? _mesh;
    private double _yaw = .65, _pitch = .35, _zoom = 1;
    private Point? _drag;
    internal void Show(ProceduralMeshPreviewFrame? mesh) { _mesh = mesh; InvalidateVisual(); }
    public ProceduralMeshPreviewView() { ClipToBounds = true; Cursor = new Cursor(StandardCursorType.SizeAll); }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_mesh is not { Positions.Length: > 0 } mesh) return;
        var min = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var max = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        for (var i = 0; i < mesh.Positions.Length; i++) { min[i % 3] = Math.Min(min[i % 3], mesh.Positions[i]); max[i % 3] = Math.Max(max[i % 3], mesh.Positions[i]); }
        var center = min.Zip(max, (a, b) => (a + b) / 2).ToArray();
        var radius = Math.Max(.001, Math.Sqrt(min.Zip(max, (a, b) => (b - a) * (b - a)).Sum()) / 2);
        var scale = Math.Min(Bounds.Width, Bounds.Height) * .42 / radius * _zoom;
        Point Project(int vertex)
        {
            var i = vertex * 3;
            var x = mesh.Positions[i] - center[0]; var y = mesh.Positions[i + 1] - center[1]; var z = mesh.Positions[i + 2] - center[2];
            var side = x * Math.Cos(_yaw) - z * Math.Sin(_yaw);
            var depth = x * Math.Sin(_yaw) + z * Math.Cos(_yaw);
            var up = y * Math.Cos(_pitch) - depth * Math.Sin(_pitch);
            return new(Bounds.Width / 2 + side * scale, Bounds.Height / 2 - up * scale);
        }
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            var stride = Math.Max(1, (mesh.Indices.Length / 3 + 4095) / 4096) * 3;
            for (var i = 0; i + 2 < mesh.Indices.Length; i += stride)
            {
                var a = mesh.Indices[i]; var b = mesh.Indices[i + 1]; var c = mesh.Indices[i + 2];
                if (a < 0 || b < 0 || c < 0 || Math.Max(a, Math.Max(b, c)) >= mesh.Positions.Length / 3) continue;
                stream.BeginFigure(Project(a), false); stream.LineTo(Project(b)); stream.LineTo(Project(c)); stream.EndFigure(true);
            }
        }
        var brush = this.FindResource("EffectForegroundBrush") as IBrush ?? Brushes.LightGray;
        context.DrawGeometry(null, new Pen(brush, .8), geometry);
        var font = new Typeface("Arial");
        context.DrawText(new FormattedText("Y ↑", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, font, 11, Brushes.LightGreen), new Point(6, 6));
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _drag = e.GetPosition(this); e.Pointer.Capture(this); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is not { } start) return;
        var point = e.GetPosition(this); _yaw += (point.X - start.X) * .012;
        _pitch = Math.Clamp(_pitch + (point.Y - start.Y) * .012, -1.5, 1.5);
        _drag = point; InvalidateVisual(); e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    { base.OnPointerReleased(e); if (_drag is null) return; _drag = null; e.Pointer.Capture(null); e.Handled = true; }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); _drag = null; }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    { base.OnPointerWheelChanged(e); _zoom = Math.Clamp(_zoom * Math.Exp(e.Delta.Y * .15), .1, 10); InvalidateVisual(); e.Handled = true; }
}
