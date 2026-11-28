using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LiberaFx.Services;

namespace LiberaFx.Views;

public partial class UpdateWindow : Window
{
    private readonly AppUpdateService _service;
    private AppUpdateInfo? _info;
    private bool _busy;

    public UpdateWindow() : this(AppUpdateService.Shared, null)
    {
    }

    public UpdateWindow(AppUpdateService service, AppUpdateInfo? preloaded)
    {
        _service = service;
        InitializeComponent();
        AutoCheckBox.IsChecked = _service.AutoCheckUpdates;
        AutoCheckBox.IsCheckedChanged += (_, _) => _service.AutoCheckUpdates = AutoCheckBox.IsChecked == true;
        if (preloaded is not null)
        {
            ApplyInfo(preloaded);
        }
        else
        {
            Opened += async (_, _) => await CheckAsync();
        }
    }

    private async Task CheckAsync()
    {
        if (_busy) return;
        _busy = true;
        UpdateButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        var text = LocalizationService.Shared;
        VersionSummaryText.Text = text.Get("Panels.Version", AppUpdateService.GetCurrentVersion());
        StatusText.Text = text.Get("Editor.UpdateChecking");
        try
        {
            var info = await _service.CheckForUpdatesAsync();
            if (info is null)
            {
                StatusText.Text = text.Get("Editor.UpdateUpToDate");
                LaterButton.Content = text.Get("Panels.Close");
                return;
            }
            ApplyInfo(info);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            StatusText.Text = text.Get("Editor.UpdateCheckFailed", ex.Message);
            LaterButton.Content = text.Get("Panels.Close");
        }
        finally
        {
            _busy = false;
            UpdateButton.IsEnabled = true;
            SkipButton.IsEnabled = true;
        }
    }

    private void ApplyInfo(AppUpdateInfo info)
    {
        _info = info;
        var text = LocalizationService.Shared;
        var current = AppUpdateService.GetCurrentVersion();
        VersionSummaryText.Text = info.IsUpdateAvailable
            ? text.Get("Editor.UpdateAvailableSummary", current, info.Version)
            : text.Get("Panels.Version", current);

        if (info.IsUpdateAvailable)
        {
            HeadingText.Text = text.Get("Editor.UpdateAvailableTitle", info.Version);
            NotesLabel.IsVisible = true;
            NotesBorder.IsVisible = true;
            ReleaseNotesText.Text = string.IsNullOrWhiteSpace(info.ReleaseNotes)
                ? text.Get("Editor.UpdateNoNotes")
                : info.ReleaseNotes;
            UpdateButton.IsVisible = true;
            SkipButton.IsVisible = true;
            LaterButton.Content = text.Get("Editor.UpdateNotNow");
            StatusText.Text = info.IsSkipped
                ? text.Get("Editor.UpdatePreviouslySkipped")
                : text.Get("Editor.UpdatePrompt");
        }
        else
        {
            NotesLabel.IsVisible = false;
            NotesBorder.IsVisible = false;
            UpdateButton.IsVisible = false;
            SkipButton.IsVisible = false;
            LaterButton.Content = text.Get("Panels.Close");
            StatusText.Text = text.Get("Editor.UpdateUpToDate");
        }
    }

    private async void OnUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _info is null) return;
        _busy = true;
        UpdateButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        var text = LocalizationService.Shared;
        StatusText.Text = text.Get("Editor.UpdateDownloading");
        try
        {
            var launchedInstaller = await _service.DownloadAndApplyUpdateAsync(_info);
            if (launchedInstaller && Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
            Close();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = text.Get("Editor.UpdateApplyFailed", ex.Message);
            _busy = false;
            UpdateButton.IsEnabled = true;
            SkipButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
        }
    }

    private void OnSkipClick(object? sender, RoutedEventArgs e)
    {
        if (_info is not null)
        {
            _service.SkipVersion(_info.Version);
        }
        Close();
    }

    private void OnLaterClick(object? sender, RoutedEventArgs e) => Close();
}
