using System.Net;
using System.Text;
using System.Text.Json;
using LiberaFx.Extensions.Abstractions;
using LiberaFx.Samples.CodexResponses;

var handler = new FakeTransport();
using var adapter = new CodexResponsesAdapter(handler, "synthetic-codex", () => "synthetic-key");
var answer = await adapter.SendAsync(new ChatRequest([new("user", "synthetic prompt")], []), default);
if (answer.Text != "synthetic response" || handler.Count != 1) throw new Exception("Text exchange failed.");
using var schema = JsonDocument.Parse("""{"type":"object"}""");
try
{
    await adapter.SendAsync(new ChatRequest([new("user", "prompt")], [new("edit", "edit", schema.RootElement.Clone())]), default);
    throw new Exception("Tools accepted by text-only adapter.");
}
catch (NotSupportedException) { }
if (handler.Count != 1) throw new Exception("Rejected request reached transport.");
handler.Reply = """{"status":"incomplete","output":[]}""";
try
{
    await adapter.SendAsync(new ChatRequest([new("user", "prompt")], []), default);
    throw new Exception("Incomplete output accepted.");
}
catch (InvalidDataException) { }
Console.WriteLine("Synthetic Responses checks passed.");

sealed class FakeTransport : HttpMessageHandler
{
    public int Count;
    public string Reply = """{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"synthetic response"}]}]}""";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        Count++;
        if (request.RequestUri?.AbsoluteUri != "https://api.openai.com/v1/responses")
            throw new Exception("Unexpected destination.");
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
        if (body.RootElement.GetProperty("store").GetBoolean() ||
            body.RootElement.TryGetProperty("tools", out _) ||
            body.RootElement.GetProperty("model").GetString() != "synthetic-codex")
            throw new Exception("Unsafe request.");
        return new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(Reply, Encoding.UTF8, "application/json") };
    }
}
