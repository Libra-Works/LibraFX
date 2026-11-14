using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LiberaFx.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var assembly = typeof(App).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
        VersionText.Text = $"Version {version.Split('+')[0]}";
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
