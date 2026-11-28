using Avalonia.Interactivity;
using Avalonia.Controls;
using LiberaFx.ViewModels.Panels;

namespace LiberaFx.Views.Timeline;

public partial class CurveEditorView : UserControl
{
    public CurveEditorView()
    {
        InitializeComponent();
        CurveCanvas.ViewTransformChanged += () => ColorBar.SyncView(CurveCanvas.ViewOriginX, CurveCanvas.ViewScaleX);
        CurveCanvas.LayoutUpdated += (_, _) => ColorBar.SyncView(CurveCanvas.ViewOriginX, CurveCanvas.ViewScaleX);
    }

    private void OnFrameAllClick(object? sender, RoutedEventArgs e) => CurveCanvas.FrameAll();
    private void OnFrameSelectedClick(object? sender, RoutedEventArgs e) => CurveCanvas.FrameSelected();

    private void OnZoomInClick(object? sender, RoutedEventArgs e) => CurveCanvas.ZoomBy(1.25);
    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => CurveCanvas.ZoomBy(1 / 1.25);
    private void OnChannelClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CurveEditorViewModel model && sender is Button { Tag: CurveChannelViewModel channel })
            model.SelectChannel(channel);
    }
}
