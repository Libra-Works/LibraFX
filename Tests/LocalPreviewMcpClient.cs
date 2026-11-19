using System.Text;
using System.Text.Json;
using LiberaFx.Services;

internal sealed class LocalPreviewMcpClient : IDisposable
{
    private readonly McpHost _host;
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };
    private int _requestId;

    public LocalPreviewMcpClient(McpHost host)
    {
        _host = host;
        _http.DefaultRequestHeaders.Add("X-LiberaFx-Token", host.AccessToken);
        _http.DefaultRequestHeaders.Add("Accept", "application/json, text/event-stream");
        _http.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-03-26");
    }

    public async Task<JsonDocument> SendAsync(string method, object? parameters = null)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = ++_requestId,
            method,
            @params = parameters
        }), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(_host.ActualEndpoint, content);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        if (body.StartsWith("event:", StringComparison.Ordinal) || body.StartsWith("data:", StringComparison.Ordinal))
        {
            body = body.Split('\n').First(line => line.StartsWith("data:", StringComparison.Ordinal))[5..].Trim();
        }
        return JsonDocument.Parse(body);
    }

    public async Task<string> CallAsync(string name, object arguments)
    {
        using var response = await SendAsync("tools/call", new { name, arguments });
        var result = response.RootElement.GetProperty("result");
        if (result.TryGetProperty("isError", out var isError) && isError.GetBoolean())
        {
            throw new InvalidOperationException(result.GetProperty("content")[0].GetProperty("text").GetString());
        }
        return result.GetProperty("content")[0].GetProperty("text").GetString()
            ?? throw new InvalidDataException("MCP tool returned no text result.");
    }

    public void Dispose() => _http.Dispose();
}
