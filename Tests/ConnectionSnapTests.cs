using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels.Graph;
using LiberaFx.Views.Graph;

internal static class ConnectionSnapTests
{
    public static void Run()
    {
        foreach (var zoom in new[] { 1d, .25d })
        {
            var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
            var source = graph.CreateNode(new("Transform"), 80, 80);
            var target = graph.CreateNode(new("CpuParticle"), 440, 80);
            var view = new GraphEditorView { DataContext = graph };
            var window = new Window { Width = 1100, Height = 650, Content = view };
            window.Show();
            try
            {
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                typeof(GraphEditorView).GetMethod("SetZoom", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(view, [zoom, new Point(0, 0)]);
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                var sourceView = view.GetVisualDescendants().OfType<GraphNodeView>().Single(node => node.DataContext == source);
                var targetView = view.GetVisualDescendants().OfType<GraphNodeView>().Single(node => node.DataContext == target);
                var output = sourceView.GetVisualDescendants().OfType<Control>().Single(control =>
                    control.Tag is GraphOutputPortDefinition { Id: "value" });
                var input = targetView.GetVisualDescendants().OfType<Button>().Single(button =>
                    button.Tag is GraphInputPortDefinition { Id: "emitterMatrix" });
                var start = output.TranslatePoint(new Point(output.Bounds.Width / 2, output.Bounds.Height / 2), window)!.Value;
                var end = input.TranslatePoint(new Point(input.Bounds.Width / 2, input.Bounds.Height / 2), window)!.Value;
                var near = end + new Vector(-18, 0);
                var pointer = new Avalonia.Input.Pointer(201, PointerType.Mouse, true);
                var before = graph.History.CurrentNodeId;
                output.RaiseEvent(new PointerPressedEventArgs(output, pointer, window, start, 0,
                    new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
                output.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, output, pointer, window, near, 1,
                    new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
                Dispatcher.UIThread.RunJobs();
                if (!input.Classes.Contains("connection-target") || graph.History.CurrentNodeId != before)
                    throw new InvalidOperationException($"Compatible input must highlight without editing at {zoom:P0} zoom.");
                output.RaiseEvent(new PointerReleasedEventArgs(output, pointer, window, near, 2,
                    new(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
                if (graph.Connections.SingleOrDefault() is not { TargetPortId: "emitterMatrix" } ||
                    input.Classes.Contains("connection-target") || pointer.Captured is not null)
                    throw new InvalidOperationException($"Release near the pin must connect and clear feedback at {zoom:P0} zoom.");
                graph.History.Undo();
                if (graph.Connections.Count != 0 || graph.History.CurrentNodeId != before)
                    throw new InvalidOperationException("A snapped connection must use exactly one undo step.");
            }
            finally { window.Close(); }
        }
        Console.WriteLine("Connection snap at 100% and 25% zoom passed.");
    }
}
