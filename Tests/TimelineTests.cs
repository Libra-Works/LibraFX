using Avalonia.Controls;
using Avalonia.Threading;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using LiberaFx.Views;

internal static class TimelineTests
{
    public static void Run()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var node = graph.CreateNode(GraphNodeKind.FloatFCurve);
        var original = new FCurveSnapshot([
            new(Guid.NewGuid(), 0, 0, 0, 0.02, CurveInterpolation.Cubic),
            new(Guid.NewGuid(), 60, 1, 0.01, 0, CurveInterpolation.Linear)
        ]);
        graph.SetCurve(node, original, "Test curve");
        using var playback = new EditorPlayback();
        var active = new ActiveGraphProvider(graph);
        var timeline = new TimelineViewModel(active, playback);
        var curves = new CurveEditorViewModel(active, playback);
        var track = timeline.Tracks.Single();
        timeline.Retime(track, 30, 120);
        var retimed = node.Curve!;
        for (var frame = 0; frame <= 60; frame += 10)
            if (Math.Abs(FCurve.Evaluate(original, frame) - FCurve.Evaluate(retimed, 30 + frame * 2)) > 1e-9)
                throw new InvalidOperationException("Timeline duration edits did not preserve the curve's interpolation shape.");
        if (!graph.History.Undo() || !graph.GetNode(node.Id).Curve!.Keys.SequenceEqual(original.Keys) ||
            !graph.History.Redo() || !graph.GetNode(node.Id).Curve!.Keys.SequenceEqual(retimed.Keys))
            throw new InvalidOperationException("Timeline edits were not part of graph history.");
        var before = graph.CreateSnapshot();
        timeline.Retime(timeline.Tracks.Single(), 0, 0);
        if (!GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()))
            throw new InvalidOperationException("An invalid timeline duration changed graph data.");
        playback.Play();
        playback.Advance(1);
        if (playback.Frame != 60 || curves.PlayheadFrame != 60)
            throw new InvalidOperationException("Timeline and curve editor do not share their playhead.");
        playback.FpsText = "30";
        playback.Advance(1);
        if (playback.Frame != 90) throw new InvalidOperationException("Playback did not use its selected FPS.");
        playback.Advance(2);
        if (playback.Frame != 30) throw new InvalidOperationException("Playback did not wrap its loop range.");
        playback.Loop = false;
        playback.Advance(10);
        if (playback.Frame != 120 || playback.IsPlaying)
            throw new InvalidOperationException("Non-looping playback did not stop at the range end.");
        playback.StopCommand.Execute(null);
        if (playback.Frame != 0 || !GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()))
            throw new InvalidOperationException("Playback controls changed authored graph data.");
        var playbackState = playback.Snapshot;
        playback.StartText = "120";
        playback.EndText = "NaN";
        playback.FpsText = "0";
        playback.Frame = double.NaN;
        if (playback.Snapshot != playbackState)
            throw new InvalidOperationException("Invalid playback settings changed the effective clock.");
        playback.StepForwardCommand.Execute(null);
        playback.StepBackCommand.Execute(null);
        if (playback.Frame != 0)
            throw new InvalidOperationException("Single-frame stepping did not return to the original playhead.");
        using var project = new MemoryStream();
        new ProjectFileService().SaveAsync(project, graph).GetAwaiter().GetResult();
        project.Position = 0;
        var loaded = ProjectDocumentReader.LoadAsync(project).GetAwaiter().GetResult();
        if (!loaded.Graph.Nodes.Single().Curve!.Keys.SequenceEqual(retimed.Keys))
            throw new InvalidOperationException("Retimed graph curves did not persist.");
        active.SetGraph(new GraphWorkspaceViewModel(new UndoRedoManager()));
        if (timeline.Tracks.Count != 0)
            throw new InvalidOperationException("Switching documents retained the previous timeline tracks.");
        active.SetGraph(graph);
        if (timeline.Tracks.Count != 1)
            throw new InvalidOperationException("Returning to a document did not restore its timeline tracks.");
        var view = new TimelineView { DataContext = timeline };
        var window = new Window { Content = view, Width = 1100, Height = 350 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            if (view.FindControl<TimelineCanvas>("Canvas")!.Bounds.Width <= 0)
                throw new InvalidOperationException("The Timeline workspace did not create its editable canvas.");
        }
        finally { window.Close(); }
    }
}
