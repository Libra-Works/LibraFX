using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Avalonia.Threading;
using LiberaFx.Models.Graph;
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
    private const int MaxClients = 16;
    private readonly X509Certificate2 _certificate = CreateCertificate();
    private readonly SemaphoreSlim _clientSlots = new(MaxClients, MaxClients);
    private readonly ConcurrentDictionary<Guid, WebSocket> _sockets = new();
    private readonly Dictionary<Guid, Channel<byte[]>> _clients = [];
    private readonly object _sync = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private WebApplication? _app;
    private MainViewModel? _model;
    private GraphWorkspaceViewModel? _graph;
    private byte[]? _latest;
    private long _sequence;

    public string AccessToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(_certificate.RawData));

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
            options.Listen(IPAddress.Any, Port, listen => listen.UseHttps(_certificate));
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
                model.ActiveGraphProvider.PropertyChanged += OnActiveGraphChanged;
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
        if (_graph is not null) _graph.History.StateChanged -= OnHistoryChanged;
        _graph = graph;
        graph.History.StateChanged += OnHistoryChanged;
    }

    private void DetachGraph()
    {
        if (_model is not null) _model.ActiveGraphProvider.PropertyChanged -= OnActiveGraphChanged;
        if (_graph is not null) _graph.History.StateChanged -= OnHistoryChanged;
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

    private void Publish()
    {
        if (_model?.ActiveDocument is not { } document || _graph is null) return;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "snapshot",
            protocolVersion = 1,
            sequence = ++_sequence,
            documentId = document.Id,
            graph = _graph.CreateSnapshot(),
            nodeTypes = GraphNodeDefinition.All.Select(type => new
            {
                kind = type.Kind.Value,
                type.TypeId,
                type.Namespace,
                type.TypeName
            })
        }, _json);
        lock (_sync)
        {
            _latest = payload;
            foreach (var channel in _clients.Values) channel.Writer.TryWrite(payload);
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

        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            _sockets[id] = socket;
            lock (_sync)
            {
                _clients.Add(id, channel);
                if (_latest is not null) channel.Writer.TryWrite(_latest);
            }
            UpdateClientCount();
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var sending = SendAsync(socket, channel.Reader, cancel.Token);
            try
            {
                var buffer = new byte[1];
                var receiving = socket.ReceiveAsync(buffer.AsMemory(), cancel.Token).AsTask();
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
            _sockets.TryRemove(id, out _);
            channel.Writer.TryComplete();
            _clientSlots.Release();
            UpdateClientCount();
        }
    }

    private static async Task SendAsync(WebSocket socket, ChannelReader<byte[]> reader, CancellationToken cancellation)
    {
        await foreach (var message in reader.ReadAllAsync(cancellation))
            await socket.SendAsync(message.AsMemory(), WebSocketMessageType.Text, true, cancellation);
    }

    private void UpdateClientCount()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_model is not null) _model.PreviewClientCount = _sockets.Count;
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
