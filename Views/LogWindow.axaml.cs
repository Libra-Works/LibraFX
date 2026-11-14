using Avalonia.Controls;
using Avalonia.Interactivity;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class LogWindow : Window
{
    public LogWindow() => InitializeComponent();

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model) model.Log.Clear();
    }
}
