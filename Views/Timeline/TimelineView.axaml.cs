using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LiberaFx.Views.Timeline;

public partial class TimelineView : UserControl
{
    public TimelineView() => InitializeComponent();
    private void OnFrameAllClick(object? sender, RoutedEventArgs e) => Canvas.FrameAll();
    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => Canvas.ZoomBy(1.0 / 1.25);
    private void OnZoomInClick(object? sender, RoutedEventArgs e) => Canvas.ZoomBy(1.25);
}
