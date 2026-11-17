using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia.Threading;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace LiberaFx.Services;

public sealed class PreviewHost : IDisposable
{
    public const int Port = 8766;
    public const string Endpoint = "wss://<editor-ip>:8766/preview";
    private readonly int _port;
    public string ActualEndpoint => $"wss://<editor-ip>:{_port}/preview";
    private const int MaxClients = 16;
    private readonly X509Certificate2 _certificate = CreateCertificate();
    private readonly SemaphoreSlim _clientSlots = new(MaxClients, MaxClients);
    private readonly ConcurrentDictionary<Guid, WebSocket> _sockets = new();
    private readonly Dictionary<Guid, PreviewClient> _clients = [];
    private readonly object _sync = new();
    private WebApplication? _app;
    private MainViewModel? _model;
    private GraphWorkspaceViewModel? _graph;
    private PreviewPublication? _latest;
    private long _sequence;
    private long _signalRequestSequence;
    private const int SignalRequestHistoryLimit = 256;
    private readonly List<PreviewSignalRequest> _signalRequests = [];
    private PreviewTelemetryStore? _telemetry;
    private PreviewAssetStore? _assets;

    public string AccessToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(_certificate.RawData));

    public PreviewHost(int port = Port) => _port = port;

    public async Task StartAsync(MainViewModel model)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(PreviewHost).Assembly.FullName,
            EnvironmentName = Environments.Production
        });
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxConcurrentConnections = 64;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Listen(IPAddress.Any, _port, listen => listen.UseHttps(_certificate));
        });

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.Map("/preview", HandleClientAsync);
        try
        {
            await app.StartAsync();
            _app = app;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _model = model;
                _telemetry = model.PreviewTelemetry;
                _assets = model.PreviewAssets;
                model.ActiveGraphProvider.PropertyChanged += OnActiveGraphChanged;
                model.Playback.StateChanged += OnPlaybackChanged;
                AttachGraph(model.ActiveGraphProvider.Graph);
                Publish();
            });
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(DetachGraph);
            await app.DisposeAsync();
            _app = null;
            throw;
        }
    }

    public async Task StopAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(DetachGraph);
        foreach (var socket in _sockets.Values) socket.Abort();
        if (_app is null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }

    private void OnActiveGraphChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActiveGraphProvider.Graph) || _model is null) return;
        AttachGraph(_model.ActiveGraphProvider.Graph);
        OnHistoryChanged(null, EventArgs.Empty);
    }

    private void AttachGraph(GraphWorkspaceViewModel graph)
    {
        if (_graph is not null)
        {
            _graph.History.StateChanged -= OnHistoryChanged;
            _graph.PreviewSignalRequested -= OnPreviewSignalRequested;
        }
        lock (_sync)
        {
            _signalRequests.Clear();
            _latest = null;
        }
        _graph = graph;
        graph.History.StateChanged += OnHistoryChanged;
        graph.PreviewSignalRequested += OnPreviewSignalRequested;
    }

    private void DetachGraph()
    {
        if (_model is not null) _model.ActiveGraphProvider.PropertyChanged -= OnActiveGraphChanged;
        if (_model is not null) _model.Playback.StateChanged -= OnPlaybackChanged;
        if (_graph is not null)
        {
            _graph.History.StateChanged -= OnHistoryChanged;
            _graph.PreviewSignalRequested -= OnPreviewSignalRequested;
        }
        lock (_sync)
        {
            _signalRequests.Clear();
            _latest = null;
        }
        _model = null;
        _graph = null;
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        try { Publish(); }
        catch (Exception ex)
        {
            if (_model is not null) _model.PreviewStatus = $"Snapshot error: {ex.Message}";
        }
    }

    private void OnPlaybackChanged(object? sender, EventArgs e)
    {
        try { Publish(reuseGraph: true); }
        catch (Exception ex)
        {
            if (_model is not null) _model.PreviewStatus = $"Snapshot error: {ex.Message}";
        }
    }

    private void OnPreviewSignalRequested(object? sender, Guid nodeId)
    {
        if (_graph is null || !ReferenceEquals(sender, _graph) || _model is null) return;
        var node = _graph.Nodes.FirstOrDefault(node => node.Id == nodeId);
        if (node is null || !_graph.CanSendPreviewSignal(node))
        {
            SetSignalStatus("Editor.SignalSend.InstanceTargetRequired");
            return;
        }
        lock (_sync)
        {
            if (!_clients.Values.Any(client => client.Version == 3) ||
                _latest is null || _latest.DocumentId != _model.ActiveDocument?.Id)
            {
                SetSignalStatus("Editor.SignalSend.CompiledClientRequired");
                return;
            }
            _signalRequests.Add(new PreviewSignalRequest(++_signalRequestSequence, nodeId));
            if (_signalRequests.Count > SignalRequestHistoryLimit) _signalRequests.RemoveAt(0);
            Publish(reuseGraph: true, reusePlayback: true);
        }
        SetSignalStatus("Editor.SignalSend.Requested");
    }

    private void SetSignalStatus(string key)
    {
        if (_model is not null) _model.PreviewStatus = LocalizationService.Shared.Get(key);
        _graph?.SetConnectionStatus(key);
    }

    private void Publish(bool reuseGraph = false, bool reusePlayback = false)
    {
        if (_model?.ActiveDocument is not { } document || _graph is null) return;
        // Invalidate commands before capturing the new history state, even if capture fails.
        if (!reuseGraph || _latest?.DocumentId != document.Id)
        {
            lock (_sync)
            {
                _signalRequests.Clear();
                _latest = null;
            }
        }
        var graph = reuseGraph && _latest?.DocumentId == document.Id ? _latest.Graph : _graph.CreateSnapshot();
        lock (_sync)
        {
            var publication = new PreviewPublication(++_sequence, document.Id, graph, GraphNodeDefinition.All,
                reusePlayback ? _latest?.Playback : _model.Playback.Snapshot)
            {
                SignalRequests = _signalRequests.ToArray(),
                SignalRequestSequence = _signalRequestSequence
            };
            _latest = publication;
            foreach (var client in _clients.Values) client.Channel.Writer.TryWrite(publication);
        }
        if (_model.PreviewStatus.StartsWith("Snapshot error:", StringComparison.Ordinal))
            _model.PreviewStatus = "Running (TLS; token required)";
    }

    private async Task HandleClientAsync(HttpContext context)
    {
        if (context.Request.Headers.ContainsKey("Origin"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var supplied = Encoding.UTF8.GetBytes(context.Request.Headers["X-LiberaFx-Preview-Token"].ToString());
        var expected = Encoding.UTF8.GetBytes(AccessToken);
        if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (!await _clientSlots.WaitAsync(0))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var version = context.Request.Query["protocol"].ToString();
        if (version is not ("" or "1" or "2" or "3"))
        {
            _clientSlots.Release();
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var id = Guid.NewGuid();
        var client = new PreviewClient(version == "3" ? 3 : version == "2" ? 2 : 1);
        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            _sockets[id] = socket;
            lock (_sync)
            {
                _clients.Add(id, client);
                if (_latest is not null) client.Channel.Writer.TryWrite(_latest);
            }
            UpdateClientCount();
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var clientCancellation = cancel.Token;
            _telemetry?.Connect(id, client.Version, bytes => SendControlAsync(socket, client, bytes, clientCancellation),
                context.Connection.RemoteIpAddress?.ToString());
            var sending = Task.Run(() => SendAsync(socket, id, client, cancel.Token), cancel.Token);
            try
            {
                var receiving = ReceiveAsync(socket, id, client, cancel.Token);
                await Task.WhenAny(sending, receiving);
                cancel.Cancel();
                socket.Abort();
                await receiving;
            }
            finally
            {
                cancel.Cancel();
                try { await sending; }
                catch (OperationCanceledException) { }
                catch (WebSocketException) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            lock (_sync) _clients.Remove(id);
            _telemetry?.Disconnect(id);
            _assets?.Disconnect(id);
            _sockets.TryRemove(id, out _);
            client.Channel.Writer.TryComplete();
            _clientSlots.Release();
            UpdateClientCount();
        }
    }

    private async Task SendAsync(WebSocket socket, Guid id, PreviewClient client, CancellationToken cancellation)
    {
        PreviewPublication? baseline = null;
        using var compiled = new CompiledPreviewSender();
        await foreach (var publication in client.Channel.Reader.ReadAllAsync(cancellation))
        {
            // Diff against the last message actually sent, never a dropped pending update.
            var force = Interlocked.Exchange(ref client.ForceSnapshot, 0) != 0;
            if (client.Version == 3)
            {
                var message = compiled.Prepare(publication, force);
                if (message is null) continue;
                await SendSerializedAsync(socket, client, message.Bytes, WebSocketMessageType.Binary, cancellation);
                if ((message.Kind == CompiledPreviewKind.Program || message.EvaluatedRenderScene is not null) &&
                    _assets?.Supports(id, "rendering") == true)
                {
                    var scene = message.EvaluatedRenderScene ?? RenderProgramBindings.CreateScene(publication.Graph, publication.Sequence);
                    await SendSerializedAsync(socket, client, CompiledPreviewProtocol.CreateScene(scene),
                        WebSocketMessageType.Binary, cancellation);
                }
                if (message.Kind == CompiledPreviewKind.Program && _assets?.Supports(id, "gpuParticles") == true)
                {
                    // Native lifecycle graphs reject GPU mixtures, but must
                    // still explicitly retire the previous document's GPU
                    // scene rather than leave unrelated particles visible.
                    var scene = GpuPreviewScene.CreateScene(message.EditorNativeExecution ?
                        new GraphSnapshot([], []) : publication.Graph, publication.Sequence);
                    await SendSerializedAsync(socket, client, CompiledPreviewProtocol.CreateScene(scene),
                        WebSocketMessageType.Binary, cancellation);
                }
                compiled.Sent(message);
                continue;
            }
            var payload = client.Version == 2 ? PreviewProtocol.Encode(publication, baseline, force) : publication.LegacyPayload;
            await SendSerializedAsync(socket, client, payload, WebSocketMessageType.Text, cancellation);
            baseline = publication;
        }
    }

    private static async Task SendSerializedAsync(WebSocket socket, PreviewClient client, byte[] bytes,
        WebSocketMessageType type, CancellationToken cancellation)
    {
        await client.SendGate.WaitAsync(cancellation);
        try { await socket.SendAsync(bytes.AsMemory(), type, true, cancellation); }
        finally { client.SendGate.Release(); }
    }

    private static async Task SendControlAsync(WebSocket socket, PreviewClient client, byte[] bytes,
        CancellationToken cancellation)
    {
        // Controls never enter the drop-oldest publication channel. Bound concurrent control writers.
        if (!await client.ControlSlots.WaitAsync(0, cancellation))
            throw new InvalidOperationException("Preview control queue is full.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await SendSerializedAsync(socket, client, bytes, WebSocketMessageType.Binary, timeout.Token);
        }
        finally { client.ControlSlots.Release(); }
    }

    private async Task ReceiveAsync(WebSocket socket, Guid id, PreviewClient client, CancellationToken cancellation)
    {
        var limit = client.Version == 3 ? CompiledPreviewProtocol.MaxSceneBytes : 255;
        var buffer = new byte[limit + 1];
        while (!cancellation.IsCancellationRequested)
        {
            var count = 0;
            var fragments = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(count), cancellation);
                if (result.MessageType == WebSocketMessageType.Close) return;
                count += result.Count;
                if (client.Version == 1 || result.MessageType != WebSocketMessageType.Text ||
                    count > limit || ++fragments > (client.Version == 3 ? 1024 : 16))
                    return;
            } while (!result.EndOfMessage);
            try
            {
                using var request = JsonDocument.Parse(buffer.AsMemory(0, count));
                if (request.RootElement.ValueKind != JsonValueKind.Object ||
                    !request.RootElement.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return;
                if (type.GetString() != "resync")
                {
                    if (client.Version != 3 ||
                        (_telemetry?.Receive(id, buffer.AsMemory(0, count)) != true &&
                         _assets?.Receive(id, buffer.AsMemory(0, count)) != true)) return;
                    UpdateClientCount();
                    if (type.GetString() == "hello" &&
                        (_assets?.Supports(id, "rendering") == true || _assets?.Supports(id, "gpuParticles") == true))
                    {
                        lock (_sync)
                        {
                            Interlocked.Exchange(ref client.ForceSnapshot, 1);
                            if (_latest is not null) client.Channel.Writer.TryWrite(_latest);
                        }
                    }
                    continue;
                }
                if (client.Version == 3 && request.RootElement.EnumerateObject().Count() != 1) return;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return; }
            lock (_sync)
            {
                Interlocked.Exchange(ref client.ForceSnapshot, 1);
                if (_latest is not null) client.Channel.Writer.TryWrite(_latest);
            }
        }
    }

    private sealed class PreviewClient(int version)
    {
        public int Version { get; } = version;
        public int ForceSnapshot;
        public SemaphoreSlim SendGate { get; } = new(1, 1);
        public SemaphoreSlim ControlSlots { get; } = new(16, 16);
        public Channel<PreviewPublication> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<PreviewPublication>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    }

    private void UpdateClientCount()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_model is not null) _model.PreviewClientCount = _sockets.Count;
            _model?.RefreshPreviewConnections();
        });
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=LIBERA FX Preview", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null,
            OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
    }

    public void Dispose()
    {
        _certificate.Dispose();
        _clientSlots.Dispose();
    }
}
