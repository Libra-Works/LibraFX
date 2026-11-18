using System.Net;
using System.Net.Sockets;
using Avalonia.Threading;
using LiberaFx.Services;
using LiberaFx.ViewModels;

internal static class ViewerDiscoveryEngineTests
{
    public static void Run(int processId)
    {
        var task = VerifyAsync(processId);
        var frame = new DispatcherFrame();
        using var timeout = DispatcherTimer.RunOnce(() => frame.Continue = false, TimeSpan.FromSeconds(60));
        _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.UIThread.PushFrame(frame);
        if (!task.IsCompleted) throw new TimeoutException("Real viewer pairing did not finish.");
        task.GetAwaiter().GetResult();
    }

    private static async Task VerifyAsync(int processId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var host = new PreviewHost(port);
        var model = new MainViewModel();
        try
        {
            await host.StartAsync(model);
            var service = new ViewerDiscovery();
            var viewers = await service.DiscoverAsync(token);
            var viewer = viewers.Single(candidate => candidate.ProcessId == processId);
            if (viewer.Engine != "Unreal") throw new InvalidDataException("Expected the dedicated Unreal viewer.");
            await service.ConnectAsync(viewer,
                PreviewConnectionInvitation.Create($"wss://127.0.0.1:{port}/preview", host.AccessToken, host.Fingerprint),
                token);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var peers = model.PreviewTelemetry.Clients();
                if (peers.Any(peer => peer.Protocol == 3 && peer.Engine == "Unreal" && peer.Role == "editor" &&
                    peer.ViewerName?.StartsWith(viewer.Name, StringComparison.Ordinal) == true))
                {
                    var current = await service.QueryAsync(viewer.PipeName, token);
                    if (current.InstanceId != viewer.InstanceId)
                        throw new InvalidDataException("Viewer restarted while connecting.");
                    if (current.Connected) break;
                }
                await Task.Delay(100, token);
            }
            Console.WriteLine("Real Unreal discovery, incarnation check and pinned TLS preview connection passed.");
        }
        finally
        {
            await host.StopAsync();
            await Dispatcher.UIThread.InvokeAsync(model.DisposeStorageFiles);
        }
    }
}
