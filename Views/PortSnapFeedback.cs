using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LiberaFx.Views;

// Distances are measured in the window, so nested editors and zoom share the same target size.
internal sealed class PortSnapFeedback : IDisposable
{
    public const double AcquireRadius = 24;
    public const double ReleaseRadius = 30;
    private Button? _pin;
    private IBrush? _foreground, _background, _border;
    private Thickness _thickness;
    private CornerRadius _corner;
    private ITransform? _transform;
    private RelativePoint _origin;

    public Button? Pin => _pin;
    public void Show(Button? pin)
    {
        if (ReferenceEquals(pin, _pin)) return;
        Dispose();
        if (pin is null) return;
        _pin = pin;
        _foreground = pin.Foreground; _background = pin.Background; _border = pin.BorderBrush;
        _thickness = pin.BorderThickness; _corner = pin.CornerRadius;
        _transform = pin.RenderTransform; _origin = pin.RenderTransformOrigin;
        var root = TopLevel.GetTopLevel(pin);
        var left = pin.TranslatePoint(default, root!);
        var right = pin.TranslatePoint(new Point(pin.Bounds.Width, 0), root!);
        var screenWidth = left is { } a && right is { } b ? ((Vector)(b - a)).Length : pin.Bounds.Width;
        var scale = Math.Max(1.35, 22 / Math.Max(1, screenWidth));
        pin.SetCurrentValue(Button.ForegroundProperty, Brushes.White);
        pin.SetCurrentValue(Button.BackgroundProperty, pin.FindResource("EffectSelectionBorderBrush") as IBrush ?? Brushes.DodgerBlue);
        pin.SetCurrentValue(Button.BorderBrushProperty, Brushes.White);
        pin.SetCurrentValue(Button.BorderThicknessProperty, new Thickness(1.5 / scale));
        pin.SetCurrentValue(Button.CornerRadiusProperty, new CornerRadius(100));
        pin.SetCurrentValue(Visual.RenderTransformOriginProperty, RelativePoint.Center);
        pin.SetCurrentValue(Visual.RenderTransformProperty, new ScaleTransform(scale, scale));
        pin.Classes.Add("connection-target");
    }
    public void Dispose()
    {
        if (_pin is not { } pin) return;
        _pin = null;
        pin.Classes.Remove("connection-target");
        pin.SetCurrentValue(Button.ForegroundProperty, _foreground);
        pin.SetCurrentValue(Button.BackgroundProperty, _background);
        pin.SetCurrentValue(Button.BorderBrushProperty, _border);
        pin.SetCurrentValue(Button.BorderThicknessProperty, _thickness);
        pin.SetCurrentValue(Button.CornerRadiusProperty, _corner);
        pin.SetCurrentValue(Visual.RenderTransformProperty, _transform);
        pin.SetCurrentValue(Visual.RenderTransformOriginProperty, _origin);
    }
}
