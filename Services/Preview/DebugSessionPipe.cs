using System.IO.Pipes;
using System.Text.Json;

namespace LiberaFx.Services;

// Explicit local debug automation; credentials never enter files or public endpoints.
public sealed class DebugSessionPipe : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _task;

    public DebugSessionPipe(McpHost mcp, PreviewHost preview)
    {
        _task = Environment.GetEnvironmentVariable("LIBERAFX_MCP_DEBUG") == "1"
            ? Task.Run(() => ServeAsync(mcp, preview, _stop.Token))
            : Task.CompletedTask;
    }

    private static async Task ServeAsync(McpHost mcp, PreviewHost preview, CancellationToken cancellation)
    {
        var name = $"LiberaFx.Editor.Debug.{Environment.ProcessId}";
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    mcpEndpoint = mcp.ActualEndpoint, mcpToken = mcp.AccessToken,
                    previewPort = new Uri(preview.ActualEndpoint.Replace("<editor-ip>", "127.0.0.1")).Port,
                    previewToken = preview.AccessToken, previewFingerprint = preview.Fingerprint
                });
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                try { await pipe.WriteAsync(bytes, timeout.Token); }
                catch (IOException) { }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _task.GetAwaiter().GetResult();
        _stop.Dispose();
    }
}
