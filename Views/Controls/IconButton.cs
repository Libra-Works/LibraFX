using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using LiberaFx.Models.Appearance;
using Path = Avalonia.Controls.Shapes.Path;

namespace LiberaFx.Views.Controls;

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
            Width = 18, Height = 18, Stretch = Stretch.None,
            StrokeThickness = 1.65, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
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
            EditorIcon.Save => "M3.7 2.2H12.8L15.8 5.2V14.3A1.5 1.5 0 0 1 14.3 15.8H3.7A1.5 1.5 0 0 1 2.2 14.3V3.7A1.5 1.5 0 0 1 3.7 2.2Z M5.8 2.2V5.6H11.5V2.2 M5.2 15.8V11.2H12.8V15.8",
            EditorIcon.SaveAs => "M7.6 15.8H3.7A1.5 1.5 0 0 1 2.2 14.3V3.7A1.5 1.5 0 0 1 3.7 2.2H12.8L15.8 5.2V7.4 M5.8 2.2V5.5H11.5V2.2 M5.2 15.8V11.4H7.6 M10.2 16.2L10.9 13.2L14.5 9.6L16.6 11.7L13 15.3Z",
            EditorIcon.Lock => "M5.8 8.2V5.6A3.2 3.2 0 0 1 12.2 5.6V8.2 M4.8 8.2H13.2A1.5 1.5 0 0 1 14.7 9.7V14.5A1.5 1.5 0 0 1 13.2 16H4.8A1.5 1.5 0 0 1 3.3 14.5V9.7A1.5 1.5 0 0 1 4.8 8.2Z M9 11.2V13.2",
            EditorIcon.Unlock => "M5.8 8.2V5.4A3.2 3.2 0 0 1 12.1 4.6 M4.8 8.2H13.2A1.5 1.5 0 0 1 14.7 9.7V14.5A1.5 1.5 0 0 1 13.2 16H4.8A1.5 1.5 0 0 1 3.3 14.5V9.7A1.5 1.5 0 0 1 4.8 8.2Z M9 11.2V13.2",
            EditorIcon.Delete => "M3 4.8H15 M6.8 4.8V3.3A1.1 1.1 0 0 1 7.9 2.2H10.1A1.1 1.1 0 0 1 11.2 3.3V4.8 M4.6 4.8L5.3 14.6A1.4 1.4 0 0 0 6.7 15.9H11.3A1.4 1.4 0 0 0 12.7 14.6L13.4 4.8 M7.5 7.6V13.2 M10.5 7.6V13.2",
            EditorIcon.Frame => "M6.2 2.5H3.7A1.2 1.2 0 0 0 2.5 3.7V6.2 M11.8 2.5H14.3A1.2 1.2 0 0 1 15.5 3.7V6.2 M2.5 11.8V14.3A1.2 1.2 0 0 0 3.7 15.5H6.2 M11.8 15.5H14.3A1.2 1.2 0 0 0 15.5 14.3V11.8 M6.8 6.8H11.2V11.2H6.8Z",
            EditorIcon.Group => "M5.6 2.4H3.6A1.2 1.2 0 0 0 2.4 3.6V5.6 M12.4 2.4H14.4A1.2 1.2 0 0 1 15.6 3.6V5.6 M2.4 12.4V14.4A1.2 1.2 0 0 0 3.6 15.6H5.6 M12.4 15.6H14.4A1.2 1.2 0 0 0 15.6 14.4V12.4 M5 5H8.4V8.4H5Z M9.6 9.6H13V13H9.6Z",
            EditorIcon.ZoomIn => "M13.2 8A5.2 5.2 0 1 1 2.8 8A5.2 5.2 0 1 1 13.2 8Z M11.7 11.7L15.8 15.8 M5.8 8H10.2 M8 5.8V10.2",
            EditorIcon.ZoomOut => "M13.2 8A5.2 5.2 0 1 1 2.8 8A5.2 5.2 0 1 1 13.2 8Z M11.7 11.7L15.8 15.8 M5.8 8H10.2",
            EditorIcon.Refresh => "M14.8 6.8A6.2 6.2 0 0 0 4.2 4.8L2.8 6.8 M2.8 3.2V6.8H6.4 M3.2 11.2A6.2 6.2 0 0 0 13.8 13.2L15.2 11.2 M15.2 14.8V11.2H11.6",
            EditorIcon.Reset => "M3.4 3.8V7.8H7.4 M3.4 7.8A5.8 5.8 0 1 1 4.6 13.2",
            EditorIcon.Close => "M4.5 4.5L13.5 13.5 M13.5 4.5L4.5 13.5",
            EditorIcon.Disconnect => "M9.6 4.4L10.8 3.2A2.7 2.7 0 0 1 14.6 7L13.4 8.2 M8.4 13.6L7.2 14.8A2.7 2.7 0 0 1 3.4 11L4.6 9.8 M3.2 3.2L14.8 14.8",
            EditorIcon.Add => "M9 3.8V14.2 M3.8 9H14.2",
            EditorIcon.Remove => "M3.8 9H14.2",
            EditorIcon.Document => "M4 2.5H10.5L14 6V15.5H4Z M10.5 2.5V6H14 M6 9H12 M6 12H12",
            EditorIcon.Open => "M2.2 13.8V4.6A1.2 1.2 0 0 1 3.4 3.4H7L8.6 5.2H14.2A1.2 1.2 0 0 1 15.4 6.4V8 M2.2 14.2L4.4 8.2H16.2L14 14.2Z",
            EditorIcon.Copy => "M7.2 6.2H14.3A1.3 1.3 0 0 1 15.6 7.5V14.5A1.3 1.3 0 0 1 14.3 15.8H7.2A1.3 1.3 0 0 1 5.9 14.5V7.5A1.3 1.3 0 0 1 7.2 6.2Z M3.6 11.8H3.5A1.3 1.3 0 0 1 2.2 10.5V3.5A1.3 1.3 0 0 1 3.5 2.2H10.5A1.3 1.3 0 0 1 11.8 3.5V3.6",
            EditorIcon.Cut => "M7.2 6A2.3 2.3 0 1 1 2.6 6A2.3 2.3 0 1 1 7.2 6Z M7.2 12A2.3 2.3 0 1 1 2.6 12A2.3 2.3 0 1 1 7.2 12Z M6.8 7.3L15.4 13.8 M6.8 10.7L15.4 4.2",
            EditorIcon.Paste => "M6.5 3.6H4.2A1.2 1.2 0 0 0 3 4.8V14.6A1.2 1.2 0 0 0 4.2 15.8H13.8A1.2 1.2 0 0 0 15 14.6V4.8A1.2 1.2 0 0 0 13.8 3.6H11.5 M6.5 2.2H11.5V4.8H6.5Z M6 9H12 M6 12.2H10.5",
            EditorIcon.Undo => "M6.8 4.2L3 8L6.8 11.8 M3 8H11.2A3.8 3.8 0 0 1 11.2 15.6H7.5",
            EditorIcon.Redo => "M11.2 4.2L15 8L11.2 11.8 M15 8H6.8A3.8 3.8 0 0 0 6.8 15.6H10.5",
            EditorIcon.Settings => "M16.34 8.03A7.4 7.4 0 0 1 16.34 9.97L14.52 10.43A5.7 5.7 0 0 1 13.91 11.89L14.87 13.5A7.4 7.4 0 0 1 13.5 14.87L11.89 13.91A5.7 5.7 0 0 1 10.43 14.52L9.97 16.34A7.4 7.4 0 0 1 8.03 16.34L7.57 14.52A5.7 5.7 0 0 1 6.11 13.91L4.5 14.87A7.4 7.4 0 0 1 3.13 13.5L4.09 11.89A5.7 5.7 0 0 1 3.48 10.43L1.66 9.97A7.4 7.4 0 0 1 1.66 8.03L3.48 7.57A5.7 5.7 0 0 1 4.09 6.11L3.13 4.5A7.4 7.4 0 0 1 4.5 3.13L6.11 4.09A5.7 5.7 0 0 1 7.57 3.48L8.03 1.66A7.4 7.4 0 0 1 9.97 1.66L10.43 3.48A5.7 5.7 0 0 1 11.89 4.09L13.5 3.13A7.4 7.4 0 0 1 14.87 4.5L13.91 6.11A5.7 5.7 0 0 1 14.52 7.57Z M11.5 9A2.5 2.5 0 1 1 6.5 9A2.5 2.5 0 1 1 11.5 9Z",
            EditorIcon.Help => "M15.8 9A6.8 6.8 0 1 1 2.2 9A6.8 6.8 0 1 1 15.8 9Z M7.2 7.2A2 2 0 1 1 10.3 8.9C9.5 9.4 9 9.9 9 10.8 M9 13.1V13.3",
            EditorIcon.Info => "M15.8 9A6.8 6.8 0 1 1 2.2 9A6.8 6.8 0 1 1 15.8 9Z M9 8.2V12.8 M9 5.5V5.7",
            EditorIcon.Play => "M5.5 3.5L14.5 9L5.5 14.5Z",
            EditorIcon.Pause => "M4.8 3.8H7.8V14.2H4.8Z M10.2 3.8H13.2V14.2H10.2Z",
            EditorIcon.Edit => "M3.2 12.2L11.8 3.6A1.2 1.2 0 0 1 13.5 3.6L14.4 4.5A1.2 1.2 0 0 1 14.4 6.2L5.8 14.8H3.2Z M10.2 5.2L12.8 7.8",
            EditorIcon.Previous => "M11.2 4.2L6.4 9L11.2 13.8",
            EditorIcon.Next => "M6.8 4.2L11.6 9L6.8 13.8",
            EditorIcon.StepBack => "M4.2 4.2V13.8 M13.8 4.2L6.8 9L13.8 13.8Z",
            EditorIcon.StepForward => "M13.8 4.2V13.8 M4.2 4.2L11.2 9L4.2 13.8Z",
            EditorIcon.Stop => "M4.5 4.5H13.5V13.5H4.5Z",
            EditorIcon.Export => "M3.2 10.8V14.2A1.2 1.2 0 0 0 4.4 15.4H13.6A1.2 1.2 0 0 0 14.8 14.2V10.8 M9 11.5V2.8 M5.5 6.2L9 2.8L12.5 6.2",
            EditorIcon.Check => "M3.5 9.4L7.2 13.1L14.8 5.2",
            EditorIcon.Pin => "M6.2 2.6H11.8 M7 2.6V7.4L4.6 10.6H13.4L11 7.4V2.6 M9 10.6V15.8",
            EditorIcon.Unpin => "M6.2 2.6H11.8 M11 2.6V7.4L13.4 10.6H11.5 M6.6 7.8L4.6 10.6H7.8 M9 11.8V15.8 M3 3L15 15",
            EditorIcon.Image => "M3.8 2.8H14.2A1.3 1.3 0 0 1 15.5 4.1V13.9A1.3 1.3 0 0 1 14.2 15.2H3.8A1.3 1.3 0 0 1 2.5 13.9V4.1A1.3 1.3 0 0 1 3.8 2.8Z M2.8 13.2L6.8 9.2L10.2 12.6L12.6 10.2L15.2 12.8 M12.6 6.2A1 1 0 1 1 10.6 6.2A1 1 0 1 1 12.6 6.2Z",
            EditorIcon.Link => "M7.5 10.5L10.5 7.5 M6.4 8.2L4.4 10.2A3.1 3.1 0 0 0 8.8 14.6L10.8 12.6 M11.6 9.8L13.6 7.8A3.1 3.1 0 0 0 9.2 3.4L7.2 5.4",
            EditorIcon.Clock => "M15.6 9A6.6 6.6 0 1 1 2.4 9A6.6 6.6 0 1 1 15.6 9Z M9 5.2V9.2L11.8 11",
            EditorIcon.SignalPreview => "M8.8 2.4L3.6 9.6H8.4L7.4 15.6L13.2 8.2H8.4Z M14 11.8V15.4 M12.2 13.6H15.8",
            EditorIcon.SignalRuntime => "M2.6 5.4L6.8 8.2L2.6 11Z M11.6 2.4L7.2 9.6H11.4L10.4 15.6L15.6 8.2H11.4Z",
            _ => throw new ArgumentOutOfRangeException(nameof(Icon))
        });
}
