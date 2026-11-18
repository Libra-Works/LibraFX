using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LiberaFx.Extensions.Abstractions;

namespace LiberaFx.Samples.CodexResponses;

public sealed class CodexResponsesExtension : IEditorExtension
{
    private readonly CodexResponsesAdapter _adapter = new();
    public void Register(IExtensionRegistration registration) => registration.AddChatAdapter(_adapter);
    public void Dispose() => _adapter.Dispose();
}

/// <summary>Responses API, not the Codex CLI/app-server. No agent tools are enabled.</summary>
public sealed class CodexResponsesAdapter : IChatAdapter, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly Func<string?> _credential;
    public string Id => "liberafx.sample.codex-responses.text";
    public string Name => "OpenAI Responses Codex model (text only / 会話専用)";
    public string Destination => "https://api.openai.com/v1/responses • " + _model;
    public string PrivacyNotice => "OpenAI Responses API, NOT Codex CLI or app-server. Text only; turn OFF graph sharing. " +
        "Prompt and history are sent to OpenAI under its API terms; API credentials and billing are required. " +
        "OpenAI Responses API であり Codex CLI 連携ではありません。会話専用、グラフ共有はオフにしてください。入力と履歴は OpenAI に送信されます。";

    public CodexResponsesAdapter() : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false },
        Environment.GetEnvironmentVariable("LIBERAFX_CODEX_MODEL") ?? "gpt-5.3-codex",
        () => Environment.GetEnvironmentVariable("OPENAI_API_KEY")) { }

    // Test transport injection does not alter the fixed production destination.
    public CodexResponsesAdapter(HttpMessageHandler handler, string model, Func<string?> credential)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Length > 128 ||
            model.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new ArgumentException("Invalid model ID.");
        _model = model;
        _credential = credential;
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
    }

    public async Task<ChatReply> SendAsync(ChatRequest request, CancellationToken cancellation)
    {
        if (request.Tools.Count != 0 || request.Messages.Count > 128 || request.Messages.Any(m =>
            m.Role is not ("user" or "assistant") || m.ToolCallId is not null || m.ToolCalls is { Count: > 0 }))
            throw new NotSupportedException("Text-only adapter: disable graph sharing.");
        var key = _credential();
        if (string.IsNullOrWhiteSpace(key) || key.Contains('\r') || key.Contains('\n'))
            throw new InvalidOperationException("API credential unavailable.");
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = _model, store = false, stream = false,
            input = request.Messages.Select(m => new { role = m.Role, content = m.Text }).ToArray()
        }), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Responses request rejected (" + (int)response.StatusCode + ").");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(bytes, timeout.Token);
            if (count == 0) break;
            if (buffer.Length + count > 1024 * 1024) throw new InvalidDataException("Response too large.");
            await buffer.WriteAsync(bytes.AsMemory(0, count), timeout.Token);
        }
        using var json = JsonDocument.Parse(buffer.ToArray());
        var root = json.RootElement;
        if (root.GetProperty("status").GetString() != "completed")
            throw new InvalidDataException("Response incomplete.");
        var output = new StringBuilder();
        foreach (var item in root.GetProperty("output").EnumerateArray())
        {
            var type = item.GetProperty("type").GetString();
            if (type == "reasoning") continue;
            if (type != "message") throw new InvalidDataException("Unsupported response item.");
            foreach (var part in item.GetProperty("content").EnumerateArray())
            {
                if (part.GetProperty("type").GetString() != "output_text")
                    throw new InvalidDataException("Response refused or unsupported.");
                output.Append(part.GetProperty("text").GetString());
            }
        }
        if (output.Length == 0) throw new InvalidDataException("No visible response.");
        return new ChatReply(output.ToString(), []);
    }

    public void Dispose() => _http.Dispose();
}
