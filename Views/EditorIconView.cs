using Avalonia;
using Avalonia.Controls.Shapes;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using LiberaFx.Models.Appearance;
using Path = Avalonia.Controls.Shapes.Path;

namespace LiberaFx.Views;

public sealed class EditorIconView : Path
{
    public static readonly StyledProperty<EditorIcon> IconProperty =
        AvaloniaProperty.Register<EditorIconView, EditorIcon>(nameof(Icon));

    public EditorIcon Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }

    public EditorIconView()
    {
        Width = Height = 18;
        Stretch = Stretch.None;
        StrokeThickness = 1.65;
        StrokeLineCap = PenLineCap.Round;
        StrokeJoin = PenLineJoin.Round;
        IsHitTestVisible = false;
        this.Bind(StrokeProperty, new DynamicResourceExtension("EffectForegroundBrush"));
        Data = IconButton.CreateGeometry(Icon);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty) Data = IconButton.CreateGeometry(Icon);
    }
}
