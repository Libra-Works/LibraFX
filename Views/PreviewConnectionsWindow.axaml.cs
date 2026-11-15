using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class PreviewConnectionsWindow : Window
{
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _signature = "";
    private bool _busy;
    private bool _refreshing;
    private Guid? _selectedId;
    public PreviewConnectionsWindow()
    {
        InitializeComponent();
        _refresh.Tick += (_, _) => Refresh();
        Opened += (_, _) => { Refresh(); _refresh.Start(); };
        Closed += (_, _) => _refresh.Stop();
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
        if (DataContext is not MainViewModel model || Clipboard is not { } clipboard) return;
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
