using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;
using LiberaFx.Services.Project;
using LiberaFx.ViewModels.Graph;
using LiberaFx.ViewModels.Panels;
using LiberaFx.Views.Controls;
using LiberaFx.Views.Graph;
using LiberaFx.Views.Panels;

internal static class ContainingSpawnableInspectorTests
{
    public static void Run()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var container = graph.CreateNode(GraphNodeKind.GraphGroup);
        graph.EnterGraph(container);
        var owner = graph.CreateNode(GraphNodeKind.SpawnableGroup);
        var ownerId = owner.Id;
        graph.EnterGraph(owner);
        var life = graph.CreateNode(new GraphNodeKind("LifeTime"), 80, 300);
        graph.SetParameter(life, "seconds", JsonSerializer.SerializeToElement(1.5));
        var itemId = graph.PublishGroupParameter(life, "seconds");
        var provider = new ActiveGraphProvider(graph);
        var pane = new NodePropertiesPaneViewModel(provider);
        var editor = new GraphEditorView { DataContext = graph };
        var inspector = new NodePropertiesView { DataContext = pane };
        var other = new Button { Content = "Commit edit" };
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,400"),
            RowDefinitions = new RowDefinitions("*,Auto") };
        layout.Children.Add(editor);
        Grid.SetColumn(inspector, 1);
        layout.Children.Add(inspector);
        Grid.SetRow(other, 1);
        layout.Children.Add(other);
        var window = new Window { Width = 1100, Height = 750, Content = layout };
        window.Show();
        try
        {
            Pump(window);
            Click(editor.FindControl<ScrollViewer>("GraphScrollViewer")!, window, new Point(20, 20));
            Pump(window);
            if (graph.SelectedNode is not null || !pane.IsInspectingContainingSpawnable ||
                pane.CanManageGroup || pane.CanConfigureGroupItems || graph.ContainingSpawnable?.Id != ownerId)
                throw new InvalidOperationException("Background click did not inspect the containing Spawnable separately from selection.");
            var parameter = pane.Parameters.Single();
            var stale = parameter;
            var box = inspector.GetVisualDescendants().OfType<ParameterEditorHost>()
                .Where(host => ReferenceEquals(host.DataContext, parameter))
                .SelectMany(host => host.GetVisualDescendants().OfType<TextBox>()).First();
            Click(box, window);
            box.SelectAll();
            SendMessage(window.TryGetPlatformHandle()!.Handle, 0x0102, (nuint)'3', 0);
            Pump(window);
            Click(other, window);
            AssertValue(graph, container.Id, ownerId, itemId, 3);
            if (graph.Breadcrumbs.Count != 3 || graph.SelectedNode is not null ||
                graph.GetNode(life.Id).Parameters!.Single(value => value.Id == "seconds").Value.GetDouble() != 1.5)
                throw new InvalidOperationException("Instance editing navigated away or rewrote the child template.");
            stale.NumericValue = 99;
            AssertValue(graph, container.Id, ownerId, itemId, 3);
            if (!graph.History.Undo() || graph.ContainingSpawnable?.GroupOverrides is { Count: > 0 } ||
                !graph.History.Redo())
                throw new InvalidOperationException("Containing Spawnable editing did not undo and redo.");
            AssertValue(graph, container.Id, ownerId, itemId, 3);
            pane.Parameters.Single().ResetCommand.Execute(null);
            if (graph.ContainingSpawnable?.GroupOverrides is { Count: > 0 } ||
                pane.Parameters.Single().NumericValue != 1.5 || !graph.History.Undo())
                throw new InvalidOperationException("Containing Spawnable reset did not restore the template default undoably.");
            AssertValue(graph, container.Id, ownerId, itemId, 3);
            var selectedStale = pane.Parameters.Single();
            graph.SelectNode(graph.GetNode(life.Id));
            selectedStale.NumericValue = 99;
            AssertValue(graph, container.Id, ownerId, itemId, 3);
            graph.SelectNode(null);
            var documentStale = pane.Parameters.Single();
            provider.SetGraph(new GraphWorkspaceViewModel(new UndoRedoManager()));
            documentStale.NumericValue = 99;
            AssertValue(graph, container.Id, ownerId, itemId, 3);
            if (pane.HasInspectorTarget)
                throw new InvalidOperationException("The containing inspector leaked into another document.");
            graph.NavigateToGraph(1);
            if (graph.ContainingSpawnable is not null)
                throw new InvalidOperationException("An ordinary group incorrectly inherited an ancestor inspector.");
            using var project = new MemoryStream();
            new ProjectFileService().SaveAsync(project, graph).GetAwaiter().GetResult();
            project.Position = 0;
            var loaded = ProjectDocumentReader.LoadAsync(project).GetAwaiter().GetResult();
            if (!GraphSnapshot.ContentEquals(graph.CreateSnapshot(), loaded.Graph))
                throw new InvalidOperationException("Containing instance edits did not persist.");
            graph.EnterGraph(graph.GetNode(ownerId));
            var curve = graph.CreateNode(GraphNodeKind.FloatFCurve);
            var templateKeyCount = curve.Curve!.Keys.Count;
            var curveItem = graph.PublishGroupCurves([curve]);
            graph.SelectNode(null);
            provider.SetGraph(graph);
            using var curveEditor = pane.CreateGroupCurveEditor();
            if (!pane.HasExposedCurves || curveEditor.Node?.Id != curve.Id)
                throw new InvalidOperationException("The containing Spawnable's published curve is unavailable.");
            curveEditor.AddKeyCommand.Execute(null);
            curveEditor.ValueText = "2";
            if (graph.ContainingSpawnable?.GroupOverrides?.Single(value => value.ItemId == curveItem)
                    .Components.Single().Curve?.Keys.Last().Value != 2 ||
                graph.GetNode(curve.Id).Curve!.Keys.Count != templateKeyCount)
                throw new InvalidOperationException("Containing curve editing did not preserve the template curve.");
        }
        finally { window.Close(); }
    }

    private static void AssertValue(GraphWorkspaceViewModel graph, Guid container, Guid owner, Guid item, double expected)
    {
        var node = graph.CreateSnapshot().Nodes.Single(node => node.Id == container).ChildGraph!.Nodes
            .Single(node => node.Id == owner);
        if (node.GroupOverrides?.Single(value => value.ItemId == item).Components.Single().Value?.GetDouble() != expected)
            throw new InvalidOperationException("The containing instance override is incorrect.");
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

    private static void Click(Control control, Window window, Point? positionInControl = null)
    {
        var handle = window.TryGetPlatformHandle()!.Handle;
        var point = control.PointToScreen(positionInControl ?? new Point(control.Bounds.Width / 2, control.Bounds.Height / 2));
        var client = new NativePoint { X = point.X, Y = point.Y };
        if (!ScreenToClient(handle, ref client)) throw new InvalidOperationException("Invalid window coordinates.");
        var position = (nint)((client.Y << 16) | (client.X & 0xffff));
        SendMessage(handle, 0x0200, 0, position);
        SendMessage(handle, 0x0201, 1, position);
        Pump(window);
        SendMessage(handle, 0x0202, 0, position);
        Pump(window);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint handle, ref NativePoint point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint handle, uint message, nuint wParam, nint lParam);
}
