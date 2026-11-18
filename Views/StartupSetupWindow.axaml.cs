using Avalonia.Controls;
using Avalonia.Interactivity;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class StartupSetupWindow : Window
{
    private readonly MainViewModel? _main;
    private readonly StartupSetupState? _state;
    private readonly CrashDraftStore? _drafts;

    public StartupSetupWindow()
    {
        InitializeComponent();
    }

    internal StartupSetupWindow(MainViewModel main, StartupSetupState state, CrashDraftStore drafts) : this()
    {
        _main = main;
        _state = state;
        _drafts = drafts;
        DataContext = main;
        ColorSchemePicker.SelectedItem = main.SelectedColorScheme;
    }

    private async void OnCrashReportsClick(object? sender, RoutedEventArgs e)
    {
        if (_main is not { } main || _drafts is not { } drafts) return;
        drafts.TryClaimStartupPrompt();
        var window = new CrashReportsWindow(drafts, () => main.Extensions.CrashReportProviders.ToArray());
        await window.ShowDialog(this);
    }

    private void OnContinueClick(object? sender, RoutedEventArgs e)
    {
        if (_main is null || _state is null || ColorSchemePicker.SelectedItem is not string scheme)
        {
            return;
        }

        _main.SelectedColorScheme = scheme;
        if (!string.Equals(_main.SelectedColorScheme, scheme, StringComparison.Ordinal))
        {
            ShowStatus(_main.AppearanceStatus);
            return;
        }

        try
        {
            _state.Complete();
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowStatus(LocalizationService.Shared.Get("Editor.StartupSetupSaveFailed"));
        }
    }

    private void ShowStatus(string message)
    {
        SetupStatus.Text = message;
        SetupStatus.IsVisible = true;
    }
}
