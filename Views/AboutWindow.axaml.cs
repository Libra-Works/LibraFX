using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LiberaFx.Services;

namespace LiberaFx.Views;

public partial class AboutWindow : Window
{
    private readonly string? _version;

    public AboutWindow()
    {
        InitializeComponent();
        var assembly = typeof(App).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString();
        _version = version?.Split('+')[0];
        LocalizationService.Shared.LanguageChanged += OnLanguageChanged;
        Closed += OnClosed;
        UpdateVersion();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => UpdateVersion();

    private void UpdateVersion() => VersionText.Text = LocalizationService.Shared.Get(
        "Panels.Version", _version ?? LocalizationService.Shared.Get("Panels.Unknown"));

    private void OnClosed(object? sender, EventArgs e) =>
        LocalizationService.Shared.LanguageChanged -= OnLanguageChanged;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
