using System.IO.Pipes;
using System.Text;

namespace LiberaFx.Services.Preview;

public sealed class PreviewDiscoveryPipe : IDisposable
{
    public const string Prefix = "LibraFX.Preview.";
    public const string Request = "librafx_preview_connection\n";
    private readonly CancellationTokenSource _stop = new();
    private readonly NamedPipeServerStream? _pipe;
    private readonly Task _task;
    private int _disposed;
    public string Name { get; }

    public PreviewDiscoveryPipe(string endpoint, string accessToken, string fingerprint, string? name = null)
    {
        Name = name ?? Prefix + Environment.ProcessId;
        var invitation = PreviewConnectionInvitation.Create(endpoint, accessToken, fingerprint);
        if (OperatingSystem.IsWindows())
            _pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _task = OperatingSystem.IsWindows()
            ? Task.Run(() => ServeAsync(_pipe!, Encoding.UTF8.GetBytes(invitation + "\n"), _stop.Token))
            : Task.CompletedTask;
    }

    private static async Task ServeAsync(NamedPipeServerStream pipe, byte[] reply, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(cancellation);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    var expected = Encoding.ASCII.GetBytes(Request);
                    var request = new byte[expected.Length];
                    await pipe.ReadExactlyAsync(request, timeout.Token);
                    if (request.AsSpan().SequenceEqual(expected))
                        await pipe.WriteAsync(reply, timeout.Token);
                }
                catch (IOException) { }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
                finally
                {
                    if (pipe.IsConnected) pipe.Disconnect();
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        try { _task.GetAwaiter().GetResult(); }
        finally
        {
            _pipe?.Dispose();
            _stop.Dispose();
        }
    }
}
