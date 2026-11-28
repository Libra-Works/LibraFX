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
using LiberaFx.Services.Gpu;
using LiberaFx.Services.Settings;

namespace LiberaFx.Views.Gpu;

public partial class GpuWorkspaceView : UserControl
{
    private void BuildInspector()
    {
        NodeInspector.Children.Clear();
        var vm = Workspace!;
        var n = vm.Kernel?.Nodes.FirstOrDefault(n => n.Id == _selection);
        SelectionLabel.Text = n is null ? LocalizationService.Shared.Get("Gpu.SelectVisualNode") : NodeDisplayName(n);
        ToolTip.SetTip(SelectionLabel, n?.Id);
        WritesSeparator.IsVisible = WritesHeading.IsVisible = WritesPanel.IsVisible = BindingsPanel.IsVisible = n is null;
        if (n is null) return;
        var type = new ComboBox { ItemsSource = Enum.GetValues<GpuType>().Where(t => t != GpuType.Quaternion).ToArray(), SelectedItem = n.Type };
        if (n.Op == GpuOp.SampleFlowMap)
            NodeInspector.Children.Add(new TextBlock { Text = LocalizationService.Shared.Get("Gpu.FlowMapTypeFromInput") });
        else
        {
            NodeInspector.Children.Add(new TextBlock { Text = LocalizationService.Shared.Get("Gpu.ResultType") });
            NodeInspector.Children.Add(type);
        }
        var constantPanel = new StackPanel { Spacing = 4 };
        var componentBoxes = new List<TextBox>();
        CheckBox? boolBox = null;
        static int ConstantComponentCount(GpuType gpuType) => gpuType switch
        {
            GpuType.Float2 => 2,
            GpuType.Float3 => 3,
            GpuType.Float4 => 4,
            _ => 1
        };
        static string[] ConstantComponentLabels(GpuType gpuType) => gpuType switch
        {
            GpuType.Float2 => ["X", "Y"],
            GpuType.Float3 => ["X", "Y", "Z"],
            GpuType.Float4 => ["X", "Y", "Z", "W"],
            _ => ["Value"]
        };
        List<double> ReadConstantValues(GpuType gpuType)
        {
            if (gpuType == GpuType.Bool)
                return [boolBox?.IsChecked == true ? 1d : 0d];
            var count = ConstantComponentCount(gpuType);
            var list = new List<double>(count);
            for (var i = 0; i < count; i++)
            {
                var raw = i < componentBoxes.Count ? componentBoxes[i].Text : null;
                var fallback = i < n.Value.Count ? n.Value[i] : 0d;
                list.Add(double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback);
            }
            return list;
        }
        void RebuildConstantInputs(GpuType gpuType, IReadOnlyList<double> values, Action onCommit)
        {
            constantPanel.Children.Clear();
            componentBoxes.Clear();
            boolBox = null;
            if (gpuType == GpuType.Bool)
            {
                boolBox = new CheckBox
                {
                    Content = "True",
                    IsChecked = values.Count > 0 && values[0] != 0d
                };
                boolBox.IsCheckedChanged += (_, _) => onCommit();
                constantPanel.Children.Add(boolBox);
                return;
            }
            var labels = ConstantComponentLabels(gpuType);
            for (var i = 0; i < labels.Length; i++)
            {
                var box = new TextBox
                {
                    Text = (i < values.Count ? values[i] : 0d).ToString("G", CultureInfo.InvariantCulture),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
                };
                box.LostFocus += (_, _) => onCommit();
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { onCommit(); e.Handled = true; } };
                componentBoxes.Add(box);
                if (labels.Length == 1)
                {
                    constantPanel.Children.Add(box);
                }
                else
                {
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*"), ColumnSpacing = 6 };
                    row.Children.Add(new TextBlock
                    {
                        Text = labels[i],
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        FontWeight = FontWeight.SemiBold
                    });
                    Grid.SetColumn(box, 1);
                    row.Children.Add(box);
                    constantPanel.Children.Add(row);
                }
            }
        }
        if (n.Op == GpuOp.Constant)
        {
            NodeInspector.Children.Add(new TextBlock { Text = LocalizationService.Shared.Get("Gpu.ConstantComponents") });
            NodeInspector.Children.Add(constantPanel);
        }
        var attr = new ComboBox { ItemsSource = ParticleAbi.Attributes.Select(a => a.Id).ToArray(), SelectedItem = n.Attribute };
        if (n.Op == GpuOp.ReadAttribute) NodeInspector.Children.Add(attr);
        var uniform = new ComboBox { ItemsSource = ParticleAbi.Uniforms.Keys.ToArray(), SelectedItem = n.Uniform };
        if (n.Op == GpuOp.ReadUniform) NodeInspector.Children.Add(uniform);
        var flowMap = new ComboBox
        {
            ItemsSource = vm.State.Program.Resources.Where(resource => resource.Kind == ResourceKind.FlowMap2D)
                .Select(resource => resource.Id).ToArray(),
            SelectedItem = n.Resource
        };
        if (n.Op == GpuOp.SampleFlowMap)
        {
            NodeInspector.Children.Add(new TextBlock { Text = LocalizationService.Shared.Get("Gpu.FlowMapResource") });
            NodeInspector.Children.Add(flowMap);
        }
        var component = new ComboBox
        {
            ItemsSource = new[] { "0 (X)", "1 (Y)", "2 (Z)", "3 (W)" },
            SelectedIndex = Math.Clamp(n.ComponentIndex ?? 0, 0, 3)
        };
        if (n.Op == GpuOp.Component) { NodeInspector.Children.Add(new TextBlock { Text = "Component" }); NodeInspector.Children.Add(component); }

        if (n.Op == GpuOp.FCurve)
        {
            NodeInspector.Children.Add(new TextBlock { Text = "F-Curve (double-click to add, right-click/Del to remove)" });
            var curveCanvas = new GpuCurveCanvas { Curve = n.Curve ?? GpuCurveData.DefaultLinear() };
            NodeInspector.Children.Add(curveCanvas);

            var keyTimeBox = new TextBox();
            var keyValueBox = new TextBox();
            var interpBox = new ComboBox
            {
                ItemsSource = new[] { "Cubic", "Linear", "Constant" }
            };
            var syncingCurveUi = false;
            void SyncSelectedKeyUi()
            {
                syncingCurveUi = true;
                try
                {
                    var key = curveCanvas.SelectedKey;
                    keyTimeBox.Text = key?.Time.ToString("0.###", CultureInfo.InvariantCulture) ?? "0";
                    keyValueBox.Text = key?.Value.ToString("0.###", CultureInfo.InvariantCulture) ?? "0";
                    interpBox.SelectedIndex = key is null ? 0 : Math.Clamp(key.Interpolation, 0, 2);
                }
                finally { syncingCurveUi = false; }
            }
            SyncSelectedKeyUi();
            curveCanvas.SelectionChanged += SyncSelectedKeyUi;

            void CommitCurve(GpuCurveData updatedCurve) => Run(() =>
            {
                var updated = n with { Curve = updatedCurve, SourceSpan = null };
                if (updated != n)
                    vm.Edit(new() { Action = "update", KernelId = vm.KernelId, Node = updated });
            });
            curveCanvas.CurveCommitted += CommitCurve;

            void ApplySelectedKeyFields()
            {
                if (syncingCurveUi || curveCanvas.SelectedKey is not { } key) return;
                if (!double.TryParse(keyTimeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) t = key.Time;
                if (!double.TryParse(keyValueBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) v = key.Value;
                var interp = Math.Clamp(interpBox.SelectedIndex, 0, 2);
                curveCanvas.UpdateSelectedKey(key with { Time = t, Value = v, Interpolation = interp });
            }
            keyTimeBox.LostFocus += (_, _) => ApplySelectedKeyFields();
            keyTimeBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplySelectedKeyFields(); e.Handled = true; } };
            keyValueBox.LostFocus += (_, _) => ApplySelectedKeyFields();
            keyValueBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplySelectedKeyFields(); e.Handled = true; } };
            interpBox.SelectionChanged += (_, _) => ApplySelectedKeyFields();

            var keyRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
            keyRow.Children.Add(new TextBlock { Text = "T", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            keyTimeBox.Width = 64;
            keyRow.Children.Add(keyTimeBox);
            keyRow.Children.Add(new TextBlock { Text = "V", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            keyValueBox.Width = 64;
            keyRow.Children.Add(keyValueBox);
            keyRow.Children.Add(interpBox);
            NodeInspector.Children.Add(keyRow);

            var presetRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
            presetRow.Children.Add(Button("0→1", () => curveCanvas.SetPreset([new(0, 0), new(1, 1)])));
            presetRow.Children.Add(Button("1→0", () => curveCanvas.SetPreset([new(0, 1), new(1, 0)])));
            presetRow.Children.Add(Button("0→1→0", () => curveCanvas.SetPreset([new(0, 0), new(0.5, 1), new(1, 0)])));
            NodeInspector.Children.Add(presetRow);
        }

        void CommitInspector() => Run(() =>
        {
            var nextType = n.Op == GpuOp.SampleFlowMap ? n.Type : (GpuType)type.SelectedItem!;
            var updated = n with
            {
                Type = nextType,
                Value = n.Op == GpuOp.Constant ? ReadConstantValues(nextType) : n.Value,
                Attribute = n.Op == GpuOp.ReadAttribute ? attr.SelectedItem as string : n.Attribute,
                Uniform = n.Op == GpuOp.ReadUniform ? uniform.SelectedItem as string : n.Uniform,
                ComponentIndex = n.Op == GpuOp.Component ? Math.Clamp(component.SelectedIndex, 0, 3) : n.ComponentIndex,
                Resource = n.Op == GpuOp.SampleFlowMap ? flowMap.SelectedItem as string : n.Resource,
                SourceSpan = null
            };
            if (updated != n)
                vm.Edit(new() { Action = "update", KernelId = vm.KernelId, Node = updated });
        });

        if (n.Op == GpuOp.Constant)
            RebuildConstantInputs(n.Type, n.Value, CommitInspector);

        type.SelectionChanged += (_, _) =>
        {
            if (n.Op == GpuOp.Constant && type.SelectedItem is GpuType nextType)
                RebuildConstantInputs(nextType, ReadConstantValues(nextType), CommitInspector);
            CommitInspector();
        };
        attr.SelectionChanged += (_, _) => CommitInspector();
        uniform.SelectionChanged += (_, _) => CommitInspector();
        flowMap.SelectionChanged += (_, _) => CommitInspector();
        component.SelectionChanged += (_, _) => CommitInspector();

        for (var i = 0; i < n.Inputs.Count; i++)
        {
            var input = i;
            NodeInspector.Children.Add(Button("Disconnect input " + input, () =>
                vm.Edit(new() { Action = "disconnect", KernelId = vm.KernelId, NodeId = n.Id, Input = input })));
        }
        NodeInspector.Children.Add(Button(LocalizationService.Shared.Get("Gpu.RemoveNode"), () =>
            vm.Edit(new() { Action = "remove", KernelId = vm.KernelId, NodeId = n.Id })));
    }
    private void BuildWrites()
    {
        WritesPanel.Children.Clear();
        var vm = Workspace!;
        if (vm.Kernel is not { } k) return;
        if (k.Stage == KernelStage.Update)
        {
            var customAge = k.Writes.Any(w => w.Attribute == "Age");
            WritesPanel.Children.Add(new TextBlock
            {
                Text = LocalizationService.Shared.Get(customAge ? "Gpu.AgeMode.Custom" : "Gpu.AgeMode.Auto"),
                TextWrapping = TextWrapping.Wrap
            });
            if (!customAge)
            {
                var customize = Button(LocalizationService.Shared.Get("Gpu.CustomizeAgeUpdate"), () =>
                {
                    var ageId = "n_" + Guid.NewGuid().ToString("N");
                    var dtId = "n_" + Guid.NewGuid().ToString("N");
                    var addId = "n_" + Guid.NewGuid().ToString("N");
                    vm.Edit(
                        new GpuEdit { Action = "add", KernelId = k.Id, Position = new(60, 40), Node = new() { Id = ageId, Op = GpuOp.ReadAttribute, Type = GpuType.Float, Attribute = "Age" } },
                        new GpuEdit { Action = "add", KernelId = k.Id, Position = new(60, 120), Node = new() { Id = dtId, Op = GpuOp.ReadUniform, Type = GpuType.Float, Uniform = "deltaTime" } },
                        new GpuEdit { Action = "add", KernelId = k.Id, Position = new(280, 80), Node = new() { Id = addId, Op = GpuOp.Add, Type = GpuType.Float, Inputs = [ageId, dtId] } },
                        new GpuEdit { Action = "write", KernelId = k.Id, Write = new("w_" + Guid.NewGuid().ToString("N"), "Age", addId) }
                    );
                });
                customize.IsEnabled = !vm.Graph.IsReadOnly;
                WritesPanel.Children.Add(customize);
            }
        }
        WritesPanel.Children.Add(new TextBlock
        {
            Text = LocalizationService.Shared.Get("Gpu.CustomAttributes"),
            Margin = new Thickness(0, 6, 0, 2),
            FontWeight = FontWeight.SemiBold
        });
        var customSlots = new[] { "Custom0", "Custom1", "Custom2" };
        foreach (var slot in customSlots)
        {
            var slotId = slot;
            var isActive = _visibleOutputAttributes.Contains(slotId) ||
                           k.Writes.Any(w => w.Attribute == slotId) ||
                           k.Nodes.Any(n => n.Op == GpuOp.ReadAttribute && n.Attribute == slotId);
            if (!isActive) continue;
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new TextBlock
            {
                Text = $"{AttributePinLabel(slotId)} ({slotId} : Float)",
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            var removeCustom = Button("×", () =>
            {
                _visibleOutputAttributes.Remove(slotId);
                _customAttributeLabels.Remove(slotId);
                var write = k.Writes.FirstOrDefault(w => w.Attribute == slotId);
                if (write is not null)
                {
                    vm.Edit(new GpuEdit { Action = "write-remove", KernelId = k.Id, NodeId = write.Id });
                }
                else if (Workspace?.Kernel is { } current)
                {
                    RebuildStageOutput(Workspace, current);
                    _anchorsDirty = true;
                    ScheduleFrame();
                    BuildWrites();
                }
            });
            removeCustom.IsEnabled = !vm.Graph.IsReadOnly;
            row.Children.Add(removeCustom);
            WritesPanel.Children.Add(row);
        }
        var nextFreeSlot = customSlots.FirstOrDefault(slotId =>
            !_visibleOutputAttributes.Contains(slotId) &&
            k.Writes.All(w => w.Attribute != slotId) &&
            k.Nodes.All(n => n.Op != GpuOp.ReadAttribute || n.Attribute != slotId));
        if (nextFreeSlot is not null)
        {
            var customNameBox = new TextBox
            {
                PlaceholderText = LocalizationService.Shared.Get("Gpu.CustomAttributeName"),
                IsEnabled = !vm.Graph.IsReadOnly
            };
            WritesPanel.Children.Add(customNameBox);
            var addCustom = Button(LocalizationService.Shared.Get("Gpu.AddCustomAttribute"), () =>
            {
                var name = customNameBox.Text?.Trim();
                if (!string.IsNullOrEmpty(name))
                {
                    _customAttributeLabels[nextFreeSlot] = name;
                }
                _visibleOutputAttributes.Add(nextFreeSlot);
                if (Workspace?.Kernel is { } current)
                {
                    RebuildStageOutput(Workspace, current);
                    _anchorsDirty = true;
                    ScheduleFrame();
                    BuildWrites();
                }
            });
            addCustom.IsEnabled = !vm.Graph.IsReadOnly;
            WritesPanel.Children.Add(addCustom);
        }
        var aliveToggle = new CheckBox
        {
            Content = LocalizationService.Shared.GetOrDefault("Gpu.Attribute.Alive", "Alive"),
            IsChecked = _visibleOutputAttributes.Contains("Alive") || k.Writes.Any(w => w.Attribute == "Alive")
        };
        aliveToggle.IsCheckedChanged += (_, _) =>
        {
            if (aliveToggle.IsChecked == true) _visibleOutputAttributes.Add("Alive");
            else _visibleOutputAttributes.Remove("Alive");
            if (Workspace?.Kernel is { } current)
            {
                RebuildStageOutput(Workspace, current);
                _anchorsDirty = true;
                ScheduleFrame();
            }
        };
        WritesPanel.Children.Add(aliveToggle);
    }
    private void RefreshDiagnostics()
    {
        if (Workspace is not { } vm) return;
        DiagnosticText.Text = string.Join("\n", vm.Diagnostics.Select(d =>
            $"{d.Code} · {d.KernelId}/{d.NodeId}/{d.PinId}\n{d.Message}" +
            (d.Span is { } span ? $" [text {span.Start}:{span.Length}]" : "")));
    }
    private void BuildBindings()
    {
        BindingsPanel.Children.Clear();
        var vm = Workspace!;
        BindingsPanel.Children.Add(new TextBlock { Text = LocalizationService.Shared.Get("Gpu.SpawnRate") });
        var spawnRate = new NumericUpDown { Name = "GpuSpawnRate", Minimum = 0, Maximum = 1_000_000,
            Value = (decimal)(vm.State.Program.SpawnRate ?? 100), IsEnabled = !vm.Graph.IsReadOnly };
        BindingsPanel.Children.Add(spawnRate);
        var applySpawnRate = Button(LocalizationService.Shared.Get("Fluid.Apply"), () =>
            vm.Edit(new() { Action = "spawn-rate", SpawnRate = (double)(spawnRate.Value ?? 0) }));
        applySpawnRate.IsEnabled = !vm.Graph.IsReadOnly;
        BindingsPanel.Children.Add(applySpawnRate);
        BindingsPanel.Children.Add(Button(LocalizationService.Shared.Get("Gpu.AddFlowMap"), () =>
        {
            var resource = new ResourceDeclaration
            {
                Id = "flowmap_" + Guid.NewGuid().ToString("N"),
                Kind = ResourceKind.FlowMap2D,
                FlowMap = new()
            };
            vm.Edit(new() { Action = "resource", Resource = resource });
        }));
        foreach (var flow in vm.State.Program.Flow.Where(f => f.Kind == FlowKind.RenderBillboard))
        {
            var settings = vm.State.RendererSettings.GetValueOrDefault(flow.Id) ?? new GpuRendererSettings();
            BindingsPanel.Children.Add(new TextBlock
            {
                Text = LocalizationService.Shared.Get("Gpu.BillboardRenderer") + " : " + flow.Id,
                TextWrapping = TextWrapping.Wrap
            });
            var presets = new[] { "builtin:unlit-alpha", "builtin:unlit-additive" };
            var material = new ComboBox
            {
                ItemsSource = new[] { LocalizationService.Shared.Get("Gpu.MaterialAlpha"),
                    LocalizationService.Shared.Get("Gpu.MaterialAdditive") },
                SelectedIndex = Array.IndexOf(presets, settings.Material)
            };
            BindingsPanel.Children.Add(material);
            BindingsPanel.Children.Add(new TextBlock { Text = LocalizationService.Shared.Get("Gpu.DrawSizeMultiplier") });
            var scale = new TextBox { Text = settings.SizeScale.ToString("G", CultureInfo.InvariantCulture) };
            BindingsPanel.Children.Add(scale);
            var spriteAnimation=new CheckBox {Content=LocalizationService.Shared.Get("Cpu.SpriteAnimationEnabled"),
                IsChecked=settings.SpriteAnimationEnabled,IsEnabled=settings.SpriteAnimation is not null || settings.SpriteComposition is not null};
            BindingsPanel.Children.Add(spriteAnimation);
            BindingsPanel.Children.Add(Button(LocalizationService.Shared.Get("Gpu.ApplyRenderer"), () =>
                vm.Edit(new()
                {
                    Action = "renderer", NodeId = flow.Id, Material = presets[material.SelectedIndex],
                    SizeScale = double.Parse(scale.Text!, CultureInfo.InvariantCulture),SpriteAnimationEnabled=spriteAnimation.IsChecked==true
                })));
        }
        foreach (var resource in vm.State.Program.Resources)
        {
            BindingsPanel.Children.Add(new TextBlock { Text = LocalizationService.Shared.Get("Gpu.TextureBinding") + " : " + resource.Id,
                TextWrapping = TextWrapping.Wrap });
            if (resource.Kind == ResourceKind.FlowMap2D && resource.FlowMap is { } settings)
            {
                BindingsPanel.Children.Add(new TextBlock
                {
                    Text = LocalizationService.Shared.Get("Gpu.FlowMapLinearHint"),
                    TextWrapping = TextWrapping.Wrap
                });
                var axes = Enum.GetValues<GpuAxis>();
                var encoding = new ComboBox { ItemsSource = Enum.GetValues<FlowMapEncoding>().Select(value => LocalizationService.Shared.Get("Fluid.FieldEncoding." + value)).ToArray(),
                    SelectedIndex = (int)settings.Encoding };
                var coordinates = new ComboBox { ItemsSource = Enum.GetValues<FlowMapCoordinates>().Select(value => LocalizationService.Shared.Get("Fluid.FieldCoordinates." + value)).ToArray(),
                    SelectedIndex = (int)settings.Coordinates };
                BindingsPanel.Children.Add(new TextBlock { Text = LocalizationService.Shared.Get("Fluid.FieldEncoding") });
                BindingsPanel.Children.Add(encoding);
                BindingsPanel.Children.Add(new TextBlock { Text = LocalizationService.Shared.Get("Fluid.FieldCoordinates") });
                BindingsPanel.Children.Add(coordinates);
                var axisU = new ComboBox { ItemsSource = axes, SelectedItem = settings.AxisU };
                var axisV = new ComboBox { ItemsSource = axes, SelectedItem = settings.AxisV };
                BindingsPanel.Children.Add(new TextBlock { Text = LocalizationService.Shared.Get("Gpu.FlowMapAxisU") });
                BindingsPanel.Children.Add(axisU);
                BindingsPanel.Children.Add(new TextBlock { Text = LocalizationService.Shared.Get("Gpu.FlowMapAxisV") });
                BindingsPanel.Children.Add(axisV);
                var minimumU = new TextBox { Text = settings.WorldMinimumU.ToString("G", CultureInfo.InvariantCulture) };
                var maximumU = new TextBox { Text = settings.WorldMaximumU.ToString("G", CultureInfo.InvariantCulture) };
                var minimumV = new TextBox { Text = settings.WorldMinimumV.ToString("G", CultureInfo.InvariantCulture) };
                var maximumV = new TextBox { Text = settings.WorldMaximumV.ToString("G", CultureInfo.InvariantCulture) };
                foreach (var (label, field) in new[]
                {
                    (LocalizationService.Shared.Get("Gpu.FlowMapMinimumU"), minimumU),
                    (LocalizationService.Shared.Get("Gpu.FlowMapMaximumU"), maximumU),
                    (LocalizationService.Shared.Get("Gpu.FlowMapMinimumV"), minimumV),
                    (LocalizationService.Shared.Get("Gpu.FlowMapMaximumV"), maximumV)
                })
                {
                    BindingsPanel.Children.Add(new TextBlock { Text = label });
                    BindingsPanel.Children.Add(field);
                }
                BindingsPanel.Children.Add(Button(LocalizationService.Shared.Get("Gpu.ApplyFlowMap"), () =>
                    vm.Edit(new()
                    {
                        Action = "resource",
                        Resource = resource with
                        {
                            FlowMap = settings with
                            {
                                AxisU = (GpuAxis)axisU.SelectedItem!,
                                AxisV = (GpuAxis)axisV.SelectedItem!,
                                Encoding = (FlowMapEncoding)encoding.SelectedIndex,
                                Coordinates = (FlowMapCoordinates)coordinates.SelectedIndex,
                                WorldMinimumU = double.Parse(minimumU.Text!, CultureInfo.InvariantCulture),
                                WorldMaximumU = double.Parse(maximumU.Text!, CultureInfo.InvariantCulture),
                                WorldMinimumV = double.Parse(minimumV.Text!, CultureInfo.InvariantCulture),
                                WorldMaximumV = double.Parse(maximumV.Text!, CultureInfo.InvariantCulture)
                            }
                        },
                        AssetPath = vm.State.ResourceAssetPaths.GetValueOrDefault(resource.Id)
                    })));
            }
            if (resource.Kind is ResourceKind.Texture2D or ResourceKind.FlowMap2D)
            {
                var producers = GpuProgramDeliveryService.Containers(vm.Graph.CreateCurrentSnapshot())
                    .Where(node => node.Id != vm.ContainerId && GpuParticleEditorService.ReadState(node).Fluid2D is not null).ToArray();
                if (producers.Length != 0)
                {
                    BindingsPanel.Children.Add(new TextBlock { Text = LocalizationService.Shared.Get("Fluid.GeneratedSource") });
                    var current = vm.State.GeneratedResources?.GetValueOrDefault(resource.Id);
                    var selection = new ComboBox
                    {
                        ItemsSource = producers.Select(node => node.Name + " · DyeOutput").ToArray(),
                        SelectedIndex = Math.Max(0, Array.FindIndex(producers, node => node.Id == current?.ProducerNodeId)),
                        IsEnabled = !vm.Graph.IsReadOnly
                    };
                    BindingsPanel.Children.Add(selection);
                    var outputs = resource.Kind == ResourceKind.Texture2D ? new[] { "DyeOutput", "ColorOutput", "FieldOutput" } : new[] { "VelocityOutput", "DensityOutput" };
                    var output = new ComboBox { ItemsSource = outputs, SelectedIndex = Math.Max(0, Array.IndexOf(outputs, current?.OutputId)), IsEnabled = !vm.Graph.IsReadOnly };
                    BindingsPanel.Children.Add(output);
                    BindingsPanel.Children.Add(Button(LocalizationService.Shared.Get("Fluid.BindGeneratedSource"), () =>
                        vm.Edit(new()
                        {
                            Action = "resource", Resource = resource.Kind == ResourceKind.FlowMap2D ? resource with
                            {
                                FlowMap = (resource.FlowMap ?? new()) with { Encoding = output.SelectedIndex == 0 ? FlowMapEncoding.FloatVelocity : FlowMapEncoding.FloatDensity,
                                    Coordinates = FlowMapCoordinates.GeneratedPlane }
                            } : resource,
                            GeneratedResource = new(producers[selection.SelectedIndex].Id, outputs[output.SelectedIndex])
                        })));
                }
            }
            var path = new TextBox { Text = vm.State.ResourceAssetPaths.GetValueOrDefault(resource.Id) ?? "",
                PlaceholderText = LocalizationService.Shared.Get("Gpu.TexturePathHint") };
            BindingsPanel.Children.Add(path);
            BindingsPanel.Children.Add(Button(LocalizationService.Shared.Get("Gpu.BindTexture"), () => vm.Edit(new()
            {
                Action = "resource", Resource = resource, AssetPath = path.Text ?? ""
            })));
        }
    }
    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_refreshing || Workspace is not { Kernel: { } k } vm) return;
        var text = KernelText.Text ?? "";
        // TextChanged can arrive after the refresh guard has ended.
        if (text == _displayedText) return;
        _displayedText = text;
        _drafting = true;
        try { Run(() => vm.Edit(new() { Action = "draft", KernelId = k.Id, Text = text })); }
        finally { _drafting = false; }
    }
    private void OnApplyText(object? sender, RoutedEventArgs e) => Run(() =>
        Workspace!.Edit(new() { Action = "text", KernelId = Workspace.KernelId, Text = KernelText.Text ?? "" }));
    private void OnEmitText(object? sender, RoutedEventArgs e) => Run(() =>
        Workspace!.Edit(new() { Action = "discard-draft", KernelId = Workspace.KernelId }));
    private void OnCapacity(object? sender, RoutedEventArgs e) => Run(() =>
        Workspace!.Edit(new() { Action = "capacity", Capacity = uint.Parse(Capacity.Text!, CultureInfo.InvariantCulture) }));
    private void OnCompile(object? sender, RoutedEventArgs e) => Workspace?.ValidateSources();
    private void OnUndo(object? sender, RoutedEventArgs e) => Workspace?.Graph.History.Undo();
    private void OnRedo(object? sender, RoutedEventArgs e) => Workspace?.Graph.History.Redo();
    private void OnClose(object? sender, RoutedEventArgs e) => CloseWorkspace();
    private void OnBack(object? sender, RoutedEventArgs e)
    {
        if (Workspace?.Kernel is null) CloseWorkspace();
        else { _selection = _wireSource = null; _selectedNodes.Clear(); Workspace.Back(); }
    }
    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_drag is not null) FinishDrag(commit: false);
            else if (_connectionDrag is not null) CancelConnectionDrag();
            else if (_marquee is not null) CancelMarquee();
            else OnBack(sender, new());
            e.Handled = true;
        }
        else if (!IsEditingSource(e.Source as Visual))
        {
            var shortcuts = ShortcutService.Shared;
            if (Workspace?.State.Fluid2D is not null &&
                !shortcuts.Matches("undo", e) && !shortcuts.Matches("redo", e))
            {
                return;
            }
            if (shortcuts.Matches("undo", e)) Workspace?.Graph.History.Undo();
            else if (shortcuts.Matches("redo", e)) Workspace?.Graph.History.Redo();
            else if (shortcuts.Matches("graph.delete", e)) DeleteSelection();
            else if (shortcuts.Matches("graph.copy", e)) await CopySelectionAsync(false);
            else if (shortcuts.Matches("graph.cut", e)) await CopySelectionAsync(true);
            else if (shortcuts.Matches("graph.paste", e)) await PasteAsync();
            else if (shortcuts.Matches("graph.all", e))
            {
                _selectedNodes.UnionWith(_nodeVisuals.Keys); _selection = _selectedNodes.LastOrDefault();
                UpdateSelectionStyles(); BuildInspector();
            }
            else if (shortcuts.Matches("graph.search", e)) OpenGraphContext(_viewport.LastPointer ?? _pastePosition);
            else if (shortcuts.Matches("graph.frame", e)) OnFrame(sender, new());
            else if (shortcuts.Matches("graph.zoomIn", e)) OnZoomIn(sender, new());
            else if (shortcuts.Matches("graph.zoomOut", e)) OnZoomOut(sender, new());
            else return;
            e.Handled = true;
        }
    }
    private static bool IsEditingSource(Visual? source)
    {
        for (var current = source; current is not null; current = current.GetVisualParent())
            if (current is TextBox or NumericUpDown or ComboBox) return true;
        return false;
    }
    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (Workspace is not { } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var format = (string)ExportFormat.SelectedItem!;
            var source = GpuParticleEditorService.Export(vm.State, format);
            var file = await top.StorageProvider.SaveFilePickerAsync(new()
            {
                Title = "GPU source export (not runtime execution)",
                SuggestedFileName = GpuParticleEditorService.SafeFileName(vm.State.Program.Id) + "." + format,
                DefaultExtension = format,
                FileTypeChoices = [new FilePickerFileType(format) { Patterns = ["*." + format] }]
            });
            if (file is null) return;
            var destination = file.TryGetLocalPath() ?? throw new IOException("Atomic GPU export requires a local destination.");
            await GpuParticleEditorService.WriteAtomicAsync(destination, source);
            if (format != "json")
            {
                var metadata = System.Text.Json.JsonSerializer.Serialize(
                    GpuParticleEditorService.ExportMetadata(vm.State, format), GpuParticleEditorService.Json);
                await GpuParticleEditorService.WriteAtomicAsync(Path.ChangeExtension(destination, ".metadata.json"), metadata);
            }
            Status.Text = LocalizationService.Shared.Get("Gpu.ExportedSource");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or GpuValidationException or ArgumentException)
        { Status.Text = error is GpuValidationException gpu ? string.Join("\n", gpu.Diagnostics.Select(d => d.Message)) : error.Message; }
    }
}
