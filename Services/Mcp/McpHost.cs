using System.Net;
using System.Security.Cryptography;
using System.Text;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Preview;
using LiberaFx.Services.Project;
using LiberaFx.ViewModels.Graph;
using LiberaFx.ViewModels.Main;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

namespace LiberaFx.Services.Mcp;

public sealed class McpHost
{
    public const int Port = 8765;
    public const string Endpoint = "http://127.0.0.1:8765/mcp";
    private readonly int _port;
    public string ActualEndpoint => $"http://127.0.0.1:{_port}/mcp";
    public string AccessToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private WebApplication? _app;

    public McpHost(int port = Port) => _port = port;

    public async Task StartAsync(ActiveGraphProvider activeGraph, PreviewTelemetryStore? previewTelemetry = null,
        PreviewAssetStore? previewAssets = null, AssetMetadataStore? assetMetadata = null,
        EditorPlayback? playback = null, MainViewModel? documents = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(McpHost).Assembly.FullName,
            EnvironmentName = Environments.Production
        });
        // Do not allow application or environment configuration to add Kestrel endpoints.
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, _port));
        builder.Services.AddSingleton(activeGraph);
        var telemetry = previewTelemetry ?? new PreviewTelemetryStore();
        builder.Services.AddSingleton(telemetry);
        builder.Services.AddSingleton(previewAssets ?? new PreviewAssetStore(telemetry));
        builder.Services.AddSingleton(assetMetadata ?? new AssetMetadataStore());
        builder.Services.AddSingleton(playback ?? new EditorPlayback());
        var mcp = builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<LiberaFxMcpTools>()
            .WithTools<GraphLayoutMcpTools>()
            .WithTools<AssetMcpTools>()
            .WithTools<GpuParticleMcpTools>()
            .WithTools<GraphDebugTools>();
        if (documents is not null)
        {
            builder.Services.AddSingleton(documents);
            mcp.WithTools<ProjectFileMcpTools>();
            mcp.WithTools<GraphDocumentationMcpTools>();
        }

        var app = builder.Build();
        try
        {
            app.Use(async (context, next) =>
            {
                var host = context.Request.Host;
                var loopbackHost = string.Equals(host.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(host.Host, "localhost", StringComparison.OrdinalIgnoreCase);
                if (!loopbackHost || (host.Port is not null && host.Port != _port) ||
                    context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
                    context.Request.Headers.ContainsKey("Origin"))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                var suppliedToken = Encoding.UTF8.GetBytes(
                    context.Request.Headers["X-LiberaFx-Token"].ToString());
                var expectedToken = Encoding.UTF8.GetBytes(AccessToken);
                if (suppliedToken.Length != expectedToken.Length ||
                    !CryptographicOperations.FixedTimeEquals(suppliedToken, expectedToken))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                using var chatContext = ChatMcpExecution.EnterRequest(
                    context.Request.Headers[ChatMcpExecution.Header].ToString());
                await next(context);
            });
            app.MapMcp("/mcp");

            await app.StartAsync();
            _app = app;
        }
        catch (Exception startError)
        {
            try
            {
                await app.DisposeAsync();
            }
            catch (Exception disposeError)
            {
                throw new AggregateException("MCP startup and cleanup failed.", startError, disposeError);
            }

            throw;
        }
    }

    public async Task StopAsync()
    {
        if (_app is null)
            return;

        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }
}
