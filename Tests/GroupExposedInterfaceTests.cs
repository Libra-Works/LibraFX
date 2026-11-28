using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Mcp;
using LiberaFx.Services.Preview;
using LiberaFx.Services.Project;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;
using LiberaFx.ViewModels.Panels;
using LiberaFx.Views.Controls;
using LiberaFx.Views.Panels;
using LiberaFx.Views.Timeline;
using LiberaFx.Services.Compilation;

internal static class GroupExposedInterfaceTests
{
    public static void Run()
    {
        _ = new ThemeSettingsService();
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var group = graph.CreateNode(GraphNodeKind.GraphGroup);
        var groupId = group.Id;
        if (!graph.EnterGraph(group)) throw new InvalidOperationException("Cannot enter group.");
        var scalar = graph.CreateNode(GraphNodeKind.Float);
        var scalarId = scalar.Id;
        var pane = new NodePropertiesPaneViewModel(new ActiveGraphProvider(graph));
        var view = new NodePropertiesView { DataContext = pane };
        var other = new Button { Content = "Other control" };
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        layout.Children.Add(view);
        Grid.SetRow(other, 1);
        layout.Children.Add(other);
        var window = new Window { Width = 700, Height = 500, Content = layout };
        window.Show();
        try
        {
            Pump(window);
            var publish = view.GetVisualDescendants().OfType<IconButton>()
                .Single(button => button.Name == "PublishGroupParameterButton" && button.IsEffectivelyVisible);
            Click(publish, window);
            var exposed = graph.CreateCurrentSnapshot().ExposedItems?.Single()
                ?? throw new InvalidOperationException("The inspector did not expose the selected parameter.");
            if (exposed.Targets.Single().NodeId != scalarId || exposed.Targets.Single().ParameterId != "value")
                throw new InvalidOperationException("The inspector exposed the wrong parameter.");
            if (!graph.History.Undo() || graph.CreateCurrentSnapshot().ExposedItems is { Count: > 0 } ||
                !graph.History.Redo() || graph.CreateCurrentSnapshot().ExposedItems?.Single().Id != exposed.Id)
                throw new InvalidOperationException("Publishing was not undoable.");
            graph.NavigateToGraph(0);
            graph.SelectNode(graph.GetNode(groupId));
            Pump(window);
            var parameter = pane.Parameters.Single(editor => editor.Name == exposed.Name);
            var box = view.GetVisualDescendants().OfType<ParameterEditorHost>()
                .Where(host => ReferenceEquals(host.DataContext, parameter))
                .SelectMany(host => host.GetVisualDescendants().OfType<TextBox>()).First();
            Click(box, window);
            box.SelectAll();
            var handle = window.TryGetPlatformHandle()?.Handle ?? 0;
            SendMessage(handle, 0x0102, (nuint)'7', 0);
            Pump(window);
            Click(other, window);
            if (graph.GetNode(groupId).GroupOverrides?.Single().Components.Single().Value?.GetDouble() != 7)
                throw new InvalidOperationException("Editing the visible group inspector did not set the instance value.");
            if (!graph.History.Undo() || graph.GetNode(groupId).GroupOverrides is { Count: > 0 } ||
                !graph.History.Redo() ||
                graph.GetNode(groupId).GroupOverrides?.Single().Components.Single().Value?.GetDouble() != 7)
                throw new InvalidOperationException("Inspector instance editing was not undoable.");
            Pump(window);
            var configure = view.GetVisualDescendants().OfType<Button>().Single(button =>
                button.IsEffectivelyVisible && button.Content?.ToString() ==
                LocalizationService.Shared.Get("Panels.ConfigureGroupItems"));
            Click(configure, window);
            var configWindow = window.OwnedWindows.Single();
            configWindow.FindControl<TextBox>("NameField")!.Text = "Power";
            configWindow.FindControl<TextBox>("CategoryField")!.Text = "Motion";
            configWindow.FindControl<TextBox>("DescriptionField")!.Text = "A description";
            configWindow.FindControl<TextBox>("MinimumField")!.Text = "0";
            configWindow.FindControl<TextBox>("MaximumField")!.Text = "10";
            configWindow.FindControl<TextBox>("StepField")!.Text = "1";
            configWindow.FindControl<ComboBox>("PresentationField")!.SelectedIndex = 2;
            Click(configWindow.FindControl<Button>("ApplyButton")!, configWindow);
            Pump(window);
            var configured = graph.GetNode(groupId).ChildGraph!.ExposedItems!.Single();
            if (configured.Name != "Power" || configured.Category != "Motion" ||
                configured.Presentation != "slider" ||
                pane.Parameters.Single().Name != "Motion / Power" ||
                !view.GetVisualDescendants().OfType<Slider>().Any())
                throw new InvalidOperationException("Configuring the group inspector did not update its presentation.");
            if (!graph.History.Undo() || graph.GetNode(groupId).ChildGraph!.ExposedItems!.Single().Name != exposed.Name ||
                !graph.History.Redo() || graph.GetNode(groupId).ChildGraph!.ExposedItems!.Single().Name != "Power")
                throw new InvalidOperationException("Group inspector configuration was not undoable.");
            var provider = new ActiveGraphProvider(graph);
            var configureMcp = LiberaFxMcpTools.ConfigureGroupItem(provider, groupId.ToString(),
                configured with { Description = "Configured through MCP" });
            Dispatcher.UIThread.RunJobs();
            configureMcp.GetAwaiter().GetResult();
            if (graph.GetNode(groupId).ChildGraph!.ExposedItems!.Single().Description != "Configured through MCP" ||
                !graph.History.Undo() ||
                graph.GetNode(groupId).ChildGraph!.ExposedItems!.Single().Description != "A description")
                throw new InvalidOperationException("MCP configuration did not use group history.");
            VerifyMcpTransport(provider, graph, groupId, exposed.Id, window);
            var set = LiberaFxMcpTools.SetGroupInstanceValue(provider, groupId.ToString(), exposed.Id.ToString(),
                JsonSerializer.SerializeToElement(9d));
            Dispatcher.UIThread.RunJobs();
            set.GetAwaiter().GetResult();
            if (graph.GetNode(groupId).GroupOverrides?.Single().Components.Single().Value?.GetDouble() != 9)
                throw new InvalidOperationException("MCP did not use group instance editing.");
            var reset = LiberaFxMcpTools.ResetGroupInstanceValue(provider, groupId.ToString(), exposed.Id.ToString());
            Dispatcher.UIThread.RunJobs();
            reset.GetAwaiter().GetResult();
            if (graph.GetNode(groupId).GroupOverrides is { Count: > 0 })
                throw new InvalidOperationException("MCP did not reset the group instance value.");
        }
        finally { window.Close(); }
        VerifyVectorCurve();
    }

    private static void VerifyMcpTransport(ActiveGraphProvider provider, GraphWorkspaceViewModel graph,
        Guid groupId, Guid itemId, Window window)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var host = new McpHost(port);
        var configured = graph.GetNode(groupId).ChildGraph!.ExposedItems!.Single() with
        {
            Description = "Configured over MCP transport"
        };
        Task.Run(() => host.StartAsync(provider)).GetAwaiter().GetResult();
        try
        {
            var request = Task.Run(async () =>
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                client.DefaultRequestHeaders.Add("X-LiberaFx-Token", host.AccessToken);
                client.DefaultRequestHeaders.Add("Accept", "application/json, text/event-stream");
                client.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-03-26");
                async Task<JsonDocument> Send(object message)
                {
                    using var content = new StringContent(JsonSerializer.Serialize(message), Encoding.UTF8, "application/json");
                    using var response = await client.PostAsync(host.ActualEndpoint, content);
                    response.EnsureSuccessStatusCode();
                    var body = await response.Content.ReadAsStringAsync();
                    if (body.StartsWith("event:") || body.StartsWith("data:"))
                        body = body.Split('\n').First(line => line.StartsWith("data:"))[5..].Trim();
                    return JsonDocument.Parse(body);
                }
                using var list = await Send(new { jsonrpc = "2.0", id = 1, method = "tools/list" });
                if (!list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
                        .Any(tool => tool.GetProperty("name").GetString() == "libera_graph_configure_group_item"))
                    throw new InvalidOperationException("The MCP server did not register group configuration.");
                using var configResult = await Send(new
                {
                    jsonrpc = "2.0", id = 2, method = "tools/call",
                    @params = new
                    {
                        name = "libera_graph_configure_group_item",
                        arguments = new { nodeId = groupId.ToString(), item = configured }
                    }
                });
                if (!configResult.RootElement.TryGetProperty("result", out var configValue) ||
                    configValue.TryGetProperty("isError", out var configError) && configError.GetBoolean())
                    throw new InvalidOperationException("The MCP server rejected group configuration.");
                using var result = await Send(new
                {
                    jsonrpc = "2.0", id = 3, method = "tools/call",
                    @params = new
                    {
                        name = "libera_graph_set_group_instance_value",
                        arguments = new { nodeId = groupId.ToString(), exposedItemId = itemId.ToString(), value = 8 }
                    }
                });
                if (!result.RootElement.TryGetProperty("result", out var value) ||
                    value.TryGetProperty("isError", out var error) && error.GetBoolean())
                    throw new InvalidOperationException("The MCP server rejected the group edit.");
                using var force = await Send(new
                {
                    jsonrpc = "2.0", id = 4, method = "tools/call",
                    @params = new
                    {
                        name = "libera_graph_create_node",
                        arguments = new { kind = "ForceEffect" }
                    }
                });
                if (!force.RootElement.TryGetProperty("result", out var forceValue) ||
                    forceValue.TryGetProperty("isError", out var forceError) && forceError.GetBoolean())
                {
                    throw new InvalidOperationException("The MCP server rejected authored Force creation.");
                }
            });
            while (!request.IsCompleted) Pump(window);
            request.GetAwaiter().GetResult();
            if (graph.GetNode(groupId).GroupOverrides?.Single().Components.Single().Value?.GetDouble() != 8 ||
                graph.GetNode(groupId).ChildGraph!.ExposedItems!.Single().Description != configured.Description)
                throw new InvalidOperationException("The MCP server did not apply the group edit.");
            var forceId = graph.Nodes.Single(node => node.Definition.Kind.Value == "ForceEffect").Id;
            if (!graph.History.Undo() || graph.Nodes.Any(node => node.Id == forceId) ||
                !graph.History.Redo() || graph.GetNode(forceId).Definition.Kind.Value != "ForceEffect" ||
                !graph.History.Undo())
            {
                throw new InvalidOperationException("MCP Force creation must be one reversible graph edit.");
            }
        }
        finally { Task.Run(host.StopAsync).GetAwaiter().GetResult(); }
    }

    private static void VerifyVectorCurve()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var group = graph.CreateNode(GraphNodeKind.GraphGroup);
        var groupId = group.Id;
        if (!graph.EnterGraph(group)) throw new InvalidOperationException("Cannot enter group.");
        var curves = Enumerable.Range(0, 3).Select(_ => graph.CreateNode(GraphNodeKind.FloatFCurve)).ToArray();
        var compose = graph.CreateNode(new("ComposeVector4"));
        var convert = graph.CreateNode(new("Vector4ToVector3"));
        var output = graph.CreateNode(new("GroupOutputVector3"));
        for (var index = 0; index < curves.Length; index++)
            if (!graph.TryConnectNodes(curves[index], compose, out _, new[] { "r", "g", "b" }[index]))
                throw new InvalidOperationException("Cannot connect vector curve channel.");
        if (!graph.TryConnectNodes(compose, convert, out _, "a", "value") ||
            !graph.TryConnectNodes(convert, output, out _))
            throw new InvalidOperationException("Cannot connect vector curve output.");
        graph.SetSelection(curves);
        using var playback = new EditorPlayback { ShowSeconds = true };
        var pane = new NodePropertiesPaneViewModel(new ActiveGraphProvider(graph), playback);
        var inspector = new NodePropertiesView { DataContext = pane };
        var inspectorWindow = new Window { Width = 700, Height = 500, Content = inspector };
        inspectorWindow.Show();
        try
        {
            Pump(inspectorWindow);
            var expose = inspector.GetVisualDescendants().OfType<Button>().Single(button =>
                button.IsEffectivelyVisible && button.Content?.ToString() ==
                LocalizationService.Shared.Get("Panels.PublishGroupCurves"));
            Click(expose, inspectorWindow);
            var item = graph.CreateCurrentSnapshot().ExposedItems?.Single()
                ?? throw new InvalidOperationException("The vector curve was not exposed.");
            if (item.Editor != "vector3Curve" || !item.Targets.Select(target => target.NodeId)
                    .SequenceEqual(curves.Select(node => node.Id)))
                throw new InvalidOperationException("Exposing curves changed XYZ order.");
            graph.NavigateToGraph(0);
            graph.SelectNode(graph.GetNode(groupId));
            Pump(inspectorWindow);
            var edit = inspector.GetVisualDescendants().OfType<Button>().SingleOrDefault(button =>
                button.IsEffectivelyVisible && button.Content?.ToString() ==
                LocalizationService.Shared.Get("Panels.EditGroupCurves"));
            if (!pane.HasExposedCurves || edit is null)
                throw new InvalidOperationException("The group inspector did not expose its curve editor.");
            Click(edit, inspectorWindow);
            var curveWindow = inspectorWindow.OwnedWindows.Single();
            var curveView = (CurveEditorView)curveWindow.Content!;
            var editor = (CurveEditorViewModel)curveView.DataContext!;
            try
            {
                Pump(curveWindow);
                if (editor.Channels.Count != 3 || editor.VisibleCurves.Count != 3)
                    throw new InvalidOperationException("The group curve editor did not show all XYZ channels.");
                if (!ReferenceEquals(editor.Playback, playback) ||
                    Math.Abs(editor.SnapDisplayTime(0.123) - 7d / 60) > 1e-10)
                    throw new InvalidOperationException("The group curve editor did not use timeline timing and frame snapping.");
                for (var index = 0; index < 3; index++)
                {
                    var channel = curveView.GetVisualDescendants().OfType<Button>().Single(button =>
                        button.Tag is CurveChannelViewModel channel && channel.Node.Id == curves[index].Id);
                    Click(channel, curveWindow);
                    if (editor.Node?.Id != curves[index].Id)
                        throw new InvalidOperationException($"Curve channel {index} was not selected.");
                    Click(curveView.FindControl<IconButton>("CurveAddKeyButton")!, curveWindow);
                    if (editor.SelectedKey is null)
                        throw new InvalidOperationException($"Curve channel {index} did not select the new key.");
                    var valueBox = curveView.FindControl<TextBox>("CurveValueBox")!;
                    Click(valueBox, curveWindow);
                    if (!valueBox.IsFocused)
                        throw new InvalidOperationException($"Curve channel {index} value box did not receive focus (enabled={valueBox.IsEffectivelyEnabled}, bounds={valueBox.Bounds}).");
                    valueBox.SelectAll();
                    if (index == 0)
                    {
                        var handle = curveWindow.TryGetPlatformHandle()?.Handle ?? 0;
                        SendMessage(handle, 0x0102, (nuint)'2', 0);
                    }
                    else valueBox.Text = (index + 2).ToString();
                    Pump(curveWindow);
                    if (valueBox.Text != (index + 2).ToString())
                        throw new InvalidOperationException($"Curve channel {index} text input failed: '{valueBox.Text}'.");
                    Click(curveView.FindControl<IconButton>("CurveFrameAllButton")!, curveWindow);
                    if (valueBox.IsFocused)
                        throw new InvalidOperationException($"Curve channel {index} value box did not lose focus.");
                }
                var values = graph.GetNode(groupId).GroupOverrides?.Single().Components
                    .Select(component => component.Curve!.Keys[^1].Value).ToArray();
                if (values is null || !values.SequenceEqual([2d, 3d, 4d]))
                    throw new InvalidOperationException($"Editing XYZ channels did not update instance curves: {string.Join(", ", values ?? [])}.");
                if (!graph.History.Undo() || !graph.History.Redo())
                    throw new InvalidOperationException("Curve instance edits did not undo and redo.");
                using var project = new MemoryStream();
                new ProjectFileService().SaveAsync(project, graph).GetAwaiter().GetResult();
                project.Position = 0;
                var reloaded = ProjectDocumentReader.LoadAsync(project).GetAwaiter().GetResult();
                var bytecode = VfxProgram.Compile(reloaded.Graph).ToPackageBinary();
                VerifyNativeVector(bytecode, output.Id, [2, 3, 4]);
            }
            finally { curveWindow.Close(); }
        }
        finally { inspectorWindow.Close(); }
    }

    private static void VerifyNativeVector(byte[] bytes, Guid target, double[] expected)
    {
        var path = Environment.GetEnvironmentVariable("LIBERAFX_NATIVE_TEST_LIBRARY");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        using var host = new NativeSpawnableSession(new SpawnableGroupPackage(bytes, []) { AutoDestroyWhenIdle = false }, path);
        host.Advance(2);
        var value = host.ReadRoot(target);
        if (value.Width != 3 || !value.Lanes.Take(3).SequenceEqual(expected))
        {
            throw new InvalidOperationException("The native runtime did not evaluate exposed XYZ curves.");
        }
    }

    private static void Pump(Window window)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.UIThread.PushFrame(frame);
        window.UpdateLayout();
    }

    private static void Click(Control control, Window window)
    {
        if (!control.IsEffectivelyVisible || control.Bounds.Width <= 0)
            throw new InvalidOperationException("The inspector control is not visible.");
        var handle = window.TryGetPlatformHandle()?.Handle ?? 0;
        if (handle == 0) throw new InvalidOperationException("The test window has no native handle.");
        var point = control.PointToScreen(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2));
        var client = new NativePoint { X = point.X, Y = point.Y };
        if (!ScreenToClient(handle, ref client)) throw new InvalidOperationException("Invalid window coordinates.");
        var position = (nint)((client.Y << 16) | (client.X & 0xffff));
        SendMessage(handle, 0x0200, 0, position);
        SendMessage(handle, 0x0201, 1, position);
        Pump(window);
        SendMessage(handle, 0x0202, 0, position);
        Pump(window);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint handle, ref NativePoint point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint handle, uint message, nuint wParam, nint lParam);
}
