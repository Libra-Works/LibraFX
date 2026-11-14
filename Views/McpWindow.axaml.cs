using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class McpWindow : Window
{
    public McpWindow() => InitializeComponent();

    private void OnShowTokenChanged(object? sender, RoutedEventArgs e) =>
        TokenBox.PasswordChar = sender is CheckBox { IsChecked: true } ? '\0' : '●';

    private async void OnCopyTokenClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model && Clipboard is { } clipboard)
        {
            try { await clipboard.SetTextAsync(model.McpAccessToken); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Clipboard copy failed: {0}", ex.Message); }
        }
    }
}
