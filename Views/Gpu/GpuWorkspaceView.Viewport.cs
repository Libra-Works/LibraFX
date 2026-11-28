using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LibraGpu.Core;
using LiberaFx.Services.Compilation;
using LiberaFx.Services.Gpu;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Panels;

namespace LiberaFx.Views.Gpu;

public partial class GpuWorkspaceView : UserControl
{
    internal Point ToGraphPoint(Point point) => _viewport.ToGraphPoint(point);
    internal double DebugZoom => _viewport.Zoom;
    private static Rect NodeBounds(IEnumerable<NodeVisual> nodes)
    {
        var all = nodes.Select(n => new Rect(n.Origin,
            new Size(n.Border.Bounds.Width > 0 ? n.Border.Bounds.Width : 230,
                n.Border.Bounds.Height > 0 ? n.Border.Bounds.Height : 80))).ToArray();
        if (all.Length == 0) return new Rect(0, 0, 1, 1);
        var left = all.Min(r => r.Left); var top = all.Min(r => r.Top);
        return new Rect(left, top, all.Max(r => r.Right) - left, all.Max(r => r.Bottom) - top);
    }
    private void OnViewportChanged()
    {
        foreach (var visual in _nodeVisuals.Values) { visual.CanvasOrigin = _viewport.Origin; visual.SyncTransform(); }
        foreach (var wire in _wireVisuals) { wire.Curve.Width = NodeCanvas.Width; wire.Curve.Height = NodeCanvas.Height; }
        if (_connectionDrag is { } connection)
        { connection.Preview.Width = NodeCanvas.Width; connection.Preview.Height = NodeCanvas.Height; }
        if (_stageInputDrag is { } inputDrag)
        { inputDrag.Preview.Width = NodeCanvas.Width; inputDrag.Preview.Height = NodeCanvas.Height; }
        UpdateWireGeometry(_wireVisuals);
        UpdateStageOutputAnchors();
        ZoomLabel.Content = $"{_viewport.Zoom:P0}";
    }
    private void OnZoomIn(object? sender, RoutedEventArgs e) => _viewport.SetZoom(_viewport.Zoom * 1.25);
    private void OnZoomOut(object? sender, RoutedEventArgs e) => _viewport.SetZoom(_viewport.Zoom / 1.25);
    private void OnResetZoom(object? sender, RoutedEventArgs e) => _viewport.SetZoom(1);
    private void OnArrange(object? sender, RoutedEventArgs e) => Run(() =>
    {
        FinishDrag(commit: false);
        Workspace!.ArrangeNodes();
        _viewport.Frame(NodeBounds(_nodeVisuals.Values));
    });
    private void OnFrame(object? sender, RoutedEventArgs e)
    {
        if (_selectedNodes.Count > 0)
            _viewport.Frame(NodeBounds(_selectedNodes.Where(_nodeVisuals.ContainsKey).Select(id => _nodeVisuals[id])));
    }
    private void OnFrameAll(object? sender, RoutedEventArgs e)
    {
        if (_nodeVisuals.Count > 0)
            _viewport.Frame(NodeBounds(_nodeVisuals.Values));
    }
    private static string OpDescription(GpuOp op) =>
        LocalizationService.Shared.GetOrDefault($"Gpu.OpDesc.{op}", op.ToString());

    private void AddPaletteNode(GpuWorkspaceViewModel vm, GpuOp op, GpuPosition? position = null) => Run(() =>
    {
        var type = TypeChoice.SelectedItem is GpuType selected ? selected : GpuType.Float;
        if (op == GpuOp.ReadUniform && !ParticleAbi.Uniforms.Values.Contains(type))
            throw new ArgumentException("Uniforms support Float and Float3.");
        if (op == GpuOp.ReadAttribute && !ParticleAbi.Attributes.Any(a => a.Type == type))
            throw new ArgumentException("No ABI attribute has this type.");
        SelectVisualNode(vm.Add(op, type, position).Id);
    });

    private void AttachPaletteDrag(Button button, GpuOp op)
    {
        button.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
            _paletteDragSource = button;
            _palettePressed = e;
            _paletteDragStart = e.GetPosition(this);
        }, RoutingStrategies.Tunnel);

        button.AddHandler(InputElement.PointerMovedEvent, async (_, e) =>
        {
            if (_paletteDragSource != button || _palettePressed is not { } pressed ||
                !e.GetCurrentPoint(button).Properties.IsLeftButtonPressed)
                return;
            var current = e.GetPosition(this);
            if (Math.Abs(current.X - _paletteDragStart.X) < 5 && Math.Abs(current.Y - _paletteDragStart.Y) < 5)
                return;
            _paletteDragSource = null;
            _palettePressed = null;
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText("gpu-op:" + op));
            await DragDrop.DoDragDropAsync(pressed, transfer, DragDropEffects.Copy);
        }, RoutingStrategies.Tunnel);

        button.AddHandler(InputElement.PointerReleasedEvent, (_, _) =>
        {
            if (_paletteDragSource == button) { _paletteDragSource = null; _palettePressed = null; }
        }, RoutingStrategies.Tunnel);

        button.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) =>
        {
            if (_paletteDragSource == button) { _paletteDragSource = null; _palettePressed = null; }
        }, RoutingStrategies.Tunnel);
    }

    private void OnNodeCanvasDragOver(object? sender, DragEventArgs e)
    {
        if (Workspace is { Kernel: not null } &&
            e.DataTransfer.TryGetText() is { } text &&
            text.StartsWith("gpu-op:", StringComparison.Ordinal))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnNodeCanvasDrop(object? sender, DragEventArgs e)
    {
        if (Workspace is not { Kernel: not null } vm ||
            e.DataTransfer.TryGetText() is not { } text ||
            !text.StartsWith("gpu-op:", StringComparison.Ordinal) ||
            !Enum.TryParse<GpuOp>(text["gpu-op:".Length..], out var op))
            return;

        var point = _viewport.ToGraphPoint(e.GetPosition(NodeCanvas));
        AddPaletteNode(vm, op, new(point.X, point.Y));
        e.Handled = true;
    }
    private void OpenGraphContext(Point position)
    {
        if (Workspace is not { Kernel: not null } vm) return;
        Focus(); _pastePosition = position;
        var add = new MenuItem { Header = LocalizationService.Shared.Get("Gpu.AddNode") };
        add.ItemsSource = vm.Palette.Select(op =>
        {
            var item = new MenuItem { Header = op.DisplayName };
            ToolTip.SetTip(item, OpDescription(op.Op));
            item.Click += (_, _) => Run(() => SelectVisualNode(vm.Add(op.Op,
                TypeChoice.SelectedItem is GpuType type ? type : GpuType.Float, new(position.X, position.Y)).Id));
            return item;
        }).ToArray();
        var remove = new MenuItem { Header = LocalizationService.Shared.Get("Gpu.Delete"), IsEnabled = _selectedNodes.Count > 0 };
        remove.Click += (_, _) => DeleteSelection();
        var copy = new MenuItem { Header = LocalizationService.Shared.Get("Gpu.Copy"), IsEnabled = _selectedNodes.Count > 0 };
        copy.Click += async (_, _) => await CopySelectionAsync(false);
        var cut = new MenuItem { Header = LocalizationService.Shared.Get("Gpu.Cut"), IsEnabled = _selectedNodes.Count > 0 };
        cut.Click += async (_, _) => await CopySelectionAsync(true);
        var paste = new MenuItem { Header = LocalizationService.Shared.Get("Gpu.Paste") };
        paste.Click += async (_, _) => await PasteAsync();
        NodeCanvas.ContextMenu = new ContextMenu { ItemsSource = new Control[] { add, new Separator(), copy, cut, paste, remove } };
        NodeCanvas.ContextMenu.Open(NodeCanvas);
    }
    private void DeleteSelection()
    {
        if (Workspace?.KernelId is not { } kernel || _selectedNodes.Count == 0) return;
        var edits = _selectedNodes.Select(id => new GpuEdit { Action = "remove", KernelId = kernel, NodeId = id }).ToArray();
        Run(() => Workspace.Edit(edits[0], edits[1..]));
    }
    private async Task CopySelectionAsync(bool cut)
    {
        if (Workspace is not { } vm || _selectedNodes.Count == 0) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("Clipboard unavailable.");
            var text = vm.CopyNodes(_selectedNodes);
            var revision = vm.Graph.History.CurrentNodeId;
            var kernel = vm.KernelId;
            var ids = _selectedNodes.ToArray();
            await clipboard.SetTextAsync(text);
            if (cut && Workspace == vm && vm.KernelId == kernel && vm.Graph.History.CurrentNodeId == revision)
            {
                var edits = ids.Select(id => new GpuEdit { Action = "remove", KernelId = vm.KernelId, NodeId = id }).ToArray();
                Run(() => vm.Edit(edits[0], edits[1..]));
            }
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or ArgumentException)
        { Status.Text = error.Message; }
    }
    private async Task PasteAsync()
    {
        if (Workspace is not { } vm) return;
        var kernel = vm.KernelId;
        try
        {
            var text = await (TopLevel.GetTopLevel(this)?.Clipboard?.TryGetTextAsync() ?? Task.FromResult<string?>(null));
            if (Workspace != vm || vm.KernelId != kernel || text is null) return;
            var ids = vm.PasteNodes(text, new(_pastePosition.X, _pastePosition.Y));
            _selectedNodes.Clear(); _selectedNodes.UnionWith(ids); _selection = ids.FirstOrDefault();
            UpdateSelectionStyles(); BuildInspector();
            _pastePosition += new Vector(24, 24);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or ArgumentException or System.Text.Json.JsonException)
        { Status.Text = error.Message; }
    }
    private void OnBackgroundPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source != NodeCanvas || !e.GetCurrentPoint(NodeCanvas).Properties.IsLeftButtonPressed) return;
        Focus(); FinishDrag(false); CancelConnectionDrag(); CancelMarquee(); _wireSource = null;
        var start = _viewport.ToGraphPoint(e.GetPosition(NodeCanvas)); _pastePosition = start;
        var box = new Border { IsHitTestVisible = false, BorderThickness = new Thickness(1),
            BorderBrush = ThemeSettingsService.EditorBrush("NodeSelectedBorder"),
            Background = this.FindResource("EffectSelectionFillBrush") as IBrush };
        NodeCanvas.Children.Add(box);
        _marquee = (e.Pointer, start, e.KeyModifiers.HasFlag(KeyModifiers.Control) ? _selectedNodes.ToArray() : [], box);
        e.Pointer.Capture(NodeCanvas); e.Handled = true;
    }
    private void OnBackgroundMoved(object? sender, PointerEventArgs e)
    {
        if (_marquee is not { } marquee || marquee.Pointer != e.Pointer) return;
        var rect = SelectionRect(marquee.Start, _viewport.ToGraphPoint(e.GetPosition(NodeCanvas)));
        Canvas.SetLeft(marquee.Box, rect.X + _viewport.Origin.X); Canvas.SetTop(marquee.Box, rect.Y + _viewport.Origin.Y);
        marquee.Box.Width = rect.Width; marquee.Box.Height = rect.Height; e.Handled = true;
    }
    private void OnBackgroundReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_marquee is not { } marquee || marquee.Pointer != e.Pointer) return;
        var rect = SelectionRect(marquee.Start, _viewport.ToGraphPoint(e.GetPosition(NodeCanvas)));
        _selectedNodes.Clear(); _selectedNodes.UnionWith(marquee.Before);
        if (rect.Width >= 3 || rect.Height >= 3)
            _selectedNodes.UnionWith(_nodeVisuals.Values.Where(n => rect.Intersects(NodeBounds([n]))).Select(n => n.Model.Id));
        _selection = _selectedNodes.LastOrDefault();
        CancelMarquee(); UpdateSelectionStyles(); BuildInspector(); e.Handled = true;
    }
    private void CancelMarquee()
    {
        if (_marquee is not { } marquee) return;
        _marquee = null; NodeCanvas.Children.Remove(marquee.Box); marquee.Pointer.Capture(null);
    }
    private static Rect SelectionRect(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
