using System.Text.Json;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;
using LiberaFx.Views;

internal static class LiveGameEditorUiTests
{
    public static async Task RunAsync(string isolatedRoot)
    {
        if (!Path.IsPathFullyQualified(isolatedRoot)) throw new ArgumentException("Supply an isolated absolute artifacts directory.");
        Directory.CreateDirectory(isolatedRoot);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var remote = await LiveGameSpoolClient.ConnectAsync(@"\\unselected.invalid\private\endpoint", CancellationToken.None);
                throw new InvalidOperationException("Remote spool was accepted.");
            }
            catch (ArgumentException) { } // Rejected before touching the network.
        }
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager()) { AutoDestroyWhenIdle = false };
        var scalar = graph.CreateNode(new("Float"));
        graph.SetParameter(scalar, "value", JsonSerializer.SerializeToElement(1d));
        var position = graph.CreateNode(new("ScalarToVector3"));
        Connect(scalar, position, "a");
        var draw = graph.CreateNode(new("ModelDraw"));
        graph.SetParameter(draw, "assetPath", JsonSerializer.SerializeToElement("/Engine/BasicShapes/Cube.Cube"));
        Connect(position, draw, "position");
        var initialBytes = LiveGameGraphCompiler.Compile(new(graph.CreateSnapshot(), "initial"));
        await using var game = new IsolatedGameContractDouble(Path.Combine(isolatedRoot, Guid.NewGuid().ToString("N")), initialBytes);
        var captures = 0;
        var model = new LiveGameWindowViewModel(() => {
            ++captures;
            return new(graph.CreateSnapshot(), "Active edited document");
        }, TimeSpan.FromSeconds(2));
        var picks = 0;
        using var window = new LiveGameWindow(model, _ => { ++picks; return Task.FromResult<string?>(game.Directory); });
        var host = new Window { Content = window, Width = 820, Height = 620 };
        host.Show();
        try
        {
            await Task.Delay(100);
            var browse = Find<Button>("LiveGameBrowse");
            var connect = Find<Button>("LiveGameConnect");
            var apply = Find<Button>("LiveGameApply");
            var refresh = Find<Button>("LiveGameRefresh");
            var cancel = Find<Button>("LiveGameCancel");
            var instances = Find<ListBox>("LiveGameInstances");
            Require(!connect.IsEffectivelyEnabled && !apply.IsEffectivelyEnabled, "No endpoint/consent must not authorize updates.");
            Invoke(browse);
            await Wait(() => model.EndpointDirectory == game.Directory, "explicit folder picker");
            Require(picks == 1 && !connect.IsEffectivelyEnabled, "Choosing a folder silently authorized game editing.");
            Find<CheckBox>("LiveGameConsent").IsChecked = true;
            await Wait(() => connect.IsEffectivelyEnabled, "opt-in connect command");
            Invoke(connect);
            await Wait(() => model.Connected && model.Instances.Count == 2 && !model.Busy, "live list over selected spool");
            Require(model.Destination.Contains(game.Session, StringComparison.Ordinal) &&
                model.Destination.Contains("Unity", StringComparison.Ordinal), "UI does not identify selected game process/session.");
            Require(!apply.IsEffectivelyEnabled && model.SelectedInstance is null, "UI auto-selected an actual game target.");
            instances.SelectedIndex = 0;
            await Wait(() => apply.IsEffectivelyEnabled, "explicit target selection");
            var firstTarget = model.SelectedInstance!.Target;
            var untouched = game.PackageFor(IsolatedGameContractDouble.SecondTarget);
            var extra = graph.CreateNode(new("Float"));
            graph.SetParameter(extra, "value", JsonSerializer.SerializeToElement(2d));
            var add = graph.CreateNode(new("Add"));
            Connect(scalar, add, "a"); Connect(extra, add, "b");
            graph.DisconnectConnection(graph.Connections.Single(edge => edge.Target.Id == position.Id && edge.TargetPortId == "a"));
            Connect(add, position, "a");
            var snapshot = graph.CreateSnapshot();
            Invoke(apply); // Automation invocation executes the actual AsyncRelayCommand.
            await Wait(() => !model.Busy && game.Mutations == 1, "active graph compiler conversion and Restart transfer");
            Require(captures == 1 && game.LastPolicy == "restart" && game.LastTarget == firstTarget,
                "UI did not capture active graph and explicit selected Restart.");
            Require(game.PackageFor(firstTarget).Length > initialBytes.Length &&
                game.PackageFor(IsolatedGameContractDouble.SecondTarget).SequenceEqual(untouched), "Graph addition did not reach only selected target.");
            Require(GraphSnapshot.ContentEquals(snapshot, graph.CreateSnapshot()), "Compile/apply mutated source graph or history.");
            Require(model.SelectedInstance?.Target == firstTarget, "Refresh after apply lost explicit target.");
            var oldPlayback = game.PackageFor(firstTarget);
            graph.CreateNode(new("OnDestroy")); // Deliberately unsupported lifecycle migration.
            Invoke(apply);
            await Wait(() => !model.Busy && captures == 2, "visible unsupported graph rejection");
            Require(model.Status.Contains("UnsupportedBinary", StringComparison.Ordinal) &&
                game.Mutations == 1 && game.PackageFor(firstTarget).SequenceEqual(oldPlayback),
                "Unsupported graph changed old playback or reported success.");
            // Return to supported graph using real authoring undo.
            graph.History.Undo();
            game.RejectNextReplacement = true; // e.g. actual engine binding/preflight rejection.
            Invoke(apply);
            await Wait(() => !model.Busy && game.Rejections == 1, "engine-side failure response");
            Require(model.Status.Contains("UnsupportedBinary", StringComparison.Ordinal) &&
                game.PackageFor(firstTarget).SequenceEqual(oldPlayback) && model.SelectedInstance?.Target == firstTarget,
                "Engine failure was hidden or lost the selected old playback.");
            // Published cancellation cannot claim rollback. Acknowledge uncertainty
            // and force list reconciliation before another apply.
            game.PauseResponses = true;
            Invoke(apply);
            await Wait(() => File.Exists(Path.Combine(game.Directory, "request.json")) && cancel.IsEffectivelyEnabled, "published pending update");
            Invoke(cancel);
            await Wait(() => !model.Busy && model.RequiresRefresh, "finite command cancellation");
            Require(!apply.IsEffectivelyEnabled, "Cancelled published update allowed blind resend.");
            game.PauseResponses = false;
            await Wait(() => !File.Exists(Path.Combine(game.Directory, "request.json")), "game consumes uncertain update");
            Invoke(refresh);
            await Wait(() => !model.Busy && !model.RequiresRefresh, "list reconciliation after cancellation");
            Require(model.SelectedInstance?.Target == firstTarget && apply.IsEffectivelyEnabled, "Reconciliation failed to retain selected identity.");
            // Finite response timeout is exercised without an engine or network.
            game.PauseResponses = true;
            Invoke(refresh);
            await Wait(() => !model.Busy && model.RequiresRefresh, "finite missing-response deadline", 6000);
            Require(model.Status.Length != 0 && !apply.IsEffectivelyEnabled, "Timeout did not show an actionable failure.");
            game.PauseResponses = false;
            await Wait(() => !File.Exists(Path.Combine(game.Directory, "request.json")), "timeout request drained");
            Invoke(refresh);
            await Wait(() => !model.Busy && !model.RequiresRefresh, "timeout reconciliation");
            var count = game.Mutations;
            Invoke(Find<Button>("LiveGameDisconnect"));
            await Wait(() => !model.Connected && model.Instances.Count == 0, "UI disconnect");
            Require(game.Mutations == count && game.DestroyRequests == 0, "Disconnect stopped an actual game instance.");
            // No directory discovery and no handshake to an unselected game.
            Require(picks == 1, "UI performed unsolicited endpoint selection.");
            Console.WriteLine("PASS live editor: real Avalonia browse/connect/list/select/apply/cancel commands; compiler graph conversion; isolated spool; rejection protects previous playback; finite timeout; no engine/network launched.");
        }
        finally { host.Close(); window.Dispose(); }
        return;
        T Find<T>(string name) where T : Control => window.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
        void Connect(GraphNodeViewModel source, GraphNodeViewModel target, string port)
        {
            if (!graph.TryConnectNodes(graph.GetNode(source.Id), graph.GetNode(target.Id), out var error, port))
                throw new InvalidOperationException(error);
        }
    }
    private static void Invoke(Button button)
    {
        Require(button.IsEffectivelyEnabled, "Attempted to invoke disabled UI action: " + button.Name);
        var peer = new ButtonAutomationPeer(button);
        peer.GetProvider<IInvokeProvider>()!.Invoke();
    }
    private static async Task Wait(Func<bool> condition, string description, int milliseconds = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException(description);
            await Task.Delay(20);
        }
    }
    private static void Require(bool value, string error) { if (!value) throw new InvalidOperationException(error); }

    // Transport contract double only: no numerical facade and no claim that this
    // replaces the separate actual Unity/Unreal component/pixel acceptance tests.
    private sealed class IsolatedGameContractDouble : IAsyncDisposable
    {
        public const string FirstTarget = "0000000000000001:0000000000000000";
        public const string SecondTarget = "0000000000000001:0000000000000001";
        private readonly object gate = new();
        private readonly Dictionary<string, byte[]> packages = [];
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;
        private int mutations, rejections, destroyRequests;
        public string Directory { get; }
        public string Session { get; } = Guid.NewGuid().ToString("N");
        public volatile bool PauseResponses, RejectNextReplacement;
        public int Mutations => Volatile.Read(ref mutations);
        public int Rejections => Volatile.Read(ref rejections);
        public int DestroyRequests => Volatile.Read(ref destroyRequests);
        public string? LastTarget { get; private set; }
        public string? LastPolicy { get; private set; }
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public IsolatedGameContractDouble(string directory, byte[] initial)
        {
            Directory = directory;
            System.IO.Directory.CreateDirectory(directory);
            packages.Add(FirstTarget, initial); packages.Add(SecondTarget, initial);
            File.WriteAllText(Path.Combine(directory, "endpoint.json"), JsonSerializer.Serialize(
                new LiveGameEndpoint(1, Session, "Unity", "isolated-local-spool", "restart-root-render-v1-v2", Environment.ProcessId), Json));
            loop = Task.Run(RunAsync);
        }
        public byte[] PackageFor(string target) { lock (gate) return packages[target].ToArray(); }
        private async Task RunAsync()
        {
            try
            {
                var previous = 0L;
                while (!stop.IsCancellationRequested)
                {
                    var path = Path.Combine(Directory, "request.json");
                    if (PauseResponses || !File.Exists(path)) { await Task.Delay(10, stop.Token); continue; }
                    var text = await File.ReadAllTextAsync(path, stop.Token);
                    File.Delete(path);
                    using var command = JsonDocument.Parse(text);
                    var root = command.RootElement;
                    var sequence = root.GetProperty("sequence").GetString()!;
                    var next = long.Parse(sequence, System.Globalization.CultureInfo.InvariantCulture);
                    Require(next > previous && root.GetProperty("session").GetString() == Session, "Unscoped/replayed UI request.");
                    previous = next;
                    var operation = root.GetProperty("operation").GetString();
                    var target = root.TryGetProperty("target", out var selected) ? selected.GetString() : null;
                    var result = "Ok";
                    var error = string.Empty;
                    if (operation == "replace")
                    {
                        LastTarget = target;
                        LastPolicy = root.GetProperty("policy").GetString();
                        var bytes = Convert.FromBase64String(root.GetProperty("packageBase64").GetString()!);
                        var package = SpawnableGroupPackageBinary.Deserialize(bytes);
                        Require(LastPolicy == "restart" && package.Templates.Count == 0, "UI did not send converted supported Restart package.");
                        if (RejectNextReplacement)
                        { RejectNextReplacement = false; result = "UnsupportedBinary"; error = "Synthetic engine preflight rejection; old component retained."; Interlocked.Increment(ref rejections); }
                        else { lock (gate) packages[target!] = bytes; Interlocked.Increment(ref mutations); }
                    }
                    else if (operation == "destroy") Interlocked.Increment(ref destroyRequests);
                    else Require(operation == "list", "Unexpected editor mutation command.");
                    var response = new LiveGameResponse(1, Session, sequence, result, error, target,
                        [new(FirstTarget, "Contract-double instance A", false, 1), new(SecondTarget, "Contract-double instance B", false, 1)]);
                    var output = Path.Combine(Directory, "response.json");
                    await File.WriteAllTextAsync(output + ".tmp", JsonSerializer.Serialize(response, Json), stop.Token);
                    File.Move(output + ".tmp", output, overwrite: true);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            await loop;
            stop.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
