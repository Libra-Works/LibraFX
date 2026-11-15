using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using LiberaFx.Models.Appearance;
using Path = Avalonia.Controls.Shapes.Path;

namespace LiberaFx.Views;

public sealed class IconButton : Button
{
    public static readonly StyledProperty<EditorIcon> IconProperty =
        AvaloniaProperty.Register<IconButton, EditorIcon>(nameof(Icon));

    private readonly Path _path;

    public EditorIcon Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }

    public IconButton()
    {
        _path = new Path
        {
            Width = 18, Height = 18, Stretch = Stretch.Uniform,
            StrokeThickness = 1.7, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
            IsHitTestVisible = false
        };
        _path.Bind(Shape.StrokeProperty, this.GetObservable(ForegroundProperty));
        Content = _path;
        Padding = new Thickness(7);
        MinWidth = 32;
        MinHeight = 32;
        UpdateIcon();
    }

    protected override Type StyleKeyOverride => typeof(Button);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty) UpdateIcon();
    }

    private void UpdateIcon()
    {
        if (_path is null) return;
        _path.Data = CreateGeometry(Icon);
    }

    internal static Geometry CreateGeometry(EditorIcon icon) =>
        Geometry.Parse(icon switch
        {
            EditorIcon.Save => "M3 2H15L18 5V18H2V2Z M6 2V8H14V2 M6 18V12H14V18",
            EditorIcon.SaveAs => "M3 2H14L17 5V9 M3 2V18H8 M6 2V8H13V2 M10 17L16 11L19 14L13 20H10Z",
            EditorIcon.Lock => "M5 9V6A5 5 0 0 1 15 6V9 M3 9H17V19H3Z M10 13V15",
            EditorIcon.Unlock => "M6 9V6A5 5 0 0 1 16 6 M3 9H17V19H3Z M10 13V15",
            EditorIcon.Delete => "M3 5H17 M7 5V2H13V5 M5 5L6 18H14L15 5 M8 8V15 M12 8V15",
            EditorIcon.Frame => "M7 2H2V7 M13 2H18V7 M2 13V18H7 M13 18H18V13 M7 7H13V13H7Z",
            EditorIcon.Group => "M2 7V2H7 M13 2H18V7 M2 13V18H7 M13 18H18V13 M5 5H10V10H5Z M10 10H15V15H10Z",
            EditorIcon.ZoomIn => "M13 8A5 5 0 1 1 3 8A5 5 0 1 1 13 8 M12 12L18 18 M5.5 8H10.5 M8 5.5V10.5",
            EditorIcon.ZoomOut => "M13 8A5 5 0 1 1 3 8A5 5 0 1 1 13 8 M12 12L18 18 M5.5 8H10.5",
            EditorIcon.Refresh or EditorIcon.Reset => "M17 7A7 7 0 0 0 4 5L2 8 M2 3V8H7 M3 13A7 7 0 0 0 16 15L18 12 M18 17V12H13",
            EditorIcon.Close or EditorIcon.Disconnect => "M4 4L16 16 M4 16L16 4",
            EditorIcon.Add => "M10 3V17 M3 10H17",
            EditorIcon.Remove => "M3 10H17",
            EditorIcon.Open => "M2 16V4H8L10 6H18V8 M2 17L5 9H19L16 17Z",
            EditorIcon.Copy => "M6 6H18V18H6Z M3 14H2V2H14V3",
            EditorIcon.Cut => "M8 13L17 3 M8 7L17 17 M8 6A3 3 0 1 1 2 6A3 3 0 1 1 8 6 M8 14A3 3 0 1 1 2 14A3 3 0 1 1 8 14",
            EditorIcon.Paste => "M7 3H3V18H17V3H13 M7 2H13V6H7Z M6 10H14 M6 14H12",
            EditorIcon.Undo => "M7 3L2 8L7 13 M2 8H11A6 6 0 0 1 11 20",
            EditorIcon.Redo => "M13 3L18 8L13 13 M18 8H9A6 6 0 0 0 9 20",
            EditorIcon.Settings => "M7 2H13L14 5L17 6L18 10L17 14L14 15L13 18H7L6 15L3 14L2 10L3 6L6 5Z M13 10A3 3 0 1 1 7 10A3 3 0 1 1 13 10",
            EditorIcon.Help => "M6 6A4 4 0 1 1 12 10L10 12V13 M10 16V17 M19 10A9 9 0 1 1 1 10A9 9 0 1 1 19 10",
            EditorIcon.Info => "M10 8V15 M10 5V6 M19 10A9 9 0 1 1 1 10A9 9 0 1 1 19 10",
            EditorIcon.Play => "M5 2L18 10L5 18Z",
            EditorIcon.Pause => "M4 3H7V17H4Z M13 3H16V17H13Z",
            EditorIcon.Edit => "M3 13L13 3L17 7L7 17H3Z M11 5L15 9",
            EditorIcon.Previous => "M13 3L6 10L13 17",
            EditorIcon.Next => "M7 3L14 10L7 17",
            EditorIcon.StepBack => "M4 3V17 M16 3L6 10L16 17Z",
            EditorIcon.StepForward => "M16 3V17 M4 3L14 10L4 17Z",
            EditorIcon.Stop => "M3 3H17V17H3Z",
            EditorIcon.Export => "M3 10V18H17V10 M10 13V2 M5 7L10 2L15 7",
            EditorIcon.Check => "M2 10L7 15L18 4",
            EditorIcon.Pin => "M6 2H14 M7 2V8L4 12H16L13 8V2 M10 12V19",
            EditorIcon.Unpin => "M6 2H14 M7 2V8L4 12H16L13 8V2 M10 12V19 M2 2L18 18",
            EditorIcon.Image => "M2 2H18V18H2Z M2 15L7 10L11 14L14 11L18 15 M14 6A1 1 0 1 1 12 6A1 1 0 1 1 14 6",
            EditorIcon.Link => "M8 6L11 3A4 4 0 0 1 17 9L14 12 M12 14L9 17A4 4 0 0 1 3 11L6 8 M7 13L13 7",
            _ => throw new ArgumentOutOfRangeException(nameof(Icon))
        });
}
