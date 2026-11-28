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
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class GpuWorkspaceView : UserControl
{
    private void BuildFlow()
    {
        var vm = Workspace!;
        FlowPanel.Children.Clear();
        var map = GpuFlowVisualization.Create(vm.State);
        var resourceText = LocalizationService.Shared;
        var resources = new StackPanel { Spacing = 6 };
        resources.Children.Add(new TextBlock { Text = resourceText.Get("Gpu.ResourcesTargets"), FontWeight = FontWeight.SemiBold });
        resources.Children.Add(new TextBlock { Text = resourceText.Get("Gpu.ParticleStorage", vm.State.Program.Capacity), TextWrapping = TextWrapping.Wrap });
        foreach (var resource in vm.State.Program.Resources)
        {
            var reference = vm.State.GeneratedResources?.GetValueOrDefault(resource.Id);
            resources.Children.Add(new TextBlock
            {
                Text = $"{resource.Id} · {resource.Kind} · " + (reference is null
                    ? resourceText.Get("Gpu.ExternalResource", vm.State.ResourceAssetPaths.GetValueOrDefault(resource.Id) ?? "")
                    : resourceText.Get("Gpu.GeneratedResource", reference.ProducerNodeId, reference.OutputId)),
                TextWrapping = TextWrapping.Wrap
            });
        }
        resources.Children.Add(new TextBlock { Text = resourceText.Get("Gpu.ResourcePassAccess"), TextWrapping = TextWrapping.Wrap });
        resources.Children.Add(BuildResourcePlanEditor());
        FlowPanel.Children.Add(FluidCard(resources));
        FlowPanel.Children.Add(new TextBlock
        {
            Text = map.Summary,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 640,
            Foreground = ThemeSettingsService.EditorBrush("GraphParameter")
        });
        for (var i = 0; i < map.Steps.Count; i++)
        {
            var step = map.Steps[i];
            var index = step.Index;
            var flow = step.Flow;
            if (i != 0)
                FlowPanel.Children.Add(new TextBlock
                {
                    Text = LocalizationService.Shared.Get("Gpu.FlowOrderHint"),
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    Foreground = ThemeSettingsService.EditorBrush("CurveAxis")
                });
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(new TextBlock
            {
                Text = $"{index + 1}. {flow.Kind}  ·  {flow.Id}",
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeight.SemiBold
            });
            if (step.CompiledMetadata is not null)
                panel.Children.Add(new TextBlock
                {
                    Text = LocalizationService.Shared.Get("Gpu.CompiledSourceRef", step.CompiledMetadata.Kind),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeSettingsService.EditorBrush("GraphSignal")
                });
            if ((flow.Kind is FlowKind.Spawn or FlowKind.Update) && step.Kernel is { } kernel)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = LocalizationService.Shared.Get("Gpu.KernelLabel", kernel.Id, kernel.Stage),
                    TextWrapping = TextWrapping.Wrap
                });
                panel.Children.Add(Button(LocalizationService.Shared.Get("Gpu.OpenKernel"), () =>
                {
                    _selection = null;
                    vm.EnterKernel(kernel.Id);
                }));
                if (flow.Kind == FlowKind.Spawn)
                    AddEmitterShapeControls(panel, vm, flow);
            }
            else if (flow.Kind == FlowKind.Spawn)
                AddEmitterShapeControls(panel, vm, flow);
            else if (flow.Kind == FlowKind.RenderBillboard)
            {
                if (flow.Kernel is not null)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = LocalizationService.Shared.Get("Gpu.UnexpectedKernelRef", flow.Kernel),
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = ThemeSettingsService.EditorBrush("GraphSignal")
                    });
                    if (step.Kernel is { } referencedKernel)
                        panel.Children.Add(Button(LocalizationService.Shared.Get("Gpu.InspectReferencedKernel"), () =>
                        {
                            _selection = null;
                            vm.EnterKernel(referencedKernel.Id);
                        }));
                }
                panel.Children.Add(new TextBlock
                {
                    Text = LocalizationService.Shared.Get("Gpu.BillboardSourceBindingPending"),
                    TextWrapping = TextWrapping.Wrap
                });
                var texture = new ComboBox
                {
                    ItemsSource = vm.State.Program.Resources.Where(r => r.Kind == ResourceKind.Texture2D).Select(r => r.Id).ToArray(),
                    SelectedItem = flow.Resource
                };
                Avalonia.Automation.AutomationProperties.SetName(
                    texture, LocalizationService.Shared.Get("Gpu.BillboardTextureResource"));
                texture.SelectionChanged += (_, _) =>
                {
                    if (texture.SelectedItem is string resource && resource != flow.Resource)
                        Run(() => vm.Edit(new() { Action = "flow-update", Flow = flow with { Resource = resource } }));
                };
                panel.Children.Add(texture);
            }
            else if (flow.Kernel is { } kernelId)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = LocalizationService.Shared.Get("Gpu.KernelRef", kernelId),
                    TextWrapping = TextWrapping.Wrap
                });
                if (step.Kernel is { } referencedKernel)
                    panel.Children.Add(Button(LocalizationService.Shared.Get("Gpu.InspectReferencedKernelUnsupported"), () =>
                    {
                        _selection = null;
                        vm.EnterKernel(referencedKernel.Id);
                    }));
            }

            AddFlowPorts(panel, LocalizationService.Shared.Get("Gpu.Inputs"), step.Inputs);
            AddFlowPorts(panel, LocalizationService.Shared.Get("Gpu.Outputs"), step.Outputs);
            foreach (var reason in step.UnsupportedReasons)
                panel.Children.Add(new TextBlock
                {
                    Text = LocalizationService.Shared.Get("Gpu.UnsupportedRelationship", reason),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeSettingsService.EditorBrush("GraphSignal")
                });

            var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 5 };
            var up = Button("↑", () => vm.Edit(new() { Action = "flow-move", NodeId = flow.Id, Input = index - 1 }));
            Avalonia.Automation.AutomationProperties.SetName(up, LocalizationService.Shared.Get("Gpu.MoveFlowEarlier"));
            ToolTip.SetTip(up, LocalizationService.Shared.Get("Gpu.MoveFlowEarlierTip"));
            up.IsEnabled = i > 0;
            var down = Button("↓", () => vm.Edit(new() { Action = "flow-move", NodeId = flow.Id, Input = index + 1 }));
            Avalonia.Automation.AutomationProperties.SetName(down, LocalizationService.Shared.Get("Gpu.MoveFlowLater"));
            ToolTip.SetTip(down, LocalizationService.Shared.Get("Gpu.MoveFlowLaterTip"));
            down.IsEnabled = i + 1 < vm.State.Program.Flow.Count;
            actions.Children.Add(up); actions.Children.Add(down);
            actions.Children.Add(Button(LocalizationService.Shared.Get("Gpu.Remove"), () => vm.Edit(new() { Action = "flow-remove", NodeId = flow.Id })));
            panel.Children.Add(actions);
            var border = new Border
            {
                Child = panel, Padding = new Thickness(14), CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1), BorderBrush = ThemeSettingsService.EditorBrush("NodeBorder"),
                Background = this.FindResource("EffectSurfaceBrush") as IBrush ?? Brushes.DimGray
            };
            border.PointerPressed += (_, e) =>
            {
                if (e.ClickCount >= 2 && (flow.Kind is FlowKind.Spawn or FlowKind.Update) &&
                    step.Kernel is { } kernel)
                {
                    _selection = null;
                    vm.EnterKernel(kernel.Id);
                    e.Handled = true;
                }
            };
            FlowPanel.Children.Add(border);
        }

        var relationTitle = new TextBlock
        {
            Text = LocalizationService.Shared.Get("Gpu.CrossFlowRelations"),
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 10, 0, 2)
        };
        FlowPanel.Children.Add(relationTitle);
        if (map.Transfers.Count == 0)
            FlowPanel.Children.Add(new TextBlock
            {
                Text = LocalizationService.Shared.Get("Gpu.NoCrossFlowMatch"),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 640,
                Foreground = ThemeSettingsService.EditorBrush("CurveAxis")
            });
        foreach (var transfer in map.Transfers)
        {
            var producer = map.Steps[transfer.ProducerIndex];
            var consumer = map.Steps[transfer.ConsumerIndex];
            var transferText =
                $"{producer.Index + 1}. {producer.Flow.Kind} ({transfer.ProducerFlowId}) → " +
                $"{consumer.Index + 1}. {consumer.Flow.Kind} ({transfer.ConsumerFlowId})\n" +
                $"{transfer.Attribute} : {transfer.TypeLabel} · write {string.Join(", ", transfer.ProducerWriteIds)} " +
                $"(value node {string.Join(", ", transfer.ProducerValueNodeIds)}) → " +
                $"read {string.Join(", ", transfer.ConsumerIds)}\n" +
                LocalizationService.Shared.Get(transfer.IsTypeCompatible ? "Gpu.StaticDependency" : "Gpu.TypeMismatchOrUnresolved") +
                (transfer.ProducerPrecedesConsumer
                    ? ""
                    : LocalizationService.Shared.Get("Gpu.InvalidTransferOrder"));
            FlowPanel.Children.Add(new Border
            {
                Child = new TextBlock { Text = transferText, TextWrapping = TextWrapping.Wrap, MaxWidth = 640 },
                Padding = new Thickness(10),
                BorderThickness = new Thickness(1),
                BorderBrush = transfer.IsTypeCompatible
                    ? ThemeSettingsService.EditorBrush("GraphParameter")
                    : ThemeSettingsService.EditorBrush("GraphSignal"),
                Background = this.FindResource("EffectSurfaceBrush") as IBrush ?? Brushes.DimGray
            });
        }

        if (map.Diagnostics.Count > 0)
        {
            FlowPanel.Children.Add(new TextBlock
            {
                Text = "Source validation / compilation diagnostics · no runtime execution is implied",
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 8, 0, 0)
            });
            foreach (var diagnostic in map.Diagnostics.Take(8))
                FlowPanel.Children.Add(new TextBlock
                {
                    Text = $"{diagnostic.Code}: {diagnostic.Message}",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 640,
                    Foreground = ThemeSettingsService.EditorBrush("GraphSignal")
                });
            if (map.Diagnostics.Count > 8)
                FlowPanel.Children.Add(new TextBlock { Text = $"… {map.Diagnostics.Count - 8} more diagnostics" });
        }
    }
    private void AddEmitterShapeControls(StackPanel panel, GpuWorkspaceViewModel vm, FlowStep flow)
    {
        var current = flow.SpawnShape ?? new GpuSpawnShapeSettings();
        panel.Children.Add(new TextBlock
        {
            Text = LocalizationService.Shared.Get("Gpu.EmitterShape"),
            FontWeight = FontWeight.SemiBold
        });
        var shapeChoice = new ComboBox();
        foreach (var kind in Enum.GetValues<GpuSpawnShapeKind>())
            shapeChoice.Items.Add(new ComboBoxItem
            {
                Content = LocalizationService.Shared.Get("Gpu.EmitterShape." + kind),
                Tag = kind
            });
        shapeChoice.SelectedItem = shapeChoice.Items
            .OfType<ComboBoxItem>().Single(item => item.Tag is GpuSpawnShapeKind kind && kind == current.Kind);
        Avalonia.Automation.AutomationProperties.SetName(
            shapeChoice, LocalizationService.Shared.Get("Gpu.EmitterShape"));
        shapeChoice.SelectionChanged += (_, _) =>
        {
            if (shapeChoice.SelectedItem is ComboBoxItem { Tag: GpuSpawnShapeKind kind } &&
                kind != current.Kind)
                Run(() => vm.Edit(new()
                {
                    Action = "flow-update",
                    Flow = flow with { SpawnShape = current with { Kind = kind } }
                }));
        };
        panel.Children.Add(shapeChoice);
        panel.Children.Add(new TextBlock
        {
            Text = LocalizationService.Shared.Get("Gpu.EmitterShapeHelp"),
            TextWrapping = TextWrapping.Wrap
        });
        if (current.Kind == GpuSpawnShapeKind.Box)
        {
            var halfExtents = current.HalfExtents;
            var x = AddEmitterShapeNumber(panel, "Gpu.EmitterHalfExtentX", halfExtents[0]);
            var y = AddEmitterShapeNumber(panel, "Gpu.EmitterHalfExtentY", halfExtents[1]);
            var z = AddEmitterShapeNumber(panel, "Gpu.EmitterHalfExtentZ", halfExtents[2]);
            panel.Children.Add(Button(LocalizationService.Shared.Get("Gpu.ApplyEmitterShape"), () => Run(() =>
            {
                var values = new[] { ParseEmitterShapeNumber(x), ParseEmitterShapeNumber(y), ParseEmitterShapeNumber(z) };
                vm.Edit(new()
                {
                    Action = "flow-update",
                    Flow = flow with { SpawnShape = current with { HalfExtents = values.ToList() } }
                });
            })));
        }
        else if (current.Kind is GpuSpawnShapeKind.Sphere or GpuSpawnShapeKind.Circle)
        {
            var radius = AddEmitterShapeNumber(panel, "Gpu.EmitterRadius", current.Radius);
            panel.Children.Add(Button(LocalizationService.Shared.Get("Gpu.ApplyEmitterShape"), () => Run(() =>
                vm.Edit(new()
                {
                    Action = "flow-update",
                    Flow = flow with { SpawnShape = current with { Radius = ParseEmitterShapeNumber(radius) } }
                }))));
        }
    }
    private static TextBox AddEmitterShapeNumber(StackPanel panel, string labelKey, double value)
    {
        panel.Children.Add(new TextBlock { Text = LocalizationService.Shared.Get(labelKey) });
        var input = new TextBox { Text = value.ToString("R", CultureInfo.InvariantCulture) };
        Avalonia.Automation.AutomationProperties.SetName(input, LocalizationService.Shared.Get(labelKey));
        panel.Children.Add(input);
        return input;
    }
    private static double ParseEmitterShapeNumber(TextBox input)
    {
        if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value) || value < 0)
            throw new ArgumentException(LocalizationService.Shared.Get("Gpu.EmitterShapeInvalid"));
        return value;
    }
    private static void AddFlowPorts(StackPanel panel, string heading, IReadOnlyList<GpuFlowPort> ports)
    {
        if (ports.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"{heading}: {LocalizationService.Shared.Get("Gpu.NoAuthoredPorts")}",
                Foreground = ThemeSettingsService.EditorBrush("CurveAxis")
            });
            return;
        }
        var content = new StackPanel { Spacing = 4 };
        foreach (var port in ports)
        {
            var prefix = port.Direction == GpuFlowPortDirection.Input ? "IN" : "OUT";
            var row = new TextBlock
            {
                Text = $"{prefix} · {port.Name} : {port.TypeLabel}\n{port.Detail}",
                TextWrapping = TextWrapping.Wrap,
                Foreground = port.IsTypeCompatible
                    ? ThemeSettingsService.EditorBrush("GraphParameter")
                    : ThemeSettingsService.EditorBrush("GraphSignal")
            };
            ToolTip.SetTip(row, port.Detail);
            content.Children.Add(row);
        }
        panel.Children.Add(new Expander
        {
            Header = LocalizationService.Shared.Get("Gpu.PortsSummary", heading, ports.Count),
            IsExpanded = false,
            Content = content
        });
    }
}
