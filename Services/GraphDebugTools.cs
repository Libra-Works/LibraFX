using System.ComponentModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.ViewModels;
using LiberaFx.Views;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LiberaFx.Services;

[McpServerToolType]
public sealed class GraphDebugTools
{
    [McpServerTool(Name = "libera_debug_graph_state", ReadOnly = true, OpenWorld = false)]
    [Description("Inspect the visible graph viewport, rendered node bounds, selection, zoom and scroll. Requires LIBERAFX_MCP_DEBUG=1.")]
    public static async Task<string> State(ActiveGraphProvider activeGraph) =>
        await Dispatcher.UIThread.InvokeAsync(() => JsonSerializer.Serialize(Inspect(FindView(activeGraph))));

    [McpServerTool(Name = "libera_debug_graph_screenshot", ReadOnly = true, OpenWorld = false)]
    [Description("Capture the active graph viewport as a PNG image. Requires LIBERAFX_MCP_DEBUG=1.")]
    public static async Task<CallToolResult> Screenshot(ActiveGraphProvider activeGraph) =>
        await Dispatcher.UIThread.InvokeAsync(() => new CallToolResult
        {
            Content = [ImageContentBlock.FromBytes(Capture(FindView(activeGraph)), "image/png")]
        });

    [McpServerTool(Name = "libera_debug_graph_pointer", OpenWorld = false)]
    [Description("Send a click, drag, or wheel event through the active graph's UI. Coordinates are viewport-local pixels. Requires LIBERAFX_MCP_DEBUG=1.")]
    public static async Task<string> Pointer(
        ActiveGraphProvider activeGraph,
        [Description("click, drag, or wheel")] string action,
        [Description("Viewport-local X coordinate.")] double x,
        [Description("Viewport-local Y coordinate.")] double y,
        [Description("left, right, or middle; ignored for wheel.")] string button = "left",
        [Description("Drag endpoint X; required for drag.")] double? endX = null,
        [Description("Drag endpoint Y; required for drag.")] double? endY = null,
        [Description("Wheel steps, from -10 to 10; required for wheel.")] double? wheel = null,
        [Description("Hold Control while using the pointer.")] bool control = false,
        [Description("Hold Shift while using the pointer.")] bool shift = false,
        [Description("Hold Alt while using the pointer.")] bool alt = false,
        [Description("Click count, 1 or 2.")] int clickCount = 1)
    {
        await Task.Delay(60);
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = FindView(activeGraph);
            SendPointer(view, action, x, y, button, endX, endY, wheel, control, shift, alt, clickCount);
            TopLevel.GetTopLevel(view)?.UpdateLayout();
            return JsonSerializer.Serialize(Inspect(view));
        });
    }

    [McpServerTool(Name = "libera_debug_graph_key", OpenWorld = false)]
    [Description("Send a graph shortcut key through the active graph UI. Requires LIBERAFX_MCP_DEBUG=1.")]
    public static async Task<string> Key(
        ActiveGraphProvider activeGraph,
        [Description("Avalonia key name, for example F, Space, Delete, or A.")] string key,
        [Description("Whether Control is held.")] bool control = false,
        [Description("Whether Shift is held.")] bool shift = false,
        [Description("Whether Alt is held.")] bool alt = false) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = FindView(activeGraph);
            if (!Enum.TryParse<Avalonia.Input.Key>(key, true, out var parsed) ||
                !Enum.IsDefined(parsed) || parsed == Avalonia.Input.Key.None)
                throw new McpException("Unknown key.");
            var modifiers = (control ? KeyModifiers.Control : KeyModifiers.None) |
                            (shift ? KeyModifiers.Shift : KeyModifiers.None) |
                            (alt ? KeyModifiers.Alt : KeyModifiers.None);
            var popup = view.FindControl<Popup>("NodeSearchPopup")!;
            var target = popup.IsOpen ? (InputElement)view.FindControl<TextBox>("NodeSearchBox")! : view;
            target.Focus();
            target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = parsed, KeyModifiers = modifiers });
            TopLevel.GetTopLevel(view)?.UpdateLayout();
            return JsonSerializer.Serialize(Inspect(view));
        });

    [McpServerTool(Name = "libera_debug_graph_type", OpenWorld = false)]
    [Description("Type text into the graph's open node search using UI text input. Requires LIBERAFX_MCP_DEBUG=1.")]
    public static async Task<string> TypeSearch(
        ActiveGraphProvider activeGraph,
        [Description("Text to type into the open node search, up to 200 characters.")] string text)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = FindView(activeGraph);
            if (string.IsNullOrEmpty(text) || text.Length > 200 || text.Any(char.IsControl))
                throw new McpException("Text must contain 1 to 200 non-control characters.");
            if (view.FindControl<Popup>("NodeSearchPopup") is not { IsOpen: true })
                throw new McpException("Open node search with the graph search shortcut first.");
            var input = view.FindControl<TextBox>("NodeSearchBox")!;
            input.Focus();
            input.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = text });
        });
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = FindView(activeGraph);
            TopLevel.GetTopLevel(view)?.UpdateLayout();
            return JsonSerializer.Serialize(Inspect(view));
        });
    }

    internal static GraphEditorView FindView(ActiveGraphProvider provider)
    {
        if (Environment.GetEnvironmentVariable("LIBERAFX_MCP_DEBUG") != "1")
            throw new McpException("Graph UI debugging is disabled. Start LIBERA FX with LIBERAFX_MCP_DEBUG=1 to enable it.");
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            throw new McpException("The desktop UI is unavailable.");
        return desktop.Windows.SelectMany(window => window.GetVisualDescendants().OfType<GraphEditorView>())
            .FirstOrDefault(view => ReferenceEquals(view.DataContext, provider.Graph) &&
                                    view.IsEffectivelyVisible && view.Bounds.Width > 0 && view.Bounds.Height > 0)
            ?? throw new McpException("The active graph view is not visible.");
    }

    internal static object Inspect(GraphEditorView view)
    {
        var viewport = view.FindControl<ScrollViewer>("GraphScrollViewer")!;
        var scene = view.FindControl<GraphSceneLayer>("SceneLayer")!;
        var graph = (GraphWorkspaceViewModel)view.DataContext!;
        var popup = view.FindControl<Popup>("NodeSearchPopup")!;
        var search = view.FindControl<TextBox>("NodeSearchBox")!;
        var results = view.FindControl<ListBox>("NodeSearchResults")!;
        var nodes = viewport.GetVisualDescendants().OfType<GraphNodeView>()
            .Where(item => item.DataContext is GraphNodeViewModel && item.IsEffectivelyVisible)
            .Select(item =>
            {
                var node = (GraphNodeViewModel)item.DataContext!;
                var start = item.TranslatePoint(default, viewport);
                var end = item.TranslatePoint(new Point(item.Bounds.Width, item.Bounds.Height), viewport);
                return new { id = node.Id, node.Name, node.IsSelected, topLeft = start, bottomRight = end };
            }).ToArray();
        return new
        {
            viewport = new { width = viewport.Viewport.Width, height = viewport.Viewport.Height,
                offsetX = viewport.Offset.X, offsetY = viewport.Offset.Y },
            zoom = view.DebugZoom,
            origin = scene.GraphOrigin,
            visibleArea = scene.VisibleArea,
            selectionCount = graph.SelectionCount,
            nodeSearch = new
            {
                isOpen = popup.IsOpen,
                query = popup.IsOpen ? search.Text : null,
                selectedIndex = popup.IsOpen ? results.SelectedIndex : -1,
                results = popup.IsOpen ? results.Items.OfType<NodeCatalogEntryViewModel>()
                    .Select(entry => new { entry.Name, entry.KindName, entry.Namespace, entry.Group }).ToArray() : []
            },
            nodes
        };
    }

    internal static byte[] Capture(GraphEditorView view)
    {
        var viewport = view.FindControl<ScrollViewer>("GraphScrollViewer")!;
        var size = viewport.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0 || size.Width > 4096 || size.Height > 4096)
            throw new McpException("Graph viewport dimensions are unavailable or too large.");
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)));
        bitmap.Render(viewport);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    internal static void SendPointer(GraphEditorView view, string action, double x, double y, string button,
        double? endX, double? endY, double? wheel, bool control, bool shift, bool alt, int clickCount)
    {
        var viewport = view.FindControl<ScrollViewer>("GraphScrollViewer")!;
        var top = TopLevel.GetTopLevel(view) ?? throw new McpException("The graph has no window.");
        var start = CheckedPoint(viewport, x, y);
        var rootStart = viewport.TranslatePoint(start, top) ?? throw new McpException("Cannot locate graph viewport.");
        var target = top.InputHitTest(rootStart) as InputElement
            ?? throw new McpException("No UI element at that position.");
        if (target is not Visual visual || !visual.GetSelfAndVisualAncestors().Contains(viewport))
            throw new McpException("The point is covered by another UI element.");

        var pointer = new Avalonia.Input.Pointer(0x4c46, PointerType.Mouse, true);
        var modifiers = (control ? KeyModifiers.Control : KeyModifiers.None) |
                        (shift ? KeyModifiers.Shift : KeyModifiers.None) |
                        (alt ? KeyModifiers.Alt : KeyModifiers.None);
        var rawModifiers = (control ? RawInputModifiers.Control : RawInputModifiers.None) |
                           (shift ? RawInputModifiers.Shift : RawInputModifiers.None) |
                           (alt ? RawInputModifiers.Alt : RawInputModifiers.None);
        if (clickCount is not (1 or 2))
            throw new McpException("Click count must be 1 or 2.");
        if (action.Equals("wheel", StringComparison.OrdinalIgnoreCase))
        {
            if (wheel is null || !double.IsFinite(wheel.Value) || wheel == 0 || Math.Abs(wheel.Value) > 10)
                throw new McpException("Wheel steps must be nonzero and between -10 and 10.");
            target.RaiseEvent(new PointerWheelEventArgs(target, pointer, top, rootStart, 0,
                new PointerPointProperties(rawModifiers, PointerUpdateKind.Other),
                modifiers, new Vector(0, wheel.Value)));
            return;
        }

        var (mouseButton, modifier, pressed, released) = button.ToLowerInvariant() switch
        {
            "left" => (MouseButton.Left, RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed, PointerUpdateKind.LeftButtonReleased),
            "right" => (MouseButton.Right, RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed, PointerUpdateKind.RightButtonReleased),
            "middle" => (MouseButton.Middle, RawInputModifiers.MiddleMouseButton, PointerUpdateKind.MiddleButtonPressed, PointerUpdateKind.MiddleButtonReleased),
            _ => throw new McpException("Button must be left, right, or middle.")
        };
        var isDrag = action.Equals("drag", StringComparison.OrdinalIgnoreCase);
        if (!isDrag && !action.Equals("click", StringComparison.OrdinalIgnoreCase))
            throw new McpException("Action must be click, drag, or wheel.");
        var end = isDrag
            ? CheckedPoint(viewport, endX ?? double.NaN, endY ?? double.NaN)
            : start;
        var rootEnd = viewport.TranslatePoint(end, top) ?? throw new McpException("Cannot locate drag endpoint.");
        var down = new PointerPointProperties(modifier | rawModifiers, pressed);
        var up = new PointerPointProperties(rawModifiers, released);
        try
        {
            target.RaiseEvent(new PointerPressedEventArgs(target, pointer, top, rootStart, 0, down, modifiers, clickCount));
            if (isDrag)
            {
                for (var i = 1; i <= 8; i++)
                {
                    var point = rootStart + (rootEnd - rootStart) * (i / 8d);
                    var recipient = pointer.Captured as InputElement ?? target;
                    recipient.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, recipient, pointer, top,
                        point, (ulong)i, down, modifiers));
                }
            }
            var releaseTarget = pointer.Captured as InputElement ?? target;
            releaseTarget.RaiseEvent(new PointerReleasedEventArgs(releaseTarget, pointer, top, rootEnd, 10, up, modifiers, mouseButton));
        }
        finally
        {
            pointer.Capture(null);
        }
    }

    private static Point CheckedPoint(ScrollViewer viewport, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 ||
            x >= viewport.Viewport.Width || y >= viewport.Viewport.Height)
            throw new McpException("Coordinates must be inside the visible graph viewport.");
        return new Point(x, y);
    }
}
