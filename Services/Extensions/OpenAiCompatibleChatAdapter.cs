using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LiberaFx.Extensions.Abstractions;
using LiberaFx.Services.Mcp;
using LiberaFx.Services.Settings;

namespace LiberaFx.Services.Extensions;

/// <summary>Only the documented Chat Completions function-tool protocol, not arbitrary CLI protocols.</summary>
public sealed class OpenAiCompatibleChatAdapter : IChatAdapter, IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _model;
    private string _apiKey;
    public string Id => "liberafx.chat.openai-compatible";
    public string Name => LocalizationService.Shared.Get("Chat.Compatible");
    public string Destination => _endpoint.AbsoluteUri + " • " + _model;
    public string PrivacyNotice => LocalizationService.Shared.Get("Chat.CompatiblePrivacy");

    public OpenAiCompatibleChatAdapter(string endpoint, string model, string apiKey)
    {
        _endpoint = new Uri(endpoint, UriKind.Absolute);
        var local = IPAddress.TryParse(_endpoint.Host, out var address) && IPAddress.IsLoopback(address) ||
                    _endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        if ((_endpoint.Scheme != "https" && !(_endpoint.Scheme == "http" && local)) ||
            _endpoint.UserInfo.Length != 0 || _endpoint.Query.Length != 0 || _endpoint.Fragment.Length != 0 ||
            !_endpoint.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal))
            throw new ArgumentException("Use an HTTPS Chat Completions endpoint, or loopback HTTP; no credentials in URLs.");
        if (string.IsNullOrWhiteSpace(model) || model.Length > 256)
            throw new ArgumentException("A model ID is required.");
        if (apiKey.Contains('\r') || apiKey.Contains('\n')) throw new ArgumentException("Invalid API key.");
        _model = model;
        _apiKey = apiKey;
        _http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false
        }) { Timeout = TimeSpan.FromMinutes(2) };
    }

    public async Task<ChatReply> SendAsync(ChatRequest request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var messages = request.Messages.Select(message =>
        {
            var value = new Dictionary<string, object?> { ["role"] = message.Role, ["content"] = message.Text };
            if (message.Role is not ("system" or "user" or "assistant" or "tool"))
                throw new InvalidDataException("Unsupported chat role.");
            if (message.ToolCallId is not null) value["tool_call_id"] = message.ToolCallId;
            if (message.ToolCalls is { Count: > 0 })
                value["tool_calls"] = message.ToolCalls.Select(call => new
                {
                    id = call.Id, type = "function",
                    function = new { name = call.Name, arguments = call.Arguments.GetRawText() }
                }).ToArray();
            return value;
        }).ToArray();
        var body = new Dictionary<string, object?>
        {
            ["model"] = _model, ["messages"] = messages, ["stream"] = false
        };
        if (request.Tools.Count > 0)
            body["tools"] = request.Tools.Select(tool => new
            {
                type = "function",
                function = new { name = tool.Name, description = tool.Description, parameters = tool.InputSchema }
            }).ToArray();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        if (_apiKey.Length > 0) httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Chat provider rejected the request (" + (int)response.StatusCode + ").");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var json = JsonDocument.Parse(await ChatHttpContent.ReadLimitedAsync(
            response.Content, 1024 * 1024, timeout.Token));
        return ParseReply(json.RootElement);
    }

    public static ChatReply ParseReply(JsonElement root)
    {
        var choice = root.GetProperty("choices")[0];
        var message = choice.GetProperty("message");
        var text = message.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null
            ? content.GetString() ?? "" : "";
        var calls = new List<ChatToolCall>();
        if (message.TryGetProperty("tool_calls", out var tools))
        {
            if (tools.GetArrayLength() > 16) throw new InvalidDataException("Too many tool calls.");
            foreach (var call in tools.EnumerateArray())
            {
                if (call.GetProperty("type").GetString() != "function")
                    throw new InvalidDataException("Unsupported tool call type.");
                var function = call.GetProperty("function");
                using var args = JsonDocument.Parse(function.GetProperty("arguments").GetString()!);
                var id = call.GetProperty("id").GetString();
                var name = function.GetProperty("name").GetString();
                if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || string.IsNullOrWhiteSpace(name) ||
                    args.RootElement.ValueKind != JsonValueKind.Object || calls.Any(item => item.Id == id))
                    throw new InvalidDataException("Invalid function call.");
                calls.Add(new(id, name, args.RootElement.Clone()));
            }
        }
        if (choice.TryGetProperty("finish_reason", out var reason) &&
            reason.GetString() is "length" or "content_filter")
            throw new InvalidDataException("Provider response is incomplete or filtered.");
        if (text.Length == 0 && calls.Count == 0) throw new InvalidDataException("Provider returned no reply.");
        return new(text, calls);
    }

    public void Dispose()
    {
        _apiKey = "";
        _http.Dispose();
    }
}
