using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using LibraGpu.Core;
using LiberaFx.Services.Gpu;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Panels;
using LiberaFx.Views.Graph;

namespace LiberaFx.Views.Gpu;

public partial class GpuWorkspaceView
{
    private sealed class StageCardVisual(Border border, Dictionary<string, (Button Pin, GpuType Type)> pins, Point position)
    {
        public readonly Border Border = border;
        public readonly Dictionary<string, (Button Pin, GpuType Type)> Pins = pins;
        public readonly Dictionary<string, Point> Anchors = [];
        public readonly TranslateTransform Translation = new();
        public Point Position = position;
        public Vector PreviewOffset;
        public Vector CanvasOrigin;
        public Point CanvasPosition => Position + PreviewOffset + CanvasOrigin;

        public void SyncTransform()
        {
            var p = CanvasPosition;
            Translation.X = p.X;
            Translation.Y = p.Y;
        }
    }

    private sealed class OutputWireVisual(NodeVisual? source, string? stageInputKey, string attribute, GpuConnectionCurve curve)
    {
        public readonly NodeVisual? Source = source;
        public readonly string? StageInputKey = stageInputKey;
        public readonly string Attribute = attribute;
        public readonly GpuConnectionCurve Curve = curve;
    }

    private sealed class InputWireVisual(string key, NodeVisual target, int input, GpuConnectionCurve curve)
    {
        public readonly string Key = key;
        public readonly NodeVisual Target = target;
        public readonly int Input = input;
        public readonly GpuConnectionCurve Curve = curve;
    }

    private static bool IsStageInputNode(GpuNode node) =>
        (node.Op == GpuOp.ReadAttribute && node.Attribute is not null) ||
        (node.Op == GpuOp.ReadUniform && node.Uniform is "deltaTime" or "time" or "emitterPosition");

    private StageCardVisual? _stageOutput;
    private StageCardVisual? _stageInput;
    private readonly Dictionary<string, Point> _stageOutputPositions = [];
    private readonly Dictionary<string, Point> _stageInputPositions = [];
    private (StageCardVisual Card, Dictionary<string, Point> Store, IPointer Pointer, Point Start, Point Original)? _stageCardDrag;
    private (string Key, bool IsUniform, GpuType Type, Button Pin, IPointer Pointer, GpuConnectionCurve Preview, Point Start, Point Latest)? _stageInputDrag;
    private readonly List<OutputWireVisual> _outputWires = [];
    private readonly List<InputWireVisual> _inputWires = [];
    private readonly Dictionary<string, string> _customAttributeLabels = [];
    private readonly HashSet<string> _visibleOutputAttributes =
        ["Position", "Velocity", "Color", "Size", "Rotation", "Age", "Lifetime"];

    private string AttributePinLabel(string attrId) =>
        _customAttributeLabels.TryGetValue(attrId, out var customName) && !string.IsNullOrWhiteSpace(customName)
            ? customName
            : LocalizationService.Shared.GetOrDefault("Gpu.Attribute." + attrId, attrId);

    private void ClearStageOutput()
    {
        CancelStageInputDrag();
        _stageCardDrag = null;
        if (_stageOutput is not null)
        {
            NodeCanvas.Children.Remove(_stageOutput.Border);
            _stageOutput = null;
        }
        if (_stageInput is not null)
        {
            NodeCanvas.Children.Remove(_stageInput.Border);
            _stageInput = null;
        }
        foreach (var wire in _outputWires)
        {
            NodeCanvas.Children.Remove(wire.Curve);
        }
        _outputWires.Clear();
        foreach (var wire in _inputWires)
        {
            NodeCanvas.Children.Remove(wire.Curve);
        }
        _inputWires.Clear();
    }

    private void AttachStageCardDrag(StageCardVisual card, Dictionary<string, Point> store, string kernelId)
    {
        card.Border.Cursor = new Cursor(StandardCursorType.SizeAll);
        card.Border.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(card.Border).Properties.IsLeftButtonPressed) return;
            if (e.Source is Visual v && v.GetSelfAndVisualAncestors().OfType<Button>().Any()) return;
            FinishDrag(commit: false);
            _stageCardDrag = (card, store, e.Pointer, _viewport.ToGraphPoint(e.GetPosition(NodeCanvas)), card.Position);
            e.Pointer.Capture(card.Border);
            e.Handled = true;
        };
        card.Border.PointerMoved += (_, e) =>
        {
            if (_stageCardDrag is not { } drag || drag.Card != card || drag.Pointer != e.Pointer) return;
            var latest = _viewport.ToGraphPoint(e.GetPosition(NodeCanvas));
            card.PreviewOffset = (Vector)(latest - drag.Start);
            card.SyncTransform();
            UpdateOutputWireGeometry();
            e.Handled = true;
        };
        card.Border.PointerReleased += (_, e) =>
        {
            if (_stageCardDrag is not { } drag || drag.Card != card || drag.Pointer != e.Pointer) return;
            card.Position = drag.Original + card.PreviewOffset;
            card.PreviewOffset = default;
            card.SyncTransform();
            store[kernelId] = card.Position;
            _stageCardDrag = null;
            drag.Pointer.Capture(null);
            UpdateOutputWireGeometry();
            e.Handled = true;
        };
        card.Border.PointerCaptureLost += (_, _) =>
        {
            if (_stageCardDrag is not { } drag || drag.Card != card) return;
            card.PreviewOffset = default;
            card.SyncTransform();
            _stageCardDrag = null;
            UpdateOutputWireGeometry();
        };
    }

    private void RebuildStageOutput(GpuWorkspaceViewModel vm, KernelGraph k)
    {
        ClearStageOutput();
        RebuildStageInput(vm, k);
        var activeAttrs = vm.State.Program.Attributes
            .Where(a => a.Id != "Seed" && (!ParticleAbi.IsBuiltinAttribute(a.Id) || _visibleOutputAttributes.Contains(a.Id) || k.Writes.Any(w => w.Attribute == a.Id)))
            .ToArray();
        var panel = new StackPanel { Spacing = 4 };
        var stageTitle = k.Stage == KernelStage.Spawn ? "Spawn Output" : "Update Output";
        panel.Children.Add(new TextBlock
        {
            Text = $"{LocalizationService.Shared.Get("Gpu.StageOutput")} ({stageTitle})",
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(4, 2)
        });
        var pins = new Dictionary<string, (Button Pin, GpuType Type)>();
        var writesByAttr = k.Writes.ToDictionary(w => w.Attribute);
        foreach (var attr in activeAttrs)
        {
            var pin = PortButton("gpu-stage-output-" + attr.Id);
            pin.Background = TypeBrush(attr.Type);
            var attrId = attr.Id;
            pin.Click += (_, _) => Run(() =>
            {
                if (_wireSource is null || Workspace?.KernelId is not { } kernelId) return;
                var existing = k.Writes.FirstOrDefault(w => w.Attribute == attrId);
                vm.Edit(new GpuEdit
                {
                    Action = "write",
                    KernelId = kernelId,
                    Write = new(existing?.Id ?? "w_" + Guid.NewGuid().ToString("N"), attrId, _wireSource)
                });
                _wireSource = null;
            });
            var label = PortLabel();
            label.Text = $"{AttributePinLabel(attr.Id)} ({GpuTypeLabel(attr.Type)})";
            label.Margin = new Thickness(6, 0, 4, 0);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            row.Children.Add(pin);
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            if (writesByAttr.TryGetValue(attr.Id, out var write))
            {
                var disconnect = new Button
                {
                    Content = "×",
                    Padding = new Thickness(4, 0),
                    MinWidth = 20,
                    MinHeight = 20,
                    FontSize = 11,
                    IsEnabled = !vm.Graph.IsReadOnly
                };
                var writeId = write.Id;
                disconnect.Click += (_, _) => Run(() =>
                {
                    if (Workspace?.KernelId is { } kernelId)
                    {
                        vm.Edit(new GpuEdit { Action = "write-remove", KernelId = kernelId, NodeId = writeId });
                    }
                });
                Grid.SetColumn(disconnect, 2);
                row.Children.Add(disconnect);
            }
            panel.Children.Add(row);
            pins[attr.Id] = (pin, attr.Type);
        }

        var maxX = _nodeVisuals.Values.Select(v => v.Position.X).DefaultIfEmpty(60).Max();
        var minY = _nodeVisuals.Values.Select(v => v.Position.Y).DefaultIfEmpty(40).Min();
        var pos = _stageOutputPositions.TryGetValue(k.Id, out var savedPos) ? savedPos : new Point(maxX + 300, minY);
        var border = new Border
        {
            Width = 245,
            Child = panel,
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(6),
            Background = this.FindResource("EffectSurfaceBrush") as IBrush ?? Brushes.DimGray,
            BorderBrush = this.FindResource("EffectAccentBrush") as IBrush ?? Brushes.SteelBlue,
            BorderThickness = new Thickness(1.5),
            Tag = "gpu-stage-output"
        };
        _stageOutput = new StageCardVisual(border, pins, pos) { CanvasOrigin = _viewport.Origin };
        border.RenderTransform = _stageOutput.Translation;
        _stageOutput.SyncTransform();
        AttachStageCardDrag(_stageOutput, _stageOutputPositions, k.Id);
        border.SizeChanged += (_, _) => { _anchorsDirty = true; ScheduleFrame(); };
        NodeCanvas.Children.Add(border);

        var nodesById = k.Nodes.ToDictionary(n => n.Id);
        foreach (var write in k.Writes)
        {
            if (_nodeVisuals.TryGetValue(write.Value, out var source))
            {
                var curve = new GpuConnectionCurve
                {
                    Width = NodeCanvas.Width,
                    Height = NodeCanvas.Height,
                    Stroke = TypeBrush(source.Model.Type),
                    StrokeThickness = 2.5,
                    IsHitTestVisible = false
                };
                _outputWires.Add(new OutputWireVisual(source, null, write.Attribute, curve));
                NodeCanvas.Children.Insert(1, curve);
            }
            else if (nodesById.TryGetValue(write.Value, out var srcNode) && IsStageInputNode(srcNode))
            {
                var key = srcNode.Op == GpuOp.ReadUniform ? srcNode.Uniform! : srcNode.Attribute!;
                var curve = new GpuConnectionCurve
                {
                    Width = NodeCanvas.Width,
                    Height = NodeCanvas.Height,
                    Stroke = TypeBrush(srcNode.Type),
                    StrokeThickness = 2.5,
                    IsHitTestVisible = false
                };
                _outputWires.Add(new OutputWireVisual(null, key, write.Attribute, curve));
                NodeCanvas.Children.Insert(1, curve);
            }
        }
    }

    private void RebuildStageInput(GpuWorkspaceViewModel vm, KernelGraph k)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = LocalizationService.Shared.Get("Gpu.StageInput"),
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(4, 2)
        });
        var pins = new Dictionary<string, (Button Pin, GpuType Type)>();
        var items = new List<(string Key, string Label, GpuType Type, bool IsUniform)>();
        foreach (var attr in vm.State.Program.Attributes)
        {
            if (attr.Id is "Alive" or "Custom0" or "Custom1" or "Custom2" &&
                !_visibleOutputAttributes.Contains(attr.Id) &&
                !k.Nodes.Any(n => n.Op == GpuOp.ReadAttribute && n.Attribute == attr.Id) &&
                !k.Writes.Any(w => w.Attribute == attr.Id))
            {
                continue;
            }
            items.Add((attr.Id, AttributePinLabel(attr.Id), attr.Type, false));
        }
        items.Add(("deltaTime", "DeltaTime", GpuType.Float, true));
        items.Add(("time", "Time", GpuType.Float, true));
        items.Add(("emitterPosition", "Emitter Position", GpuType.Float3, true));

        foreach (var (key, labelText, type, isUniform) in items)
        {
            var pin = PortButton("gpu-stage-input-" + key);
            pin.Background = TypeBrush(type);
            var capturedKey = key;
            var capturedType = type;
            var capturedUniform = isUniform;
            pin.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
            {
                if (!e.GetCurrentPoint(pin).Properties.IsLeftButtonPressed) return;
                FinishDrag(commit: false);
                CancelConnectionDrag();
                CancelStageInputDrag();
                var start = e.GetPosition(NodeCanvas);
                var preview = new GpuConnectionCurve
                {
                    Width = NodeCanvas.Width,
                    Height = NodeCanvas.Height,
                    Stroke = TypeBrush(capturedType),
                    StrokeThickness = 2,
                    StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 4 },
                    IsHitTestVisible = false
                };
                _stageInputDrag = (capturedKey, capturedUniform, capturedType, pin, e.Pointer, preview, start, start);
                NodeCanvas.Children.Insert(1, preview);
                e.Pointer.Capture(pin);
                ScheduleFrame();
                e.Handled = true;
            }, RoutingStrategies.Tunnel);
            pin.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
            {
                if (_stageInputDrag is not { } drag || drag.Pin != pin || drag.Pointer != e.Pointer) return;
                drag.Latest = e.GetPosition(NodeCanvas);
                _stageInputDrag = drag;
                ScheduleFrame();
                e.Handled = true;
            }, RoutingStrategies.Tunnel);
            pin.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
            {
                if (_stageInputDrag is not { } drag || drag.Pin != pin || drag.Pointer != e.Pointer) return;
                var point = e.GetPosition(NodeCanvas);
                var target = FindStageInputConnectionTarget(drag.Type, point);
                var clicked = Math.Abs(point.X - drag.Start.X) < 4 && Math.Abs(point.Y - drag.Start.Y) < 4;
                CancelStageInputDrag();
                if (target is { } snap && Workspace?.KernelId is { } kernelId)
                {
                    var sourceId = EnsureStageInputNode(vm, k, drag.Key, drag.IsUniform, drag.Type);
                    Run(() => vm.Edit(new GpuEdit
                    {
                        Action = "connect",
                        KernelId = kernelId,
                        NodeId = snap.Node.Model.Id,
                        SourceId = sourceId,
                        Input = snap.Input
                    }));
                    _wireSource = null;
                }
                else if (clicked)
                {
                    _wireSource = EnsureStageInputNode(vm, k, drag.Key, drag.IsUniform, drag.Type);
                    Status.Text = LocalizationService.Shared.Get("Gpu.SelectTargetInput");
                }
                e.Handled = true;
            }, RoutingStrategies.Tunnel);
            pin.PointerCaptureLost += (_, _) =>
            {
                if (_stageInputDrag?.Pin == pin) CancelStageInputDrag();
            };

            var label = PortLabel();
            label.Text = $"{labelText} ({GpuTypeLabel(type)})";
            label.Margin = new Thickness(4, 0, 6, 0);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(label);
            Grid.SetColumn(pin, 1);
            row.Children.Add(pin);
            panel.Children.Add(row);
            pins[key] = (pin, type);
        }

        var minX = _nodeVisuals.Values.Select(v => v.Position.X).DefaultIfEmpty(300).Min();
        var minY = _nodeVisuals.Values.Select(v => v.Position.Y).DefaultIfEmpty(40).Min();
        var pos = _stageInputPositions.TryGetValue(k.Id, out var savedPos) ? savedPos : new Point(minX - 270, minY);
        var border = new Border
        {
            Width = 225,
            Child = panel,
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(6),
            Background = this.FindResource("EffectSurfaceBrush") as IBrush ?? Brushes.DimGray,
            BorderBrush = this.FindResource("EffectBorderBrush") as IBrush ?? Brushes.Gray,
            BorderThickness = new Thickness(1.5),
            Tag = "gpu-stage-input"
        };
        _stageInput = new StageCardVisual(border, pins, pos) { CanvasOrigin = _viewport.Origin };
        border.RenderTransform = _stageInput.Translation;
        _stageInput.SyncTransform();
        AttachStageCardDrag(_stageInput, _stageInputPositions, k.Id);
        border.SizeChanged += (_, _) => { _anchorsDirty = true; ScheduleFrame(); };
        NodeCanvas.Children.Add(border);

        var modelsById = k.Nodes.ToDictionary(n => n.Id);
        foreach (var n in k.Nodes)
        {
            if (IsStageInputNode(n) || !_nodeVisuals.TryGetValue(n.Id, out var target)) continue;
            for (var input = 0; input < n.Inputs.Count; input++)
            {
                if (!modelsById.TryGetValue(n.Inputs[input], out var src) || !IsStageInputNode(src)) continue;
                var key = src.Op == GpuOp.ReadUniform ? src.Uniform! : src.Attribute!;
                if (!pins.ContainsKey(key)) continue;
                var curve = new GpuConnectionCurve
                {
                    Width = NodeCanvas.Width,
                    Height = NodeCanvas.Height,
                    Stroke = TypeBrush(src.Type),
                    StrokeThickness = 2,
                    IsHitTestVisible = false
                };
                _inputWires.Add(new InputWireVisual(key, target, input, curve));
                NodeCanvas.Children.Insert(1, curve);
            }
        }
    }

    private string EnsureStageInputNode(GpuWorkspaceViewModel vm, KernelGraph k, string key, bool isUniform, GpuType type)
    {
        var existing = k.Nodes.FirstOrDefault(n =>
            isUniform ? n.Op == GpuOp.ReadUniform && n.Uniform == key : n.Op == GpuOp.ReadAttribute && n.Attribute == key);
        if (existing is not null) return existing.Id;
        var id = "n_" + Guid.NewGuid().ToString("N");
        var pos = _stageInput is not null
            ? new GpuPosition(_stageInput.Position.X + 240, _stageInput.Position.Y + (k.Nodes.Count % 6) * 45)
            : new GpuPosition(80, 80);
        vm.Edit(new GpuEdit
        {
            Action = "add",
            KernelId = k.Id,
            Position = pos,
            Node = new GpuNode
            {
                Id = id,
                Op = isUniform ? GpuOp.ReadUniform : GpuOp.ReadAttribute,
                Type = type,
                Attribute = isUniform ? null : key,
                Uniform = isUniform ? key : null
            }
        });
        return id;
    }

    private void CancelStageInputDrag()
    {
        _connectionFeedback.Dispose();
        if (_stageInputDrag is not { } drag) return;
        _stageInputDrag = null;
        NodeCanvas.Children.Remove(drag.Preview);
        drag.Pointer.Capture(null);
    }

    private (NodeVisual Node, int Input)? FindStageInputConnectionTarget(GpuType sourceType, Point point)
    {
        if (Workspace?.Kernel is not { } k) return null;
        var root = TopLevel.GetTopLevel(this);
        if (root is null || NodeCanvas.TranslatePoint(point, root) is not { } pointer) return null;
        (NodeVisual Node, int Input)? best = null;
        var distance = PortSnapFeedback.AcquireRadius;
        foreach (var node in k.Nodes)
        {
            if (!_nodeVisuals.TryGetValue(node.Id, out var visual)) continue;
            var op = GpuOperationRegistry.Operations[node.Op];
            for (var input = 0; input < visual.Inputs.Length && input < op.MaxInputs; input++)
            {
                var pin = visual.Inputs[input];
                if (!pin.IsEffectivelyVisible || pin.TranslatePoint(new Point(pin.Bounds.Width / 2, pin.Bounds.Height / 2), root) is not { } center) continue;
                var next = ((Vector)(center - pointer)).Length;
                if (next <= distance || best is null && ReferenceEquals(pin, _connectionFeedback.Pin) && next <= PortSnapFeedback.ReleaseRadius)
                {
                    best = (visual, input);
                    distance = next;
                }
            }
        }
        return best;
    }

    private void UpdateStageOutputAnchors()
    {
        foreach (var card in new[] { _stageOutput, _stageInput })
        {
            if (card is null) continue;
            card.CanvasOrigin = _viewport.Origin;
            card.SyncTransform();
            foreach (var (attrId, (pin, _)) in card.Pins)
            {
                card.Anchors[attrId] = pin.TranslatePoint(
                    new Point(pin.Bounds.Width / 2, pin.Bounds.Height / 2), card.Border) ?? new(0, 40);
            }
        }
        UpdateOutputWireGeometry();
    }

    private void UpdateOutputWireGeometry()
    {
        if (_stageOutput is { } stage)
        {
            foreach (var wire in _outputWires)
            {
                if (!stage.Anchors.TryGetValue(wire.Attribute, out var endAnchor)) continue;
                Point? startPoint = wire.Source is not null
                    ? wire.Source.CanvasPosition + (Vector)wire.Source.OutputAnchor
                    : wire.StageInputKey is not null && _stageInput is { } inputCard &&
                      inputCard.Anchors.TryGetValue(wire.StageInputKey, out var startAnchor)
                        ? inputCard.CanvasPosition + (Vector)startAnchor
                        : null;
                if (startPoint is { } start)
                {
                    wire.Curve.Width = NodeCanvas.Width;
                    wire.Curve.Height = NodeCanvas.Height;
                    wire.Curve.SetEndpoints(start, stage.CanvasPosition + (Vector)endAnchor);
                }
            }
        }
        if (_stageInput is { } inputStage)
        {
            foreach (var wire in _inputWires)
            {
                if (inputStage.Anchors.TryGetValue(wire.Key, out var startAnchor) &&
                    wire.Input < wire.Target.InputAnchors.Length)
                {
                    wire.Curve.Width = NodeCanvas.Width;
                    wire.Curve.Height = NodeCanvas.Height;
                    wire.Curve.SetEndpoints(
                        inputStage.CanvasPosition + (Vector)startAnchor,
                        wire.Target.CanvasPosition + (Vector)wire.Target.InputAnchors[wire.Input]);
                }
            }
        }
    }

    private (string Attribute, Button Pin, Point Anchor)? FindStageOutputTarget(ConnectionDrag connection, Point point)
    {
        if (_stageOutput is not { } stage) return null;
        var root = TopLevel.GetTopLevel(this);
        if (root is null || NodeCanvas.TranslatePoint(point, root) is not { } pointer) return null;
        (string Attribute, Button Pin, Point Anchor)? best = null;
        var distance = PortSnapFeedback.AcquireRadius;
        foreach (var (attrId, (pin, type)) in stage.Pins)
        {
            if (connection.Source.Model.Type != type) continue;
            if (!pin.IsEffectivelyVisible || pin.TranslatePoint(new Point(pin.Bounds.Width / 2, pin.Bounds.Height / 2), root) is not { } center) continue;
            var next = ((Vector)(center - pointer)).Length;
            if (next <= distance || best is null && ReferenceEquals(pin, _connectionFeedback.Pin) && next <= PortSnapFeedback.ReleaseRadius)
            {
                if (stage.Anchors.TryGetValue(attrId, out var anchor))
                {
                    best = (attrId, pin, anchor);
                    distance = next;
                }
            }
        }
        return best;
    }
}
