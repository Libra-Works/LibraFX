using System.Text.Json;
using Avalonia.Threading;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using ModelContextProtocol;

internal static class McpParameterTests
{
    public static void Run()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var untouched = graph.CreateNode(GraphNodeKind.Time);
        var node = graph.CreateNode(GraphNodeKind.Float);
        var definition = node.Definition.Parameters.Single(p => p.Id == "value");
        var original = node.GetParameterValue(definition).Clone();
        var provider = new ActiveGraphProvider(graph);
        var result = Complete(LiberaFxMcpTools.SetParameter(provider, node.Id.ToString("D"), "value", JsonSerializer.SerializeToElement(7)));
        using (var json = JsonDocument.Parse(result))
            Require(json.RootElement.GetProperty("id").GetGuid() == node.Id, "Setter must return the edited node.");
        Require(node.GetParameterValue(definition).GetDouble() == 7 && graph.Nodes.Contains(untouched),
            "Setter must preserve unrelated nodes and set a nonzero constant.");
        var edited = graph.CreateSnapshot();
        Reject(() => LiberaFxMcpTools.SetParameter(provider, node.Id.ToString("D"), "value", JsonSerializer.SerializeToElement("7")));
        Reject(() => LiberaFxMcpTools.SetParameter(provider, node.Id.ToString("D"), "missing", JsonSerializer.SerializeToElement(3)));
        Reject(() => LiberaFxMcpTools.SetParameter(provider, Guid.NewGuid().ToString("D"), "value", JsonSerializer.SerializeToElement(3)));
        Require(GraphSnapshot.ContentEquals(edited, graph.CreateSnapshot()), "Rejected edits must not mutate the graph.");
        Require(graph.History.Undo() &&
            JsonElement.DeepEquals(original, graph.Nodes.Single(item => item.Id == node.Id).GetParameterValue(definition)) &&
            graph.Nodes.Any(item => item.Id == untouched.Id), "Parameter edits must use exactly the existing undo mechanism.");
        Require(graph.History.Redo() &&
            graph.Nodes.Single(item => item.Id == node.Id).GetParameterValue(definition).GetDouble() == 7,
            "Parameter edits must be redoable.");
    }

    private static T Complete<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var registration = timeout.Token.Register(() => Dispatcher.UIThread.Post(() => frame.Continue = false));
            _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
        }
        if (!task.IsCompleted) throw new TimeoutException("MCP parameter edit timed out.");
        return task.GetAwaiter().GetResult();
    }

    private static void Reject(Func<Task<string>> action)
    {
        try { Complete(action()); }
        catch (McpException) { return; }
        throw new InvalidOperationException("An invalid parameter edit must be rejected as an MCP error.");
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
