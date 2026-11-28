using Avalonia.Interactivity;
using LiberaFx.Services.Crash;
using LiberaFx.ViewModels.Main;

namespace LiberaFx.Views.Windows;

public partial class MainWindow
{
    private CrashDraftStore? _crashDrafts;
    private CrashReportsWindow? _crashReportsWindow;

    public void ConfigureCrashReports(CrashDraftStore drafts) => _crashDrafts = drafts;

    internal void ShowRecoveredCrashReports()
    {
        if (IsVisible && _crashDrafts?.TryClaimStartupPrompt() == true)
        {
            OnCrashReportsClick(this, new RoutedEventArgs());
        }
    }

    private void OnCrashReportsClick(object? sender, RoutedEventArgs e)
    {
        if (_crashDrafts is null || DataContext is not MainViewModel main) return;
        if (_crashReportsWindow is not null) { _crashReportsWindow.Activate(); return; }
        _crashReportsWindow = new CrashReportsWindow(_crashDrafts,
            () => main.Extensions.CrashReportProviders.ToArray());
        _crashReportsWindow.Closed += (_, _) => _crashReportsWindow = null;
        _crashReportsWindow.Show(this);
    }
}
