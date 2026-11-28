using Avalonia.Controls;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class WindowLayoutsWindow : UserControl
{
    public WindowLayoutsWindow() => InitializeComponent();
    public WindowLayoutsWindow(WindowLayoutsViewModel model) : this() => DataContext = model;
}
