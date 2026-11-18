using System.Text;
using System.Text.Json;
using LiberaFx.Extensions.Abstractions;

namespace LiberaFx.Samples;

/// <summary>Separate DLL, referencing ONLY the public extension SDK; no editor/graph access.</summary>
public sealed class ChatExtension : IEditorExtension
{
    private readonly GeminiTextAdapter _adapter = new();
    public void Register(IExtensionRegistration registration) => registration.AddChatAdapter(_adapter);
    public void Dispose() => _adapter.Dispose();
}

public sealed class GeminiTextAdapter : IChatAdapter, IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false
    }) { Timeout = TimeSpan.FromMinutes(2) };
    private readonly string _model = Environment.GetEnvironmentVariable("LIBERAFX_GEMINI_MODEL") ?? "gemini-2.5-flash";
    public string Id => "liberafx.sample.gemini-text.adapter";
    public string Name => "Gemini REST (text only / 会話専用)";
    public string Destination => "https://generativelanguage.googleapis.com • " + _model;
    public string PrivacyNotice =>
        "Text-only sample: turn OFF graph sharing. Uses GEMINI_API_KEY from the editor process environment. " +
        "Prompts/history go to Google's Gemini API; Google's retention/usage policy applies. " +
        "Not Gemini CLI; no filesystem access, agent tools or graph editing. " +
        "会話専用の例です。グラフ共有はオフにしてください。入力と履歴を Google に送信します。CLI 連携・グラフ編集は未対応です。";

    public async Task<ChatReply> SendAsync(ChatRequest request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (request.Tools.Count != 0 || request.Messages.Any(message =>
                message.Role is not ("user" or "assistant") || message.ToolCalls is { Count: > 0 }))
            throw new NotSupportedException("This sample is text-only. Disable graph sharing.");
        if (_model.Length is < 1 or > 128 || _model.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
            throw new InvalidOperationException("Invalid Gemini model ID.");
        var key = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(key) || key.Contains('\r') || key.Contains('\n'))
            throw new InvalidOperationException("GEMINI_API_KEY is missing or invalid.");
        var uri = "https://generativelanguage.googleapis.com/v1beta/models/" + _model + ":generateContent";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri);
        httpRequest.Headers.Add("x-goog-api-key", key);
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(new
        {
            contents = request.Messages.Select(message => new
            {
                role = message.Role == "assistant" ? "model" : "user",
                parts = new[] { new { text = message.Text } }
            }).ToArray()
        }), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Gemini request rejected (" + (int)response.StatusCode + ").");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(), timeout.Token);
            if (count == 0) break;
            if (buffer.Length + count > 1024 * 1024) throw new InvalidDataException("Response too large.");
            await buffer.WriteAsync(bytes.AsMemory(0, count), timeout.Token);
        }
        using var json = JsonDocument.Parse(buffer.ToArray());
        var candidate = json.RootElement.GetProperty("candidates")[0];
        if (candidate.GetProperty("finishReason").GetString() != "STOP")
            throw new InvalidDataException("Gemini response incomplete or blocked.");
        var parts = candidate.GetProperty("content").GetProperty("parts");
        if (parts.EnumerateArray().Any(part => part.TryGetProperty("functionCall", out _)))
            throw new NotSupportedException("Function calls are not supported by this text-only sample.");
        var answer = string.Concat(parts.EnumerateArray()
            .Where(part => !part.TryGetProperty("thought", out var thought) || thought.ValueKind != JsonValueKind.True)
            .Where(part => part.TryGetProperty("text", out _)).Select(part => part.GetProperty("text").GetString()));
        if (string.IsNullOrWhiteSpace(answer)) throw new InvalidDataException("Gemini returned no visible text.");
        return new(answer, []);
    }

    public void Dispose() => _http.Dispose();
}
