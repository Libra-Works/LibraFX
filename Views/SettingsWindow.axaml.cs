using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel model || sender is not TextBox { Tag: string id }) return;
        e.Handled = true;
        if (e.Key != Key.Escape) model.Shortcuts.Assign(id, e.Key, e.KeyModifiers);
    }

    private void OnResetShortcutsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model) model.Shortcuts.Reset();
    }
}
