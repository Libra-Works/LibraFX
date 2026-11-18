using System.Reflection;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Threading;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Commands;
using LiberaFx.Extensions.Abstractions;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using LiberaFx.Views;

internal static class ChatGraphReferenceTests
{
    public static void Run()
    {
        var main = new MainViewModel();
        var document = main.ActiveDocument!;
        var graph = main.Graph;
        var group = graph.CreateNode(GraphNodeKind.GraphGroup);
        Require(graph.EnterGraph(group), "Could not enter reference fixture group.");
        var firstChild = graph.CreateNode(GraphNodeKind.Float);
        graph.NavigateToGraph(0);
        var second = graph.CreateNode(GraphNodeKind.GraphGroup);
        Require(graph.EnterGraph(second), "Could not enter the second group.");
        var child = graph.CreateNode(GraphNodeKind.Float);
        graph.NavigateToNode([group.Id], firstChild.Id);
        var otherInstancePreview = graph.PreviewLayout([firstChild.Id], originX: 777);
        graph.NavigateToNode([second.Id], child.Id);
        var rejected = false;
        try { graph.ApplyLayout(otherInstancePreview.PreviewId); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "A layout preview must not apply to a different group scope.");
        graph.NavigateToGraph(0);
        var link = $"[Explain this node](liberafx-node:/{second.Id:D}/{child.Id:D})";
        var references = ChatGraphReferences.Read(link, document, []);
        Require(references.Count == 1 && references[0].ParentPath.SequenceEqual([second.Id]) &&
            ChatGraphReferences.DisplayText(link) == "Explain this node",
            "References must keep the group path while displaying readable labels.");
        var reference = references.Single();
        var before = graph.CreateSnapshot();
        var revision = graph.History.CurrentNodeId;
        var framed = 0;
        graph.FrameSelectionRequested += (_, _) => framed++;
        using var window = new ChatWindow(main);
        var host = new Window { Content = window, Width = 820, Height = 820 };
        host.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Field<CheckBox>(window, "_share").IsChecked = true;
            var messages = Field<List<ChatMessage>>(window, "_messages");
            messages.Add(new("system", "Old selected node context"));
            messages.Add(new("user", "Explain how this value works."));
            messages.Add(new("assistant", "", ToolCalls:
                [new("old-read", "libera_graph_read", System.Text.Json.JsonSerializer.SerializeToElement(new { }))]));
            messages.Add(new("tool", "Old graph snapshot", "old-read"));
            messages.Add(new("assistant", "This value controls the movement."));
            var oldContext = ChatMcpExecution.Capture(main.ActiveGraphProvider);
            typeof(ChatWindow).GetField("_conversationContext", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, oldContext);
            typeof(ChatWindow).GetMethod("ShowNodeReferences", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [link]);
            var buttons = Field<WrapPanel>(window, "_nodeReferences");
            Invoke((Button)buttons.Children.Single());
            Dispatcher.UIThread.RunJobs();
            Require(graph.CurrentGraphPath.SequenceEqual([second.Id]) && graph.SelectedNode?.Id == child.Id &&
                framed > 0 && graph.History.CurrentNodeId == revision &&
                GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()),
                "Reference navigation must select/frame the correct group instance without editing history.");
            Require(messages.Any(message => message.Role == "user" && message.Text == "Explain how this value works.") &&
                messages.Any(message => message.Role == "assistant" && message.Text == "This value controls the movement.") &&
                messages.All(message => message.Role != "tool" && message.ToolCalls is null &&
                    !message.Text.Contains("Old graph snapshot") && !message.Text.Contains("Old selected node context")),
                "Navigation must preserve explanations without carrying stale tool cycles or selection instructions.");
            var expired = false;
            try { oldContext.Validate(); }
            catch (ModelContextProtocol.McpException) { expired = true; }
            Require(expired, "Navigation must revoke the old editing capability.");
            Field<ChatMcpExecution.Lease>(window, "_conversationContext").Validate();
            var rootLink = $"[Root group](liberafx-node:/{group.Id:D})";
            var rootReference = ChatGraphReferences.Read(rootLink, document, [second.Id]).Single();
            Require(rootReference.ParentPath.Count == 0, "Absolute root references must not bind to a child scope.");
            main.NewDocumentCommand.Execute(null);
            var otherGraph = main.Graph;
            otherGraph.History.Execute(new ReplaceGraphSnapshotCommand(otherGraph, otherGraph.CreateSnapshot(), before));
            Require(ChatGraphReferences.Navigate(main, reference) && ReferenceEquals(main.ActiveDocument, document),
                "A reference must activate its original document, not a same-ID node in another document.");
            graph.DeleteNode(graph.GetNode(child.Id));
            var retainedConversation = messages.ToArray();
            var retainedContext = Field<ChatMcpExecution.Lease>(window, "_conversationContext");
            Invoke((Button)buttons.Children.Single());
            Dispatcher.UIThread.RunJobs();
            Require(messages.SequenceEqual(retainedConversation) &&
                ReferenceEquals(retainedContext, Field<ChatMcpExecution.Lease>(window, "_conversationContext")),
                "Unavailable references must not discard conversation or renew an invalid editing capability.");
            Require(!ChatGraphReferences.Navigate(main, reference) &&
                !ChatGraphReferences.Navigate(main, reference with { DocumentId = "closed-document" }),
                "Deleted/closed targets must not navigate to another node with the same name.");
            Require(ChatGraphReferences.Read($"[Unknown](liberafx-node:{Guid.NewGuid():D})", document, []).Count == 0 &&
                ChatGraphReferences.Read("[External](https://example.invalid)", document, []).Count == 0,
                "Only locally resolved graph references can become navigation controls.");
        }
        finally
        {
            host.Close();
            main.DisposeStorageFiles();
        }
        Console.WriteLine("Chat reference scope, document identity, deleted targets and transient UI navigation passed.");
    }

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static void Invoke(Button button) =>
        new ButtonAutomationPeer(button).GetProvider<IInvokeProvider>()!.Invoke();
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
