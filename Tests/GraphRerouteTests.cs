using System.Text.Json;
using Avalonia.Threading;
using LiberaFx.Models.Commands;
using LiberaFx.Services.Mcp;
using LiberaFx.ViewModels.Graph;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;

internal static class GraphRerouteTests
{
    public static void Run()
    {
        Verify("OnStart", "then", "SpawnableGroup", "spawn", "Signal", GraphPortCategory.Signal);
        Verify("FloatX3", null, "AddVector3", "a", "FloatX3", GraphPortCategory.Parameter);
        VerifyMcp("OnStart", "then", "SpawnableGroup", "spawn", GraphPortCategory.Signal);
        VerifyMcp("FloatX3", null, "AddVector3", "a", GraphPortCategory.Parameter);
        Console.WriteLine("Signal, typed and MCP multihop reroute tests passed.");
    }

    private static void Verify(string sourceKind, string? output, string targetKind, string input,
        string expectedType, GraphPortCategory category)
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var source = graph.CreateNode(new GraphNodeKind(sourceKind));
        var target = graph.CreateNode(new GraphNodeKind(targetKind));
        Require(graph.TryConnectNodes(source, target, out _, input, output), "Original connection failed.");
        var original = graph.CreateSnapshot();
        var originalBinary = Compile(original, category);
        var first = graph.InsertRerouteOnConnection(graph.Connections.Single(), 200, 200);
        Require(first is not null, "First reroute was not inserted.");
        var second = graph.InsertRerouteOnConnection(graph.Connections.Single(edge => edge.Source == first), 400, 200);
        Require(second is not null, "Second reroute was not inserted.");
        Require(first!.Definition.PortCategory == category && second!.Definition.PortCategory == category &&
            first.Definition.PortType == expectedType && second.Definition.PortType == expectedType,
            "Reroute chain did not inherit endpoint type and category.");
        GraphValidator.Validate(graph.CreateSnapshot());
        Require(GraphTypeDiagnostics.Analyze(graph.CreateSnapshot()).Count == 0, "Unexpected type diagnostic.");
        Require(originalBinary.SequenceEqual(Compile(graph.CreateSnapshot(), category)),
            "Reroutes changed compiled behavior.");
        if (category == GraphPortCategory.Signal)
        {
            var extra = graph.CreateNode(new GraphNodeKind("SpawnableGroup"));
            Require(!graph.CanConnectNodes(second!, extra, out _, "spawn", "out"),
                "A rerouted Signal output must not branch.");
            Require(graph.History.Undo(), "Undo extra node failed.");
        }
        Require(graph.History.Undo() && graph.History.Undo() &&
            GraphSnapshot.ContentEquals(original, graph.CreateSnapshot()), "Undo did not restore original connection.");
        Require(graph.History.Redo() && graph.History.Redo(), "Redo did not restore reroutes.");
    }

    private static void VerifyMcp(string sourceKind, string? output, string targetKind, string input,
        GraphPortCategory category)
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var provider = new ActiveGraphProvider(graph);
        var source = graph.CreateNode(new GraphNodeKind(sourceKind));
        var target = graph.CreateNode(new GraphNodeKind(targetKind));
        Complete(LiberaFxMcpTools.ConnectNodes(provider, source.Id.ToString(), target.Id.ToString(), input, output));
        var original = graph.CreateSnapshot();
        var binary = Compile(original, category);
        var firstJson = Complete(LiberaFxMcpTools.InsertReroute(provider, source.Id.ToString(),
            target.Id.ToString(), 200, 200, output, input));
        using var firstDocument = JsonDocument.Parse(firstJson);
        var firstNode = firstDocument.RootElement;
        var firstId = firstNode.GetProperty("id").GetGuid();
        Require(firstNode.GetProperty("effectivePortCategory").GetString() == category.ToString() &&
            firstNode.GetProperty("effectivePortType").GetString() ==
                (category == GraphPortCategory.Signal ? "Signal" : "FloatX3"),
            "MCP inspection did not report the effective reroute type.");
        var secondJson = Complete(LiberaFxMcpTools.InsertReroute(provider, firstId.ToString(),
            target.Id.ToString(), 400, 200, "out", input));
        var secondId = JsonDocument.Parse(secondJson).RootElement.GetProperty("id").GetGuid();
        var read = Complete(LiberaFxMcpTools.GetGraph(provider));
        Require(read.Contains(firstId.ToString(), StringComparison.OrdinalIgnoreCase) &&
            read.Contains(secondId.ToString(), StringComparison.OrdinalIgnoreCase), "MCP graph read omitted reroutes.");
        GraphValidator.Validate(graph.CreateSnapshot());
        Require(binary.SequenceEqual(Compile(graph.CreateSnapshot(), category)), "MCP reroutes changed compilation.");
        var compiled = category == GraphPortCategory.Signal
            ? Complete(LiberaFxMcpTools.CompileSpawnable(provider))
            : Complete(LiberaFxMcpTools.CompileGraph(provider));
        Require(!string.IsNullOrWhiteSpace(compiled), "MCP compilation returned no result.");
        Complete(LiberaFxMcpTools.RemoveReroute(provider, firstId.ToString()));
        Require(graph.Connections.Any(edge => edge.Source.Id == source.Id && edge.Target.Id == secondId),
            "Removing a reroute did not reconnect the chain.");
        GraphValidator.Validate(graph.CreateSnapshot());
        Require(binary.SequenceEqual(Compile(graph.CreateSnapshot(), category)), "Removal changed compilation.");
        Require(graph.History.Undo() && graph.Nodes.Any(node => node.Id == firstId), "Undo removal failed.");
        Complete(LiberaFxMcpTools.RemoveReroute(provider, secondId.ToString()));
        Complete(LiberaFxMcpTools.RemoveReroute(provider, firstId.ToString()));
        Require(GraphSnapshot.ContentEquals(original, graph.CreateSnapshot()), "MCP removal did not restore original graph.");
        Require(binary.SequenceEqual(Compile(graph.CreateSnapshot(), category)), "Restored graph did not compile identically.");
        Require(graph.History.Undo() && graph.History.Undo(), "Undo removal chain failed.");
        Require(graph.History.Undo() && graph.History.Undo() &&
            GraphSnapshot.ContentEquals(original, graph.CreateSnapshot()), "MCP insertion undo failed.");
    }

    private static T Complete<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            using var timeout = DispatcherTimer.RunOnce(() => frame.Continue = false, TimeSpan.FromSeconds(10));
            _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
        }
        if (!task.IsCompleted)
        {
            throw new TimeoutException("MCP reroute operation timed out.");
        }
        return task.GetAwaiter().GetResult();
    }

    private static byte[] Compile(GraphSnapshot graph, GraphPortCategory category)
    {
        return category == GraphPortCategory.Signal
            ? SpawnableGroupPackage.Compile(graph).ParentBytecode
            : VfxProgram.Compile(graph).ToBinary();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
