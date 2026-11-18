using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using LiberaFx.Views;

internal static class InlineGroupInterfaceTests
{
    public static void Run()
    {
        var main = new MainViewModel();
        var root = main.ActiveDocument!.Workspace;
        var group = root.CreateNode(GraphNodeKind.GraphGroup, 40, 40);
        var groupId = group.Id;
        var rootNodeIds = root.Nodes.Select(node => node.Id).ToHashSet();
        var editor = new GraphEditorView { DataContext = root };
        var window = new Window
        {
            Title = "LIBERA FX isolated inline verification",
            Width = 1200, Height = 850, Content = editor
        };
        try
        {
            window.Show();
            Pump(window);
            Invoke("Inline.Toggle");
            Pump(window);
            Require(root.GetNode(groupId).IsInlineExpanded, "The bound expand action did not run.");
            var resizeName = LocalizationService.Shared.Get("Inline.Resize");
            var grip = editor.GetVisualDescendants().OfType<Border>().Single(control =>
                control.IsEffectivelyVisible && AutomationProperties.GetName(control) == resizeName);
            var initialWidth = root.GetNode(groupId).InlineWidth;
            var initialHeight = root.GetNode(groupId).InlineHeight;
            Drag(grip, window, new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), new Vector(88, 52));
            Require(root.GetNode(groupId).InlineWidth > initialWidth &&
                root.GetNode(groupId).InlineHeight > initialHeight, "The visible resize grip did not resize the group.");
            main.UndoCommand.Execute(null);
            Pump(window);
            Require(root.GetNode(groupId).InlineWidth == initialWidth &&
                root.GetNode(groupId).InlineHeight == initialHeight, "Undo did not restore the inline canvas size.");
            var inline = editor.GetVisualDescendants().OfType<GraphEditorView>()
                .Single(view => view.DataContext is GraphWorkspaceViewModel { IsInlineWorkspace: true });
            var child = (GraphWorkspaceViewModel)inline.DataContext!;
            var scalar = child.CreateNode(GraphNodeKind.Float, 80, 80);
            var scalarId = scalar.Id;
            child.SetSelection([]);
            root.SelectNode(root.GetNode(groupId));
            root.ActivateEditingWorkspace();
            Pump(window);
            var nodeView = inline.GetVisualDescendants().OfType<GraphNodeView>()
                .Single(view => ReferenceEquals(view.DataContext, scalar));
            Click(nodeView, window);
            Require(ReferenceEquals(main.ActiveGraphProvider.Graph, child) &&
                child.SelectedNode?.Id == scalarId && ReferenceEquals(editor.DataContext, root),
                "Real child input did not select its own workspace while retaining the parent canvas.");
            var edit = LiberaFxMcpTools.CreateNode(main.ActiveGraphProvider, "Float", 150, 100);
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!edit.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                Pump(window);
            }
            edit.WaitAsync(TimeSpan.Zero).GetAwaiter().GetResult();
            Require(child.Nodes.Count == 2 && rootNodeIds.SetEquals(root.Nodes.Select(node => node.Id)),
                "MCP creation did not edit the focused inline canvas exclusively.");
            main.UndoCommand.Execute(null);
            Pump(window);
            Require(child.Nodes.Single().Id == scalarId, "Shared Undo did not remove the MCP edit.");
            nodeView = editor.GetVisualDescendants().OfType<GraphNodeView>()
                .Single(view => view.DataContext is GraphNodeViewModel node && node.Id == scalarId);
            Click(nodeView, window);
            var handle = window.TryGetPlatformHandle()!.Handle;
            SendMessage(handle, 0x0100, 0x2e, 0);
            SendMessage(handle, 0x0101, 0x2e, 0);
            Pump(window);
            Require(child.Nodes.Count == 0 && rootNodeIds.SetEquals(root.Nodes.Select(node => node.Id)),
                "Delete from the focused inline canvas affected its parent or did not execute.");
            main.UndoCommand.Execute(null);
            Pump(window);
            Require(child.Nodes.Single().Id == scalarId, "Shared Undo did not restore the visible inline node.");
            Invoke("Inline.Maximize");
            Pump(window);
            Require(root.GraphPath.SequenceEqual([groupId]) && root.Nodes.Single().Id == scalarId,
                "The bound maximize action did not navigate to the child.");
            root.NavigateToGraph(0);
            Pump(window);
            Invoke("Inline.Toggle");
            Pump(window);
            Require(!root.GetNode(groupId).IsInlineExpanded &&
                !editor.GetVisualDescendants().OfType<GraphEditorView>().Any(view => view.IsEffectivelyVisible),
                "The bound collapse action left the child canvas visible.");
        }
        finally
        {
            window.Close();
            main.DisposeStorageFiles();
        }

        void Invoke(string key)
        {
            var name = LocalizationService.Shared.Get(key);
            var button = editor.GetVisualDescendants().OfType<Button>()
                .Single(control => control.IsEffectivelyVisible && AutomationProperties.GetName(control) == name);
            new ButtonAutomationPeer(button).GetProvider<IInvokeProvider>()!.Invoke();
        }
    }

    private static void Pump(Window window)
    {
        var frame = new DispatcherFrame();
        using var timer = DispatcherTimer.RunOnce(() => frame.Continue = false, TimeSpan.FromMilliseconds(150));
        Dispatcher.UIThread.PushFrame(frame);
        window.UpdateLayout();
    }

    private static void Click(Control control, Window window)
        => Drag(control, window, new Point(Math.Min(90, control.Bounds.Width / 2), 16), default);

    private static void Drag(Control control, Window window, Point start, Vector delta)
    {
        var handle = window.TryGetPlatformHandle()!.Handle;
        var from = Position(start);
        var to = Position(start + delta);
        SendMessage(handle, 0x0200, 0, from);
        SendMessage(handle, 0x0201, 1, from);
        Pump(window);
        if (from != to)
        {
            SendMessage(handle, 0x0200, 1, to);
            Pump(window);
        }
        SendMessage(handle, 0x0202, 0, to);
        Pump(window);

        nint Position(Point relative)
        {
            var point = control.PointToScreen(relative);
            var client = new NativePoint { X = point.X, Y = point.Y };
            Require(ScreenToClient(handle, ref client), "The isolated window has no native input coordinates.");
            return (nint)((client.Y << 16) | (client.X & 0xffff));
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint handle, ref NativePoint point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint handle, uint message, nuint wParam, nint lParam);
}
