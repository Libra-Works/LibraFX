using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LiberaFx.Extensions.Abstractions;

namespace LiberaFx.Services;

/// <summary>Real stateless Streamable HTTP MCP client. Never connects to a remote MCP endpoint.</summary>
public sealed class ChatMcpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _token;
    private long _nextId;
    private string? _protocol;
    public bool Connected { get; private set; }
    public IReadOnlyList<ChatTool> Tools { get; private set; } = [];
    // Conservative allowlist: every exposed tool executes through the guarded UI dispatcher.
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "libera_graph_analyze_optimization", "libera_graph_read", "libera_graph_list_node_types", "libera_graph_list_enums", "libera_graph_describe_node_ports",
        "libera_graph_describe_group", "libera_graph_describe_spawnable",
        "libera_graph_create_node", "libera_graph_connect_nodes", "libera_graph_rename_node",
        "libera_graph_move_node", "libera_graph_set_parameter", "libera_graph_delete_node",
        "libera_graph_apply_plan", "libera_graph_enter", "libera_graph_up",
        "libera_graph_preview_layout", "libera_graph_apply_layout"
    };

    public static bool NeedsConfirmation(string name) => name is not
        ("libera_graph_analyze_optimization" or "libera_graph_read" or "libera_graph_list_node_types" or "libera_graph_list_enums" or "libera_graph_describe_node_ports" or
         "libera_graph_describe_group" or "libera_graph_describe_spawnable" or "libera_graph_preview_layout");

    public ChatMcpClient(string endpoint, string token)
    {
        _endpoint = new Uri(endpoint, UriKind.Absolute);
        if (_endpoint.Scheme != "http" || _endpoint.Host != "127.0.0.1" ||
            _endpoint.AbsolutePath != "/mcp" || _endpoint.Query.Length != 0 ||
            _endpoint.Fragment.Length != 0 || _endpoint.UserInfo.Length != 0)
            throw new ArgumentException("Only the editor's IPv4 loopback MCP endpoint is supported.");
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("MCP authentication is required.");
        _token = token;
        _http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false
        }) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task ConnectAsync(CancellationToken cancellation)
    {
        Connected = false;
        Tools = [];
        var initialized = await RpcAsync("initialize", new
        {
            protocolVersion = "2025-03-26",
            capabilities = new { },
            clientInfo = new { name = "LiberaFx.EditorChat", version = "1.0" }
        }, null, cancellation);
        _protocol = initialized.GetProperty("protocolVersion").GetString();
        if (_protocol is not ("2025-03-26" or "2025-06-18" or "2025-11-25"))
            throw new InvalidDataException("Unsupported MCP protocol version.");
        await NotifyAsync("notifications/initialized", cancellation);
        var tools = new List<ChatTool>();
        string? cursor = null;
        for (var page = 0; page < 32; page++)
        {
            var result = await RpcAsync("tools/list", cursor is null ? new { } : (object)new { cursor },
                null, cancellation);
            foreach (var tool in result.GetProperty("tools").EnumerateArray())
            {
                var name = tool.GetProperty("name").GetString()!;
                if (!Allowed.Contains(name)) continue;
                tools.Add(new(name, tool.TryGetProperty("description", out var description)
                    ? description.GetString() ?? "" : "", tool.GetProperty("inputSchema").Clone()));
            }
            cursor = result.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
            if (string.IsNullOrEmpty(cursor)) break;
            if (page == 31) throw new InvalidDataException("MCP tool pagination limit exceeded.");
        }
        if (!tools.Any(tool => tool.Name == "libera_graph_read"))
            throw new InvalidDataException("Editor graph tools are unavailable.");
        Tools = tools.DistinctBy(tool => tool.Name, StringComparer.Ordinal).ToArray();
        Connected = true;
    }

    public async Task<string> CallAsync(ChatToolCall call, string context, CancellationToken cancellation)
    {
        if (!Connected || !Tools.Any(tool => tool.Name == call.Name) ||
            call.Arguments.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Unknown tool or invalid arguments.");
        try
        {
            var result = await RpcAsync("tools/call", new { name = call.Name, arguments = call.Arguments },
                context, cancellation);
            // Preserve isError and content verbatim, not a fabricated success.
            return result.GetRawText();
        }
        catch
        {
            Connected = false;
            throw;
        }
    }

    public async Task PingAsync(CancellationToken cancellation)
    {
        if (!Connected) throw new InvalidOperationException("MCP is disconnected.");
        await RpcAsync("ping", new { }, null, cancellation);
    }

    private HttpRequestMessage Request(object payload, string? context)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("X-LiberaFx-Token", _token);
        if (_protocol is not null) request.Headers.Add("MCP-Protocol-Version", _protocol);
        if (context is not null) request.Headers.Add(ChatMcpExecution.Header, context);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return request;
    }

    private async Task NotifyAsync(string method, CancellationToken cancellation)
    {
        using var request = Request(new { jsonrpc = "2.0", method }, null);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("MCP initialization was rejected.");
    }

    private async Task<JsonElement> RpcAsync(string method, object parameters, string? context,
        CancellationToken cancellation)
    {
        var id = Interlocked.Increment(ref _nextId);
        using var request = Request(new { jsonrpc = "2.0", id, method, @params = parameters }, context);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("MCP request rejected (" + (int)response.StatusCode + ").");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var text = await ChatHttpContent.ReadLimitedAsync(response.Content, 4 * 1024 * 1024, timeout.Token);
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            foreach (var block in text.Replace("\r\n", "\n").Split("\n\n"))
            {
                var data = string.Join("\n", block.Split('\n')
                    .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                    .Select(line => line[5..].TrimStart(' ')));
                if (data.Length == 0) continue;
                using var item = JsonDocument.Parse(data);
                if (item.RootElement.TryGetProperty("id", out var responseId) &&
                    responseId.ValueKind == JsonValueKind.Number && responseId.GetInt64() == id)
                    return Result(item.RootElement);
            }
            throw new InvalidDataException("MCP response did not contain the expected result.");
        }
        using var json = JsonDocument.Parse(text);
        if (!json.RootElement.TryGetProperty("id", out var actualId) ||
            actualId.ValueKind != JsonValueKind.Number || actualId.GetInt64() != id)
            throw new InvalidDataException("MCP response ID mismatch.");
        return Result(json.RootElement);
    }

    private static JsonElement Result(JsonElement envelope)
    {
        if (envelope.TryGetProperty("error", out _))
            throw new InvalidDataException("MCP rejected the request. No success can be inferred.");
        return envelope.GetProperty("result").Clone();
    }

    public void Dispose()
    {
        Connected = false;
        Tools = [];
        _http.Dispose();
    }
}

internal static class ChatHttpContent
{
    public static async Task<string> ReadLimitedAsync(HttpContent content, int maximumBytes,
        CancellationToken cancellation)
    {
        if (content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("Response too large.");
        await using var stream = await content.ReadAsStreamAsync(cancellation);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(), cancellation);
            if (count == 0) break;
            if (buffer.Length + count > maximumBytes) throw new InvalidDataException("Response too large.");
            await buffer.WriteAsync(bytes.AsMemory(0, count), cancellation);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
