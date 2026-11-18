using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class PreviewConnectionsWindow : UserControl
{
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _signature = "";
    private bool _busy;
    private bool _refreshing;
    private Guid? _selectedId;
    private CancellationTokenSource? _stop;
    private readonly ViewerDiscovery _discovery = new();
    private bool _discovering;
    public PreviewConnectionsWindow()
    {
        InitializeComponent();
        _refresh.Tick += (_, _) => Refresh();
        AttachedToVisualTree += (_, _) =>
        {
            _stop = new();
            Refresh();
            _refresh.Start();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _refresh.Stop();
            _stop?.Cancel();
            _stop?.Dispose();
            _stop = null;
        };
    }

    private void OnAvailableViewerChanged(object? sender, SelectionChangedEventArgs e) =>
        ConnectViewerButton.IsEnabled = !_discovering && AvailableViewers.SelectedItem is AvailableViewer;

    private async void OnFindViewers(object? sender, RoutedEventArgs e)
    {
        if (_discovering || _stop is not { Token: var cancellation }) return;
        _discovering = true;
        FindViewersButton.IsEnabled = ConnectViewerButton.IsEnabled = false;
        AvailableViewers.ItemsSource = null;
        DiscoveryStatus.Text = LocalizationService.Shared.Get("Connections.FindingViewers");
        try
        {
            var viewers = await _discovery.DiscoverAsync(cancellation);
            if (cancellation.IsCancellationRequested) return;
            AvailableViewers.ItemsSource = viewers;
            AvailableViewers.SelectedIndex = viewers.Count == 1 ? 0 : -1;
            DiscoveryStatus.Text = LocalizationService.Shared.Get(viewers.Count == 0
                ? "Connections.NoViewers" : "Connections.ViewersFound", viewers.Count);
        }
        catch (OperationCanceledException)
        {
            if (!cancellation.IsCancellationRequested)
                DiscoveryStatus.Text = LocalizationService.Shared.Get("Connections.ViewerTimeout");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { DiscoveryStatus.Text = LocalizationService.Shared.Get("Connections.ViewerFailed", exception.Message); }
        finally
        {
            _discovering = false;
            FindViewersButton.IsEnabled = true;
            ConnectViewerButton.IsEnabled = AvailableViewers.SelectedItem is AvailableViewer;
        }
    }

    private async void OnConnectViewer(object? sender, RoutedEventArgs e)
    {
        if (_discovering || _stop is not { Token: var cancellation } || AvailableViewers.SelectedItem is not AvailableViewer viewer ||
            DataContext is not MainViewModel model) return;
        _discovering = true;
        FindViewersButton.IsEnabled = ConnectViewerButton.IsEnabled = false;
        try
        {
            var invitation = PreviewConnectionInvitation.Create(model.PreviewEndpoint,
                model.PreviewAccessToken, model.PreviewFingerprint);
            await _discovery.ConnectAsync(viewer, invitation, cancellation);
            DiscoveryStatus.Text = LocalizationService.Shared.Get("Connections.ViewerQueued");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            if (!cancellation.IsCancellationRequested)
                DiscoveryStatus.Text = LocalizationService.Shared.Get("Connections.ViewerFailed", exception.Message);
        }
        finally
        {
            _discovering = false;
            FindViewersButton.IsEnabled = true;
            ConnectViewerButton.IsEnabled = AvailableViewers.SelectedItem is AvailableViewer;
        }
    }

    private void Refresh()
    {
        if (DataContext is not MainViewModel model) return;
        var peers = model.PreviewTelemetry.Clients();
        var signature = string.Join("|", peers.Select(peer =>
            $"{peer.ClientId}:{peer.Engine}:{peer.ViewerName}:{peer.Role}:{peer.EngineVersion}:{peer.Platform}"));
        if (signature != _signature)
        {
            var selected = _selectedId;
            _signature = signature;
            _refreshing = true;
            Peers.ItemsSource = peers;
            Peers.SelectedItem = peers.FirstOrDefault(peer => peer.ClientId == selected);
            _selectedId = (Peers.SelectedItem as PreviewPeer)?.ClientId;
            _refreshing = false;
        }
        ReconnectButton.IsEnabled = !_busy && _selectedId is { } id && model.PreviewAssets.Supports(id);
        if (peers.Count == 0) Status.Text = LocalizationService.Shared.Get("Connections.Disconnected");
        var feedback = new List<string>();
        foreach (var peer in peers.Where(peer => peer.SentProgramSequence.HasValue))
            feedback.Add(LocalizationService.Shared.Get(peer.AwaitingProgramAcknowledgement
                    ? "Connections.ProgramAckPending" : "Connections.ProgramAcknowledged",
                peer.ViewerName ?? peer.Engine ?? peer.ClientId.ToString(), peer.SentProgramSequence!.Value));
        foreach (var peer in peers.Where(peer => model.PreviewAssets.Supports(peer.ClientId, "rendering")))
        {
            try
            {
                if (model.PreviewAssets.RenderStatus(peer.ClientId) is not { } render) continue;
                var errors = render.GetProperty("errors").EnumerateArray().Select(error => error.GetString())
                    .Where(error => !string.IsNullOrEmpty(error)).Take(8).ToArray();
                if (errors.Length > 0)
                    feedback.Add(LocalizationService.Shared.Get("Connections.RenderErrors", string.Join("\n", errors)));
            }
            catch (InvalidOperationException) { }
        }
        foreach (var peer in peers.Where(peer => model.PreviewAssets.Supports(peer.ClientId, "gpuParticles")))
        {
            try
            {
                var gpu = model.PreviewAssets.GpuStatus(peer.ClientId);
                if (gpu is null)
                {
                    feedback.Add(LocalizationService.Shared.Get("Connections.GpuPending"));
                    continue;
                }
                var errors = gpu.Value.GetProperty("errors").EnumerateArray()
                    .Select(error => error.GetString()).Where(error => !string.IsNullOrEmpty(error)).ToList();
                foreach (var program in gpu.Value.GetProperty("programs").EnumerateArray())
                    if (program.TryGetProperty("errors", out var programErrors))
                        errors.AddRange(programErrors.EnumerateArray().Select(error => error.GetString())
                            .Where(error => !string.IsNullOrEmpty(error)));
                feedback.Add(errors.Count > 0
                    ? LocalizationService.Shared.Get("Connections.GpuErrors", string.Join("\n", errors.Take(8)))
                    : LocalizationService.Shared.Get(gpu.Value.GetProperty("runtimeExecuted").GetBoolean()
                        ? "Connections.GpuExecuted" : "Connections.GpuPending"));
            }
            catch (InvalidOperationException) { }
        }
        GpuFeedback.Text = string.Join("\n", feedback.Distinct());
        model.RefreshPreviewConnections();
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        _selectedId = (Peers.SelectedItem as PreviewPeer)?.ClientId;
        if (!_busy) Status.Text = _selectedId.HasValue ?
            LocalizationService.Shared.Get("Connections.Selected", _selectedId.Value) :
            LocalizationService.Shared.Get("Connections.Select");
        Refresh();
    }

    private async void OnReconnect(object? sender, RoutedEventArgs e)
    {
        if (_busy || DataContext is not MainViewModel model || _selectedId is not { } id) return;
        _busy = true;
        Status.Text = LocalizationService.Shared.Get("Connections.Reconnecting");
        Refresh();
        try
        {
            await model.PreviewAssets.ReconnectAsync(id);
            Status.Text = LocalizationService.Shared.Get("Connections.ReconnectSent");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or TimeoutException or
            OperationCanceledException or System.Net.WebSockets.WebSocketException)
        { Status.Text = LocalizationService.Shared.Get("Connections.Failed", ex.Message); }
        finally { _busy = false; Refresh(); }
    }

    private async void OnCopyConnection(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try
        {
            var invitation = PreviewConnectionInvitation.Create(model.PreviewEndpoint,
                model.PreviewAccessToken, model.PreviewFingerprint);
            await clipboard.SetTextAsync(invitation);
            InvitationStatus.Text = LocalizationService.Shared.Get("Connections.ConnectionCopied");
        }
        catch (Exception)
        {
            InvitationStatus.Text = LocalizationService.Shared.Get("Connections.CopyFailed");
        }
    }
}
