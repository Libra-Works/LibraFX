using Avalonia.Interactivity;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class MainWindow
{
    private CrashDraftStore? _crashDrafts;
    private CrashReportsWindow? _crashReportsWindow;

    public void ConfigureCrashReports(CrashDraftStore drafts)
    {
        _crashDrafts = drafts;
        Opened += (_, _) =>
        {
            if (drafts.Recover().Count > 0 || drafts.Status.Length > 0)
                OnCrashReportsClick(this, new RoutedEventArgs());
        };
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
