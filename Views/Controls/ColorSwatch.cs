using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LiberaFx.Views.Controls;

public sealed class ColorSwatch : Control
{
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<ColorSwatch, Color>(nameof(Color));

    static ColorSwatch() => AffectsRender<ColorSwatch>(ColorProperty);

    public Color Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        const double cell = 6;
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.LightGray, bounds);
        for (var y = 0d; y < bounds.Height; y += cell)
            for (var x = 0d; x < bounds.Width; x += cell)
                if (((int)(x / cell) + (int)(y / cell)) % 2 == 0)
                    context.FillRectangle(Brushes.Gray,
                        new Rect(x, y, Math.Min(cell, bounds.Width - x), Math.Min(cell, bounds.Height - y)));
        context.FillRectangle(new SolidColorBrush(Color), bounds);
    }
}
