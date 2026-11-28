using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;
using LiberaFx.Views.Graph;

internal static class GraphUxTests
{
    public static void Run()
    {
        _ = new ThemeSettingsService();
        SelectionAndUndo();
        WirePointerInput();
        ZoomHitTesting();
        BreadcrumbHistory();
        NestedOverlay();
        InlineRightClickOpensInnerContextMenu();
        WorkspaceNavigationTests.Run();
        Console.WriteLine("Graph wire selection, history, breadcrumbs and nested overlay passed.");
    }

    private static void InlineRightClickOpensInnerContextMenu()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var group = graph.CreateNode(GraphNodeKind.SpawnableGroup, 80, 80);
        graph.SetInlinePresentation(group, true, 640, 380);
        var outerView = new GraphEditorView { DataContext = graph };
        var window = new Window { Width = 1200, Height = 750, Content = outerView };
        window.Show();
        try
        {
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var arrived = false;
            window.RequestAnimationFrame(_ => arrived = true);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!arrived && sw.Elapsed < TimeSpan.FromSeconds(3))
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(2);
            }
            Dispatcher.UIThread.RunJobs();
            var innerView = outerView.GetVisualDescendants().OfType<GraphEditorView>().First(v => !ReferenceEquals(v, outerView));
            var innerCanvas = innerView.FindControl<Grid>("GraphCanvas")!;
            var innerBg = innerView.FindControl<Border>("GraphBackground")!;
            var pt = innerView.TranslatePoint(new Point(200, 150), window)!.Value;
            var hit = window.InputHitTest(pt);
            Require(hit is Visual v && v.GetSelfAndVisualAncestors().Any(a => ReferenceEquals(a, innerCanvas)),
                $"InputHitTest at {pt} hit {hit?.GetType().Name ?? "null"} instead of inner GraphCanvas!");
            var originalPan = ShortcutService.Shared.GraphPanButton;
            try
            {
                foreach (var panMode in new[] { "Right + Middle", "Middle" })
                {
                    ShortcutService.Shared.GraphPanButton = panMode;
                    var ptr = new Pointer(901, PointerType.Mouse, true);
                    innerBg.RaiseEvent(new PointerPressedEventArgs(innerBg, ptr, window, pt, 0,
                        new(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed), KeyModifiers.None, 1));
                    innerBg.RaiseEvent(new PointerReleasedEventArgs(innerBg, ptr, window, pt, 1,
                        new(RawInputModifiers.None, PointerUpdateKind.RightButtonReleased), KeyModifiers.None, MouseButton.Right));
                    Dispatcher.UIThread.RunJobs();
                    Require(innerCanvas.ContextMenu!.IsOpen,
                        $"Inner GraphCanvas.ContextMenu did not open in panMode={panMode}!");
                    innerCanvas.ContextMenu.Close();
                    Dispatcher.UIThread.RunJobs();
                }
            }
            finally
            {
                ShortcutService.Shared.GraphPanButton = originalPan;
            }
        }
        finally { window.Close(); }
    }

    private static void SelectionAndUndo()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var source = graph.CreateNode(GraphNodeKind.Float, 0, 0);
        var one = graph.CreateNode(GraphNodeKind.Add, 500, 0);
        var two = graph.CreateNode(GraphNodeKind.Add, 500, 250);
        Require(graph.TryConnectNodes(source, one, out _, "a") && graph.TryConnectNodes(source, two, out _, "a"), "Fixture wiring failed.");
        var before = graph.CreateSnapshot();
        var wires = graph.Connections.ToArray();
        graph.SelectConnection(wires[0]);
        graph.SelectConnection(wires[1], true);
        Require(graph.SelectedConnections.Count == 2 && wires.All(edge => edge.IsSelected), "Ctrl selection failed.");
        graph.SelectConnection(wires[0], true);
        Require(!wires[0].IsSelected && graph.SelectedConnections.Count == 1, "Ctrl toggle failed.");
        graph.SetSelection([], wires);
        Require(GraphSceneLayer.IntersectsConnection(wires[0], new Rect(wires[0].StartX - 1, wires[0].StartY - 1, 2, 2)),
            "Marquee must intersect wire geometry.");
        graph.InsertReroutesOnConnections(graph.SelectedConnections.ToArray());
        Require(graph.Nodes.Count(node => node.IsReroute) == 2 && graph.Connections.Count == 4, "Bulk reroute failed.");
        Require(graph.History.Undo() && GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()), "Bulk reroute was not one undo step.");
        Require(graph.History.Redo() && graph.Nodes.Count(node => node.IsReroute) == 2, "Bulk reroute redo failed.");
        Require(graph.History.Undo(), "Undo reroute failed.");
        var oneId = one.Id;
        var remainingWire = graph.Connections.Single(edge => edge.Target.Id == two.Id);
        graph.SetSelection([graph.GetNode(oneId)], [remainingWire]);
        graph.DeleteSelection();
        Require(graph.Nodes.All(node => node.Id != oneId) && graph.Connections.Count == 0, "Mixed deletion failed.");
        Require(graph.History.Undo() && GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()), "Mixed deletion was not one undo step.");
        Require(graph.History.Redo() && graph.Connections.Count == 0, "Mixed deletion redo failed.");
        var document = new GraphWorkspaceViewModel(new UndoRedoManager());
        var group = document.CreateNode(GraphNodeKind.GraphGroup);
        var nested = document.OpenInlineGraph(group);
        var nestedSource = nested.CreateNode(GraphNodeKind.Float);
        var nestedTarget = nested.CreateNode(GraphNodeKind.Add);
        Require(nested.TryConnectNodes(nestedSource, nestedTarget, out _, "a"), "Nested wire fixture failed.");
        var nestedBefore = document.CreateSnapshot();
        nested.InsertReroutesOnConnections(nested.Connections.ToArray());
        Require(nested.Nodes.Any(node => node.IsReroute) && document.History.Undo() &&
            GraphSnapshot.ContentEquals(nestedBefore, document.CreateSnapshot()), "Nested bulk edit did not undo at document level.");
        Require(document.History.Redo() && nested.Nodes.Any(node => node.IsReroute), "Nested bulk redo failed.");
    }

    private static void ZoomHitTesting()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var source = graph.CreateNode(GraphNodeKind.Float, 80, 80);
        var target = graph.CreateNode(GraphNodeKind.Add, 640, 80);
        Require(graph.TryConnectNodes(source, target, out _, "a"), "Zoom fixture wiring failed.");
        var view = new GraphEditorView { DataContext = graph };
        var window = new Window { Width = 1200, Height = 750, Content = view };
        window.Show();
        try
        {
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); view.FrameAll();
            typeof(GraphEditorView).GetMethod("SetZoom", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(view, [0.1, new Point(200, 200)]);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var canvas = view.FindControl<Grid>("GraphCanvas")!;
            var background = view.FindControl<Border>("GraphBackground")!;
            var edge = graph.Connections.Single();
            var middle = new Point((edge.StartX + edge.EndX) / 2 + view.OriginX,
                (edge.StartY + edge.EndY) / 2 + view.OriginY);
            var point = canvas.TranslatePoint(middle, window)!.Value + new Vector(0, 6);
            var pointer = new Pointer(811, PointerType.Mouse, true);
            background.RaiseEvent(new PointerPressedEventArgs(background, pointer, window, point, 0,
                new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
            Require(edge.IsSelected, "Wire hit tolerance must remain in screen pixels at 10% zoom.");
        }
        finally
        {
            window.Close();
        }
    }

    private static void WirePointerInput()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var source = graph.CreateNode(GraphNodeKind.Float, 80, 80);
        var first = graph.CreateNode(GraphNodeKind.Add, 640, 80);
        var second = graph.CreateNode(GraphNodeKind.Add, 640, 340);
        Require(graph.TryConnectNodes(source, first, out _, "a") && graph.TryConnectNodes(source, second, out _, "a"), "Pointer fixture wiring failed.");
        var view = new GraphEditorView { DataContext = graph };
        var window = new Window { Width = 1200, Height = 750, Content = view };
        window.Show();
        try
        {
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); view.FrameAll();
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var canvas = view.FindControl<Grid>("GraphCanvas")!;
            var background = view.FindControl<Border>("GraphBackground")!;
            var edges = graph.Connections.ToArray();
            Point Midpoint(GraphConnectionViewModel edge) => new(
                (edge.StartX + edge.EndX) / 2 + view.OriginX, (edge.StartY + edge.EndY) / 2 + view.OriginY);
            void Click(GraphConnectionViewModel edge, bool control, int pointerId)
            {
                var point = canvas.TranslatePoint(Midpoint(edge), window)!.Value;
                var pointer = new Pointer(pointerId, PointerType.Mouse, true);
                background.RaiseEvent(new PointerPressedEventArgs(background, pointer, window, point, 0,
                    new(RawInputModifiers.LeftMouseButton | (control ? RawInputModifiers.Control : RawInputModifiers.None),
                        PointerUpdateKind.LeftButtonPressed), control ? KeyModifiers.Control : KeyModifiers.None, 1));
            }
            Click(edges[0], false, 801);
            Require(graph.SelectedConnections.SequenceEqual([edges[0]]) && edges[0].IsSelected, "Click did not select a wire.");
            Click(edges[1], true, 802);
            Require(graph.SelectedConnections.Count == 2 && edges.All(edge => edge.IsSelected), "Ctrl-click did not extend wire selection.");
            Click(edges[0], true, 803);
            Require(!edges[0].IsSelected && edges[1].IsSelected, "Ctrl-click did not toggle a wire off.");
            Click(edges[0], true, 805);
            var before = graph.CreateSnapshot();
            var rightPoint = canvas.TranslatePoint(Midpoint(edges[0]), window)!.Value;
            var contextPointer = new Pointer(806, PointerType.Mouse, true);
            background.RaiseEvent(new PointerPressedEventArgs(background, contextPointer, window, rightPoint, 0,
                new(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed), KeyModifiers.None, 1));
            var menu = view.FindControl<MenuItem>("AddRerouteMenu")!;
            Require(menu.IsVisible, "Reroute context action is not available on the selected wire.");
            menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            contextPointer.Capture(null);
            Require(graph.Nodes.Count(node => node.IsReroute) == 2, "Context menu did not reroute all selected wires.");
            Require(graph.History.Undo() && GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()),
                "Context bulk reroute was not one undo step.");
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var start = canvas.TranslatePoint(new Point(view.OriginX + 40, view.OriginY + 40), window)!.Value;
            var end = canvas.TranslatePoint(new Point(view.OriginX + 1000, view.OriginY + 700), window)!.Value;
            var marquee = new Pointer(804, PointerType.Mouse, true);
            background.RaiseEvent(new PointerPressedEventArgs(background, marquee, window, start, 0,
                new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
            background.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, background, marquee, window, end, 1,
                new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
            background.RaiseEvent(new PointerReleasedEventArgs(background, marquee, window, end, 2,
                new(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            Require(graph.SelectedConnections.Count == 2 && graph.SelectedNodes.Count == 3,
                $"Marquee selected {graph.SelectedNodes.Count} nodes and {graph.SelectedConnections.Count} wires.");
            view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete });
            Require(graph.Nodes.Count == 0 && graph.Connections.Count == 0, "Delete key did not remove the selection.");
            Require(graph.History.Undo() && GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()), "Delete key was not a single undo step.");
        }
        finally
        {
            window.Close();
        }
    }

    private static void BreadcrumbHistory()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var document = new GraphDocumentViewModel(graph);
        document.MarkSaved("sample.lfxproj", "sample", graph.CreateSnapshot(), null);
        var group = graph.CreateNode(GraphNodeKind.GraphGroup);
        var groupId = group.Id;
        var before = graph.History.CurrentNodeId;
        Require(graph.EnterGraph(group), "Enter group failed.");
        var nested = graph.CreateNode(GraphNodeKind.GraphGroup);
        var nestedId = nested.Id;
        Require(graph.EnterGraph(nested), "Enter nested group failed.");
        before = graph.History.CurrentNodeId;
        Require(graph.Breadcrumbs[0].Name == "sample.lfxproj" && graph.CanNavigateBack, "Document breadcrumb/history missing.");
        graph.NavigateBack();
        Require(graph.GraphPath.SequenceEqual(new[] { groupId }) && graph.CanNavigateForward, "Back did not restore parent.");
        graph.NavigateForward();
        Require(graph.GraphPath.SequenceEqual(new[] { groupId, nestedId }), "Forward did not restore nested graph.");
        graph.NavigateBack();
        graph.NavigateToGraph(0);
        Require(!graph.CanNavigateForward && graph.History.CurrentNodeId == before, "New navigation must discard forward path without editing.");
        Require(graph.EnterGraph(graph.GetNode(groupId)), "Re-enter group failed.");
        graph.NavigateBack();
        graph.DeleteNode(graph.GetNode(groupId));
        Require(!graph.CanNavigateForward, "A deleted graph must not be a forward destination.");
        Require(graph.History.Undo() && graph.CanNavigateForward, "Undo must restore a valid navigation destination.");
    }

    private static void NestedOverlay()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var group = graph.CreateNode(GraphNodeKind.GraphGroup, 80, 80);
        graph.SetInlinePresentation(group, true, 620, 340);
        var child = graph.OpenInlineGraph(group);
        var source = child.CreateNode(GraphNodeKind.Float, 80, 80);
        var target = child.CreateNode(GraphNodeKind.Add, 400, 80);
        Require(child.TryConnectNodes(source, target, out _, "a"), "Nested fixture connection failed.");
        var view = new GraphEditorView { DataContext = graph };
        var window = new Window { Width = 1400, Height = 800, Content = view };
        window.Show();
        try
        {
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            view.FrameAll();
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var inner = view.GetVisualDescendants().OfType<GraphEditorView>().Single(editor => ReferenceEquals(editor.DataContext, child));
            var overlay = view.FindControl<GraphInteractionOverlay>("InteractionOverlay")!;
            var scroll = inner.FindControl<ScrollViewer>("GraphScrollViewer")!;
            var groupView = view.GetVisualDescendants().OfType<GraphNodeView>().Single(node => ReferenceEquals(node.DataContext, group));
            var sourceView = inner.GetVisualDescendants().OfType<GraphNodeView>().Single(node => ReferenceEquals(node.DataContext, source));
            var output = sourceView.GetVisualDescendants().OfType<Control>().Single(control => Equals(control.Tag, "OutputPort"));
            var start = output.TranslatePoint(new Point(output.Bounds.Width / 2, output.Bounds.Height / 2), window)!.Value;
            var right = scroll.TranslatePoint(new Point(scroll.Bounds.Width, 0), window)!.Value.X;
            var end = new Point(Math.Min(window.Bounds.Width - 40, right + 100), start.Y);
            Require(end.X > right + 8, "Fixture has no space outside the inline viewport.");
            var pointer = new Pointer(701, PointerType.Mouse, true);
            output.RaiseEvent(new PointerPressedEventArgs(output, pointer, window, start, 0,
                new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
            output.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, output, pointer, window, end, 1,
                new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Require(child.ConnectionPreviewPoint is not null, "Nested drag did not start a pending connection.");
            var overlayEnd = window.TranslatePoint(end, overlay)!.Value;
            var inlineRight = window.TranslatePoint(new Point(right, start.Y), overlay)!.Value.X;
            Require(overlayEnd.X > inlineRight && HasVisiblePixels(overlay, (int)inlineRight + 3),
                "Pending wire must render outside the clipped inline viewport.");
            output.RaiseEvent(new PointerReleasedEventArgs(output, pointer, window, end, 2,
                new(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            Require(child.PendingOutputNode is null, "Cancelled connection left pending state.");
            var targetView = inner.GetVisualDescendants().OfType<GraphNodeView>().Single(node => ReferenceEquals(node.DataContext, target));
            var header = targetView.FindControl<Border>("NodeHeader")!;
            var dragStart = header.TranslatePoint(new Point(20, 10), window)!.Value;
            var dragEnd = new Point(Math.Min(window.Bounds.Width - 40, right + 100), dragStart.Y);
            var dragPointer = new Pointer(702, PointerType.Mouse, true);
            header.RaiseEvent(new PointerPressedEventArgs(header, dragPointer, window, dragStart, 0,
                new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
            header.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, header, dragPointer, window, dragEnd, 1,
                new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Require(HasVisiblePixels(overlay, (int)inlineRight + 3), "Dragging a connected node must lift its wire beyond the child viewport.");
            header.RaiseEvent(new PointerReleasedEventArgs(header, dragPointer, window, dragEnd, 2,
                new(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            Require(!HasVisiblePixels(overlay, (int)inlineRight + 3), "The interaction overlay must clear after release.");
            Require(overlay.GetVisualAncestors().Contains(view) && groupView.GetVisualAncestors().Contains(view),
                "Overlay must be hosted by the document editor.");
        }
        finally
        {
            window.Close();
        }
    }

    private static bool HasVisiblePixels(Control control, int left)
    {
        var width = (int)Math.Ceiling(control.Bounds.Width);
        var height = (int)Math.Ceiling(control.Bounds.Height);
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
        bitmap.Render(control);
        var bytes = new byte[width * height * 4];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), bytes.Length, width * 4);
        }
        finally
        {
            handle.Free();
        }
        for (var y = 0; y < height; y++)
        {
            for (var x = Math.Max(0, left); x < width; x++)
            {
                if (bytes[(y * width + x) * 4 + 3] != 0)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
