using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Threading;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;
using LiberaFx.Views;

internal static class SignalTimelineTests
{
    public static void Run(string? output = null)
    {
        var library = EditorNativePreviewSession.ResolveLibraryPath();
        VerifyAuthoring();
        var root = RootGraph();
        VerifyExport(root, library);
        VerifyPlayback(root, library);
        VerifySubframePlayback(library);
        var scoped = VerifyScopes(library);
        VerifyScopedSeek(scoped, library);
        VerifyManualSend(root, library);
        VerifySender(scoped, library);
        VerifyGroupPersistence(root);
        VerifyOrdinaryGroup(library);
        if (output is not null)
        {
            Directory.CreateDirectory(output);
            File.WriteAllBytes(Path.Combine(output, "Timeline.lfxp"),
                SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(root)));
            File.WriteAllBytes(Path.Combine(output, "ScopedTimeline.lfxp"),
                SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(scoped)));
            var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
            workspace.InitializeFromSnapshot(root);
            using var project = new MemoryStream();
            new ProjectFileService().SaveAsync(project, workspace).GetAwaiter().GetResult();
            File.WriteAllBytes(Path.Combine(output, "Timeline.lfxproj"), project.ToArray());
        }
        Console.WriteLine("Signal timeline editing/history/persistence, preview/runtime separation, frame-step and native scoped dispatch passed.");
    }

    private static GraphSnapshot RootGraph()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager()) { AutoDestroyWhenIdle = false };
        var input = graph.CreateNode(new("InputSignal"));
        var output = graph.CreateNode(new("OutputSignal"));
        graph.SetParameter(input, "name", JsonSerializer.SerializeToElement("trigger"));
        graph.SetParameter(output, "name", JsonSerializer.SerializeToElement("echo"));
        Require(graph.TryConnectNodes(input, output, out _, "signal", "then"), "Could not connect named Signal fixture.");
        graph.SetSignalKeys(input, [
            new(Guid.NewGuid(), 0, SignalTimelineMode.Preview),
            new(Guid.NewGuid(), 1d / 60, SignalTimelineMode.Runtime),
            new(Guid.NewGuid(), 2d / 60, SignalTimelineMode.Preview)
        ], "Signal fixture keys");
        return graph.CreateSnapshot();
    }

    private static void VerifyAuthoring()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var input = graph.CreateNode(new("InputSignal"));
        graph.SetParameter(input, "name", JsonSerializer.SerializeToElement("test-input"));
        graph.SelectNode(input);
        using var playback = new EditorPlayback();
        var active = new ActiveGraphProvider(graph);
        var timeline = new TimelineViewModel(active, playback);
        var view = new TimelineView { DataContext = timeline };
        var window = new Window { Content = view, Width = 1200, Height = 400 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            new ButtonAutomationPeer(view.FindControl<Button>("AddPreviewSignalButton")!)
                .GetProvider<IInvokeProvider>()!.Invoke();
            Dispatcher.UIThread.RunJobs();
            Require(timeline.Tracks.Single().IsSignal && input.SignalKeys is { Count: 1 },
                "The real preview-key button did not create an authored track.");
            var preview = input.SignalKeys![0];
            playback.Frame = 1;
            new ButtonAutomationPeer(view.FindControl<Button>("AddRuntimeSignalButton")!)
                .GetProvider<IInvokeProvider>()!.Invoke();
            Dispatcher.UIThread.RunJobs();
            Require(timeline.Tracks.Count == 2 && timeline.Tracks.Select(track => track.SignalMode).Distinct().Count() == 2,
                "Preview/runtime tracks were not visibly distinguished.");
            var sourceId = input.Id;
            var runtime = input.SignalKeys!.Single(key => key.Mode == SignalTimelineMode.Runtime);
            timeline.MoveSignalKey(timeline.SelectedTrack!, runtime.Id, 0.25);
            Require(graph.GetNode(sourceId).SignalKeys!.Single(key => key.Id == runtime.Id).TimeSeconds == 0.25,
                "Signal key time did not update.");
            Require(graph.History.Undo() &&
                graph.GetNode(sourceId).SignalKeys!.Single(key => key.Id == runtime.Id).TimeSeconds == 1d / 60 &&
                graph.History.Redo(), "Signal key editing did not use graph history.");
            var before = graph.CreateSnapshot();
            timeline.MoveSignalKey(timeline.SelectedTrack!, runtime.Id, double.NaN);
            Require(GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()), "Invalid Signal time mutated the graph.");
            using var stream = new MemoryStream();
            new ProjectFileService().SaveAsync(stream, graph).GetAwaiter().GetResult();
            stream.Position = 0;
            var loaded = ProjectDocumentReader.LoadAsync(stream).GetAwaiter().GetResult();
            Require(GraphSnapshot.ContentEquals(before, loaded.Graph), "Signal key modes/IDs/times/history did not persist.");
            var old = JsonNode.Parse(stream.ToArray())!.AsObject();
            old["FormatVersion"] = 11;
            ExpectInvalid(() => ProjectDocumentReader.LoadAsync(new MemoryStream(
                JsonSerializer.SerializeToUtf8Bytes(old))).GetAwaiter().GetResult());
            graph.PasteSnapshot(new GraphSnapshot([before.Nodes.Single()], []), 300, 100);
            var copy = graph.Nodes.Single(node => node.Id != sourceId);
            Require(SignalTimeline.Equal(copy.SignalKeys, graph.GetNode(sourceId).SignalKeys),
                "Copy/paste lost Signal timeline metadata.");
            graph.SelectNode(graph.GetNode(sourceId));
            var track = timeline.Tracks.Single(item => item.Node.Id == sourceId && item.SignalMode == SignalTimelineMode.Preview);
            timeline.SelectSignalKey(track, preview.Id);
            timeline.DeleteSignalKeyCommand.Execute(null);
            Require(graph.GetNode(sourceId).SignalKeys!.All(key => key.Id != preview.Id) && graph.History.Undo(),
                "Signal key deletion was not undoable.");
        }
        finally { window.Close(); }
    }

    private static void VerifyExport(GraphSnapshot root, string library)
    {
        var before = JsonSerializer.Serialize(root);
        ExpectInvalid(() => VfxProgram.Compile(root));
        using (var native = new EditorNativeHostSession(SpawnableGroupPackage.Compile(root), library))
        {
            native.Advance(0);
            native.Advance(0.5 / 60);
            Require(native.GameIoEvents.Count == 0, "A preview-only key escaped into runtime export.");
            native.Advance(1d / 60);
            native.Advance(2d / 60);
            Require(native.GameIoEvents.Count == 1 && native.GameIoEvents[0].PortName == "echo" &&
                native.GameIoEvents[0].ContainerId == Guid.Empty && native.GameIoEvents[0].InstanceId == 0,
                "Runtime key did not dispatch the named root Signal exactly once.");
        }
        using (var native = new EditorNativeHostSession(SpawnableGroupPackage.Compile(root, true), library))
        {
            native.Advance(0);
            native.Advance(2d / 60);
            Require(native.GameIoEvents.Count == 3, "Preview compilation did not include both track roles.");
        }
        Require(JsonSerializer.Serialize(root) == before, "Signal lowering mutated source graph data.");
    }

    private static void VerifyPlayback(GraphSnapshot graph, string library)
    {
        using var playback = new EditorPlayback();
        playback.EndText = "3";
        playback.Loop = false;
        using var native = new EditorNativePreviewSession(graph, library);
        void Sample() => native.EvaluateScene(playback.TimeSeconds, 1, playback.Snapshot);
        Sample();
        Require(native.GameIoEvents.Count == 0, "Opening a paused preview fired timeline keys.");
        playback.StepForwardCommand.Execute(null);
        Sample();
        Require(native.GameIoEvents.Count == 2, "Forward frame stepping skipped the start/current-frame Signal keys.");
        playback.Play();
        playback.Pause();
        Sample();
        Require(native.GameIoEvents.Count == 2, "Pause/Resume re-fired a Signal key.");
        playback.StepForwardCommand.Execute(null);
        Sample();
        Require(native.GameIoEvents.Count == 3, "Second frame step did not dispatch its preview key.");
        playback.Frame = 1;
        Sample();
        Require(native.GameIoEvents.Count == 3, "Seeking backwards fired a Signal key.");
        playback.StepForwardCommand.Execute(null);
        Sample();
        Require(native.GameIoEvents.Count == 4, "Forward-after-seek did not exclude the seek position and include the crossed key.");
        playback.StopCommand.Execute(null);
        Sample();
        playback.Play();
        Sample();
        Require(native.GameIoEvents.Count == 5, "Restart did not re-fire the start key.");
        playback.Pause();

        using var looping = new EditorPlayback();
        looping.EndText = "3";
        using var loopNative = new EditorNativePreviewSession(graph, library);
        void LoopSample() => loopNative.EvaluateScene(looping.TimeSeconds, 1, looping.Snapshot);
        LoopSample();
        looping.Play();
        LoopSample();
        looping.Advance(6d / 60);
        LoopSample();
        Require(looping.Frame == 0 && loopNative.GameIoEvents.Count == 7,
            "Coalesced full-loop advances dropped cycles or duplicated boundary keys.");
        looping.Pause();
        LoopSample();
        Require(loopNative.GameIoEvents.Count == 7, "Paused loop position dispatched more keys.");
    }

    private static GraphSnapshot VerifyScopes(string library)
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var source = graph.CreateNode(GraphNodeKind.Signal);
        graph.SetSignalKeys(source, [
            new(Guid.NewGuid(), 0, SignalTimelineMode.Runtime),
            new(Guid.NewGuid(), 0.5, SignalTimelineMode.Runtime)
        ], "Root spawns");
        var group = graph.CreateNode(GraphNodeKind.SpawnableGroup);
        Require(graph.EnterGraph(group), "Could not enter scoped Signal fixture.");
        var input = graph.CreateNode(new("SpawnInputSignal"));
        var output = graph.CreateNode(new("SpawnOutputSignal"));
        var life = graph.CreateNode(new("LifeTime"));
        graph.SetParameter(life, "seconds", JsonSerializer.SerializeToElement(0.5));
        graph.SetSignalKeys(input, [new(Guid.NewGuid(), 0.25, SignalTimelineMode.Runtime)], "Local Signal");
        Require(graph.TryConnectNodes(input, output, out _, "signal", "then"), "Could not connect scoped Signal.");
        var outputId = output.Id;
        graph.NavigateToGraph(0);
        source = graph.GetNode(source.Id);
        group = graph.GetNode(group.Id);
        Require(graph.TryConnectNodes(source, group, out _, "spawn"), "Could not connect scheduled spawns.");
        var snapshot = graph.CreateSnapshot();
        using var native = new EditorNativeHostSession(SpawnableGroupPackage.Compile(snapshot), library);
        native.Advance(0);
        native.Advance(1);
        var signals = native.Events.Where(item => item.Kind == 4).ToArray();
        Require(signals.Length == 2 && signals.All(item => item.ContainerId == group.Id &&
            item.SignalNodeId == outputId && item.InstanceId != 0) &&
            signals[0].InstanceId != signals[1].InstanceId &&
            signals[0].Time == 0.25 && signals[1].Time == 0.75,
            "Timeline dispatch lost per-instance local clocks or scoped Signal output identity.");
        Require(native.ActiveCount == 0, "Scoped Signal fixture failed natural completion.");
        return snapshot;
    }

    private static void VerifySubframePlayback(string library)
    {
        var graph = RootGraph();
        graph = graph with { Nodes = graph.Nodes.Select(node => node.Kind.Value == "InputSignal"
            ? node with { SignalKeys = [new(Guid.NewGuid(), 1d / 240, SignalTimelineMode.Preview)] } : node).ToArray() };
        using var playback = new EditorPlayback();
        playback.FpsText = "240";
        using var native = new EditorNativePreviewSession(graph, library);
        native.EvaluateScene(0, 1, playback.Snapshot);
        playback.StepForwardCommand.Execute(null);
        native.EvaluateScene(playback.TimeSeconds, 1, playback.Snapshot);
        Require(native.GameIoEvents.Count == 1 && native.EvaluatedTimeSeconds == 1d / 240,
            "A sub-60Hz frame step skipped its Signal key.");
        playback.Frame = 0;
        native.EvaluateScene(0, 1, playback.Snapshot);
        playback.Frame = 1;
        native.EvaluateScene(playback.TimeSeconds, 1, playback.Snapshot);
        Require(native.GameIoEvents.Count == 1, "Seek reconstruction re-sent a Signal notification.");
        playback.StepForwardCommand.Execute(null);
        native.EvaluateScene(playback.TimeSeconds, 1, playback.Snapshot);
        Require(native.GameIoEvents.Count == 1, "Forward-after-seek re-fired the seek-destination key.");
    }

    private static void VerifyScopedSeek(GraphSnapshot graph, string library)
    {
        using var playback = new EditorPlayback();
        using var native = new EditorNativePreviewSession(graph, library);
        playback.TimeSeconds = 0.6;
        var scene = JsonNode.Parse(native.EvaluateScene(0.6, 1, playback.Snapshot))!;
        Require(scene["activeInstances"]!.GetValue<int>() == 1 && native.Events.Count == 0,
            "Seek did not reconstruct spawned state silently.");
        playback.Play();
        playback.Advance(0.2);
        playback.Pause();
        native.EvaluateScene(playback.TimeSeconds, 1, playback.Snapshot);
        var signals = native.Events.Where(item => item.Kind == 4).ToArray();
        Require(signals.Length == 1 && signals[0].Time == 0.75,
            "A global seek cutoff incorrectly suppressed a later instance's local Signal key.");
        playback.TimeSeconds = 0.1;
        native.EvaluateScene(playback.TimeSeconds, 1, playback.Snapshot);
        Require(native.ActiveCount == 1 && native.Events.Count(item => item.Kind == 4) == 1,
            "Backward seek lost reconstructed state or re-sent notifications.");
    }

    private static void VerifyManualSend(GraphSnapshot graph, string library)
    {
        var node = graph.Nodes.Single(item => item.Kind.Value == "InputSignal").Id;
        using var playback = new EditorPlayback();
        using var native = new EditorNativePreviewSession(graph, library);
        native.EnableManualSignals();
        native.EvaluateScene(0, 1, playback.Snapshot);
        native.SendSignal(node);
        native.SendSignal(node);
        native.EvaluateScene(0, 1, playback.Snapshot);
        Require(native.GameIoEvents.Count == 2 && playback.Frame == 0 && !playback.IsPlaying,
            "Paused Send did not dispatch once per click without advancing time.");
        playback.StepForwardCommand.Execute(null);
        native.EvaluateScene(playback.TimeSeconds, 1, playback.Snapshot);
        Require(native.GameIoEvents.Count == 4, "Starting after Send duplicated exploratory-input notifications.");
        Require(!graph.Nodes.Any(item => item.SignalKeys?.Count > 3), "Send changed authored graph data.");
        var rejected = false;
        try { native.SendSignal(Guid.NewGuid()); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "Manual Send silently accepted an unknown/scope-less target.");
    }

    private static void VerifyGroupPersistence(GraphSnapshot graph)
    {
        var directory = Path.Combine(Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT")!,
            "signal-groups-" + Guid.NewGuid().ToString("N"));
        var library = new GroupAssetLibrary(directory);
        var asset = library.Create("Signal timeline", graph);
        Require(GraphSnapshot.ContentEquals(library.Load(asset.Id).Graph, graph),
            "Reusable group storage lost Signal key identity/mode/time.");
        File.WriteAllText(Path.Combine(directory, asset.Id.ToString("N") + GroupAssetLibrary.FileExtension),
            JsonSerializer.Serialize(asset with { FormatVersion = 4 }));
        ExpectInvalid(() => library.Load(asset.Id));
    }

    private static void VerifySender(GraphSnapshot graph, string library)
    {
        var signal = graph.Nodes.Single(node => node.Kind == GraphNodeKind.Signal).Id;
        using var playback = new EditorPlayback();
        PreviewPublication Publication(long sequence, long request, params PreviewSignalRequest[] requests) =>
            new(sequence, "signal-document", graph, GraphNodeDefinition.All, playback.Snapshot)
            { SignalRequestSequence = request, SignalRequests = requests };
        static int Instances(CompiledPreviewMessage message) =>
            JsonNode.Parse(message.EvaluatedRenderScene!)!["activeInstances"]!.GetValue<int>();
        using var sender = new CompiledPreviewSender(library);
        var first = sender.Prepare(Publication(1, 0))!;
        Require(first.Kind == CompiledPreviewKind.Program && Instances(first) == 1, "Initial keyed state was not reconstructed.");
        sender.Sent(first);
        var click = sender.Prepare(Publication(2, 1, new PreviewSignalRequest(1, signal)))!;
        Require(click.Kind == CompiledPreviewKind.Playback && Instances(click) == 2,
            "The preview publication did not deliver a paused Send to native execution.");
        sender.Sent(click);
        var coalesced = Publication(4, 3, new(1, signal), new(2, signal), new(3, signal));
        var two = sender.Prepare(coalesced)!;
        Require(Instances(two) == 4, "Coalesced publications lost consecutive Send clicks.");
        sender.Sent(two);
        Require(Instances(sender.Prepare(coalesced, true)!) == 4, "Snapshot/resync re-fired previous Send requests.");
        using (var fresh = new CompiledPreviewSender(library))
            Require(Instances(fresh.Prepare(coalesced)!) == 1, "A new connection replayed stale exploratory inputs.");
        var gap = Publication(7, 7, new PreviewSignalRequest(7, signal));
        var error = sender.Prepare(gap)!;
        Require(error.Kind == CompiledPreviewKind.CompileError, "Missing input history was silently dropped.");
        sender.Sent(error);
        Require(sender.Prepare(gap, true)!.Kind == CompiledPreviewKind.Program, "Explicit resync could not recover from an input-history gap.");
        var child = graph.Nodes.Single(node => node.Kind == GraphNodeKind.SpawnableGroup)
            .ChildGraph!.Nodes.Single(node => node.Kind.Value == "SpawnInputSignal").Id;
        Require(sender.Prepare(Publication(8, 8, new PreviewSignalRequest(8, child)))!.Kind == CompiledPreviewKind.CompileError,
            "The native preview silently broadcast an instance-less child Send.");
    }

    private static void VerifyOrdinaryGroup(string library)
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var group = graph.CreateNode(GraphNodeKind.GraphGroup);
        Require(graph.EnterGraph(group), "Could not enter ordinary Group.");
        var input = graph.CreateNode(new("GroupInputSignal"));
        graph.SetSignalKeys(input, [new(Guid.NewGuid(), 0.125, SignalTimelineMode.Runtime)], "Group cue");
        var child = graph.CreateNode(GraphNodeKind.SpawnableGroup);
        Require(graph.TryConnectNodes(input, child, out _, "spawn"), "Could not connect Group input.");
        Require(graph.EnterGraph(child), "Could not enter grouped template.");
        var life = graph.CreateNode(new("LifeTime"));
        graph.SetParameter(life, "seconds", JsonSerializer.SerializeToElement(0.5));
        graph.NavigateToGraph(0);
        using var native = new EditorNativeHostSession(SpawnableGroupPackage.Compile(graph.CreateSnapshot()), library);
        native.Advance(0);
        Require(native.ActiveCount == 0, "Group cue fired before its key.");
        native.Advance(0.25);
        Require(native.ActiveCount == 1 && native.Events.Any(item => item.Kind == 1 &&
            item.ContainerId == child.Id && item.Time == 0.125), "Ordinary Group lowering lost its named Signal cue.");
    }

    private static void ExpectInvalid(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid timeline export or legacy metadata was accepted.");
    }
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
