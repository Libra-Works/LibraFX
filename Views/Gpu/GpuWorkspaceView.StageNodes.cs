using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LibraGpu.Core;
using LiberaFx.Services.Gpu;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Panels;
using LiberaFx.Views.Graph;

namespace LiberaFx.Views.Gpu;

public partial class GpuWorkspaceView
{
    private sealed class StageOutputVisual(Border border, Dictionary<string, (Button Pin, GpuType Type)> pins, Point position)
    {
        public readonly Border Border = border;
        public readonly Dictionary<string, (Button Pin, GpuType Type)> Pins = pins;
        public readonly Dictionary<string, Point> Anchors = [];
        public readonly TranslateTransform Translation = new();
        public Point Position = position;
        public Vector CanvasOrigin;
        public Point CanvasPosition => Position + CanvasOrigin;

        public void SyncTransform()
        {
            var p = CanvasPosition;
            Translation.X = p.X;
            Translation.Y = p.Y;
        }
    }

    private sealed class OutputWireVisual(NodeVisual source, string attribute, GpuConnectionCurve curve)
    {
        public readonly NodeVisual Source = source;
        public readonly string Attribute = attribute;
        public readonly GpuConnectionCurve Curve = curve;
    }

    private StageOutputVisual? _stageOutput;
    private readonly List<OutputWireVisual> _outputWires = [];
    private readonly HashSet<string> _visibleOutputAttributes =
        ["Position", "Velocity", "Color", "Size", "Rotation", "Age", "Lifetime"];

    private void ClearStageOutput()
    {
        if (_stageOutput is not null)
        {
            NodeCanvas.Children.Remove(_stageOutput.Border);
            _stageOutput = null;
        }
        foreach (var wire in _outputWires)
        {
            NodeCanvas.Children.Remove(wire.Curve);
        }
        _outputWires.Clear();
    }

    private void RebuildStageOutput(GpuWorkspaceViewModel vm, KernelGraph k)
    {
        ClearStageOutput();
        var activeAttrs = ParticleAbi.Attributes
            .Where(a => a.Id != "Seed" && (_visibleOutputAttributes.Contains(a.Id) || k.Writes.Any(w => w.Attribute == a.Id)))
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
            label.Text = $"{LocalizationService.Shared.GetOrDefault("Gpu.Attribute." + attr.Id, attr.Id)} ({attr.Type})";
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
        var pos = new Point(maxX + 300, minY);
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
        _stageOutput = new StageOutputVisual(border, pins, pos) { CanvasOrigin = _viewport.Origin };
        border.RenderTransform = _stageOutput.Translation;
        _stageOutput.SyncTransform();
        border.SizeChanged += (_, _) => { _anchorsDirty = true; ScheduleFrame(); };
        NodeCanvas.Children.Add(border);

        foreach (var write in k.Writes)
        {
            if (!_nodeVisuals.TryGetValue(write.Value, out var source)) continue;
            var curve = new GpuConnectionCurve
            {
                Width = NodeCanvas.Width,
                Height = NodeCanvas.Height,
                Stroke = TypeBrush(source.Model.Type),
                StrokeThickness = 2.5,
                IsHitTestVisible = false
            };
            _outputWires.Add(new OutputWireVisual(source, write.Attribute, curve));
            NodeCanvas.Children.Insert(1, curve);
        }
    }

    private void UpdateStageOutputAnchors()
    {
        if (_stageOutput is not { } stage) return;
        stage.CanvasOrigin = _viewport.Origin;
        stage.SyncTransform();
        foreach (var (attrId, (pin, _)) in stage.Pins)
        {
            stage.Anchors[attrId] = pin.TranslatePoint(
                new Point(pin.Bounds.Width / 2, pin.Bounds.Height / 2), stage.Border) ?? new(0, 40);
        }
        UpdateOutputWireGeometry();
    }

    private void UpdateOutputWireGeometry()
    {
        if (_stageOutput is not { } stage) return;
        foreach (var wire in _outputWires)
        {
            if (stage.Anchors.TryGetValue(wire.Attribute, out var endAnchor))
            {
                wire.Curve.Width = NodeCanvas.Width;
                wire.Curve.Height = NodeCanvas.Height;
                wire.Curve.SetEndpoints(
                    wire.Source.CanvasPosition + (Vector)wire.Source.OutputAnchor,
                    stage.CanvasPosition + (Vector)endAnchor);
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
