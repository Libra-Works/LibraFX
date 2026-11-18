using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;
using LiberaFx.Views;

internal static class GraphFlowLayoutTests
{
    public static void Run()
    {
        _ = new ThemeSettingsService();
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var chain = Enumerable.Range(0, 9).Select(index => graph.CreateNode(GraphNodeKind.Add,
            -9000 + index * 12, 7000 + index * 8)).ToArray();
        for (var index = 1; index < chain.Length; index++)
            Require(graph.TryConnectNodes(chain[index - 1], chain[index], out _, "a"), "Could not connect the test chain.");
        var manual = graph.CreateNode(GraphNodeKind.Float, -7000, 7400);
        var before = graph.CreateSnapshot();
        var revision = graph.History.CurrentNodeId;
        var ids = chain.Select(node => node.Id).ToArray();
        var preview = graph.PreviewLayout(ids);
        Require(GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()) && graph.History.CurrentNodeId == revision,
            "Preview changed graph coordinates or history.");
        var repeated = graph.PreviewLayout(ids.Reverse().ToArray());
        Require(preview.Moves.SequenceEqual(repeated.Moves), "Layout must be deterministic, independent of ID-list order.");
        graph.ApplyLayout(preview.PreviewId);
        var arranged = graph.CreateSnapshot();
        Require(VfxProgram.Compile(before).ToBinary().SequenceEqual(VfxProgram.Compile(arranged).ToBinary()),
            "Layout must not change compiled effect behavior.");
        for (var index = 1; index < chain.Length; index++)
            Require(chain[index].X > chain[index - 1].X + chain[index - 1].RenderedWidth,
                "A chain longer than four nodes wrapped or lost dependency order.");
        Require(manual.X == -7000 && manual.Y == 7400, "Unselected manual coordinates moved.");
        Require(graph.History.Undo() && GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()) &&
            graph.History.Redo() && GraphSnapshot.ContentEquals(arranged, graph.CreateSnapshot()),
            "Layout must be exactly one undoable edit.");
        var stale = graph.PreviewLayout(ids);
        graph.RenameNode(graph.GetNode(ids[0]), "Changed after preview");
        Reject(() => graph.ApplyLayout(stale.PreviewId));
        var source = graph.GetNode(ids[0]);
        var fixedPreview = graph.PreviewLayout(ids, [source.Id], originX: -8000, originY: -1200);
        var fixedMove = fixedPreview.Moves.Single(move => move.NodeId == source.Id);
        Require(fixedMove.Fixed && fixedMove.DeltaX == 0 && fixedMove.DeltaY == 0, "A fixed node would move.");
        using (var stream = new MemoryStream())
        {
            new ProjectFileService().SaveAsync(stream, graph).GetAwaiter().GetResult();
            stream.Position = 0;
            var loaded = new ProjectFileService().LoadAsync(stream).GetAwaiter().GetResult();
            Require(GraphSnapshot.ContentEquals(loaded.Graph, graph.CreateSnapshot()), "Layout coordinates did not persist.");
        }
        VerifyPlanAndGeometry();
        Console.WriteLine("Deterministic flow layout, scope/fixed guards, stale previews, plans, geometry and Undo/Redo passed.");
    }

    private static void VerifyPlanAndGeometry()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var provider = new ActiveGraphProvider(graph);
        var prior = graph.CreateNode(GraphNodeKind.Float, 80, 80);
        var before = graph.CreateSnapshot();
        var nodes = Enumerable.Range(0, 8).Select(index => new LiberaFxMcpPlanNode("n" + index, "Add",
            "Long readable stage " + index + " with descriptive author text", IsPolymorphic: false)).ToArray();
        var edges = Enumerable.Range(1, 7).Select(index =>
            new LiberaFxMcpPlanConnection("n" + (index - 1), "n" + index, "a")).ToArray();
        Complete(LiberaFxMcpTools.ApplyGraphPlan(provider, nodes, edges, layout: "flow"));
        var added = graph.Nodes.Where(node => !before.Nodes.Any(original => original.Id == node.Id)).ToArray();
        Require(added.Length == 8 && added.Zip(added.Skip(1)).All(pair => pair.First.X < pair.Second.X) &&
            prior.X == 80 && prior.Y == 80, "Flow plans must only position their new nodes.");
        Require(graph.History.Undo() && GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()) && graph.History.Redo(),
            "Creation plus automatic layout must share one history edit.");
        var fork = graph.CreateNode(GraphNodeKind.Add, 900, 200);
        var merge = graph.CreateNode(GraphNodeKind.Add, 1200, 200);
        var first = graph.GetNode(added[0].Id);
        var second = graph.GetNode(added[1].Id);
        Require(graph.TryConnectNodes(first, fork, out _, "a") &&
            graph.TryConnectNodes(fork, merge, out _, "a") &&
            graph.TryConnectNodes(second, merge, out _, "b"), "Could not create branch/merge fixture.");
        var group = graph.CreateNode(GraphNodeKind.GraphGroup, -4000, -3000);
        graph.SetInlinePresentation(group, true, 880, 480);
        var view = new GraphEditorView { DataContext = graph };
        var window = new Window { Width = 1400, Height = 900, Content = view };
        window.Show();
        try
        {
            Pump(window);
            var preview = graph.PreviewLayout(graph.Nodes.Select(node => node.Id).ToArray(), originX: -2500, originY: -2000);
            graph.ApplyLayout(preview.PreviewId);
            Pump(window);
            var rendered = view.GetVisualDescendants().OfType<GraphNodeView>().ToArray();
            foreach (var node in graph.Nodes)
            {
                var visual = rendered.Single(item => ReferenceEquals(item.DataContext, node));
                var box = GraphFlowLayout.Bounds(node);
                Require(visual.Bounds.Height <= box.Height + 1 && visual.Bounds.Width <= box.Width + 1,
                    "Layout dimensions underestimated an actual rendered node.");
            }
            var boxes = graph.Nodes.Select(GraphFlowLayout.Bounds).ToArray();
            for (var index = 0; index < boxes.Length; index++)
                for (var other = index + 1; other < boxes.Length; other++)
                    Require(!boxes[index].Intersects(boxes[other]), "Flow layout overlaps rendered nodes or expanded groups.");
            var snapshot = graph.CreateSnapshot();
            view.FrameAll();
            Pump(window);
            CaptureReview(view, "flow-overview.png");
            graph.NavigateToNode([], group.Id);
            Pump(window);
            CaptureReview(view, "flow-group.png");
            Require(GraphSnapshot.ContentEquals(snapshot, graph.CreateSnapshot()),
                "Framing a wide graph or selected node must not reposition it.");
        }
        finally { window.Close(); }
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
    private static void CaptureReview(Control view, string name)
    {
        if (Environment.GetEnvironmentVariable("LIBERAFX_LAYOUT_REVIEW") is not { Length: > 0 } directory) return;
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Use an absolute review output path.");
        Directory.CreateDirectory(directory);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)view.Bounds.Width, (int)view.Bounds.Height));
        bitmap.Render(view);
        using var stream = File.Create(Path.Combine(directory, name));
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
    }
    private static T Complete<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            using var timeout = DispatcherTimer.RunOnce(() => frame.Continue = false, TimeSpan.FromSeconds(15));
            _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
        }
        if (!task.IsCompleted) throw new TimeoutException("Layout MCP timed out.");
        return task.GetAwaiter().GetResult();
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return; }
        throw new InvalidOperationException("Stale layout was accepted.");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
