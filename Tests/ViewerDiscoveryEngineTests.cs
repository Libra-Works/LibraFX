using System.Net;
using System.Net.Sockets;
using Avalonia.Threading;
using LiberaFx.Services.Preview;
using LiberaFx.ViewModels.Main;

internal static class ViewerDiscoveryEngineTests
{
    public static void Run(int[] processIds)
    {
        var task = VerifyAsync(processIds);
        var frame = new DispatcherFrame();
        using var timeout = DispatcherTimer.RunOnce(() => frame.Continue = false, TimeSpan.FromSeconds(60));
        _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.UIThread.PushFrame(frame);
        if (!task.IsCompleted) throw new TimeoutException("Real viewer pairing did not finish.");
        task.GetAwaiter().GetResult();
    }

    private static async Task VerifyAsync(int[] processIds)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        var service = new ViewerDiscovery();
        var viewers = await FindExpectedViewersAsync();
        foreach (var viewer in viewers.Append(viewers[0]))
        {
            var current = await FindExpectedViewersAsync();
            if (!current.Select(item => item.InstanceId).SequenceEqual(viewers.Select(item => item.InstanceId)))
                throw new InvalidDataException("A viewer restarted between searches.");
            await VerifyPairingAsync(service, viewer, current, token);
            while ((await service.QueryAsync(viewer.PipeName, token)).Connected)
                await Task.Delay(100, token);
        }
        Console.WriteLine($"Real Unreal discovery, selection and pinned TLS reconnection passed ({viewers.Length} viewers).");

        async Task<AvailableViewer[]> FindExpectedViewersAsync()
        {
            var discovered = await service.DiscoverAsync(token);
            var expected = processIds.Select(id => discovered.SingleOrDefault(viewer => viewer.ProcessId == id)
                ?? throw new InvalidDataException($"Dedicated Viewer {id} was not discovered.")).ToArray();
            if (expected.Any(viewer => viewer.Engine != "Unreal" || viewer.Connected))
                throw new InvalidDataException("Expected dedicated, disconnected Unreal Viewers.");
            return expected;
        }
    }

    private static async Task VerifyPairingAsync(ViewerDiscovery service, AvailableViewer viewer,
        AvailableViewer[] candidates, CancellationToken token)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var host = new PreviewHost(port);
        var model = new MainViewModel();
        try
        {
            await host.StartAsync(model);
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
            foreach (var other in candidates.Where(item => item.ProcessId != viewer.ProcessId))
            {
                if ((await service.QueryAsync(other.PipeName, token)).Connected)
                    throw new InvalidDataException("A Viewer that was not selected connected.");
            }
        }
        finally
        {
            await host.StopAsync();
            await Dispatcher.UIThread.InvokeAsync(model.DisposeStorageFiles);
        }
    }
}
