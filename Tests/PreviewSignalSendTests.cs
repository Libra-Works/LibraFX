using System.Collections;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Threading;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using LiberaFx.Views;

// Invoke Run() from the initialized, isolated Avalonia smoke runner.
// Publication unit checks use in-memory clients; VerifySendTransportAsync uses real pinned TLS/native execution.
internal static class PreviewSignalSendTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run()
    {
        var root = Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT");
        Require(!string.IsNullOrEmpty(root) && Path.IsPathFullyQualified(root),
            "Set an absolute isolated LIBERAFX_DATA_ROOT for preview Send checks.");
        VerifyScopesAndTranslations();
        RunWithDispatcher(VerifyPublicationsAsync());
        RunWithDispatcher(VerifySendTransportAsync());
        GodotPackagePreviewTests.Run();
    }

    private static void VerifyScopesAndTranslations()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var requests = new List<Guid>();
        graph.PreviewSignalRequested += (_, id) => requests.Add(id);
        var signal = graph.CreateNode(GraphNodeKind.Signal);
        var input = graph.CreateNode(new GraphNodeKind("InputSignal"));
        VerifyNode(signal, enabled: true);
        VerifyNode(input, enabled: true);
        var group = graph.CreateNode(GraphNodeKind.GraphGroup);
        Require(graph.EnterGraph(group), "Cannot enter ordinary Graph Group.");
        VerifyNode(graph.CreateNode(GraphNodeKind.Signal), enabled: true);
        VerifyNode(graph.CreateNode(new GraphNodeKind("GroupInputSignal")), enabled: true);
        Require(requests.Count == 4 && requests[0] == signal.Id && requests[1] == input.Id,
            "Send did not preserve individual Signal entry IDs.");
        graph.NavigateToGraph(0);
        var spawnable = graph.CreateNode(GraphNodeKind.SpawnableGroup);
        Require(graph.EnterGraph(spawnable), "Cannot enter Spawnable Group.");
        VerifyNode(graph.CreateNode(GraphNodeKind.Signal), enabled: false);
        VerifyNode(graph.CreateNode(new GraphNodeKind("SpawnInputSignal")), enabled: false);
        var nested = graph.CreateNode(GraphNodeKind.GraphGroup);
        Require(graph.EnterGraph(nested), "Cannot enter Graph Group within Spawnable scope.");
        VerifyNode(graph.CreateNode(GraphNodeKind.Signal), enabled: false);
        VerifyNode(graph.CreateNode(new GraphNodeKind("GroupInputSignal")), enabled: false);
        Require(requests.Count == 4, "A child Signal was implicitly broadcast.");
        var ordinary = graph.CreateNode(GraphNodeKind.Float);
        Require(!ordinary.IsPreviewSignal && !ordinary.SendPreviewSignalCommand.CanExecute(null),
            "A parameter node exposed Send.");

        void VerifyNode(GraphNodeViewModel node, bool enabled)
        {
            var view = new GraphNodeView { DataContext = node };
            var window = new Window { Width = 400, Height = 400, Content = view };
            var localization = LocalizationService.Shared;
            var previousLanguage = localization.SelectedLanguage;
            window.Show();
            try
            {
                var button = view.FindControl<Button>("PreviewSignalSendButton")!;
                foreach (var language in localization.Languages)
                {
                    localization.SelectedLanguage = language;
                    Pump(window);
                    Require(ReferenceEquals(button.Command, node.SendPreviewSignalCommand),
                        $"Signal Send command binding is missing for {node.Definition.Kind.Value}.");
                    // Button applies Command.CanExecute through IsEnabledCore, leaving the
                    // user-supplied IsEnabled setting unchanged. Check actual input/UIA state.
                    Require(button.IsEffectivelyVisible && button.IsEffectivelyEnabled == enabled &&
                        new ButtonAutomationPeer(button).IsEnabled() == enabled &&
                        node.SendPreviewSignalCommand.CanExecute(null) == enabled,
                        $"Signal Send state does not match scope: kind={node.Definition.Kind.Value}, " +
                        $"language={language.Id}, expectedEnabled={enabled}, visible={button.IsEffectivelyVisible}, " +
                        $"localEnabled={button.IsEnabled}, effectiveEnabled={button.IsEffectivelyEnabled}, " +
                        $"canExecute={node.SendPreviewSignalCommand.CanExecute(null)}.");
                    Require(button is IconButton { Icon: LiberaFx.Models.Appearance.EditorIcon.Play } &&
                        AutomationProperties.GetName(button) == localization.Get("Editor.SignalSend.AccessibleName") &&
                        AutomationProperties.GetHelpText(button) == localization.Get(enabled
                            ? "Editor.SignalSend.Help" : "Editor.SignalSend.InstanceTargetRequired"),
                        "Signal Send labels or accessible scope help are not localized.");
                    if (!enabled)
                        Require(node.PreviewSignalSendHelp.Contains("インスタンス") ||
                            node.PreviewSignalSendHelp.Contains("instance targeting", StringComparison.Ordinal),
                            "Disabled Send does not explain instance targeting.");
                }
                if (enabled)
                {
                    var snapshot = graph.CreateSnapshot();
                    var history = JsonSerializer.Serialize(graph.History.CreateSnapshot());
                    Invoke(button);
                    Require(GraphSnapshot.ContentEquals(snapshot, graph.CreateSnapshot()) &&
                        history == JsonSerializer.Serialize(graph.History.CreateSnapshot()),
                        "Send modified graph data or history.");
                }
            }
            finally
            {
                localization.SelectedLanguage = previousLanguage;
                window.Close();
            }
        }
    }

    private static async Task VerifyPublicationsAsync()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var host = new PreviewHost(port);
        var model = new MainViewModel();
        var clients = (IDictionary)typeof(PreviewHost).GetField("_clients", PrivateInstance)!.GetValue(host)!;
        try
        {
            await host.StartAsync(model);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                model.Playback.Pause();
                model.Playback.Frame = 24;
                var graph = model.Graph;
                var node = graph.CreateNode(GraphNodeKind.Signal);
                var view = new GraphNodeView { DataContext = node };
                var window = new Window { Width = 400, Height = 400, Content = view };
                window.Show();
                try
                {
                    Pump(window);
                    var button = view.FindControl<Button>("PreviewSignalSendButton")!;
                    var initial = Latest(host);
                    Invoke(button);
                    Require(ReferenceEquals(initial, Latest(host)) &&
                        graph.ConnectionStatus == LocalizationService.Shared.Get("Editor.SignalSend.CompiledClientRequired"),
                        "Send without a compiled client was silently accepted.");
                    AddClient(2);
                    Invoke(button);
                    Require(ReferenceEquals(initial, Latest(host)) &&
                        model.PreviewStatus == LocalizationService.Shared.Get("Editor.SignalSend.CompiledClientRequired"),
                        "A legacy client incorrectly enabled Signal dispatch.");
                    var compiled = AddClient(3);
                    var channel = (Channel<PreviewPublication>)compiled.GetType().GetProperty("Channel")!.GetValue(compiled)!;
                    var beforeGraph = graph.CreateSnapshot();
                    var beforeHistory = JsonSerializer.Serialize(graph.History.CreateSnapshot());
                    var beforePlayback = model.Playback.Snapshot;
                    model.ActiveDocument!.MarkSaved("Preview Send fixture", "preview-send-fixture",
                        beforeGraph, graph.History.CreateSnapshot());
                    var beforeDirty = model.ActiveDocument!.IsDirty;
                    Require(!beforeDirty, "The in-memory Send fixture should start clean.");
                    var playbackChanges = 0;
                    model.Playback.StateChanged += CountPlayback;
                    try
                    {
                        Invoke(button);
                        var first = Latest(host);
                        Invoke(button);
                        var second = Latest(host);
                        Require(ReferenceEquals(first.Graph, initial.Graph) &&
                            ReferenceEquals(second.Graph, initial.Graph) &&
                            ReferenceEquals(first.Playback, initial.Playback) &&
                            ReferenceEquals(second.Playback, initial.Playback) &&
                            first.Sequence > initial.Sequence && second.Sequence > first.Sequence,
                            "Send rebuilt graph/playback state instead of publishing transient intent.");
                        Require(first.SignalRequests.Count == 1 && second.SignalRequests.Count == 2 &&
                            second.SignalRequests.All(request => request.NodeId == node.Id) &&
                            second.SignalRequests[1].Sequence == second.SignalRequests[0].Sequence + 1 &&
                            second.SignalRequestSequence == second.SignalRequests[^1].Sequence,
                            "Consecutive clicks did not accumulate distinct monotonic requests.");
                        Require(model.Playback.Snapshot == beforePlayback && !model.Playback.IsPlaying &&
                            playbackChanges == 0 && beforeDirty == model.ActiveDocument.IsDirty &&
                            GraphSnapshot.ContentEquals(beforeGraph, graph.CreateSnapshot()) &&
                            beforeHistory == JsonSerializer.Serialize(graph.History.CreateSnapshot()),
                            "Send changed paused playhead, graph, history, or project dirty state.");
                        for (var index = 0; index < 270; index++) Invoke(button);
                        model.Playback.Frame = 25;
                        var tick = Latest(host);
                        Require(tick.SignalRequests.Count == 256 &&
                            tick.SignalRequests.Zip(tick.SignalRequests.Skip(1))
                                .All(pair => pair.Second.Sequence == pair.First.Sequence + 1) &&
                            ReferenceEquals(tick.Graph, initial.Graph) &&
                            channel.Reader.TryRead(out var pending) && ReferenceEquals(pending, tick) &&
                            pending.SignalRequests.Count == 256 && !channel.Reader.TryRead(out _),
                            "DropOldest playback publication lost cumulative requests or exceeded the bound.");
                        Require(first.SignalRequests.Count == 1,
                            "A published request list was mutated by later clicks.");
                        var highWater = tick.SignalRequestSequence;
                        graph.RenameNode(node, "Changed graph");
                        Require(Latest(host).SignalRequests.Count == 0,
                            "Graph editing retained commands from the previous history identity.");
                        Invoke(button);
                        Require(Latest(host).SignalRequests.Single().Sequence > highWater,
                            "Signal request sequence reset after graph editing.");
                        graph.History.Undo();
                        Require(Latest(host).SignalRequests.Count == 0,
                            "Undo retained requests from another history state.");
                        view.DataContext = graph.GetNode(node.Id);
                        Pump(window);
                        Invoke(button);
                        Require(Latest(host).SignalRequests.Count == 1, "Send after Undo was not accepted.");
                        graph.History.Redo();
                        Require(Latest(host).SignalRequests.Count == 0,
                            "Redo retained requests from another history state.");
                        view.DataContext = graph.GetNode(node.Id);
                        Pump(window);
                        Invoke(button);
                        var previousDocument = Latest(host);
                        model.NewDocumentCommand.Execute(null);
                        var switched = Latest(host);
                        Require(switched.DocumentId != previousDocument.DocumentId && switched.SignalRequests.Count == 0 &&
                            switched.SignalRequestSequence == previousDocument.SignalRequestSequence,
                            "Document switching retained requests or reset the host sequence.");
                        var obsolete = graph.GetNode(node.Id);
                        obsolete.SendPreviewSignalCommand.Execute(null);
                        Require(ReferenceEquals(switched, Latest(host)) &&
                            graph.ConnectionStatus == LocalizationService.Shared.Get("Editor.SignalSend.CompiledClientRequired"),
                            "The host remained subscribed to the previous workspace.");
                    }
                    finally { model.Playback.StateChanged -= CountPlayback; }

                    void CountPlayback(object? sender, EventArgs e) => playbackChanges++;
                }
                finally { window.Close(); }
            });
        }
        finally
        {
            clients.Clear();
            await host.StopAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    var stopped = LatestOrNull(host);
                    var node = model.Graph.CreateNode(GraphNodeKind.Signal);
                    node.SendPreviewSignalCommand.Execute(null);
                    Require(ReferenceEquals(stopped, LatestOrNull(host)) &&
                        model.Graph.ConnectionStatus == LocalizationService.Shared.Get("Editor.SignalSend.CompiledClientRequired"),
                        "Stopping the host left a Signal subscription behind.");
                }
                finally { model.DisposeStorageFiles(); }
            });
        }

        object AddClient(int version)
        {
            var type = typeof(PreviewHost).GetNestedType("PreviewClient", BindingFlags.NonPublic)!;
            var client = Activator.CreateInstance(type, [version, false, false])!;
            clients.Add(Guid.NewGuid(), client);
            return client;
        }
    }

    private static async Task VerifySendTransportAsync()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var host = new PreviewHost(port);
        var model = new MainViewModel();
        Window? window = null;
        using var socket = new ClientWebSocket();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = cancellation.Token;
        try
        {
            var graph = model.Graph;
            graph.DeleteNodes(graph.Nodes.ToArray());
            var signal = graph.CreateNode(GraphNodeKind.Signal);
            var group = graph.CreateNode(GraphNodeKind.SpawnableGroup);
            Require(graph.EnterGraph(group), "Cannot enter TLS Send fixture template.");
            graph.DeleteNodes(graph.Nodes.ToArray());
            var life = graph.CreateNode(new GraphNodeKind("LifeTime"));
            graph.SetParameter(life, "seconds", JsonSerializer.SerializeToElement(0.5));
            graph.NavigateToGraph(0);
            signal = graph.GetNode(signal.Id);
            group = graph.GetNode(group.Id);
            Require(graph.TryConnectNodes(signal, group, out var error, "spawn"),
                "Cannot connect TLS Send fixture: " + error);
            model.Playback.Pause();
            model.Playback.Frame = 0;
            var snapshot = graph.CreateSnapshot();
            var history = JsonSerializer.Serialize(graph.History.CreateSnapshot());
            model.ActiveDocument!.MarkSaved("TLS Send fixture", "tls-send-fixture",
                snapshot, graph.History.CreateSnapshot());
            Require(!model.ActiveDocument.IsDirty, "TLS Send fixture must start clean.");
            var paused = model.Playback.Snapshot;
            var view = new GraphNodeView { DataContext = signal };
            window = new Window { Width = 400, Height = 400, Content = view };
            window.Show();
            Pump(window);
            var button = view.FindControl<Button>("PreviewSignalSendButton")!;

            await host.StartAsync(model);
            socket.Options.SetRequestHeader("X-LiberaFx-Preview-Token", host.AccessToken);
            var fingerprint = Convert.FromHexString(host.Fingerprint);
            socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is not null && CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(certificate.GetRawCertData()), fingerprint);
            await socket.ConnectAsync(new Uri($"wss://localhost:{port}/preview?protocol=3"), token);
            await ReceiveCompiledAsync(socket, CompiledPreviewKind.Program, token);
            await socket.SendAsync(Encoding.UTF8.GetBytes(
                """{"type":"hello","engine":"LiberaFx Send regression","role":"editor","version":1,"capabilities":["rendering"]}"""),
                WebSocketMessageType.Text, true, token);
            var program = await ReceiveCompiledAsync(socket, CompiledPreviewKind.Program, token);
            var programSequence = BinaryPrimitives.ReadUInt64LittleEndian(program.AsSpan(8));
            await ExpectInstancesAsync(0);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Require(button.IsEffectivelyEnabled && ReferenceEquals(button.Command, signal.SendPreviewSignalCommand),
                    "Real TLS Send button lost its command binding or became disabled.");
                Invoke(button);
                VerifyAuthoredState();
                Require(model.Playback.Snapshot == paused && !model.Playback.IsPlaying,
                    "Real TLS Send advanced or changed paused playback.");
            });
            await ReceiveCompiledAsync(socket, CompiledPreviewKind.Playback, token);
            await ExpectInstancesAsync(1);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Require(model.Playback.Snapshot == paused, "Scene delivery changed the paused playhead.");
                VerifyAuthoredState();
                model.Playback.StepForwardCommand.Execute(null);
                Require(model.Playback.Frame == 1 && !model.Playback.IsPlaying,
                    "The first forward step did not advance exactly one paused frame.");
            });
            await ReceiveCompiledAsync(socket, CompiledPreviewKind.Playback, token);
            await ExpectInstancesAsync(1);
            await Dispatcher.UIThread.InvokeAsync(VerifyAuthoredState);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Send regression complete", token);
            Console.WriteLine("Preview Send TLS PASS: real button -> paused native spawn 0/1/1 across Send/first forward step; graph/history unchanged.");

            void VerifyAuthoredState()
            {
                Require(GraphSnapshot.ContentEquals(snapshot, graph.CreateSnapshot()) &&
                    history == JsonSerializer.Serialize(graph.History.CreateSnapshot()) &&
                    !model.ActiveDocument!.IsDirty,
                    "Real TLS Send or forward stepping changed project data/history.");
            }

            async Task ExpectInstancesAsync(int count)
            {
                var scene = await ReceiveCompiledAsync(socket, CompiledPreviewKind.Scene, token);
                using var json = JsonDocument.Parse(scene.AsMemory(48));
                var root = json.RootElement;
                Require(root.GetProperty("executionOwner").GetString() == "editor-native" &&
                    root.GetProperty("programSequence").GetUInt64() == programSequence &&
                    root.GetProperty("activeInstances").GetInt32() == count &&
                    root.GetProperty("nodes").GetArrayLength() == 0,
                    $"Real TLS Send expected {count} native instances and no render assets: {root.GetRawText()}");
            }
        }
        finally
        {
            socket.Abort();
            await host.StopAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                try { window?.Close(); }
                finally { model.DisposeStorageFiles(); }
            });
        }
    }

    private static async Task<byte[]> ReceiveCompiledAsync(ClientWebSocket socket, CompiledPreviewKind expected,
        CancellationToken token)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), token);
            Require(result.MessageType == WebSocketMessageType.Binary && stream.Length + result.Count <= 2 * 1024 * 1024,
                "Invalid or oversized compiled preview frame in real Send regression.");
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        var bytes = stream.ToArray();
        Require(bytes.Length >= 48, "Truncated compiled preview frame in real Send regression.");
        var kind = (CompiledPreviewKind)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6));
        Require(kind == expected, $"Real Send expected {expected}, received {kind}." +
            (kind == CompiledPreviewKind.CompileError ? " " + Encoding.UTF8.GetString(bytes.AsSpan(48)) : ""));
        return bytes;
    }

    private static PreviewPublication? LatestOrNull(PreviewHost host) =>
        (PreviewPublication?)typeof(PreviewHost).GetField("_latest", PrivateInstance)!.GetValue(host);

    private static PreviewPublication Latest(PreviewHost host) =>
        LatestOrNull(host) ?? throw new InvalidOperationException("Preview publication is missing.");

    private static void Invoke(Button button)
    {
        new ButtonAutomationPeer(button).GetProvider<IInvokeProvider>()!.Invoke();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void RunWithDispatcher(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            using var registration = timeout.Token.Register(() => Dispatcher.UIThread.Post(() => frame.Continue = false));
            _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
        }
        Require(task.IsCompleted, "The isolated preview Send check timed out.");
        task.GetAwaiter().GetResult();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
