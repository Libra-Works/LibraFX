using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LibraGpu.Core;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using LiberaFx.Views;
using Line = LiberaFx.Views.GpuConnectionCurve;
using Pointer = Avalonia.Input.Pointer;

namespace LiberaFx.Tests;

internal static class GpuParticleDragTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static bool Near(Point a, Point b) => Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01;
    private static readonly PointerPointProperties Held = new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);

    public static void Run()
    {
        var appearance = new ThemeSettingsService();
        appearance.Apply(appearance.Current with { ColorScheme = "Dark" });
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var container = GpuParticleEditorService.Create(graph);
        var kernel = GpuParticleEditorService.ReadProgram(container).Kernels.Single(k => k.Stage == KernelStage.Update);
        var selected = kernel.Nodes.First(n => n.Inputs.Count > 0 && kernel.Nodes.Any(other => other.Inputs.Contains(n.Id)));
        var target = kernel.Nodes.First(n => n.Inputs.Contains(selected.Id));
        var targetInput = target.Inputs.IndexOf(selected.Id);
        GpuParticleEditorService.Apply(graph, container.Id,
        [
            new() { Action = "move", KernelId = kernel.Id, NodeId = selected.Id, Position = new(260, 180) },
            new() { Action = "move", KernelId = kernel.Id, NodeId = selected.Inputs[0], Position = new(30, 130) },
            new() { Action = "move", KernelId = kernel.Id, NodeId = target.Id, Position = new(490, 260) }
        ]);
        var view = new GpuWorkspaceView();
        var window = new Window { Content = view, Width = 1280, Height = 850, ShowActivated = false };
        try
        {
            view.Open(graph, container.Id);
            view.Workspace!.EnterKernel(kernel.Id);
            window.Show();
            Frame(window);
            var localization = LocalizationService.Shared;
            var originalLanguage = localization.SelectedLanguage;
            try
            {
                foreach (var (language, expected) in new[] { ("en", "Kernel Graph"), ("ja", "カーネルグラフ") })
                {
                    localization.SelectedLanguage = localization.Languages.Single(l => l.Id == language);
                    Frame(window);
                    var graphTab = view.FindControl<TabControl>("KernelTabs")!.Items.OfType<TabItem>().First();
                    Check(graphTab.Header?.ToString() == expected && graphTab.Header?.ToString() == localization.Get("Gpu.Visual"),
                        "Mounted artist-visible Kernel Graph tab is localized: " + language);
                }
            }
            finally { localization.SelectedLanguage = originalLanguage; }
            Frame(window);
            var canvas = view.FindControl<Canvas>("NodeCanvas")!;
            var border = Node(canvas, selected.Id);
            var viewport = canvas.FindAncestorOfType<ScrollViewer>()!;
            Check(Near(World(view, (Point)(viewport.Offset / Zoom(view))), default) &&
                new Rect(default(Point), viewport.Viewport).Intersects(new Rect(
                    border.TranslatePoint(default, viewport)!.Value, border.Bounds.Size)),
                $"Mounted initial viewport shows world origin and actual nodes: offset={viewport.Offset}, world={World(view, (Point)(viewport.Offset / Zoom(view)))}, node={border.TranslatePoint(default, viewport)}");
            var title = ((StackPanel)border.Child!).Children.OfType<TextBlock>().First();
            var outgoing = canvas.Children.OfType<Line>().Single(l =>
                Near(l.StartPoint, Output(border, canvas)) && Near(l.EndPoint, Input(Node(canvas, target.Id), targetInput, canvas)));
            var incoming = canvas.Children.OfType<Line>().Single(l => Near(l.EndPoint, Input(border, 0, canvas)));
            Check(Output(border, canvas).X > Input(border, 0, canvas).X,
                "Kernel ports have inputs on the left and output on the right");
            Check(outgoing.Data is StreamGeometry, "Displayed wire uses the retained cubic curve geometry");
            var wireObjects = canvas.Children.OfType<Line>().ToArray();
            var text = view.Workspace.Text;
            var diagnostics = view.Workspace.Diagnostics;
            var portable = GpuCompiler.Serialize(view.Workspace.State.Program);
            Check(view.Workspace.State.Drafts.Count == 0, "Opening/refreshing kernel code does not create a spurious text draft");
            var historyEvents = 0;
            var refreshEvents = 0;
            var programEvents = 0;
            graph.History.StateChanged += (_, _) => historyEvents++;
            graph.GpuProgramChanged += (_, _) => programEvents++;
            view.Workspace.Refreshed += (_, _) => refreshEvents++;
            Render(view, "before.png");

            foreach (var delta in new[] { new Vector(120, 0), new Vector(0, 90), new Vector(80, -40) })
            {
                if (delta.Y < 0)
                {
                    Wheel(canvas.FindAncestorOfType<ScrollViewer>()!, window, Math.Log(1.35) / Math.Log(1.15));
                    canvas.FindAncestorOfType<ScrollViewer>()!.Offset += new Vector(170, 100);
                    Frame(window);
                }
                var original = Origin(border, canvas);
                var stationary = Origin(Node(canvas, selected.Inputs[0]), canvas);
                var originalOutgoingEnd = outgoing.EndPoint;
                var originalIncomingStart = incoming.StartPoint;
                var start = title.TranslatePoint(new Point(20, 8), canvas)!.Value;
                var pointer = new Pointer(400, PointerType.Mouse, true);
                title.RaiseEvent(new PointerPressedEventArgs(title, pointer, window, Root(canvas, start, window), 0,
                    new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
                Check(ReferenceEquals(pointer.Captured, border), "GPU drag captures the retained full node body");
                var eventsBefore = historyEvents;
                var updatesBefore = refreshEvents;
                var publicationsBefore = programEvents;
                var before = graph.CreateSnapshot();
                for (var i = 1; i <= 4; i++)
                {
                    var partial = delta * (i / 4d);
                    Move(title, pointer, window, Root(canvas, start + partial, window));
                    Frame(window);
                    Check(Near(Origin(border, canvas), original + partial), "Rendered node follows pointer delta without coordinate jumps");
                    Check(ReferenceEquals(pointer.Captured, border), "Capture survives successive rendered drag frames");
                    Check(Near(outgoing.StartPoint, Output(border, canvas)) && Near(incoming.EndPoint, Input(border, 0, canvas)),
                        "Attached wire endpoints follow the actual rendered port coordinates");
                    Check(Near(outgoing.EndPoint, originalOutgoingEnd) && Near(incoming.StartPoint, originalIncomingStart) &&
                        Near(Origin(Node(canvas, selected.Inputs[0]), canvas), stationary), "Stationary nodes and opposite wire endpoints stay fixed");
                    Check(historyEvents == eventsBefore && refreshEvents == updatesBefore && programEvents == publicationsBefore &&
                        ReferenceEquals(view.Workspace.Text, text) && ReferenceEquals(view.Workspace.Diagnostics, diagnostics),
                        $"Drag frames must not commit/publish/rebuild: history={historyEvents - eventsBefore}, refresh={refreshEvents - updatesBefore}, publish={programEvents - publicationsBefore}, textSame={ReferenceEquals(view.Workspace.Text, text)}, diagnosticsSame={ReferenceEquals(view.Workspace.Diagnostics, diagnostics)}, drafts={view.Workspace.State.Drafts.Count}");
                }
                Check(Models.Graph.GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()), "Transient movement leaves serialized authoring state untouched");
                title.RaiseEvent(new PointerReleasedEventArgs(title, pointer, window, Root(canvas, start + delta, window), 5,
                    new(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
                Frame(window);
                Check(historyEvents == eventsBefore + 1 && programEvents == publicationsBefore + 1 && refreshEvents == updatesBefore,
                    "Release commits one layout-only undo/publication, not a full kernel refresh");
                Check(pointer.Captured is null && ReferenceEquals(Node(canvas, selected.Id), border) &&
                    wireObjects.SequenceEqual(canvas.Children.OfType<Line>()), "Release preserves node/connection objects and clears capture");
                Check(Near(Origin(border, canvas), original + delta) && Near(outgoing.StartPoint, Output(border, canvas)), "Committed node and wires keep final pointer position");
                graph.History.Undo();
                Frame(window);
                Check(Near(Origin(border, canvas), original) && Near(outgoing.StartPoint, Output(border, canvas)) &&
                    ReferenceEquals(Node(canvas, selected.Id), border), "Undo restores layout and live wires without rebuilding controls");
                graph.History.Redo();
                Frame(window);
                Check(Near(Origin(border, canvas), original + delta) &&
                    wireObjects.SequenceEqual(canvas.Children.OfType<Line>()), "Redo restores retained graph layout");
            }

            var floodStart = title.TranslatePoint(new Point(20, 8), canvas)!.Value;
            var floodOrigin = Origin(border, canvas);
            var floodPointer = new Pointer(401, PointerType.Mouse, true);
            title.RaiseEvent(new PointerPressedEventArgs(title, floodPointer, window, Root(canvas, floodStart, window), 0,
                new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
            var pendingMoves = Enumerable.Range(1, 1024).Select(i => new PointerEventArgs(InputElement.PointerMovedEvent,
                title, floodPointer, window, Root(canvas, floodStart + new Vector(i / 16d, i / 32d), window), (ulong)i, Held, KeyModifiers.None)).ToArray();
            var allocations = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            foreach (var move in pendingMoves) title.RaiseEvent(move);
            var dispatchMilliseconds = timer.Elapsed.TotalMilliseconds;
            var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocations;
            Check(Near(Origin(border, canvas), floodOrigin), "Flooded pointer events coalesce rather than performing per-event layout updates");
            Frame(window);
            Check(Near(Origin(border, canvas), floodOrigin + new Vector(64, 32)), "Next rendered frame uses the latest cursor, without smoothing lag");
            var beforeLoss = historyEvents;
            floodPointer.Capture(null);
            Frame(window);
            Check(historyEvents == beforeLoss + 1 && Near(Origin(border, canvas), floodOrigin + new Vector(64, 32)),
                "Capture loss commits the latest transient position once");

            var cancelOrigin = Origin(border, canvas);
            var cancelStart = title.TranslatePoint(new Point(20, 8), canvas)!.Value;
            var cancelPointer = new Pointer(402, PointerType.Mouse, true);
            title.RaiseEvent(new PointerPressedEventArgs(title, cancelPointer, window, Root(canvas, cancelStart, window), 0,
                new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
            Move(title, cancelPointer, window, Root(canvas, cancelStart + new Vector(40, 30), window));
            Frame(window);
            var beforeCancel = historyEvents;
            view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Frame(window);
            Check(cancelPointer.Captured is null && historyEvents == beforeCancel && Near(Origin(border, canvas), cancelOrigin) &&
                view.Workspace.KernelId == kernel.Id, "Escape cancels/reverts the gesture without leaving the kernel or adding history");
            Check(GpuCompiler.Serialize(view.Workspace.State.Program) == portable && ReferenceEquals(view.Workspace.Text, text) &&
                ReferenceEquals(view.Workspace.Diagnostics, diagnostics), "Layout-only editing preserves portable IR and cached source/diagnostics");
            Render(view, "after.png");
            VerifyBodyAndViewport(view, window, graph, canvas, selected.Id);
            VerifyClipboardAndDefaults(view.Workspace);
            VerifyGroupSelection(view, window, canvas);
            VerifyPortGestures(view, window, graph, canvas, kernel.Id);
            Console.WriteLine($"GPU routed drag: 1024 moves {dispatchMilliseconds:F2} ms, {allocatedBytes} dispatch bytes; rendered horizontal/vertical/scrolled-zoom, live wires, capture-loss/cancel and undo/redo passed.");
        }
        finally
        {
            view.CloseWorkspace();
            window.Close();
        }
    }
    private static Border Node(Canvas canvas, string id) => canvas.Children.OfType<Border>().Single(b => b.Tag as string == id);
    private static Point Origin(Border border, Canvas canvas) => border.TranslatePoint(default, canvas)!.Value;
    private static Button[] Ports(Border border) => border.GetVisualDescendants().OfType<Button>()
        .Where(b => b.Tag is string tag && tag.StartsWith("gpu-", StringComparison.Ordinal)).ToArray();
    private static Point Output(Border border, Canvas canvas)
    {
        var port = Ports(border)[0];
        return port.TranslatePoint(new Point(port.Bounds.Width / 2, port.Bounds.Height / 2), canvas)!.Value;
    }
    private static Point Input(Border border, int index, Canvas canvas)
    {
        var port = Ports(border)[index + 1];
        return port.TranslatePoint(new Point(port.Bounds.Width / 2, port.Bounds.Height / 2), canvas)!.Value;
    }
    private static Point Root(Canvas canvas, Point point, Window window) => canvas.TranslatePoint(point, window)!.Value;
    private static void Move(Control title, Pointer pointer, Window window, Point point) =>
        title.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, title, pointer, window, point, 0, Held, KeyModifiers.None));
    private static void Frame(Window window)
    {
        var arrived = false;
        window.RequestAnimationFrame(_ => arrived = true);
        var timer = Stopwatch.StartNew();
        while (!arrived && timer.Elapsed < TimeSpan.FromSeconds(3))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
        Check(arrived, "GPU UI render frame timed out");
        Dispatcher.UIThread.RunJobs();
    }
    private static void Render(GpuWorkspaceView view, string name)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)view.Bounds.Width, (int)view.Bounds.Height), new Vector(96, 96));
        bitmap.Render((Visual)TopLevel.GetTopLevel(view)!);
        var directory = Path.Combine(Environment.CurrentDirectory, "build", "gpu-drag-validation");
        Directory.CreateDirectory(directory);
        bitmap.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
    }
    private static double Zoom(Control view) => (double)view.GetType().GetProperty("DebugZoom",
        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
    private static Point World(Control view, Point canvasPoint) => (Point)view.GetType().GetMethod("ToGraphPoint",
        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [canvasPoint])!;
    private static void Wheel(ScrollViewer scroll, Window window, double delta, KeyModifiers modifiers = KeyModifiers.None)
    {
        var point = scroll.TranslatePoint(new Point(220, 150), window)!.Value;
        scroll.RaiseEvent(new PointerWheelEventArgs(scroll, new Pointer(450, PointerType.Mouse, true), window, point, 0,
            new(RawInputModifiers.None, PointerUpdateKind.Other), modifiers, new Vector(0, delta)));
    }
    private static void VerifyBodyAndViewport(GpuWorkspaceView view, Window window, GraphWorkspaceViewModel graph, Canvas canvas, string id)
    {
        var border = Node(canvas, id);
        var local = new Point(110, border.Bounds.Height - 4);
        var hit = border.InputHitTest(local) as Control;
        Check(hit is not null && hit is not Button, "Blank node body is a real independent drag hit target");
        var start = border.TranslatePoint(local, window)!.Value;
        var origin = Origin(border, canvas);
        var history = 0;
        graph.History.StateChanged += (_, _) => history++;
        var pointer = new Pointer(451, PointerType.Mouse, true);
        hit!.RaiseEvent(new PointerPressedEventArgs(hit, pointer, window, start, 0,
            new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
        Move(hit, pointer, window, start + new Vector(1, 1)); Frame(window);
        hit.RaiseEvent(new PointerReleasedEventArgs(hit, pointer, window, start + new Vector(1, 1), 1,
            new(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
        Check(history == 0 && Near(Origin(border, canvas), origin), "Body click/sub-threshold motion selects without movement/history");
        var worldDelta = new Vector(42.25, 17.5);
        pointer = new Pointer(452, PointerType.Mouse, true);
        hit.RaiseEvent(new PointerPressedEventArgs(hit, pointer, window, start, 0,
            new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
        Move(hit, pointer, window, start + worldDelta * Zoom(view)); Frame(window);
        Check(ReferenceEquals(pointer.Captured, border) && Near(Origin(border, canvas), origin + worldDelta),
            "Blank-body drag keeps its pointer offset at fractional zoom");
        hit.RaiseEvent(new PointerReleasedEventArgs(hit, pointer, window, start + worldDelta * Zoom(view), 1,
            new(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
        Frame(window);
        Check(history == 1, "Body drag creates exactly one undo");
        view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Z, KeyModifiers = KeyModifiers.Control });
        Frame(window);
        Check(Near(Origin(border, canvas), origin), "Focused GPU viewport uses normal configured undo gesture");
        var scroll = canvas.FindAncestorOfType<ScrollViewer>()!;
        var grid = canvas.Children.OfType<GraphSceneLayer>().Single();
        Check(grid.GridOnly && grid.VisibleArea.Width > 0 && grid.VisibleArea.Height > 0,
            "GPU viewport renders the same visible-world grid layer as the ordinary graph");
        var offset = scroll.Offset;
        var zoomBefore = Zoom(view);
        var anchor = new Point(220, 150);
        var anchorWorld = World(view, (Point)((offset + (Vector)anchor) / zoomBefore));
        Wheel(scroll, window, 1); Frame(window);
        Check(Math.Abs(Zoom(view) - zoomBefore * 1.15) < 0.0001 &&
            Near(canvas.Bounds.Position, default) &&
            Near(World(view, (Point)((scroll.Offset + (Vector)anchor) / Zoom(view))), anchorWorld),
            "Wheel uses ordinary 1.15 anchored world zoom");
        var mouse = new Pointer(453, PointerType.Mouse, true);
        var panStart = scroll.TranslatePoint(new Point(200, 200), window)!.Value;
        offset = scroll.Offset;
        scroll.RaiseEvent(new PointerPressedEventArgs(scroll, mouse, window, panStart, 0,
            new(RawInputModifiers.MiddleMouseButton, PointerUpdateKind.MiddleButtonPressed), KeyModifiers.None, 1));
        scroll.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, scroll, mouse, window, panStart + new Vector(45, 20), 1,
            new(RawInputModifiers.MiddleMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
        scroll.RaiseEvent(new PointerReleasedEventArgs(scroll, mouse, window, panStart + new Vector(45, 20), 2,
            new(RawInputModifiers.None, PointerUpdateKind.MiddleButtonReleased), KeyModifiers.None, MouseButton.Middle));
        Frame(window);
        Check(Near((Point)scroll.Offset, (Point)(offset - new Vector(45, 20))) && history == 2,
            "Configured middle-button pan moves viewport only, never authoring history");
        VerifyCpuGestureParity(view, window, border);
    }
    private static void VerifyCpuGestureParity(GpuWorkspaceView gpu, Window gpuWindow, Border gpuBorder)
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var node = graph.CreateNode(GraphNodeKind.Float, 260, 180);
        var cpu = new GraphEditorView { DataContext = graph };
        var window = new Window { Content = cpu, Width = 1280, Height = 850, ShowActivated = false };
        try
        {
            window.Show(); Frame(window);
            var cpuScroll = cpu.FindControl<ScrollViewer>("GraphScrollViewer")!;
            var gpuScroll = gpu.FindControl<ScrollViewer>("GpuGraphScrollViewer")!;
            Wheel(cpuScroll, window, 1); Frame(window);
            Wheel(gpuScroll, gpuWindow, Math.Log(Zoom(cpu) / Zoom(gpu)) / Math.Log(1.15)); Frame(gpuWindow);
            var cpuNode = cpu.GetVisualDescendants().OfType<GraphNodeView>().Single(v => v.DataContext == node);
            var header = cpuNode.FindControl<Control>("NodeHeader")!;
            var cpuStart = header.TranslatePoint(new Point(40, 12), window)!.Value;
            var gpuStart = gpuBorder.TranslatePoint(new Point(40, 12), gpuWindow)!.Value;
            var gpuCanvas = gpu.FindControl<Canvas>("NodeCanvas")!;
            var cpuBefore = new Point(node.X, node.Y);
            var gpuBefore = Origin(gpuBorder, gpuCanvas);
            var physicalDelta = new Vector(92, 46);
            foreach (var (source, root, start) in new[] { (header, window, cpuStart), ((Control)gpuBorder, gpuWindow, gpuStart) })
            {
                var pointer = new Pointer(455, PointerType.Mouse, true);
                source.RaiseEvent(new PointerPressedEventArgs(source, pointer, root, start, 0,
                    new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
                Move(source, pointer, root, start + physicalDelta); Frame(root);
                source.RaiseEvent(new PointerReleasedEventArgs(source, pointer, root, start + physicalDelta, 1,
                    new(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
                Frame(root);
            }
            Check(Near(new Point(node.X - cpuBefore.X, node.Y - cpuBefore.Y),
                Origin(gpuBorder, gpuCanvas) - gpuBefore),
                "Same routed press/move/release and wheel gestures produce equivalent ordinary/GPU world deltas");
            gpu.Workspace!.Graph.History.Undo(); Frame(gpuWindow);
        }
        finally { window.Close(); }
    }
    private static void VerifyClipboardAndDefaults(GpuWorkspaceViewModel vm)
    {
        var kernel = vm.Kernel!;
        var selected = kernel.Nodes.First(n => n.Inputs.Count > 0);
        var before = vm.Graph.CreateSnapshot();
        var text = vm.CopyNodes([selected.Id, selected.Inputs[0]]);
        var commits = 0;
        vm.Graph.History.StateChanged += (_, _) => commits++;
        var pasted = vm.PasteNodes(text, new(-50, 500));
        var cloned = vm.Kernel!.Nodes.Single(n => pasted.Contains(n.Id) && n.Op == selected.Op);
        Check(commits == 1 && pasted.Count == 2 && !pasted.Contains(selected.Id) &&
            pasted.Contains(cloned.Inputs[0]) && vm.State.Positions.Values.Any(p => p.X < 0),
            "GPU clipboard remaps internal edges/IDs and persists negative positions in one atomic undo");
        vm.Graph.History.Undo();
        Check(GraphSnapshot.ContentEquals(before, vm.Graph.CreateSnapshot()), "Undo removes the entire pasted subgraph and positions");
        before = vm.Graph.CreateSnapshot();
        var curl = vm.Add(GpuOp.CurlNoise, GpuType.Float);
        var operands = curl.Inputs.Select(id => vm.Kernel!.Nodes.Single(n => n.Id == id)).ToArray();
        Check(curl.Type == GpuType.Float3 && operands.Select(n => n.Type).SequenceEqual(
            new[] { GpuType.Float3, GpuType.UInt, GpuType.Int, GpuType.Float }) &&
            operands[2].Value.Single() == 4 && operands[3].Value.Single() == 0.5 && vm.TryCompile(out _),
            "Adding Curl Noise creates valid editable typed defaults and the inferred vector output");
        vm.Graph.History.Undo();
        Check(GraphSnapshot.ContentEquals(before, vm.Graph.CreateSnapshot()), "One undo removes operation and all its default operands");
    }
    private static void VerifyGroupSelection(GpuWorkspaceView view, Window window, Canvas canvas)
    {
        var vm = view.Workspace!;
        var a = vm.Add(GpuOp.Constant, GpuType.Float, new(80, 80));
        var b = vm.Add(GpuOp.Constant, GpuType.Float, new(380, 80));
        Frame(window);
        var borders = new[] { Node(canvas, a.Id), Node(canvas, b.Id) };
        foreach (var (border, modifiers) in new[] { (borders[0], KeyModifiers.None), (borders[1], KeyModifiers.Control) })
        {
            var point = border.TranslatePoint(new Point(100, border.Bounds.Height - 4), window)!.Value;
            var pointer = new Pointer(460, PointerType.Mouse, true);
            border.RaiseEvent(new PointerPressedEventArgs(border, pointer, window, point, 0,
                new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), modifiers, 1));
            border.RaiseEvent(new PointerReleasedEventArgs(border, pointer, window, point, 1,
                new(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), modifiers, MouseButton.Left));
        }
        var before = vm.Graph.CreateSnapshot();
        var origins = borders.Select(border => Origin(border, canvas)).ToArray();
        var start = borders[0].TranslatePoint(new Point(100, borders[0].Bounds.Height - 4), window)!.Value;
        var drag = new Pointer(461, PointerType.Mouse, true);
        var delta = new Vector(60, 25);
        var commits = 0;
        vm.Graph.History.StateChanged += (_, _) => commits++;
        borders[0].RaiseEvent(new PointerPressedEventArgs(borders[0], drag, window, start, 0,
            new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
        Move(borders[0], drag, window, start + delta * Zoom(view)); Frame(window);
        Check(borders.Select((border, i) => Near(Origin(border, canvas), origins[i] + delta)).All(match => match),
            "Ordinary Ctrl-selection drags multiple GPU nodes together");
        borders[0].RaiseEvent(new PointerReleasedEventArgs(borders[0], drag, window, start + delta * Zoom(view), 1,
            new(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
        Check(commits == 1, "Multi-node movement is one atomic undo");
        vm.Graph.History.Undo(); Frame(window);
        Check(GraphSnapshot.ContentEquals(before, vm.Graph.CreateSnapshot()), "Group undo restores every original coordinate");
        view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.A, KeyModifiers = KeyModifiers.Control });
        view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete });
        Frame(window);
        Check(vm.Kernel!.Nodes.Count == 0 && vm.Graph.Nodes.Any(n => n.Id == vm.ContainerId),
            "Select-all/Delete affects the GPU kernel, not its CPU container");
        view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Z, KeyModifiers = KeyModifiers.Control });
        Frame(window);
        Check(GraphSnapshot.ContentEquals(before, vm.Graph.CreateSnapshot()), "Keyboard undo restores deleted kernel nodes and writes");
    }
    private static void VerifyPortGestures(GpuWorkspaceView view, Window window, GraphWorkspaceViewModel graph, Canvas canvas, string kernel)
    {
        view.FindControl<Button>("ZoomLabel")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var source = view.Workspace!.Add(GpuOp.Constant, GpuType.Float);
        var target = view.Workspace.Add(GpuOp.Add, GpuType.Float);
        view.Workspace.Edit(
            new GpuEdit { Action = "move", KernelId = kernel, NodeId = source.Id, Position = new(80, 400) },
            new GpuEdit { Action = "move", KernelId = kernel, NodeId = target.Id, Position = new(400, 400) });
        Frame(window);
        var sourceBorder = Node(canvas, source.Id);
        var outputPin = Ports(sourceBorder)[0];
        var origin = Origin(sourceBorder, canvas);
        var start = Output(sourceBorder, canvas);
        var end = Input(Node(canvas, target.Id), 0, canvas);
        var pointer = new Pointer(403, PointerType.Mouse, true);
        var before = graph.CreateSnapshot();
        var committed = 0;
        graph.History.StateChanged += (_, _) => committed++;
        outputPin.RaiseEvent(new PointerPressedEventArgs(outputPin, pointer, window, Root(canvas, start, window), 0,
            new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
        var middle = start + (Vector)(end - start) * 0.5;
        outputPin.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, outputPin, pointer, window,
            Root(canvas, middle, window), 1, Held, KeyModifiers.None));
        Frame(window);
        var preview = canvas.Children.OfType<Line>().Single(l => l.StrokeDashArray?.Count > 0);
        Check(ReferenceEquals(pointer.Captured, outputPin) && Near(preview.StartPoint, start) && Near(preview.EndPoint, middle) &&
            Near(Origin(sourceBorder, canvas), origin) && committed == 0,
            $"Output-pin preview: capture={ReferenceEquals(pointer.Captured, outputPin)}, start={preview.StartPoint}/{start}, end={preview.EndPoint}/{middle}, node={Origin(sourceBorder, canvas)}/{origin}, commits={committed}");
        outputPin.RaiseEvent(new PointerReleasedEventArgs(outputPin, pointer, window, Root(canvas, end, window), 2,
            new(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
        Frame(window);
        Check(committed == 1 && pointer.Captured is null &&
            view.Workspace.Kernel!.Nodes.Single(n => n.Id == target.Id).Inputs[0] == source.Id &&
            !canvas.Children.OfType<Line>().Any(l => l.StrokeDashArray?.Count > 0), "Releasing on an input pin commits exactly one typed connection and removes only its preview");
        graph.History.Undo();
        Frame(window);
        Check(Models.Graph.GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()), "One undo restores the pre-connection IR");
        outputPin = Ports(Node(canvas, source.Id))[0];
        start = Output(Node(canvas, source.Id), canvas);
        pointer = new Pointer(404, PointerType.Mouse, true);
        outputPin.RaiseEvent(new PointerPressedEventArgs(outputPin, pointer, window, Root(canvas, start, window), 0,
            new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
        outputPin.RaiseEvent(new PointerReleasedEventArgs(outputPin, pointer, window, Root(canvas, start, window), 1,
            new(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
        var inputPin = Ports(Node(canvas, target.Id))[2];
        end = Input(Node(canvas, target.Id), 1, canvas);
        var beforeClick = committed;
        inputPin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Frame(window);
        Check(committed == beforeClick + 1 && view.Workspace.Kernel!.Nodes.Single(n => n.Id == target.Id).Inputs[1] == source.Id,
            "Output click then input click remains a distinct connection gesture");
        var code = view.FindControl<TextBox>("KernelText")!;
        view.FindControl<TabControl>("KernelTabs")!.SelectedIndex = 1;
        code.Text = "@Age = 12;";
        Frame(window);
        Check(view.Workspace.State.Drafts.TryGetValue(kernel, out var draft) && draft.Text == "@Age = 12;",
            "Ignoring programmatic text refreshes does not discard real code edits");
        var nodeCount = view.Workspace.Kernel!.Nodes.Count;
        code.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete });
        Frame(window);
        Check(view.Workspace.Kernel.Nodes.Count == nodeCount, "Text editor Delete is not intercepted as graph deletion");
    }
}
