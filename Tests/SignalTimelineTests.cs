using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Threading;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Preview;
using LiberaFx.Services.Project;
using LiberaFx.ViewModels.Graph;
using LiberaFx.ViewModels.Panels;
using LiberaFx.Views.Timeline;
using LiberaFx.Services.Compilation;

internal static class SignalTimelineTests
{
    public static void Run(string? output = null)
    {
        VerifyAutomaticTracks();
        var library = PreviewNativeLibrary.ResolveLibraryPath();
        VerifyAuthoring();
        var root = RootGraph();
        VerifyExport(root, library);
        var scoped = VerifyScopes(library);
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
        Console.WriteLine("Signal timeline editing/history/persistence and native scoped dispatch passed.");
    }

    public static void VerifyAutomaticTracks()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var first = graph.CreateNode(GraphNodeKind.Signal);
        var second = graph.CreateNode(GraphNodeKind.Signal);
        graph.CreateNode(GraphNodeKind.Float);
        var timeline = new TimelineViewModel(new ActiveGraphProvider(graph), new EditorPlayback());
        Require(timeline.Tracks.Count == 2 && timeline.Tracks.All(track => track.IsSignal && track.SignalKeys.Count == 0) &&
            timeline.SelectedTrack is null, "Empty Signal tracks must appear without selecting a Signal node or adding a key.");
        graph.DeleteNodes([first]);
        Require(timeline.Tracks.Count == 1 && timeline.Tracks.Single().Node.Id == second.Id,
            "Deleting a Signal must remove its automatic track.");
        graph.History.Undo();
        Require(timeline.Tracks.Count == 2, "Undo must restore an empty Signal track.");
        graph.CreateNode(GraphNodeKind.Signal);
        Require(timeline.Tracks.Count == 3, "Creating another Signal must append its track automatically.");
        var track = timeline.Tracks.Single(track => track.Node.Id == second.Id);
        timeline.Select(track);
        timeline.AddSignalKey(SignalTimelineMode.Preview, track, .5);
        Require(timeline.Tracks.Single(track => track.Node.Id == second.Id).SignalKeys.Single().TimeSeconds == .5,
            "An automatically created Signal track must accept timeline key edits.");
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
