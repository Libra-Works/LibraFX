using Avalonia;
using Avalonia.Controls;

namespace LiberaFx.Views.Timeline;

public partial class PlaybackTransport : UserControl
{
    public static readonly StyledProperty<bool> ShowFrameToolsProperty =
        AvaloniaProperty.Register<PlaybackTransport, bool>(nameof(ShowFrameTools));

    public bool ShowFrameTools
    {
        get => GetValue(ShowFrameToolsProperty);
        set => SetValue(ShowFrameToolsProperty, value);
    }

    public PlaybackTransport() => InitializeComponent();
}
