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
    private static bool IsNearBezier(Point p, Point start, Point end, double threshold = 14)
    {
        var dx = Math.Max(40, Math.Abs(end.X - start.X) * 0.5);
        var c1 = new Point(start.X + dx, start.Y);
        var c2 = new Point(end.X - dx, end.Y);
        for (var i = 0; i <= 16; i++)
        {
            var t = i / 16.0;
            var u = 1.0 - t;
            var bx = u * u * u * start.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * end.X;
            var by = u * u * u * start.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * end.Y;
            if (((Vector)(p - new Point(bx, by))).Length <= threshold) return true;
        }
        return false;
    }

    private Action? FindInsertRerouteAction(GpuWorkspaceViewModel vm, KernelGraph k, Point graphPosition)
    {
        var canvasPoint = graphPosition + _viewport.Origin;
        foreach (var wire in _wireVisuals)
        {
            var start = wire.Source.CanvasPosition + (Vector)wire.Source.OutputAnchor;
            var end = wire.Target.CanvasPosition + (Vector)wire.Target.InputAnchors[wire.Input];
            if (!IsNearBezier(canvasPoint, start, end)) continue;
            var srcId = wire.Source.Model.Id;
            var srcType = wire.Source.Model.Type;
            var dstId = wire.Target.Model.Id;
            var dstInput = wire.Input;
            return () =>
            {
                var rerouteId = "n_" + Guid.NewGuid().ToString("N");
                vm.Edit(
                    new GpuEdit { Action = "add", KernelId = k.Id, Position = new(graphPosition.X - 28, graphPosition.Y - 14),
                        Node = new GpuNode { Id = rerouteId, Op = GpuOp.Reroute, Type = srcType, Inputs = [srcId] } },
                    new GpuEdit { Action = "connect", KernelId = k.Id, NodeId = dstId, SourceId = rerouteId, Input = dstInput });
                SelectVisualNode(rerouteId);
            };
        }
        if (_stageInput is { } inputCard)
        {
            foreach (var wire in _inputWires)
            {
                if (!inputCard.Anchors.TryGetValue(wire.Key, out var startAnchor) ||
                    wire.Input >= wire.Target.InputAnchors.Length) continue;
                var start = inputCard.CanvasPosition + (Vector)startAnchor;
                var end = wire.Target.CanvasPosition + (Vector)wire.Target.InputAnchors[wire.Input];
                if (!IsNearBezier(canvasPoint, start, end)) continue;
                var srcNodeId = wire.Target.Model.Inputs.ElementAtOrDefault(wire.Input);
                if (string.IsNullOrEmpty(srcNodeId)) continue;
                var srcNode = k.Nodes.FirstOrDefault(n => n.Id == srcNodeId);
                if (srcNode is null) continue;
                var dstId = wire.Target.Model.Id;
                var dstInput = wire.Input;
                return () =>
                {
                    var rerouteId = "n_" + Guid.NewGuid().ToString("N");
                    vm.Edit(
                        new GpuEdit { Action = "add", KernelId = k.Id, Position = new(graphPosition.X - 28, graphPosition.Y - 14),
                            Node = new GpuNode { Id = rerouteId, Op = GpuOp.Reroute, Type = srcNode.Type, Inputs = [srcNode.Id] } },
                        new GpuEdit { Action = "connect", KernelId = k.Id, NodeId = dstId, SourceId = rerouteId, Input = dstInput });
                    SelectVisualNode(rerouteId);
                };
            }
        }
        if (_stageOutput is { } stage)
        {
            foreach (var wire in _outputWires)
            {
                if (!stage.Anchors.TryGetValue(wire.Attribute, out var endAnchor)) continue;
                Point? startPoint = wire.Source is not null
                    ? wire.Source.CanvasPosition + (Vector)wire.Source.OutputAnchor
                    : wire.StageInputKey is not null && _stageInput is { } inCard &&
                      inCard.Anchors.TryGetValue(wire.StageInputKey, out var startAnchor)
                        ? inCard.CanvasPosition + (Vector)startAnchor
                        : null;
                if (startPoint is not { } start) continue;
                var end = stage.CanvasPosition + (Vector)endAnchor;
                if (!IsNearBezier(canvasPoint, start, end)) continue;
                var write = k.Writes.FirstOrDefault(w => w.Attribute == wire.Attribute);
                var srcNode = write is not null ? k.Nodes.FirstOrDefault(n => n.Id == write.Value) : null;
                if (write is null || srcNode is null) continue;
                return () =>
                {
                    var rerouteId = "n_" + Guid.NewGuid().ToString("N");
                    vm.Edit(
                        new GpuEdit { Action = "add", KernelId = k.Id, Position = new(graphPosition.X - 28, graphPosition.Y - 14),
                            Node = new GpuNode { Id = rerouteId, Op = GpuOp.Reroute, Type = srcNode.Type, Inputs = [srcNode.Id] } },
                        new GpuEdit { Action = "write", KernelId = k.Id, Write = new(write.Id, write.Attribute, rerouteId) });
                    SelectVisualNode(rerouteId);
                };
            }
        }
        return null;
    }

    private void OpenGraphContext(Point position)
    {
        if (Workspace is not { Kernel: { } k } vm) return;
        Focus(); _pastePosition = position;
        var menuItems = new List<Control>();
        if (FindInsertRerouteAction(vm, k, position) is { } insertReroute)
        {
            var rerouteItem = new MenuItem { Header = LocalizationService.Shared.GetOrDefault("Editor.AddReroute", "Add reroute") };
            rerouteItem.Click += (_, _) => Run(insertReroute);
            menuItems.Add(rerouteItem);
            menuItems.Add(new Separator());
        }
        var add = new MenuItem { Header = LocalizationService.Shared.Get("Gpu.AddNode") };
        add.ItemsSource = vm.Palette
            .Where(op => op.Op is not (GpuOp.ReadAttribute or GpuOp.ReadUniform))
            .Select(op =>
        {
            var item = new MenuItem { Header = op.DisplayName };
            ToolTip.SetTip(item, OpDescription(op.Op));
            item.Click += (_, _) => Run(() => SelectVisualNode(vm.Add(op.Op,
                TypeChoice.SelectedItem is GpuType type ? type : GpuType.Float, new(position.X, position.Y)).Id));
            return item;
        }).ToArray();
        var quickAdd = new MenuItem { Header = LocalizationService.Shared.Get("Gpu.AddNode") + " (Search…)" };
        quickAdd.Click += (_, _) => OpenQuickSearch(position);
        var remove = new MenuItem { Header = LocalizationService.Shared.Get("Gpu.Delete"), IsEnabled = _selectedNodes.Count > 0 };
        remove.Click += (_, _) => DeleteSelection();
        var copy = new MenuItem { Header = LocalizationService.Shared.Get("Gpu.Copy"), IsEnabled = _selectedNodes.Count > 0 };
        copy.Click += async (_, _) => await CopySelectionAsync(false);
        var cut = new MenuItem { Header = LocalizationService.Shared.Get("Gpu.Cut"), IsEnabled = _selectedNodes.Count > 0 };
        cut.Click += async (_, _) => await CopySelectionAsync(true);
        var paste = new MenuItem { Header = LocalizationService.Shared.Get("Gpu.Paste") };
        paste.Click += async (_, _) => await PasteAsync();
        menuItems.Add(quickAdd);
        menuItems.Add(add);
        menuItems.Add(new Separator());
        menuItems.Add(copy);
        menuItems.Add(cut);
        menuItems.Add(paste);
        menuItems.Add(remove);
        NodeCanvas.ContextMenu = new ContextMenu { ItemsSource = menuItems };
        NodeCanvas.ContextMenu.Open(NodeCanvas);
    }

    private void OpenQuickSearch(Point position)
    {
        if (Workspace is not { Kernel: not null } vm) return;
        var items = vm.Palette
            .Where(op => op.Op is not (GpuOp.ReadAttribute or GpuOp.ReadUniform))
            .ToArray();
        var search = new TextBox { PlaceholderText = "Search GPU node…" };
        var list = new ListBox
        {
            MaxHeight = 240,
            ItemsSource = items.Select(op => op.DisplayName).ToArray()
        };
        if (list.ItemCount > 0) list.SelectedIndex = 0;
        var filtered = items;
        search.TextChanged += (_, _) =>
        {
            var q = search.Text?.Trim() ?? "";
            filtered = string.IsNullOrEmpty(q)
                ? items
                : items.Where(op => op.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                    op.Op.ToString().Contains(q, StringComparison.OrdinalIgnoreCase)).ToArray();
            list.ItemsSource = filtered.Select(op => op.DisplayName).ToArray();
            if (filtered.Length > 0) list.SelectedIndex = 0;
        };
        var popup = new Avalonia.Controls.Primitives.Popup
        {
            PlacementTarget = NodeCanvas,
            Placement = PlacementMode.Pointer,
            IsLightDismissEnabled = true
        };
        void CommitSelection()
        {
            var idx = list.SelectedIndex;
            if (idx >= 0 && idx < filtered.Length)
            {
                var op = filtered[idx].Op;
                popup.IsOpen = false;
                Run(() => SelectVisualNode(vm.Add(op,
                    TypeChoice.SelectedItem is GpuType type ? type : GpuType.Float, new(position.X, position.Y)).Id));
            }
        }
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitSelection(); e.Handled = true; }
            else if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; }
            else if (e.Key == Key.Down && filtered.Length > 0)
            {
                list.SelectedIndex = Math.Min(filtered.Length - 1, Math.Max(0, list.SelectedIndex + 1));
                e.Handled = true;
            }
            else if (e.Key == Key.Up && filtered.Length > 0)
            {
                list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1);
                e.Handled = true;
            }
        };
        list.DoubleTapped += (_, _) => CommitSelection();
        popup.Child = new Border
        {
            Width = 240,
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(6),
            Background = this.FindResource("EffectSurfaceBrush") as IBrush ?? Brushes.DimGray,
            BorderBrush = this.FindResource("EffectBorderBrush") as IBrush ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            Child = new StackPanel { Spacing = 4, Children = { search, list } }
        };
        popup.Opened += (_, _) => search.Focus();
        popup.IsOpen = true;
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
