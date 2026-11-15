using Avalonia.Controls;
using Avalonia.Interactivity;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class WindowLayoutsWindow : Window
{
    public WindowLayoutsWindow() => InitializeComponent();
    public WindowLayoutsWindow(WindowLayoutsViewModel model) : this() => DataContext = model;
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
