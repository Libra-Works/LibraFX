using Avalonia.Threading;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Mcp;
using LiberaFx.ViewModels.Graph;
using LiberaFx.ViewModels.Main;
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
                using var initialized = await Send("initialize", new
                {
                    protocolVersion = "2025-03-26", capabilities = new { },
                    clientInfo = new { name = "LiberaFx wire regression", version = "1" }
                });
                Require(!Failed(initialized), "MCP initialize failed.");
                using var list = await Send("tools/list");
                var tools = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
                foreach (var name in new[] { "libera_graph_open_project", "libera_graph_save_project" })
                    Require(tools.Any(tool => tool.GetProperty("name").GetString() == name &&
                        !tool.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("documents", out _)),
                        "Project tools must be registered with document services injected, not exposed as arguments.");
                var gpuPlan = tools.Single(tool => tool.GetProperty("name").GetString() == "gpu_apply_plan");
                Require(gpuPlan.GetProperty("inputSchema").GetProperty("properties").GetProperty("editsJson")
                        .GetProperty("description").GetString()!.Contains("editSchema", StringComparison.Ordinal),
                    "The GPU edit argument must point clients to the wire schema.");
                var path = Path.Combine(directory, "Http.lfxproj");
                using var saved = await Send("tools/call",
                    new { name = "libera_graph_save_project", arguments = new { path } });
                Require(!Failed(saved) && File.Exists(path), "Authenticated HTTP Save failed.");
                using var opened = await Send("tools/call",
                    new { name = "libera_graph_open_project", arguments = new { path = source } });
                Require(!Failed(opened), "Authenticated HTTP Open failed.");

                async Task<JsonDocument> CallGraph(string tool, object? arguments = null)
                {
                    using var response = await Send("tools/call", new { name = "libera_graph_" + tool, arguments = arguments ?? new { } });
                    Require(!Failed(response), $"Graph MCP tool failed: {tool}");
                    return JsonDocument.Parse(response.RootElement.GetProperty("result").GetProperty("content")[0]
                        .GetProperty("text").GetString()!);
                }

                using var summary = await CallGraph("list_node_types", new { limit = 5 });
                using var next = await CallGraph("list_node_types", new { limit = 5, offset = 5 });
                Require(summary.RootElement.GetArrayLength() == 5 && !summary.RootElement[0].TryGetProperty("inputPorts", out _) &&
                    !summary.RootElement.EnumerateArray().Any(row => next.RootElement.EnumerateArray().Any(other =>
                        other.GetProperty("kind").GetString() == row.GetProperty("kind").GetString())),
                    "Catalog discovery must return bounded, non-overlapping summary pages.");

                using var details = await CallGraph("list_node_types",
                    new { summaryOnly = false, kinds = new[] { "FloatFCurve", "Comment", "GroupOutputFloat", "LoopClock" } });
                JsonElement Type(string kind) => details.RootElement.EnumerateArray().Single(row => row.GetProperty("kind").GetString() == kind);
                Require(Type("FloatFCurve").GetProperty("outputPorts")[0].GetProperty("id").ValueKind == JsonValueKind.Null &&
                    Type("Comment").GetProperty("outputPorts").GetArrayLength() == 0 &&
                    Type("GroupOutputFloat").GetProperty("inputPorts").GetArrayLength() == 1 &&
                    !string.IsNullOrEmpty(Type("LoopClock").GetProperty("description").GetString()),
                    "Node discovery must distinguish default pins, absent pins and resolved help.");
                using var enums = await CallGraph("list_enums", new { query = "TransformOrder" });
                Require(enums.RootElement[0].GetProperty("options").EnumerateArray()
                        .Any(option => option.GetProperty("value").ValueKind == JsonValueKind.Number &&
                            !string.IsNullOrEmpty(option.GetProperty("name").GetString())),
                    "Enum discovery must return named integer options.");
                using var comparisonTypes = await CallGraph("list_node_types", new { query = "Compare" });
                var compareKind = comparisonTypes.RootElement.EnumerateArray()
                    .Single(row => row.GetProperty("typeName").GetString() == "Compare").GetProperty("kind").GetString()!;
                Require(Guid.TryParse(compareKind, out _), "The Compare regression requires a UUID-based kind.");
                using var compare = await CallGraph("create_node", new { kind = compareKind });
                var compareId = compare.RootElement.GetProperty("id").GetGuid();
                using var renamed = await CallGraph("rename_node", new { nodeId = compareId, name = "Custom label" });
                using var sequenceNode = await CallGraph("create_node", new { kind = "Sequence" });
                var sequenceId = sequenceNode.RootElement.GetProperty("id").GetGuid();
                using var expanded = await CallGraph("set_parameter", new { nodeId = sequenceId, parameterId = "outputs", value = 5 });
                using var ports = await CallGraph("describe_node_ports", new { nodeId = sequenceId });
                Require(ports.RootElement.GetProperty("outputPorts").EnumerateArray().Select(port => port.GetProperty("id").GetString())
                    .SequenceEqual(Enumerable.Range(0, 5).Select(index => $"then_{index}")), "Instance pin discovery must use the authored Sequence arity.");
                using var curve = await CallGraph("create_node", new { kind = "FloatFCurve" });
                var curveId = curve.RootElement.GetProperty("id").GetGuid();
                using var edited = await CallGraph("set_curve", new
                {
                    nodeId = curveId,
                    keys = new[] { new { timeSeconds = 0.5, value = 2.0, interpolation = "Cubic", outSlope = 1.0 } }
                });
                Require(edited.RootElement.GetProperty("curve").GetProperty("keys").GetArrayLength() == 1,
                    "Curve replacement did not reach the document.");
                using var signal = await CallGraph("create_node", new { kind = "Signal" });
                var signalId = signal.RootElement.GetProperty("id").GetGuid();
                using var keyedSignal = await CallGraph("set_signal_keys", new
                {
                    nodeId = signalId, keys = new[] { new { timeSeconds = 0.125, mode = "Runtime" } }
                });
                Require(keyedSignal.RootElement.GetProperty("signalKeys")[0].GetProperty("timeSeconds").GetDouble() == 0.125,
                    "The Signal edit lost the key's runtime time.");
                using var badSignal = await Send("tools/call", new
                {
                    name = "libera_graph_set_signal_keys",
                    arguments = new { nodeId = signalId, keys = new[] { new { timeSeconds = -1, mode = "Runtime" } } }
                });
                Require(Failed(badSignal), "The MCP Signal edit must reject invalid times.");
                using var add = await CallGraph("create_node", new { kind = "Add" });
                var addId = add.RootElement.GetProperty("id").GetGuid();
                using var connected = await CallGraph("connect_nodes", new { sourceId = curveId, targetId = addId, targetPortId = "a" });
                using var disconnected = await CallGraph("disconnect_nodes", new { sourceId = curveId, targetId = addId, targetPortId = "a" });
                using var undone = await CallGraph("undo");
                using var graph = await CallGraph("read");
                var comparedNode = graph.RootElement.GetProperty("nodes").EnumerateArray()
                    .Single(row => row.GetProperty("id").GetGuid() == compareId);
                Require(comparedNode.GetProperty("name").GetString() == "Custom label" &&
                    comparedNode.GetProperty("typeName").GetString() == "Compare",
                    "Graph read must identify UUID-based types independently of the authored node label.");
                Require(undone.RootElement.GetProperty("changed").GetBoolean() &&
                    graph.RootElement.GetProperty("connections").EnumerateArray()
                        .Any(edge => edge.GetProperty("sourceNodeId").GetGuid() == curveId &&
                            edge.GetProperty("targetNodeId").GetGuid() == addId) &&
                    graph.RootElement.GetProperty("nodes").EnumerateArray()
                        .Any(row => row.GetProperty("id").GetGuid() == curveId &&
                            row.GetProperty("typeName").GetString() == "FloatFCurve") &&
                    graph.RootElement.GetProperty("nodes").EnumerateArray().Single(row => row.GetProperty("id").GetGuid() == signalId)
                        .GetProperty("signalKeys")[0].GetProperty("timeSeconds").GetDouble() == 0.125,
                    "Undo must restore the disconnected wire and rejected edits must not replace the valid Signal key.");
                using var redone = await CallGraph("redo");
                Require(redone.RootElement.GetProperty("changed").GetBoolean(), "Redo did not replay the disconnect.");
                using var status = await CallGraph("status");
                Require(status.RootElement.GetProperty("isDirty").GetBoolean() &&
                    status.RootElement.GetProperty("documentPath").GetString() == source &&
                    status.RootElement.GetProperty("breadcrumbs").GetArrayLength() == 1,
                    "Status must identify the dirty root graph and its saved path.");

                using var modules = await CallGraph("list_modules");
                var module = modules.RootElement.EnumerateArray()
                    .First(row => row.GetProperty("name").GetString()!.Contains("Billboard", StringComparison.Ordinal));
                var moduleId = module.GetProperty("id").GetGuid();
                using var instance = await CallGraph("insert_module", new { moduleId, x = 120, y = 180 });
                Require(instance.RootElement.GetProperty("groupAsset").GetProperty("id").GetGuid() == moduleId,
                    "Module insertion must keep the standard asset link.");
                using var group = await CallGraph("create_node", new { kind = "GraphGroup" });
                var groupId = group.RootElement.GetProperty("id").GetGuid();
                using var child = await CallGraph("enter", new { nodeId = groupId });
                using var nestedStatus = await CallGraph("status");
                var breadcrumbs = nestedStatus.RootElement.GetProperty("breadcrumbs");
                Require(breadcrumbs.GetArrayLength() == 2 && breadcrumbs[1].GetProperty("nodeId").GetGuid() == groupId,
                    "Graph status must identify the entered Group instance.");
                using var parent = await CallGraph("up");

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
