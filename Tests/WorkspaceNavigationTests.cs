using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.ViewModels;
using LiberaFx.Views;

internal static class WorkspaceNavigationTests
{
    public static void Run()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var host = new GraphEditorView { DataContext = graph };
        var side = new Button { Content = "Preview remains visible" };
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,240"), Children = { host, side } };
        Grid.SetColumn(side, 1);
        var window = new Window { Width = 1200, Height = 700, Content = layout };
        window.Show();
        try
        {
            var group = graph.CreateNode(GraphNodeKind.GraphGroup);
            Require(graph.MaximizeInlineGraph(group), "Parent group must open.");
            var cpu = graph.CreateNode(new("CpuParticle"));
            var gpu = graph.CreateNode(new("GpuParticle"));
            var procedural = graph.CreateNode(new("ProceduralTexture"));
            var cpuId = cpu.Id; var gpuId = gpu.Id; var proceduralId = procedural.Id;
            Require(graph.MaximizeInlineGraph(cpu) && graph.Breadcrumbs.Count == 3, "CPU Particle establishes the common hierarchy.");
            graph.NavigateToGraph(1);
            var history = graph.History.CurrentNodeId;
            Require(graph.MaximizeInlineGraph(graph.GetNode(gpuId)), "GPU Particle must maximize.");
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var workspace = host.FindControl<ContentControl>("SpecializedWorkspace")!;
            var breadcrumbs = host.FindControl<StackPanel>("SpecializedBreadcrumbs")!;
            Require(workspace is { IsVisible: true, Content: GpuWorkspaceView } && side.IsEffectivelyVisible &&
                workspace.Bounds.Width <= host.Bounds.Width && workspace.Bounds.Height <= host.Bounds.Height &&
                breadcrumbs.Children.OfType<Button>().Count() == 1 && graph.GraphPath.Count == 1,
                "GPU must use the graph area and retain the parent hierarchy and preview.");
            var gpuView = (GpuWorkspaceView)workspace.Content!;
            gpuView.Workspace!.EnterKernel(gpuView.Workspace.State.Program.Kernels.First().Id);
            Require(breadcrumbs.Children.OfType<Button>().Count() == 2, "GPU kernels must extend the common hierarchy.");
            breadcrumbs.Children.OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(gpuView.Workspace.Kernel is null, "The GPU breadcrumb must return to its Flow.");
            host.GetVisualDescendants().OfType<Button>().Single(b => b.Tag is int depth && depth == 1)
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!workspace.IsVisible && graph.GraphPath.Count == 1 && graph.History.CurrentNodeId == history,
                "Parent navigation must close the specialized workspace without adding history.");
            Require(graph.MaximizeInlineGraph(graph.GetNode(proceduralId)), "Procedural texture must maximize.");
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Require(workspace is { IsVisible: true, Content: ProceduralWorkspaceView } &&
                breadcrumbs.Children.OfType<Button>().Count() == 1 && side.IsEffectivelyVisible &&
                workspace.Bounds.Width <= host.Bounds.Width && workspace.Bounds.Height <= host.Bounds.Height,
                "Procedural textures must share GPU/CPU bounds and hierarchy.");
            var other = new GraphWorkspaceViewModel(new UndoRedoManager());
            host.DataContext = other;
            Require(!workspace.IsVisible, "Switching documents must close the previous specialized workspace.");
            host.DataContext = graph;
            // The originating inline graph may be a different object from the document.
            graph.NavigateToGraph(0);
            var inline = graph.OpenInlineGraph(graph.GetNode(group.Id));
            Require(inline.MaximizeInlineGraph(inline.GetNode(gpuId)), "Inline GPU must maximize into the document area.");
            Require(graph.GraphPath.Count == 1 && workspace.IsVisible && workspace.Content is GpuWorkspaceView,
                "Inline GPU maximization must retain its absolute parent path.");
            Require(graph.History.CurrentNodeId == history, "Workspace navigation must not edit the effect.");
        }
        finally { window.Close(); }
        Console.WriteLine("CPU/GPU/procedural workspace navigation passed.");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
