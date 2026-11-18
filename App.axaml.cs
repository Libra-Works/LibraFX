using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using LiberaFx.Views;
using System.Diagnostics;

namespace LiberaFx;

public partial class App : Application
{
    private ServerSettingsService _serverSettings = null!;
    private McpHost _mcpHost = null!;
    private PreviewHost _previewHost = null!;
    private DebugSessionPipe? _debugSessionPipe;
    private PreviewDiscoveryPipe? _previewDiscovery;
    private CrashReportLifecycle? _crashReports;
    private Task _mcpStart = Task.CompletedTask;
    private Task _previewStart = Task.CompletedTask;
    private bool _shutdownInProgress;
    private bool _allowShutdown;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        LocalizationService.Initialize(this);
        _serverSettings = new ServerSettingsService();
        _crashReports = new CrashReportLifecycle();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _mcpHost = new McpHost(DebugPort("LIBERAFX_MCP_DEBUG_PORT", _serverSettings.StartupMcpPort));
            _previewHost = new PreviewHost(DebugPort("LIBERAFX_PREVIEW_DEBUG_PORT", _serverSettings.StartupPreviewPort));
            _debugSessionPipe = new DebugSessionPipe(_mcpHost, _previewHost);
            desktop.Exit += (_, _) =>
            {
                _previewDiscovery?.Dispose();
                _debugSessionPipe?.Dispose();
                _crashReports?.CompleteCleanExit();
                _crashReports?.Dispose();
            };
            var viewModel = new MainViewModel(_mcpHost.AccessToken, _previewHost.AccessToken, _previewHost.Fingerprint,
                _mcpHost.ActualEndpoint, _previewHost.ActualEndpoint, _serverSettings);
            var window = new MainWindow
            {
                DataContext = viewModel,
            };
            var crashDrafts = _crashReports!.Drafts;
            var startupSetup = new StartupSetupState();
            window.ConfigureCrashReports(crashDrafts);
            var startupProject = desktop.Args is { Length: 1 } args &&
                Path.GetExtension(args[0]).Equals(ProjectFileService.FileExtension, StringComparison.OrdinalIgnoreCase)
                ? args[0] : null;
            window.Opened += async (_, _) =>
            {
                viewModel.SetStorageProvider(window.StorageProvider);
                if (startupSetup.IsPending)
                {
                    await new StartupSetupWindow(viewModel, startupSetup, crashDrafts).ShowDialog(window);
                }
                else
                {
                    var splash = new SplashWindow();
                    splash.Show(window);
                    DispatcherTimer.RunOnce(() =>
                    {
                        if (splash.IsVisible) splash.Close();
                    }, TimeSpan.FromMilliseconds(500));
                }
                window.ShowRecoveredCrashReports();
                if (startupProject is not null)
                {
                    var path = startupProject;
                    startupProject = null;
                    await viewModel.OpenStartupProjectAsync(path);
                }
            };
            desktop.MainWindow = window;
            window.Closing += (_, e) =>
            {
                if (_allowShutdown) return;
                e.Cancel = true;
                _ = ShutdownAsync(desktop, viewModel);
            };
            desktop.ShutdownRequested += (_, e) =>
            {
                if (_allowShutdown) return;
                e.Cancel = true;
                _ = ShutdownAsync(desktop, viewModel);
            };
            _mcpStart = StartMcpAsync(viewModel);
            _previewStart = StartPreviewAsync(viewModel);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static int DebugPort(string name, int fallback) =>
        Environment.GetEnvironmentVariable("LIBERAFX_MCP_DEBUG") == "1" &&
        int.TryParse(Environment.GetEnvironmentVariable(name), out var port) && port is >= 1024 and <= 65535
            ? port : fallback;

    private async Task StartPreviewAsync(MainViewModel viewModel)
    {
        try
        {
            await Task.Run(() => _previewHost.StartAsync(viewModel));
            _previewDiscovery = new PreviewDiscoveryPipe(_previewHost.ActualEndpoint,
                _previewHost.AccessToken, _previewHost.Fingerprint);
            await Dispatcher.UIThread.InvokeAsync(() => viewModel.SetPreviewStatus("Editor.PreviewRunning"));
        }
        catch (Exception ex)
        {
            Trace.TraceError("Preview startup failed: {0}", ex);
            await Dispatcher.UIThread.InvokeAsync(() => viewModel.SetPreviewStatus("Editor.StartupFailed", ex.Message));
        }
    }

    private async Task StartMcpAsync(MainViewModel viewModel)
    {
        try
        {
            await Task.Run(() => _mcpHost.StartAsync(viewModel.ActiveGraphProvider, viewModel.PreviewTelemetry,
                viewModel.PreviewAssets, viewModel.AssetMetadata, viewModel.Playback, documents: viewModel));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                viewModel.SetMcpStatus(Environment.GetEnvironmentVariable("LIBERAFX_MCP_DEBUG") == "1"
                    ? "Editor.McpRunningDebug" : "Editor.McpRunning");
            });
        }
        catch (Exception ex)
        {
            Trace.TraceError("MCP startup failed: {0}", ex);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                viewModel.SetMcpStatus("Editor.StartupFailed", ex.Message);
            });
        }
    }

    private async Task ShutdownAsync(IClassicDesktopStyleApplicationLifetime desktop, MainViewModel viewModel)
    {
        if (_shutdownInProgress) return;
        _shutdownInProgress = true;
        try
        {
            if (desktop.MainWindow is MainWindow mainWindow &&
                !await mainWindow.ConfirmCloseAsync())
            {
                _shutdownInProgress = false;
                return;
            }

            await _mcpStart;
            await _previewStart;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                viewModel.SetMcpStatus("Editor.Stopping");
            });
            await Task.Run(() => _mcpHost.StopAsync());
            _previewDiscovery?.Dispose();
            _previewDiscovery = null;
            await _previewHost.StopAsync();
            _debugSessionPipe?.Dispose();
            _debugSessionPipe = null;
            _previewHost.Dispose();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                viewModel.DisposeStorageFiles();
                _allowShutdown = true;
                desktop.Shutdown();
            });
        }
        catch (Exception ex)
        {
            Trace.TraceError("MCP shutdown failed: {0}", ex);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                viewModel.SetMcpStatus("Editor.ShutdownFailed", ex.Message);
                _shutdownInProgress = false;
            });
        }
    }
}