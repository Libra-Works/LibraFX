using Avalonia.Controls;
using Avalonia.Interactivity;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class NodePropertiesView : UserControl
{
    public NodePropertiesView()
    {
        InitializeComponent();
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
