using Avalonia.Controls;
using LiberaFx.ViewModels.Main;

namespace LiberaFx.Views.Windows;

public partial class WindowLayoutsWindow : UserControl
{
    public WindowLayoutsWindow() => InitializeComponent();
    public WindowLayoutsWindow(WindowLayoutsViewModel model) : this() => DataContext = model;
}
