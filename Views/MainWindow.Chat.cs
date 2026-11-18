using Avalonia.Interactivity;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class MainWindow
{
    private void OnChatClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel main) main.ShowChatWindow(this);
    }
}
