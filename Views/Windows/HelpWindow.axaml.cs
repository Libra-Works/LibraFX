using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LiberaFx.Views.Windows;

public partial class HelpWindow : Window
{
    public HelpWindow() => InitializeComponent();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
