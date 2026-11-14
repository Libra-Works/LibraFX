using Avalonia.Controls;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class OutlinerView : UserControl
{
    public OutlinerView()
    {
        InitializeComponent();
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is OutlinerViewModel outliner &&
            sender is ListBox { SelectedItem: GraphNodeViewModel node } &&
            outliner.Graph.Nodes.Contains(node) &&
            outliner.Graph.SelectedNode != node)
        {
            outliner.Graph.SelectNode(node);
        }
    }
}
