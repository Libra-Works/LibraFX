using System.Text.Json;
using LiberaFx.Models.Commands;
using LiberaFx.Services.Extensions;
using LiberaFx.Services.Mcp;
using LiberaFx.ViewModels.Graph;
using ModelContextProtocol;

/// <summary>Source regressions; no model, HTTP service, keys or settings required. Run on initialized UI thread.</summary>
internal static class ChatSourceTests
{
    public static async Task RunAsync()
    {
        using var reply = JsonDocument.Parse("""
            {"choices":[{"finish_reason":"tool_calls","message":{"content":null,"tool_calls":[
              {"id":"one","type":"function","function":{"name":"libera_graph_read","arguments":"{}"}}
            ]}}]}
            """);
        var parsed = OpenAiCompatibleChatAdapter.ParseReply(reply.RootElement);
        if (parsed.ToolCalls.Single().Name != "libera_graph_read" ||
            parsed.ToolCalls[0].Arguments.ValueKind != JsonValueKind.Object)
            throw new Exception("OpenAI function calls were not preserved.");
        using var duplicate = JsonDocument.Parse("""
            {"choices":[{"message":{"content":"","tool_calls":[
              {"id":"same","type":"function","function":{"name":"a","arguments":"{}"}},
              {"id":"same","type":"function","function":{"name":"b","arguments":"{}"}}
            ]}}]}
            """);
        Reject(() => OpenAiCompatibleChatAdapter.ParseReply(duplicate.RootElement));
        using var truncated = JsonDocument.Parse("""
            {"choices":[{"finish_reason":"length","message":{"content":"partial"}}]}
            """);
        Reject(() => OpenAiCompatibleChatAdapter.ParseReply(truncated.RootElement));
        Reject(() => new ChatMcpClient("https://example.com/mcp", "not-a-key"));
        Reject(() => new ChatMcpClient("http://127.0.0.1:8765/mcp", ""));
        Reject(() => new OpenAiCompatibleChatAdapter("http://example.com/v1/chat/completions", "model", ""));
        Reject(() => new OpenAiCompatibleChatAdapter("https://key@example.com/v1/chat/completions", "model", ""));
        if (!ChatMcpClient.NeedsConfirmation("libera_graph_enter") ||
            !ChatMcpClient.NeedsConfirmation("libera_graph_create_node") ||
            ChatMcpClient.NeedsConfirmation("libera_graph_read"))
            throw new Exception("Chat must confirm all graph-changing/navigation tools.");

        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var graphs = new ActiveGraphProvider(graph);
        using (var context = ChatMcpExecution.Capture(graphs))
        using (ChatMcpExecution.EnterRequest(context.Token))
        {
            await LiberaFxMcpTools.CreateNode(graphs, "Float", 100, 100);
            context.Validate(); // Its own validated edit advances only its own context.
            if (graph.Nodes.Count != 1 || !graph.History.CanUndo)
                throw new Exception("Guarded tools must retain the existing graph/history path.");
            graph.History.Undo();
            await RejectAsync(() => LiberaFxMcpTools.CreateNode(graphs, "Float", 200, 200));
            if (graph.Nodes.Count != 0) throw new Exception("Stale history context performed an edit.");
        }
        using (var context = ChatMcpExecution.Capture(graphs))
        using (ChatMcpExecution.EnterRequest(context.Token))
        {
            graphs.SetGraph(new GraphWorkspaceViewModel(new UndoRedoManager()));
            await RejectAsync(() => LiberaFxMcpTools.CreateNode(graphs, "Float", 200, 200));
            if (graphs.Graph.Nodes.Count != 0) throw new Exception("Tool edited a different document.");
        }
        using (var context = ChatMcpExecution.Capture(graphs))
        using (ChatMcpExecution.EnterRequest(context.Token))
        {
            context.Revoke();
            await RejectAsync(() => LiberaFxMcpTools.CreateNode(graphs, "Float", 200, 200));
        }
        using (ChatMcpExecution.EnterRequest("unknown-chat-context"))
            await RejectAsync(() => LiberaFxMcpTools.GetGraph(graphs));
        await ChatUi47Tests.RunAsync();
        await CrashUi48Tests.RunAsync();
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { return; }
        throw new Exception("Invalid chat input was accepted.");
    }
    private static async Task RejectAsync(Func<Task<string>> action)
    {
        try { await action(); }
        catch (McpException) { return; }
        throw new Exception("Expired chat capability was accepted.");
    }
}
