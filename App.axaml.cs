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
    private readonly McpHost _mcpHost = new();
    private Task _mcpStart = Task.CompletedTask;
    private bool _shutdownInProgress;
    private bool _allowShutdown;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel(_mcpHost.AccessToken);
            var window = new MainWindow
            {
                DataContext = viewModel,
            };
            window.Opened += (_, _) => viewModel.SetStorageProvider(window.StorageProvider);
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
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartMcpAsync(MainViewModel viewModel)
    {
        try
        {
            await Task.Run(() => _mcpHost.StartAsync(viewModel.ActiveGraphProvider));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                viewModel.McpStatus = Environment.GetEnvironmentVariable("LIBERAFX_MCP_DEBUG") == "1"
                    ? "Running (loopback only; token required; graph UI debug enabled)"
                    : "Running (loopback only; access token required)";
            });
        }
        catch (Exception ex)
        {
            Trace.TraceError("MCP startup failed: {0}", ex);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                viewModel.McpStatus = $"Startup failed: {ex.Message}";
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
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                viewModel.McpStatus = "Stopping";
            });
            await Task.Run(() => _mcpHost.StopAsync());
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
                viewModel.McpStatus = $"Shutdown failed: {ex.Message}. Close the window to retry.";
                _shutdownInProgress = false;
            });
        }
    }
}