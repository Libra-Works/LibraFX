using System.Net;
using System.Security.Cryptography;
using System.Text;
using LiberaFx.ViewModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

namespace LiberaFx.Services;

public sealed class McpHost
{
    public const int Port = 8765;
    public const string Endpoint = "http://127.0.0.1:8765/mcp";
    public string AccessToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private WebApplication? _app;

    public async Task StartAsync(ActiveGraphProvider activeGraph)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(McpHost).Assembly.FullName,
            EnvironmentName = Environments.Production
        });
        // Do not allow application or environment configuration to add Kestrel endpoints.
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, Port));
        builder.Services.AddSingleton(activeGraph);
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<LiberaFxMcpTools>()
            .WithTools<GraphDebugTools>();

        var app = builder.Build();
        try
        {
            app.Use(async (context, next) =>
            {
                var host = context.Request.Host;
                var loopbackHost = string.Equals(host.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(host.Host, "localhost", StringComparison.OrdinalIgnoreCase);
                if (!loopbackHost || (host.Port is not null && host.Port != Port) ||
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
