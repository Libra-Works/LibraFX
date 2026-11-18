using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiberaFx.Services;

public sealed record LiveGameEndpoint(int Protocol, string Session, string Engine, string Transport,
    string SupportedUpdate, int Process);
public sealed record LiveGameInstance(string Target, string Label, bool Finished, int RenderCount);
public sealed record LiveGameResponse(int Protocol, string Session, string Sequence, string Result,
    string? Error, string? Target, LiveGameInstance[] Instances);
public sealed class LiveGameOutcomeUnknownException(Exception inner)
    : IOException("The command was published but its outcome is unknown. Refresh the live list before applying again.", inner);

/// <summary>
/// Explicitly selected, private local spool only. No discovery, remote endpoint,
/// credential, source-file upload, retry of mutations or arbitrary engine path.
/// A timeout after publication requires reconciliation via list.
/// </summary>
public sealed class LiveGameSpoolClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex SessionPattern = new("^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant);
    private static readonly Regex TargetPattern = new("^[0-9a-fA-F]{16}:[0-9a-fA-F]{16}$", RegexOptions.CultureInvariant);
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly string directory;
    private long sequence;
    private bool disposed;
    public LiveGameEndpoint Endpoint { get; }
    public bool RequiresRefresh { get; private set; }
    public TimeSpan Timeout { get; }

    private LiveGameSpoolClient(string directory, LiveGameEndpoint endpoint, TimeSpan timeout)
    { this.directory = directory; Endpoint = endpoint; Timeout = timeout; }

    public static async Task<LiveGameSpoolClient> ConnectAsync(string selectedDirectory,
        CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var duration = timeout ?? TimeSpan.FromSeconds(10);
        if (duration < TimeSpan.FromMilliseconds(50) || duration > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (string.IsNullOrWhiteSpace(selectedDirectory) || !Path.IsPathFullyQualified(selectedDirectory))
            throw new ArgumentException("Select an explicit absolute isolated endpoint directory.");
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedDirectory));
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
            new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network)
            throw new ArgumentException("Remote shares and network drives are not supported. Select a private local development directory.");
        AssertNotLinked(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Selected endpoint directory does not exist.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(duration);
        var endpoint = await ReadAsync<LiveGameEndpoint>(Path.Combine(path, "endpoint.json"), 16 * 1024, deadline.Token);
        ValidateEndpoint(endpoint);
        return new LiveGameSpoolClient(path, endpoint, duration);
    }

    public Task<LiveGameResponse> ListAsync(CancellationToken cancellationToken) =>
        SendAsync("list", null, null, cancellationToken);
    public Task<LiveGameResponse> ReplaceAsync(string target, byte[] package, CancellationToken cancellationToken)
    {
        if (!TargetPattern.IsMatch(target)) throw new ArgumentException("Select a live target returned by this endpoint.");
        if (package.Length == 0 || package.Length > 16 * 1024 * 1024)
            throw new ArgumentException("Compiled package must be nonempty and at most 16 MiB.");
        if (RequiresRefresh) throw new InvalidOperationException("Refresh the live list after the previous uncertain operation.");
        return SendAsync("replace", target, package, cancellationToken);
    }
    private async Task<LiveGameResponse> SendAsync(string kind, string? target, byte[]? bytes, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        var token = deadline.Token;
        await operation.WaitAsync(token);
        var published = false;
        var acknowledged = false;
        var temporary = Path.Combine(directory, "editor-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            AssertNotLinked(directory);
            var current = await ReadAsync<LiveGameEndpoint>(Path.Combine(directory, "endpoint.json"), 16 * 1024, token);
            if (current != Endpoint)
            {
                RequiresRefresh = true;
                throw new IOException("Selected endpoint changed or reconnected. Disconnect and explicitly select it again.");
            }
            var lockPath = Path.Combine(directory, "editor.lock");
            if (File.Exists(lockPath)) AssertNotLinked(lockPath);
            // Same-user tools serialize on a fixed lock. Engine never reads it.
            await using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
            var requestPath = Path.Combine(directory, "request.json");
            if (File.Exists(requestPath)) throw new IOException("Endpoint has an outstanding command. Wait for the game, then refresh; do not overwrite it.");
            var next = Math.Max(DateTime.UtcNow.Ticks, checked(sequence + 1));
            sequence = next;
            var requestSequence = next.ToString(CultureInfo.InvariantCulture);
            var command = new
            {
                protocol = 1, session = Endpoint.Session, sequence = requestSequence,
                operation = kind, target, policy = kind == "replace" ? "restart" : null,
                packageBase64 = bytes is null ? null : Convert.ToBase64String(bytes)
            };
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(stream, command, Json, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, requestPath, overwrite: false);
            published = true;
            var responsePath = Path.Combine(directory, "response.json");
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(responsePath))
                {
                    LiveGameResponse? response = null;
                    try { response = await ReadAsync<LiveGameResponse>(responsePath, 1024 * 1024, token); }
                    catch (FileNotFoundException) { } // Endpoint atomically replaces its response.
                    catch (JsonException) { } // A writer may have been interrupted; finite deadline applies.
                    if (response is not null && response.Session == Endpoint.Session && response.Sequence == requestSequence)
                    {
                        ValidateResponse(response);
                        if (kind == "replace" && response.Result == "Ok" && response.Target != target)
                            throw new InvalidDataException("Replacement acknowledgement names a different instance.");
                        acknowledged = true;
                        if (kind == "list" && response.Result == "Ok") RequiresRefresh = false;
                        return response;
                    }
                }
                await Task.Delay(30, token);
            }
        }
        catch (Exception error) when (published && !acknowledged && error is not LiveGameOutcomeUnknownException)
        {
            // Never assume cancellation rolled back a command already in the game.
            RequiresRefresh = true;
            throw new LiveGameOutcomeUnknownException(error);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            operation.Release();
        }
    }
    private static void ValidateEndpoint(LiveGameEndpoint endpoint)
    {
        if (endpoint.Protocol != 1 || endpoint.Transport != "isolated-local-spool" ||
            endpoint.Session is null || !SessionPattern.IsMatch(endpoint.Session) ||
            endpoint.Engine is not ("Unity" or "Unreal") || endpoint.Process <= 0 ||
            endpoint.SupportedUpdate != "restart-root-render-v1-v2")
            throw new InvalidDataException("Unsupported or malformed development endpoint descriptor.");
    }
    private static void ValidateResponse(LiveGameResponse response)
    {
        if (response.Protocol != 1 || string.IsNullOrWhiteSpace(response.Result) ||
            response.Instances is null || response.Instances.Length > 256)
            throw new InvalidDataException("Malformed live instance response.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instance in response.Instances)
            if (instance is null || instance.Target is null || !TargetPattern.IsMatch(instance.Target) ||
                !seen.Add(instance.Target) || instance.Label?.Length > 128 || instance.RenderCount < 0)
                throw new InvalidDataException("Malformed live instance identity.");
    }
    private static async Task<T> ReadAsync<T>(string path, int maximumBytes, CancellationToken token)
    {
        AssertNotLinked(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous);
        if (stream.Length > maximumBytes) throw new InvalidDataException("Oversized isolated protocol file.");
        // Bounded copy also rejects a file that grows after the initial stat.
        var expectedBytes = checked((int)stream.Length);
        var buffer = new byte[expectedBytes + 1];
        var used = 0;
        while (used < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(used), token);
            if (read == 0) break;
            used += read;
        }
        if (used > expectedBytes) throw new InvalidDataException("Isolated protocol file changed while reading.");
        return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, used), Json)
            ?? throw new InvalidDataException("Empty isolated protocol file.");
    }
    private static void AssertNotLinked(string path)
    {
        for (var item = Path.GetFullPath(path); !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked isolated endpoints are not supported.");
    }
    // Cancellation belongs to the window operation. Do not dispose the semaphore
    // while an in-flight asynchronous method still owns it.
    public void Dispose() => disposed = true;
}
