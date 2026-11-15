using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LiberaFx.Views;

public partial class TimelineView : UserControl
{
    public TimelineView() => InitializeComponent();
    private void OnFrameAllClick(object? sender, RoutedEventArgs e) => Canvas.FrameAll();
}
