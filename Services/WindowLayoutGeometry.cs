using Avalonia;
using LiberaFx.Models;

namespace LiberaFx.Services;

public static class WindowLayoutGeometry
{
    public static LayoutWindowBounds Clamp(LayoutWindowBounds bounds, IEnumerable<PixelRect> workingAreas, double scaling = 1)
    {
        var areas = workingAreas.Where(a => a.Width > 0 && a.Height > 0).ToArray();
        if (areas.Length == 0) return bounds;
        if (!double.IsFinite(scaling) || scaling <= 0) scaling = 1;
        var centerX = bounds.X + bounds.Width * scaling / 2;
        var centerY = bounds.Y + bounds.Height * scaling / 2;
        var area = areas.MinBy(a => Math.Pow(Math.Max(a.X - centerX, Math.Max(0, centerX - a.Right)), 2) +
                                   Math.Pow(Math.Max(a.Y - centerY, Math.Max(0, centerY - a.Bottom)), 2));
        var width = Math.Min(bounds.Width, area.Width / scaling);
        var height = Math.Min(bounds.Height, area.Height / scaling);
        return new()
        {
            X = Math.Clamp(bounds.X, area.X, area.Right - width * scaling),
            Y = Math.Clamp(bounds.Y, area.Y, area.Bottom - height * scaling),
            Width = width,
            Height = height,
            Maximized = bounds.Maximized
        };
    }
}
