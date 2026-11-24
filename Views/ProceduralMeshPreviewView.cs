using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LiberaFx.Services;

namespace LiberaFx.Views;

public sealed class ProceduralMeshPreviewView : Control
{
    private const int CheckerSize = 256;
    private static readonly uint[] CheckerTexture = BuildCheckerTexture();
    private ProceduralMeshPreviewFrame? _mesh;
    private double _yaw = .65, _pitch = .35, _zoom = 1;
    private Point? _drag;
    private WriteableBitmap? _bitmap;

    internal void Show(ProceduralMeshPreviewFrame? mesh) { _mesh = mesh; InvalidateVisual(); }
    public ProceduralMeshPreviewView() { ClipToBounds = true; Cursor = new Cursor(StandardCursorType.SizeAll); }

    private static uint[] BuildCheckerTexture()
    {
        var tex = new uint[CheckerSize * CheckerSize];
        const int cells = 8;
        const int cellPx = CheckerSize / cells;
        const uint light = 0xFFB8C4E0;
        const uint dark = 0xFF3D4B6B;
        const uint grid = 0xFF6B7BA0;
        const uint arrow = 0xFFF2F6FF;
        for (var y = 0; y < CheckerSize; y++)
        {
            var cy = y / cellPx;
            var ly = y % cellPx;
            for (var x = 0; x < CheckerSize; x++)
            {
                var cx = x / cellPx;
                var lx = x % cellPx;
                if (lx == 0 || ly == 0)
                {
                    tex[y * CheckerSize + x] = grid;
                    continue;
                }
                var c = ((cx + cy) & 1) == 0 ? light : dark;
                // Draw a crisp right-pointing UV arrow (→) in the center of each tile.
                var midY = cellPx / 2;
                var shaft = ly == midY && lx >= 6 && lx <= cellPx - 7;
                var head = lx >= cellPx - 11 && lx <= cellPx - 7 &&
                    Math.Abs(ly - midY) == (cellPx - 7 - lx);
                tex[y * CheckerSize + x] = (shaft || head) ? arrow : c;
            }
        }
        return tex;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_mesh is not { Positions.Length: > 0 } mesh) return;

        var width = Math.Max(1, (int)Math.Ceiling(Bounds.Width));
        var height = Math.Max(1, (int)Math.Ceiling(Bounds.Height));
        if (_bitmap is null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
        }

        var vertexCount = mesh.Positions.Length / 3;
        var min = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var max = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        for (var i = 0; i < mesh.Positions.Length; i++)
        {
            min[i % 3] = Math.Min(min[i % 3], mesh.Positions[i]);
            max[i % 3] = Math.Max(max[i % 3], mesh.Positions[i]);
        }
        var centerX = (min[0] + max[0]) / 2;
        var centerY = (min[1] + max[1]) / 2;
        var centerZ = (min[2] + max[2]) / 2;
        var radius = Math.Max(.001, Math.Sqrt(min.Zip(max, (a, b) => (b - a) * (b - a)).Sum()) / 2);
        var scale = Math.Min(width, height) * .42 / radius * _zoom;
        var cosY = Math.Cos(_yaw);
        var sinY = Math.Sin(_yaw);
        var cosP = Math.Cos(_pitch);
        var sinP = Math.Sin(_pitch);

        var sx = new float[vertexCount];
        var sy = new float[vertexCount];
        var sz = new float[vertexCount];
        for (var v = 0; v < vertexCount; v++)
        {
            var i = v * 3;
            var x = mesh.Positions[i] - centerX;
            var y = mesh.Positions[i + 1] - centerY;
            var z = mesh.Positions[i + 2] - centerZ;
            var side = x * cosY - z * sinY;
            var depth = x * sinY + z * cosY;
            var up = y * cosP - depth * sinP;
            var viewZ = y * sinP + depth * cosP;
            sx[v] = (float)(width * 0.5 + side * scale);
            sy[v] = (float)(height * 0.5 - up * scale);
            sz[v] = (float)viewZ;
        }

        var pixels = new uint[width * height];
        var zbuf = new float[width * height];
        Array.Fill(zbuf, float.NegativeInfinity);
        var hasUv = mesh.Uvs.Length >= vertexCount * 2;

        static float Edge(float ax, float ay, float bx, float by, float cx, float cy) =>
            (cx - ax) * (by - ay) - (cy - ay) * (bx - ax);

        for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            var a = mesh.Indices[i];
            var b = mesh.Indices[i + 1];
            var c = mesh.Indices[i + 2];
            if (a < 0 || b < 0 || c < 0 || Math.Max(a, Math.Max(b, c)) >= vertexCount) continue;

            var ax = sx[a]; var ay = sy[a]; var az = sz[a];
            var bx = sx[b]; var by = sy[b]; var bz = sz[b];
            var cx = sx[c]; var cy = sy[c]; var cz = sz[c];
            var area = Edge(ax, ay, bx, by, cx, cy);
            if (MathF.Abs(area) < 1e-4f) continue;

            var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))));
            var maxX = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
            var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))));
            var maxY = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));
            var invArea = 1f / area;

            var au = hasUv ? mesh.Uvs[a * 2] : 0f;
            var av = hasUv ? mesh.Uvs[a * 2 + 1] : 0f;
            var bu = hasUv ? mesh.Uvs[b * 2] : 0f;
            var bv = hasUv ? mesh.Uvs[b * 2 + 1] : 0f;
            var cu = hasUv ? mesh.Uvs[c * 2] : 0f;
            var cv = hasUv ? mesh.Uvs[c * 2 + 1] : 0f;

            for (var py = minY; py <= maxY; py++)
            {
                var ySample = py + 0.5f;
                for (var px = minX; px <= maxX; px++)
                {
                    var xSample = px + 0.5f;
                    var w0 = Edge(bx, by, cx, cy, xSample, ySample) * invArea;
                    var w1 = Edge(cx, cy, ax, ay, xSample, ySample) * invArea;
                    var w2 = 1f - w0 - w1;
                    if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f) continue;

                    var z = w0 * az + w1 * bz + w2 * cz;
                    var idx = py * width + px;
                    if (z <= zbuf[idx]) continue;
                    zbuf[idx] = z;

                    var u = w0 * au + w1 * bu + w2 * cu;
                    var v = w0 * av + w1 * bv + w2 * cv;
                    var tx = ((int)MathF.Floor((u - MathF.Floor(u)) * CheckerSize)) & (CheckerSize - 1);
                    var ty = ((int)MathF.Floor((v - MathF.Floor(v)) * CheckerSize)) & (CheckerSize - 1);
                    pixels[idx] = CheckerTexture[ty * CheckerSize + tx];
                }
            }
        }

        using (var locked = _bitmap.Lock())
        {
            System.Runtime.InteropServices.Marshal.Copy(
                Array.ConvertAll(pixels, u => unchecked((int)u)), 0, locked.Address, pixels.Length);
        }
        context.DrawImage(_bitmap, new Rect(0, 0, width, height));

        // Subtle wireframe overlay so mesh topology stays clear over the UV checkerboard.
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            var stride = Math.Max(1, (mesh.Indices.Length / 3 + 4095) / 4096) * 3;
            for (var i = 0; i + 2 < mesh.Indices.Length; i += stride)
            {
                var a = mesh.Indices[i]; var b = mesh.Indices[i + 1]; var c = mesh.Indices[i + 2];
                if (a < 0 || b < 0 || c < 0 || Math.Max(a, Math.Max(b, c)) >= vertexCount) continue;
                stream.BeginFigure(new Point(sx[a], sy[a]), false);
                stream.LineTo(new Point(sx[b], sy[b]));
                stream.LineTo(new Point(sx[c], sy[c]));
                stream.EndFigure(true);
            }
        }
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), .6), geometry);
        var font = new Typeface("Arial");
        context.DrawText(new FormattedText("Y ↑", System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, font, 11, Brushes.LightGreen), new Point(6, 6));
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
        if (_drag is not { } last) return;
        var current = e.GetPosition(this); var delta = current - last; _drag = current;
        _yaw += delta.X * .012; _pitch = Math.Clamp(_pitch + delta.Y * .012, -1.5, 1.5); InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is null) return;
        _drag = null; e.Pointer.Capture(null); e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _zoom = Math.Clamp(_zoom * Math.Pow(1.12, e.Delta.Y), .2, 8); InvalidateVisual(); e.Handled = true;
    }
}
