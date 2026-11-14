using Avalonia.Controls;
using Avalonia.Interactivity;
using LiberaFx.ViewModels;
using LiberaFx.Services;

namespace LiberaFx.Views;

public partial class NodePropertiesView : UserControl
{
    public NodePropertiesView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            LocalizationService.Shared.LanguageChanged += OnLanguageChanged;
            RefreshLocalizedText();
        };
        DetachedFromVisualTree += (_, _) => LocalizationService.Shared.LanguageChanged -= OnLanguageChanged;
        DataContextChanged += (_, _) => RefreshLocalizedText();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshLocalizedText();

    private void RefreshLocalizedText()
    {
        if (DataContext is NodePropertiesPaneViewModel pane) pane.RefreshLocalizedText();
    }

    private void OnDisconnectClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is NodePropertiesPaneViewModel pane &&
            sender is Button { Tag: GraphConnectionViewModel connection })
        {
            pane.Graph.DisconnectConnection(connection);
        }
    }
}
