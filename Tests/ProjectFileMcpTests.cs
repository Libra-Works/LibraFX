using Avalonia.Threading;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using ModelContextProtocol;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

internal static class ProjectFileMcpTests
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "liberafx-project-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var first = new MainViewModel();
        var second = new MainViewModel();
        try
        {
            var path = Path.Combine(directory, "History.lfxproj");
            var graph = first.Graph;
            var node = graph.CreateNode(GraphNodeKind.Float);
            graph.SetParameter(node, "value", JsonSerializer.SerializeToElement(9d));
            Require(graph.History.Undo() && graph.History.CanRedo, "Fixture must include a redo branch.");
            var expected = graph.CreateSnapshot();
            Complete(Task.Run(() => ProjectFileMcpTools.Save(first, path)));
            Require(!first.ActiveDocument!.IsDirty && File.Exists(path),
                "Path save must work without any storage provider or file dialog.");
            var original = File.ReadAllBytes(path);
            Reject(() => ProjectFileMcpTools.Save(first, path));
            Require(original.SequenceEqual(File.ReadAllBytes(path)), "Refused overwrite changed the destination.");
            var initial = second.ActiveDocument!;
            var count = second.Documents.Count;
            Complete(Task.Run(() => ProjectFileMcpTools.Open(second, path)));
            var opened = second.ActiveDocument!;
            Require(second.Documents.Count == count + 1 && second.Documents.Contains(initial) &&
                GraphSnapshot.ContentEquals(expected, opened.Workspace.CreateSnapshot()) && opened.Workspace.History.CanRedo,
                "Opening must retain other documents and restore the saved graph and history.");
            Require(opened.Workspace.History.Redo(), "Opened history must support Redo.");
            var restoredNode = opened.Workspace.GetNode(node.Id);
            Require(restoredNode.GetParameterValue(restoredNode.Definition.Parameters.Single()).GetDouble() == 9,
                "Redo after path open lost the original value.");
            Complete(ProjectFileMcpTools.Open(second, path));
            Require(ReferenceEquals(opened, second.ActiveDocument) && opened.IsDirty &&
                second.Documents.Count == count + 1, "Reopening an open path must not discard edits or duplicate the tab.");
            Require(Complete(second.SaveActiveProjectAsync(false)) && !opened.IsDirty,
                "Normal Save after MCP Open must remain dialog-free and clear the saved dirty state.");
            second.NewDocumentCommand.Execute(null);
            var active = second.ActiveDocument;
            var before = File.ReadAllBytes(path);
            Reject(() => ProjectFileMcpTools.Save(second, path, overwrite: true));
            Require(ReferenceEquals(second.ActiveDocument, active) && before.SequenceEqual(File.ReadAllBytes(path)),
                "Save must not overwrite another open document's file.");
            var bad = Path.Combine(directory, "Invalid.lfxproj");
            File.WriteAllText(bad, "{}");
            count = second.Documents.Count;
            Reject(() => ProjectFileMcpTools.Open(second, bad));
            Require(second.Documents.Count == count && ReferenceEquals(second.ActiveDocument, active),
                "Rejected project input must not replace the active document.");
            Reject(() => ProjectFileMcpTools.Save(first, "relative.lfxproj"));
            Reject(() => ProjectFileMcpTools.Save(first, Path.Combine(directory, "wrong.json")));
            Reject(() => ProjectFileMcpTools.Open(first, @"\\server\share\project.lfxproj"));
            Reject(() => ProjectFileMcpTools.Open(first, "//server/share/project.lfxproj"));
            var unavailable = Path.Combine(directory, "Directory.lfxproj");
            Directory.CreateDirectory(unavailable);
            first.Graph.CreateNode(GraphNodeKind.Time);
            Reject(() => ProjectFileMcpTools.Save(first, unavailable, overwrite: true));
            Require(first.ActiveDocument!.IsDirty && before.SequenceEqual(File.ReadAllBytes(path)),
                "Failed writes must retain the dirty state and existing project files.");
            var denied = Path.Combine(directory, "ChatDenied.lfxproj");
            using (ChatMcpExecution.EnterRequest("graph-scoped-test"))
            {
                Reject(() => ProjectFileMcpTools.Save(first, denied));
                Reject(() => ProjectFileMcpTools.Open(first, path));
            }
            Require(!File.Exists(denied) && Directory.GetFiles(directory, ".liberafx-*.tmp").Length == 0,
                "Graph-scoped chat must not perform project I/O or leave temporary files.");
            VerifyTransport(first, directory, path);
            Console.WriteLine("Dialog-free MCP project paths, atomic overwrite guards, document preservation and history passed.");
        }
        finally
        {
            first.DisposeStorageFiles();
            second.DisposeStorageFiles();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyTransport(MainViewModel model, string directory, string source)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var host = new McpHost(((IPEndPoint)listener.LocalEndpoint).Port);
        listener.Stop();
        Task.Run(() => host.StartAsync(model.ActiveGraphProvider, documents: model)).GetAwaiter().GetResult();
        try
        {
            Complete(Task.Run(async () =>
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                client.DefaultRequestHeaders.Add("X-LiberaFx-Token", host.AccessToken);
                client.DefaultRequestHeaders.Add("Accept", "application/json, text/event-stream");
                client.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-03-26");
                var sequence = 0;
                async Task<JsonDocument> Send(string method, object? arguments = null)
                {
                    using var content = new StringContent(JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0", id = ++sequence, method, @params = arguments
                    }), Encoding.UTF8, "application/json");
                    using var response = await client.PostAsync(host.ActualEndpoint, content);
                    response.EnsureSuccessStatusCode();
                    var body = await response.Content.ReadAsStringAsync();
                    if (body.StartsWith("event:") || body.StartsWith("data:"))
                        body = body.Split('\n').First(line => line.StartsWith("data:"))[5..].Trim();
                    return JsonDocument.Parse(body);
                }
                static bool Failed(JsonDocument response) =>
                    response.RootElement.TryGetProperty("error", out _) ||
                    response.RootElement.GetProperty("result").TryGetProperty("isError", out var error) && error.GetBoolean();
                using var list = await Send("tools/list");
                var tools = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
                foreach (var name in new[] { "libera_graph_open_project", "libera_graph_save_project" })
                    Require(tools.Any(tool => tool.GetProperty("name").GetString() == name &&
                        !tool.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("documents", out _)),
                        "Project tools must be registered with document services injected, not exposed as arguments.");
                var path = Path.Combine(directory, "Http.lfxproj");
                using var saved = await Send("tools/call",
                    new { name = "libera_graph_save_project", arguments = new { path } });
                Require(!Failed(saved) && File.Exists(path), "Authenticated HTTP Save failed.");
                using var opened = await Send("tools/call",
                    new { name = "libera_graph_open_project", arguments = new { path = source } });
                Require(!Failed(opened), "Authenticated HTTP Open failed.");
                var denied = Path.Combine(directory, "HttpDenied.lfxproj");
                client.DefaultRequestHeaders.Add(ChatMcpExecution.Header, "graph-scoped-test");
                using var scoped = await Send("tools/call",
                    new { name = "libera_graph_save_project", arguments = new { path = denied } });
                Require(Failed(scoped) && !File.Exists(denied), "HTTP chat context escaped its graph capability.");
                return true;
            }));
        }
        finally { Task.Run(host.StopAsync).GetAwaiter().GetResult(); }
    }

    private static T Complete<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var registration = timeout.Token.Register(() => Dispatcher.UIThread.Post(() => frame.Continue = false));
            _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
        }
        if (!task.IsCompleted) throw new TimeoutException("Project MCP operation timed out.");
        return task.GetAwaiter().GetResult();
    }

    private static void Reject(Func<Task<string>> operation)
    {
        try { Complete(operation()); }
        catch (McpException) { return; }
        throw new InvalidOperationException("Unsafe or invalid project file I/O was accepted.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
