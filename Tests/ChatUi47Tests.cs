using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using LiberaFx.Extensions.Abstractions;
using LiberaFx.Services.Mcp;
using LiberaFx.ViewModels.Graph;
using LiberaFx.ViewModels.Main;
using LiberaFx.Views.Windows;

internal static class ChatUi47Tests
{
    public static async Task RunAsync()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var host = new McpHost(port);
        var main = new MainViewModel(host.AccessToken, mcpEndpoint: host.ActualEndpoint);
        var adapter = new SyntheticAdapter();
        main.Extensions.RegisterBuiltIn(adapter);
        using var window = new ChatWindow(main);
        var chatHost = new Window { Content = window, Width = 820, Height = 820 };
        try
        {
            await host.StartAsync(main.ActiveGraphProvider);
            using (var unauthorized = new ChatMcpClient(host.ActualEndpoint, "synthetic-invalid"))
            {
                try
                {
                    await unauthorized.ConnectAsync(CancellationToken.None);
                    throw new Exception("Unauthenticated MCP connected.");
                }
                catch (HttpRequestException) { }
            }
            chatHost.Show();
            await Task.Delay(250); // Allow initial control bindings/layout to settle.
            var input = Field<TextBox>(window, "_input");
            var send = Field<Button>(window, "_send");
            var connect = Field<Button>(window, "_connect");
            var disconnect = Field<Button>(window, "_disconnect");
            if (input.IsEnabled || send.IsEnabled) throw new Exception("Offline chat was enabled.");
            var destination = Field<TextBlock>(window, "_destination");
            if (!destination.IsEffectivelyVisible || destination.Text?.Contains("127.0.0.1:1234") != true)
            {
                throw new Exception("The compatible endpoint must be visible with settings collapsed.");
            }
            Field<ComboBox>(window, "_adapters").SelectedIndex = 1;
            if (!destination.IsEffectivelyVisible || destination.Text?.Contains(adapter.Destination) != true ||
                Field<TextBlock>(window, "_privacyDetails").Text?.Contains(adapter.PrivacyNotice) != true)
            {
                throw new Exception("The selected adapter must show its destination and privacy notice.");
            }
            adapter.Mode = "continue";
            Invoke(connect);
            await WaitAsync(() => input.IsEnabled, "MCP connection without sharing consent");
            if (adapter.Calls != 0)
            {
                throw new Exception("Connect sent a provider request before Send.");
            }
            input.Text = "Say hello without project data";
            Invoke(send);
            await WaitAsync(() => adapter.Continuation is not null && input.IsEnabled, "Chat without sharing consent");
            var privateRequest = adapter.Continuation!;
            if (privateRequest.Tools.Count != 0 || privateRequest.Messages.Count != 1 ||
                privateRequest.Messages[0] is not { Role: "user", Text: "Say hello without project data" })
            {
                throw new Exception("The provider received project context without consent.");
            }
            Invoke(disconnect);
            adapter.Continuation = null;
            adapter.Mode = "edit";
            Field<CheckBox>(window, "_share").IsChecked = true;
            await Task.Delay(100);
            var initialCount = main.Graph.Nodes.Count;
            Invoke(connect);
            try { await WaitAsync(() => input.IsEnabled, "MCP connection"); }
            catch (TimeoutException)
            {
                throw new TimeoutException("MCP connection: " + Field<TextBlock>(window, "_status").Text);
            }
            input.Text = "Create a synthetic node";
            Invoke(send);
            await WaitAsync(() => Field<Window?>(window, "_confirmation") is not null, "Edit approval");
            var dialog = Field<Window>(window, "_confirmation");
            var grid = (Grid)dialog.Content!;
            var buttons = (StackPanel)grid.Children[2];
            Invoke((Button)buttons.Children[1]);
            await WaitAsync(() => main.Graph.Nodes.Count == initialCount + 1 && input.IsEnabled, "Approved edit");
            if (!main.Graph.History.CanUndo) throw new Exception("MCP edit lacks undo.");
            main.Graph.History.Undo();
            if (main.Graph.Nodes.Count != initialCount) throw new Exception("Undo failed.");
            main.Graph.History.Redo();
            if (main.Graph.Nodes.Count != initialCount + 1) throw new Exception("Redo failed.");
            var referenceButtons = Field<WrapPanel>(window, "_nodeReferences");
            if (referenceButtons.Children.Count != 1) throw new Exception("Reply did not expose a readable node reference.");
            var referenceRevision = main.Graph.History.CurrentNodeId;
            Invoke((Button)referenceButtons.Children.Single());
            await WaitAsync(() => main.Graph.SelectedNode?.Definition.Kind == LiberaFx.Models.Graph.GraphNodeKind.Float,
                "Chat node navigation");
            if (main.Graph.History.CurrentNodeId != referenceRevision)
                throw new Exception("Chat navigation edited history.");
            adapter.Mode = "continue";
            input.Text = "What happens if I change that value?";
            Invoke(send);
            await WaitAsync(() => input.IsEnabled, "Conversation after navigation");
            var continuation = adapter.Continuation ??
                throw new Exception("Navigation did not deliver the follow-up conversation.");
            if (!continuation.Messages.Any(message => message.Role == "user" &&
                    message.Text == "Create a synthetic node") ||
                !continuation.Messages.Any(message => message.Role == "assistant" &&
                    message.Text.Contains("Synthetic edit complete")) ||
                continuation.Messages.Any(message => message.Role == "tool" || message.ToolCalls is not null) ||
                !continuation.Messages.Any(message => message.Role == "system" &&
                    message.Text.Contains(main.Graph.SelectedNode!.Id.ToString("D")) &&
                    message.Text.Contains("graphRead") && message.Text.Contains("selectedNodeIds")))
                throw new Exception("Follow-up lost the explanation or reused stale tool context without a fresh graph read.");
            adapter.Mode = "edit";
            var callsBeforeSelection = adapter.Calls;
            Invoke(Field<Button>(window, "_explainSelection"));
            await WaitAsync(() => (input.Text ?? "").Contains(main.Graph.SelectedNode!.Name), "Selection question draft");
            if (adapter.Calls != callsBeforeSelection) throw new Exception("Selection question sent data before Send.");
            Invoke(disconnect);
            if (input.IsEnabled || send.IsEnabled) throw new Exception("Disconnected chat stayed enabled.");
            Field<CheckBox>(window, "_share").IsChecked = true;
            Invoke(connect);
            await WaitAsync(() => input.IsEnabled, "MCP reconnect");
            input.Text = "Reject this synthetic edit";
            Invoke(send);
            await WaitAsync(() => Field<Window?>(window, "_confirmation") is not null, "Reject approval");
            dialog = Field<Window>(window, "_confirmation");
            grid = (Grid)dialog.Content!;
            buttons = (StackPanel)grid.Children[2];
            Invoke((Button)buttons.Children[0]);
            await WaitAsync(() => input.IsEnabled, "Rejected edit");
            if (main.Graph.Nodes.Count != initialCount + 1) throw new Exception("Rejected edit changed graph.");
            Invoke(disconnect);
            adapter.Mode = "invalid";
            Field<CheckBox>(window, "_share").IsChecked = true;
            Invoke(connect);
            await WaitAsync(() => input.IsEnabled, "MCP reconnect for invalid tool");
            input.Text = "Synthetic invalid tool";
            Invoke(send);
            await WaitAsync(() => !input.IsEnabled && connect.IsEnabled, "Invalid tool rejected");
            if (main.Graph.Nodes.Count != initialCount + 1) throw new Exception("Invalid tool changed graph.");

            adapter.Mode = "wait";
            Field<CheckBox>(window, "_share").IsChecked = true;
            Invoke(connect);
            await WaitAsync(() => input.IsEnabled, "MCP reconnect for cancellation");
            input.Text = "Synthetic cancellation";
            Invoke(send);
            var cancel = Field<Button>(window, "_cancel");
            await WaitAsync(() => cancel.IsEnabled, "Pending synthetic response");
            Invoke(cancel);
            await WaitAsync(() => !input.IsEnabled && connect.IsEnabled, "Cancellation");
            if (main.Graph.Nodes.Count != initialCount + 1) throw new Exception("Cancelled turn changed graph.");
        }
        finally
        {
            chatHost.Close();
            await host.StopAsync();
            main.Extensions.Dispose();
        }
    }

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static void Invoke(Button button) =>
        new ButtonAutomationPeer(button).GetProvider<IInvokeProvider>()!.Invoke();
    private static async Task WaitAsync(Func<bool> predicate, string step)
    {
        for (var i = 0; i < 400; i++)
        {
            if (predicate()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException(step);
    }

    private sealed class SyntheticAdapter : IChatAdapter
    {
        public string Id => "synthetic.chat.ui";
        public string Name => "Synthetic offline";
        public string Destination => "Synthetic in-process";
        public string PrivacyNotice => "No external service.";
        public string Mode = "edit";
        public int Calls;
        public ChatRequest? Continuation;
        public async Task<ChatReply> SendAsync(ChatRequest request, CancellationToken cancellation)
        {
            Calls++;
            if (Mode == "continue")
            {
                Continuation = request;
                return new ChatReply("Synthetic explanation continued with the current graph.", []);
            }
            if (Mode == "wait") await Task.Delay(Timeout.Infinite, cancellation);
            if (request.Messages.Any(m => m.Role == "tool"))
            {
                using var result = JsonDocument.Parse(request.Messages.Last(m => m.Role == "tool").Text);
                var content = result.RootElement.GetProperty("content")[0].GetProperty("text").GetString()!;
                using var node = JsonDocument.Parse(content);
                var id = node.RootElement.GetProperty("id").GetGuid();
                return new ChatReply($"Synthetic edit complete: [Created Float](liberafx-node:{id:D})", []);
            }
            using var json = JsonDocument.Parse("""{"kind":"Float","x":100,"y":100}""");
            return new ChatReply("", [new("synthetic-call", Mode == "invalid" ? "untrusted_tool" : "libera_graph_create_node", json.RootElement.Clone())]);
        }
    }
}
