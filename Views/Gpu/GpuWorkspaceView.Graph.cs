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
using LiberaFx.Views.Graph;

namespace LiberaFx.Views.Gpu;

public partial class GpuWorkspaceView : UserControl
{
    private void BuildGraph()
    {
        var vm = Workspace!; var k = vm.Kernel!;
        if (_visualKernelId != k.Id) _selectedNodes.Clear();
        _selectedNodes.IntersectWith(k.Nodes.Select(n => n.Id));
        if (_selection is not null && !_selectedNodes.Contains(_selection)) _selection = _selectedNodes.LastOrDefault();
        var sameTopology = _visualKernelId == k.Id && _nodeVisuals.Count == k.Nodes.Count &&
            k.Nodes.All(n => _nodeVisuals.TryGetValue(n.Id, out var visual) &&
                visual.Model.Op == n.Op && visual.Model.Inputs.SequenceEqual(n.Inputs));
        if (!sameTopology)
        {
            FinishDrag(commit: false);
            CancelConnectionDrag();
            NodeCanvas.Children.Clear();
            _grid = new GraphSceneLayer { GridOnly = true, DataContext = null, IsHitTestVisible = false };
            NodeCanvas.Children.Add(_grid);
            _viewport.SetGrid(_grid);
            _nodeVisuals.Clear();
            _wireVisuals.Clear();
            _visualKernelId = k.Id;
            foreach (var n in k.Nodes)
            {
                var panel = new StackPanel { Spacing = 3 };
                var title = new TextBlock
                {
                    Margin = new Thickness(4, 0), MinHeight = 26, Tag = n.Id,
                    TextWrapping = TextWrapping.Wrap
                };
                ToolTip.SetTip(title, OpDescription(n.Op));
                panel.Children.Add(title);
                var output = PortButton("gpu-output");
                var outputLabel = PortLabel();
                outputLabel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
                var outputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                outputRow.Background = Brushes.Transparent;
                outputRow.Children.Add(outputLabel);
                Grid.SetColumn(output, 1);
                outputRow.Children.Add(output);
                panel.Children.Add(outputRow);
                var destinationLabel = PortLabel();
                destinationLabel.Margin = new Thickness(4, 0, 4, 0);
                panel.Children.Add(destinationLabel);
                var inputs = new Button[GpuOperationRegistry.Operations[n.Op].MaxInputs];
                var inputLabels = new TextBlock[inputs.Length];
                for (var i = 0; i < inputs.Length; i++)
                {
                    var input = i;
                    inputs[i] = PortButton("gpu-input-" + i);
                    inputs[i].Click += (_, _) => Run(() =>
                    {
                        if (_wireSource is null) { SelectVisualNode(n.Id); return; }
                        vm.Edit(new() { Action = "connect", KernelId = k.Id, NodeId = n.Id, SourceId = _wireSource, Input = input });
                        _wireSource = null;
                    });
                    inputLabels[i] = PortLabel();
                    inputLabels[i].Margin = new Thickness(4, 0, 0, 0);
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                    row.Children.Add(inputs[i]);
                    Grid.SetColumn(inputLabels[i], 1);
                    row.Children.Add(inputLabels[i]);
                    panel.Children.Add(row);
                }
                var border = new Border
                {
                    Width = 230, Child = panel, Padding = new Thickness(6), CornerRadius = new CornerRadius(5),
                    Background = this.FindResource("EffectSurfaceBrush") as IBrush ?? Brushes.DimGray,
                    BorderThickness = new Thickness(1), Tag = n.Id, Cursor = new Cursor(StandardCursorType.SizeAll)
                };
                var visual = new NodeVisual(n, border, title, output, inputs, outputLabel, destinationLabel, inputLabels)
                { CanvasOrigin = _viewport.Origin };
                border.RenderTransform = visual.Translation;
                _nodeVisuals.Add(n.Id, visual);
                border.PointerPressed += (_, e) => BeginDrag(visual, e);
                border.PointerMoved += (_, e) => MoveDrag(visual, e);
                border.PointerReleased += (_, e) => ReleaseDrag(visual, e);
                border.PointerCaptureLost += (_, _) =>
                {
                    if (_drag?.Visual == visual) FinishDrag(commit: true);
                };
                output.AddHandler(InputElement.PointerPressedEvent, (_, e) => BeginConnectionDrag(visual, e),
                    RoutingStrategies.Tunnel);
                output.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
                {
                    if (_connectionDrag is not { } connection || connection.Source != visual || connection.Pointer != e.Pointer) return;
                    connection.Latest = e.GetPosition(NodeCanvas);
                    ScheduleFrame();
                    e.Handled = true;
                }, RoutingStrategies.Tunnel);
                output.AddHandler(InputElement.PointerReleasedEvent, (_, e) => ReleaseConnectionDrag(visual, e),
                    RoutingStrategies.Tunnel);
                output.PointerCaptureLost += (_, _) =>
                {
                    if (_connectionDrag?.Source == visual) CancelConnectionDrag();
                };
                border.PointerEntered += (_, _) => { visual.IsHovered = true; UpdateSelectionStyles(); };
                border.PointerExited += (_, _) => { visual.IsHovered = false; UpdateSelectionStyles(); };
                border.SizeChanged += (_, _) => { _anchorsDirty = true; ScheduleFrame(); };
                NodeCanvas.Children.Add(border);
            }
            foreach (var n in k.Nodes)
                for (var input = 0; input < n.Inputs.Count; input++)
                    if (_nodeVisuals.TryGetValue(n.Inputs[input], out var source))
                    {
                        var target = _nodeVisuals[n.Id];
                        var curve = new GpuConnectionCurve { Width = NodeCanvas.Width, Height = NodeCanvas.Height,
                            StrokeThickness = 2, IsHitTestVisible = false };
                        var wire = new WireVisual(source, target, input, curve);
                        _wireVisuals.Add(wire);
                        source.Wires.Add(wire);
                        target.Wires.Add(wire);
                        NodeCanvas.Children.Insert(1, curve);
                    }
            _anchorsDirty = true;
        }
        RebuildStageOutput(vm, k);
        var destinations = _wireVisuals.ToLookup(wire => wire.Source.Model.Id);
        var modelsById = k.Nodes.ToDictionary(node => node.Id);
        var writes = k.Writes.ToLookup(write => write.Value);
        foreach (var n in k.Nodes)
        {
            var visual = _nodeVisuals[n.Id];
            visual.Model = n;
            visual.Title.Text = NodeDisplayName(n);
            var targets = destinations[n.Id].Select(wire =>
            {
                var target = modelsById[wire.Target.Model.Id];
                var input = GpuOperationRegistry.GetDefaultInputs(target.Op, target.Type).ElementAtOrDefault(wire.Input);
                return $"{NodeDisplayName(target)} / {InputDisplayName(input?.Id ?? "input")}";
            }).Concat(writes[n.Id].Select(write =>
                LocalizationService.Shared.Get("Gpu.OutputAttributeTarget", AttributeDisplayName(write.Attribute)))).ToArray();
            var outputText = $"{OutputDisplayName(n)} : {n.Type}";
            visual.OutputLabel.Text = outputText;
            visual.DestinationLabel.IsVisible = targets.Length != 0;
            visual.DestinationLabel.Text = targets.Length switch
            {
                0 => "",
                1 => $"→ {targets[0]}",
                _ => $"→ {LocalizationService.Shared.Get("Gpu.OutputTargetCount", targets.Length)}"
            };
            var outputDetails = targets.Length == 0 ? outputText : outputText + Environment.NewLine +
                string.Join(Environment.NewLine, targets.Select(target => $"→ {target}"));
            ToolTip.SetTip(visual.Output, outputDetails);
            if (visual.OutputLabel.Parent is Control outputRow) ToolTip.SetTip(outputRow, outputDetails);
            Avalonia.Automation.AutomationProperties.SetName(visual.Output, outputDetails);
            visual.Output.Foreground = TypeBrush(n.Type);
            for (var i = 0; i < visual.Inputs.Length; i++)
            {
                var source = k.Nodes.FirstOrDefault(x => x.Id == n.Inputs.ElementAtOrDefault(i));
                var defaults = GpuOperationRegistry.GetDefaultInputs(n.Op, n.Type);
                var descriptor = defaults.ElementAtOrDefault(i);
                visual.InputLabels[i].Text = $"{InputDisplayName(descriptor?.Id ?? "input")} : {source?.Type ?? descriptor?.DefaultType ?? n.Type}";
                visual.Inputs[i].Foreground = TypeBrush(source?.Type ?? descriptor?.DefaultType ?? n.Type);
            }
        }
        UpdateSelectionStyles();
        if (_drag is null) ApplySavedPositions();
        Dispatcher.UIThread.Post(_viewport.Initialize);
        ScheduleFrame();
    }
    private static Button PortButton(string tag) => new()
    {
        Content = "●", Tag = tag, Width = 24, Height = 24, MinWidth = 24, MinHeight = 24,
        Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        Cursor = new Cursor(StandardCursorType.Hand),
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center
    };
    private static TextBlock PortLabel() => new()
    {
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        IsHitTestVisible = false
    };
    private static string InputDisplayName(string id) => LocalizationService.Shared.Get("Gpu.Input." + id);
    private static string AttributeDisplayName(string? id) => id is null
        ? "?"
        : LocalizationService.Shared.Get("Gpu.Attribute." + id);
    private static string UniformDisplayName(string? id) => id is null
        ? "?"
        : LocalizationService.Shared.Get("Gpu.Uniform." + id);
    private static string NodeDisplayName(GpuNode node)
    {
        var operation = node.Op switch
        {
            GpuOp.ReadAttribute => LocalizationService.Shared.GetNodeLabel("Gpu.ReadAttribute", "Read Attribute") + " · " +
                AttributeDisplayName(node.Attribute),
            GpuOp.ReadUniform => LocalizationService.Shared.GetNodeLabel("Gpu.ReadUniform", "Read Uniform") + " · " +
                UniformDisplayName(node.Uniform),
            GpuOp.SampleFlowMap => GpuOperationRegistry.Operations[node.Op].DisplayName + " · " + node.Resource,
            _ => OperationDisplayName(node.Op)
        };
        return $"{operation} : {node.Type}";
    }
    private static string OperationDisplayName(GpuOp operation) => operation is GpuOp.GreaterThan or GpuOp.LessThan
        ? LocalizationService.Shared.Get("Fluid.Operation." + operation) : GpuOperationRegistry.Operations[operation].DisplayName;
    private static string OutputDisplayName(GpuNode node) => node.Op switch
    {
        GpuOp.ReadAttribute => AttributeDisplayName(node.Attribute),
        GpuOp.ReadUniform => UniformDisplayName(node.Uniform),
        _ => LocalizationService.Shared.Get("Gpu.Output")
    };
    private static IBrush TypeBrush(GpuType type) => ThemeSettingsService.EditorBrush(
        type == GpuType.Bool ? "GraphSignal" : "GraphParameter");
    private void SelectVisualNode(string id, bool preserveGroup = false)
    {
        if (!preserveGroup || !_selectedNodes.Contains(id)) { _selectedNodes.Clear(); _selectedNodes.Add(id); }
        _selection = id;
        UpdateSelectionStyles();
        BuildInspector();
    }
    private void UpdateSelectionStyles()
    {
        FrameButton.IsEnabled = _selectedNodes.Count > 0;
        var errors = Workspace!.Diagnostics.Where(d => d.KernelId == Workspace.KernelId && d.NodeId is not null)
            .Select(d => d.NodeId!).ToHashSet();
        foreach (var visual in _nodeVisuals.Values)
            visual.Border.BorderBrush = errors.Contains(visual.Model.Id) ? Brushes.Red :
                _selectedNodes.Contains(visual.Model.Id) ? ThemeSettingsService.EditorBrush("NodeSelectedBorder") :
                ThemeSettingsService.EditorBrush(visual.IsHovered ? "CurveAxis" : "NodeBorder");
        foreach (var wire in _wireVisuals)
            wire.Curve.Stroke = errors.Contains(wire.Target.Model.Id) ? Brushes.Red : TypeBrush(wire.Source.Model.Type);
    }
    private void ApplySavedPositions()
    {
        if (Workspace?.Kernel is not { } k || _visualKernelId != k.Id) return;
        for (var i = 0; i < k.Nodes.Count; i++)
        {
            var n = k.Nodes[i];
            if (!_nodeVisuals.TryGetValue(n.Id, out var visual)) continue;
            var p = Workspace.GetPosition(n);
            visual.Position = new(p.X, p.Y);
            visual.PreviewOffset = default;
            if (!visual.AnchorInitialized)
            {
                Canvas.SetLeft(visual.Border, p.X + _viewport.Origin.X);
                Canvas.SetTop(visual.Border, p.Y + _viewport.Origin.Y);
                visual.AnchorInitialized = true;
            }
            visual.SyncTransform();
        }
        _viewport.EnsureWorldBounds(NodeBounds(_nodeVisuals.Values));
        UpdateWireGeometry(_wireVisuals);
        UpdateStageOutputAnchors();
    }
    private void BeginDrag(NodeVisual visual, PointerPressedEventArgs e)
    {
        if (IsInteractiveSource(e.Source as Visual, visual.Border) ||
            !e.GetCurrentPoint(visual.Border).Properties.IsLeftButtonPressed) return;
        FinishDrag(commit: false);
        CancelConnectionDrag();
        Focus();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (!_selectedNodes.Add(visual.Model.Id)) _selectedNodes.Remove(visual.Model.Id);
            _selection = _selectedNodes.LastOrDefault();
            UpdateSelectionStyles(); BuildInspector(); e.Handled = true; return;
        }
        SelectVisualNode(visual.Model.Id, preserveGroup: true);
        // Coordinates belong to the stationary canvas, never the translating captured node.
        _drag = new(visual, e.Pointer, _viewport.ToGraphPoint(e.GetPosition(NodeCanvas)), e.GetPosition(this))
        { Members = _selectedNodes.Where(_nodeVisuals.ContainsKey)
            .Select(id => (_nodeVisuals[id], _nodeVisuals[id].Position)).ToArray() };
        e.Pointer.Capture(visual.Border);
        e.Handled = true;
    }
    private void MoveDrag(NodeVisual visual, PointerEventArgs e)
    {
        if (_drag is not { } drag || drag.Visual != visual || drag.Pointer != e.Pointer) return;
        UpdateDragPoint(drag, e);
        if (drag.IsDragging) ScheduleFrame();
        e.Handled = true;
    }
    private void ReleaseDrag(NodeVisual visual, PointerReleasedEventArgs e)
    {
        if (_drag is not { } drag || drag.Visual != visual || drag.Pointer != e.Pointer) return;
        UpdateDragPoint(drag, e);
        FinishDrag(commit: true);
        e.Handled = true;
    }
    private static bool IsInteractiveSource(Visual? source, Border node)
    {
        for (var current = source; current is not null && current != node; current = current.GetVisualParent())
            if (current is Avalonia.Controls.Button or TextBox or NumericUpDown or Slider or Avalonia.Controls.Primitives.SelectingItemsControl)
                return true;
        return false;
    }
    private void UpdateDragPoint(NodeDrag drag, PointerEventArgs e)
    {
        drag.Latest = _viewport.ToGraphPoint(e.GetPosition(NodeCanvas));
        var cursor = e.GetPosition(this);
        var dx = cursor.X - drag.StartView.X;
        var dy = cursor.Y - drag.StartView.Y;
        drag.IsDragging |= dx * dx + dy * dy >= 9;
    }
    private void ScheduleFrame()
    {
        if (_framePending) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) { OnRenderFrame(default); return; }
        _framePending = true;
        top.RequestAnimationFrame(_renderFrame);
    }
    private void OnRenderFrame(TimeSpan time)
    {
        _framePending = false;
        if (_anchorsDirty) UpdatePortAnchors();
        if (_drag is { } drag) ApplyDrag(drag);
        if (_connectionDrag is { } connection)
        {
            var target = FindConnectionTarget(connection, connection.Latest);
            var stageTarget = target is null ? FindStageOutputTarget(connection, connection.Latest) : null;
            _connectionFeedback.Show(target?.Node.Inputs[target.Value.Input] ?? stageTarget?.Pin);
            var end = target is { } snap
                ? snap.Node.CanvasPosition + (Vector)snap.Node.InputAnchors[snap.Input]
                : stageTarget is { } stageSnap && _stageOutput is not null
                    ? _stageOutput.CanvasPosition + (Vector)stageSnap.Anchor
                    : connection.Latest;
            connection.Preview.SetEndpoints(connection.Source.CanvasPosition + (Vector)connection.Source.OutputAnchor, end);
        }
    }
    private void UpdatePortAnchors()
    {
        _anchorsDirty = false;
        foreach (var visual in _nodeVisuals.Values)
        {
            visual.SyncTransform();
            visual.OutputAnchor = visual.Output.TranslatePoint(
                new Point(visual.Output.Bounds.Width / 2, visual.Output.Bounds.Height / 2), visual.Border) ?? new(230, 45);
            for (var i = 0; i < visual.Inputs.Length; i++)
                visual.InputAnchors[i] = visual.Inputs[i].TranslatePoint(
                    new Point(visual.Inputs[i].Bounds.Width / 2, visual.Inputs[i].Bounds.Height / 2), visual.Border) ?? new(0, 70 + i * 27);
        }
        UpdateWireGeometry(_wireVisuals);
        UpdateStageOutputAnchors();
    }
    private static void UpdateWireGeometry(List<WireVisual> wires)
    {
        foreach (var wire in wires)
        {
            wire.Curve.SetEndpoints(wire.Source.CanvasPosition + (Vector)wire.Source.OutputAnchor,
                wire.Target.CanvasPosition + (Vector)wire.Target.InputAnchors[wire.Input]);
        }
    }
    private void ApplyDrag(NodeDrag drag)
    {
        if (!drag.IsDragging) return;
        var delta = (Vector)(drag.Latest - drag.Start);
        foreach (var (visual, _) in drag.Members)
        {
            visual.PreviewOffset = delta;
            visual.SyncTransform();
            UpdateWireGeometry(visual.Wires);
        }
        UpdateOutputWireGeometry();
        _viewport.EnsureWorldBounds(NodeBounds(drag.Members.Select(m => m.Visual)));
    }
    private void FinishDrag(bool commit)
    {
        if (_drag is not { } drag) return;
        ApplyDrag(drag);
        var position = drag.Visual.Origin;
        _drag = null;
        drag.Pointer.Capture(null);
        if (commit && position != drag.Original && Workspace?.KernelId is { } kernel)
        {
            var edits = drag.Members.Select(m => new GpuEdit { Action = "move", KernelId = kernel,
                NodeId = m.Visual.Model.Id, Position = new(m.Visual.Origin.X, m.Visual.Origin.Y) }).ToArray();
            Run(() => Workspace.Edit(edits[0], edits[1..]));
            ApplySavedPositions();
        }
        else
        {
            foreach (var (visual, _) in drag.Members)
            {
                visual.PreviewOffset = default;
                visual.SyncTransform();
                UpdateWireGeometry(visual.Wires);
            }
        }
    }
    private void BeginConnectionDrag(NodeVisual source, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(source.Output).Properties.IsLeftButtonPressed) return;
        FinishDrag(commit: false);
        CancelConnectionDrag();
        SelectVisualNode(source.Model.Id);
        _wireSource = null;
        var preview = new GpuConnectionCurve
        {
            Width = NodeCanvas.Width, Height = NodeCanvas.Height,
            Stroke = TypeBrush(source.Model.Type), StrokeThickness = 2, StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 4 },
            IsHitTestVisible = false
        };
        _connectionDrag = new(source, e.Pointer, preview, e.GetPosition(NodeCanvas))
        { Compatible = Workspace!.CompatibleInputs(source.Model.Id) };
        NodeCanvas.Children.Insert(1, preview);
        e.Pointer.Capture(source.Output);
        ScheduleFrame();
        e.Handled = true;
    }
    private void ReleaseConnectionDrag(NodeVisual source, PointerReleasedEventArgs e)
    {
        if (_connectionDrag is not { } connection || connection.Source != source || connection.Pointer != e.Pointer) return;
        var point = e.GetPosition(NodeCanvas);
        var target = FindConnectionTarget(connection, point);
        var stageTarget = target is null ? FindStageOutputTarget(connection, point) : null;
        var clicked = Math.Abs(point.X - connection.Start.X) < 4 && Math.Abs(point.Y - connection.Start.Y) < 4;
        CancelConnectionDrag();
        if (target is { } snap && Workspace?.KernelId is { } kernel)
        {
            Run(() => Workspace.Edit(new() { Action = "connect", KernelId = kernel,
                NodeId = snap.Node.Model.Id, SourceId = source.Model.Id, Input = snap.Input }));
            _wireSource = null;
        }
        else if (stageTarget is { } stageSnap && Workspace?.KernelId is { } writeKernel && Workspace.Kernel is { } currentKernel)
        {
            var existing = currentKernel.Writes.FirstOrDefault(w => w.Attribute == stageSnap.Attribute);
            Run(() => Workspace.Edit(new GpuEdit
            {
                Action = "write",
                KernelId = writeKernel,
                Write = new(existing?.Id ?? "w_" + Guid.NewGuid().ToString("N"), stageSnap.Attribute, source.Model.Id)
            }));
            _wireSource = null;
        }
        else
        {
            _wireSource = clicked ? source.Model.Id : null;
            if (clicked) Status.Text = LocalizationService.Shared.Get("Gpu.SelectTargetInput");
        }
        e.Handled = true;
    }
    private void CancelConnectionDrag()
    {
        _connectionFeedback.Dispose();
        if (_connectionDrag is not { } connection) return;
        _connectionDrag = null;
        NodeCanvas.Children.Remove(connection.Preview);
        connection.Pointer.Capture(null);
    }
    private (NodeVisual Node, int Input)? FindConnectionTarget(ConnectionDrag connection, Point point)
    {
        var root = TopLevel.GetTopLevel(this);
        if (root is null || NodeCanvas.TranslatePoint(point, root) is not { } pointer) return null;
        (NodeVisual Node, int Input)? best = null;
        var distance = PortSnapFeedback.AcquireRadius;
        foreach (var (id, input) in connection.Compatible)
        {
            if (!_nodeVisuals.TryGetValue(id, out var visual) || input >= visual.Inputs.Length) continue;
            var pin = visual.Inputs[input];
            if (!pin.IsEffectivelyVisible || pin.TranslatePoint(new Point(pin.Bounds.Width / 2, pin.Bounds.Height / 2), root) is not { } center) continue;
            var next = ((Vector)(center - pointer)).Length;
            if (next <= distance || best is null && ReferenceEquals(pin, _connectionFeedback.Pin) && next <= PortSnapFeedback.ReleaseRadius)
            { best = (visual, input); distance = next; }
        }
        return best;
    }
}
