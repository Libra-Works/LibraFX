using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace LiberaFx.Services;

public sealed record AvailableViewer(string PipeName, int ProcessId, Guid InstanceId,
    string Engine, string Name, bool Connected)
{
    public override string ToString() => $"{Engine} — {Name} ({ProcessId})";
}

public sealed class ViewerDiscovery
{
    public const string Prefix = "LibraFX.Viewer.";
    private readonly Func<NamedPipeClientStream, int, bool> _trust;

    public ViewerDiscovery() : this(TrustServer) { }
    internal ViewerDiscovery(Func<NamedPipeClientStream, int, bool> trust) => _trust = trust;

    public async Task<IReadOnlyList<AvailableViewer>> DiscoverAsync(CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var names = await Task.Run(() => Directory.EnumerateFiles(@"\\.\pipe\", Prefix + "*")
            .Select(Path.GetFileName).OfType<string>().Take(32).ToArray(), timeout.Token);
        var found = new List<AvailableViewer>();
        foreach (var name in names)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try { found.Add(await QueryAsync(name, timeout.Token)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                InvalidDataException or TimeoutException) { }
            catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { }
        }
        return found;
    }

    public async Task<AvailableViewer> QueryAsync(string pipeName, CancellationToken cancellation)
    {
        var processId = ProcessId(pipeName);
        var reply = await ExchangeAsync(pipeName, processId,
            new { type = "librafx_viewer_query", version = 1 }, cancellation);
        try
        {
            if (reply.GetProperty("type").GetString() != "librafx_viewer" ||
                reply.GetProperty("version").GetInt32() != 1 ||
                reply.GetProperty("processId").GetInt32() != processId ||
                !Guid.TryParse(reply.GetProperty("instanceId").GetString(), out var instanceId) || instanceId == Guid.Empty)
                throw new InvalidDataException("Invalid viewer identity.");
            var engine = reply.GetProperty("engine").GetString()!;
            var name = reply.GetProperty("name").GetString()!;
            if (engine is not ("Unreal" or "Unity" or "Godot") ||
                string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl))
                throw new InvalidDataException("Invalid viewer metadata.");
            return new(pipeName, processId, instanceId, engine, name, reply.GetProperty("connected").GetBoolean());
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Invalid viewer response.", exception);
        }
    }

    public async Task ConnectAsync(AvailableViewer viewer, string invitation, CancellationToken cancellation)
    {
        var current = await QueryAsync(viewer.PipeName, cancellation);
        if (current.InstanceId != viewer.InstanceId || current.ProcessId != viewer.ProcessId)
            throw new InvalidDataException("The viewer restarted. Search again.");
        using var parsed = JsonDocument.Parse(invitation);
        if (parsed.RootElement.GetProperty("type").GetString() != "librafx_preview_connection" ||
            invitation.Length > 4096) throw new InvalidDataException("Invalid invitation.");
        var result = await ExchangeAsync(viewer.PipeName, viewer.ProcessId, new
        {
            type = "librafx_viewer_connect", version = 1, instanceId = viewer.InstanceId, invitation
        }, cancellation);
        if (!result.TryGetProperty("accepted", out var accepted) || accepted.ValueKind != JsonValueKind.True)
            throw new IOException("Viewer rejected the request or is busy.");
    }

    private async Task<JsonElement> ExchangeAsync(string name, int processId, object request,
        CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);
        if (!_trust(pipe, processId)) throw new UnauthorizedAccessException("Untrusted viewer process.");
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request) + "\n");
        if (bytes.Length > 8192) throw new InvalidDataException("Viewer request too large.");
        await pipe.WriteAsync(bytes, timeout.Token);
        using var response = new MemoryStream();
        var buffer = new byte[512];
        while (response.Length <= 8192)
        {
            var count = await pipe.ReadAsync(buffer, timeout.Token);
            if (count == 0) throw new IOException("Viewer disconnected.");
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, count);
            response.Write(buffer, 0, newline < 0 ? count : newline);
            if (response.Length > 8192) break;
            if (newline < 0) continue;
            if (newline != count - 1) throw new InvalidDataException("Unexpected viewer reply data.");
            try
            {
                using var json = JsonDocument.Parse(response.ToArray());
                if (json.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Invalid viewer reply.");
                await pipe.WriteAsync(new byte[] { 6 }, timeout.Token);
                return json.RootElement.Clone();
            }
            catch (JsonException exception) { throw new InvalidDataException("Invalid viewer JSON.", exception); }
        }
        throw new InvalidDataException("Viewer response too large.");
    }

    private static int ProcessId(string name)
    {
        if (name.StartsWith(Prefix, StringComparison.Ordinal) &&
            name.Length > Prefix.Length && name[Prefix.Length] != '0' &&
            int.TryParse(name.AsSpan(Prefix.Length), out var id) && id > 0 &&
            name.AsSpan(Prefix.Length).IndexOfAnyExceptInRange('0', '9') < 0)
            return id;
        throw new InvalidDataException("Invalid viewer pipe name.");
    }

    private static bool TrustServer(NamedPipeClientStream pipe, int expected)
    {
        if (!OperatingSystem.IsWindows() ||
            !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actual) || actual != expected) return false;
        try
        {
            using var process = Process.GetProcessById(expected);
            return process.ProcessName is "UnrealEditor" or "UnrealEditor-Cmd" or "Unity" or "Godot";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception) { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
