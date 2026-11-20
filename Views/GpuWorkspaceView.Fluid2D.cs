using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LibraGpu.Core;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class GpuWorkspaceView
{
    private void AddSimulationModeControl(GpuWorkspaceViewModel vm)
    {
        var fluid = vm.State.Fluid2D is not null;
        var button = Button(LocalizationService.Shared.Get(fluid ? "Fluid.UseParticles" : "Fluid.UseSmoke"), () =>
        {
            vm.Back();
            vm.Edit(new() { Action = fluid ? "fluid-disable" : "fluid-enable" });
        });
        button.Content = new TextBlock
        {
            Text = LocalizationService.Shared.Get(fluid ? "Fluid.UseParticles" : "Fluid.UseSmoke"),
            TextWrapping = TextWrapping.Wrap
        };
        button.Name = "FluidModeButton";
        button.IsEnabled = !vm.Graph.IsReadOnly;
        Palette.Children.Add(button);
    }

    private void BuildFluidFlow()
    {
        var settings = Workspace!.State.Fluid2D!;
        var text = LocalizationService.Shared;
        FlowPanel.Children.Clear();
        FlowPanel.Children.Add(new TextBlock
        {
            Text = text.Get("Fluid.Model." + settings.Model), FontSize = 20, FontWeight = FontWeight.SemiBold
        });
        FlowPanel.Children.Add(new TextBlock
        {
            Text = text.Get("Fluid.Summary"), TextWrapping = TextWrapping.Wrap, MaxWidth = 640
        });
        var resources = new StackPanel { Spacing = 5, Name = "FluidResources" };
        resources.Children.Add(new TextBlock { Text = text.Get("Fluid.Resources"), FontWeight = FontWeight.SemiBold });
        foreach (var resource in Fluid2DCompiler.Resources(settings))
        {
            resources.Children.Add(new TextBlock
            {
                Text = $"{resource.Id} · {resource.Kind} · {resource.Format} · {resource.Width} × {resource.Height}",
                TextWrapping = TextWrapping.Wrap
            });
        }
        resources.Children.Add(new TextBlock
        {
            Text = text.Get("Fluid.ResourceOwnership"), TextWrapping = TextWrapping.Wrap, MaxWidth = 640
        });
        resources.Children.Add(BuildResourcePlanEditor());
        FlowPanel.Children.Add(FluidCard(resources));
        var pressureShown = false;
        foreach (var pass in Fluid2DCompiler.Step(settings))
        {
            if (pass.Pass == Fluid2DPass.Jacobi && pressureShown)
            {
                continue;
            }
            pressureShown |= pass.Pass == Fluid2DPass.Jacobi;
            FlowPanel.Children.Add(new TextBlock { Text = "↓", HorizontalAlignment = HorizontalAlignment.Center });
            var panel = new StackPanel { Spacing = 5 };
            panel.Children.Add(new TextBlock
            {
                Text = text.Get("Fluid.Pass." + pass.Pass) +
                    (pass.Pass == Fluid2DPass.Jacobi ? $" × {settings.PressureIterations}" : ""),
                FontWeight = FontWeight.SemiBold
            });
            if (pass.Pass == Fluid2DPass.Jacobi)
            {
                panel.Children.Add(new TextBlock { Text = "Pressure ↔ PressureScratch · Divergence" });
            }
            else
            {
                var reads = string.Join(", ", new[] { pass.Read0, pass.Read1 }.OfType<Fluid2DField>());
                var writes = pass.Pass == Fluid2DPass.RenderDye ? "DyeOutput" :
                    pass.Pass == Fluid2DPass.RenderFields ? "FieldOutput" : pass.Pass == Fluid2DPass.RenderColor ? "ColorOutput" :
                    string.Join(", ", new[] { pass.Write0, pass.Write1 }.OfType<Fluid2DField>());
                panel.Children.Add(new TextBlock
                {
                    Text = $"{text.Get("Fluid.Reads")} {reads}\n{text.Get("Fluid.Writes")} {writes}",
                    TextWrapping = TextWrapping.Wrap
                });
            }
            var card = FluidCard(panel);
            card.Tag = pass.Pass;
            FlowPanel.Children.Add(card);
        }
        FlowPanel.Children.Add(FluidCard(new TextBlock
        {
            Text = text.Get("Fluid.Renderer." + settings.Model) + "\n" + text.Get("Fluid.FieldLayout." + settings.Model),
            TextWrapping = TextWrapping.Wrap
        }));
    }

    private static Border FluidCard(Control content)
    {
        var card = new Border
        {
            Child = content, Padding = new Thickness(14), CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1), MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("EffectSurfaceBrush"));
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("EffectBorderBrush"));
        return card;
    }

    private void BuildFluidInspector()
    {
        var vm = Workspace!;
        var settings = vm.State.Fluid2D!;
        var text = LocalizationService.Shared;
        SelectionLabel.Text = text.Get("Fluid.Settings");
        NodeInspector.Children.Clear();
        WritesPanel.Children.Clear();
        BindingsPanel.Children.Clear();
        NodeInspector.Children.Add(new TextBlock { Text = text.Get("Fluid.SimulationModel") });
        var model = new ComboBox
        {
            Name = "FluidSimulationModel", ItemsSource = Enum.GetValues<Fluid2DModel>().Select(value => text.Get("Fluid.Model." + value)).ToArray(),
            SelectedIndex = (int)settings.Model, IsEnabled = !vm.Graph.IsReadOnly
        };
        model.SelectionChanged += (_, _) =>
        {
            if (model.SelectedIndex < 0 || model.SelectedIndex == (int)vm.State.Fluid2D!.Model) return;
            Run(() => vm.Edit(new() { Action = "fluid-settings",
                Fluid2D = Fluid2DPresets.Create((Fluid2DModel)model.SelectedIndex, settings.Width, settings.Height) }));
        };
        NodeInspector.Children.Add(model);
        NodeInspector.Children.Add(new TextBlock { Text = text.Get("Fluid.ModelHint"), TextWrapping = TextWrapping.Wrap });
        var plane = new CheckBox
        {
            Name = "FluidAutomaticPlane", Content = text.Get("Fluid.AutomaticPlane"), IsThreeState = true,
            IsChecked = vm.State.FluidAutomaticPlane, IsEnabled = !vm.Graph.IsReadOnly
        };
        plane.Click += (_, _) => Run(() => vm.Edit(new() { Action = "fluid-output", AutomaticPlane = plane.IsChecked }));
        NodeInspector.Children.Add(plane);
        NodeInspector.Children.Add(new TextBlock { Text = text.Get("Fluid.AutomaticPlaneHint"), TextWrapping = TextWrapping.Wrap });
        Add("Width", settings.Width, (s, v) => s with { Width = Integer(v) });
        Add("Height", settings.Height, (s, v) => s with { Height = Integer(v) });
        Add("DomainWidth", settings.DomainWidth, (s, v) => s with { DomainWidth = (float)v });
        Add("DomainHeight", settings.DomainHeight, (s, v) => s with { DomainHeight = (float)v });
        if (settings.Model != Fluid2DModel.ShallowWater)
            Add("PressureIterations", settings.PressureIterations, (s, v) => s with { PressureIterations = Integer(v) });
        Add("MaximumStep", settings.MaximumStep, (s, v) => s with { MaximumStep = (float)v });
        Add("MaximumSubsteps", settings.MaximumSubsteps, (s, v) => s with { MaximumSubsteps = Integer(v) });
        Add("VelocityDissipation", settings.VelocityDissipation, (s, v) => s with { VelocityDissipation = (float)v });
        if (settings.Model is Fluid2DModel.Smoke or Fluid2DModel.Combustion)
            Add("DensityDissipation", settings.DensityDissipation, (s, v) => s with { DensityDissipation = (float)v });
        if (settings.Model is Fluid2DModel.FreeSurface or Fluid2DModel.ShallowWater)
        {
            Add("Gravity", settings.Gravity ?? 9.81f, (s, v) => s with { Gravity = (float)v });
            Add("InitialWaterDepth", settings.InitialWaterDepth ?? .25f, (s, v) => s with { InitialWaterDepth = (float)v });
            Add("MaximumWaterSpeed", settings.MaximumWaterSpeed ?? 4, (s, v) => s with { MaximumWaterSpeed = (float)v });
        }
        if (settings.Model == Fluid2DModel.Combustion)
        {
            Add("FuelBurnRate", settings.FuelBurnRate ?? 4, (s, v) => s with { FuelBurnRate = (float)v });
            Add("IgnitionTemperature", settings.IgnitionTemperature ?? .5f, (s, v) => s with { IgnitionTemperature = (float)v });
            Add("HeatYield", settings.HeatYield ?? 2, (s, v) => s with { HeatYield = (float)v });
            Add("CoolingRate", settings.CoolingRate ?? 1, (s, v) => s with { CoolingRate = (float)v });
            Add("SourceTemperature", settings.SourceTemperature ?? 1, (s, v) => s with { SourceTemperature = (float)v });
        }
        var source = new CheckBox
        {
            Name = "FluidSourceEnabled", Content = text.Get("Fluid.SourceEnabled"), IsChecked = settings.SourceEnabled,
            IsEnabled = !vm.Graph.IsReadOnly
        };
        source.Click += (_, _) => Run(() => vm.Edit(new()
        {
            Action = "fluid-settings", Fluid2D = vm.State.Fluid2D! with { SourceEnabled = source.IsChecked == true }
        }));
        NodeInspector.Children.Add(source);
        Add("SourceX", settings.SourceX, (s, v) => s with { SourceX = (float)v });
        Add("SourceY", settings.SourceY, (s, v) => s with { SourceY = (float)v });
        Add("SourceRadius", settings.SourceRadius, (s, v) => s with { SourceRadius = (float)v });
        Add("SourceDensity", settings.SourceDensity, (s, v) => s with { SourceDensity = (float)v });
        Add("SourceVelocityX", settings.SourceVelocityX, (s, v) => s with { SourceVelocityX = (float)v });
        Add("SourceVelocityY", settings.SourceVelocityY, (s, v) => s with { SourceVelocityY = (float)v });
        if (settings.Model is Fluid2DModel.Smoke or Fluid2DModel.Combustion)
            Add("Buoyancy", settings.Buoyancy, (s, v) => s with { Buoyancy = (float)v });
        Add("Vorticity", settings.Vorticity, (s, v) => s with { Vorticity = (float)v });
        Add("ColorR", settings.ColorR, (s, v) => s with { ColorR = (float)v });
        Add("ColorG", settings.ColorG, (s, v) => s with { ColorG = (float)v });
        Add("ColorB", settings.ColorB, (s, v) => s with { ColorB = (float)v });
        Add("ColorA", settings.ColorA, (s, v) => s with { ColorA = (float)v });
        AddNumber("Scale", (vm.State.FluidRenderer ?? new("builtin:fluid-alpha", 100)).SizeScale,
            value => vm.Edit(new() { Action = "fluid-renderer", SizeScale = value }));
        NodeInspector.Children.Add(new TextBlock
        {
            Text = text.Get("Fluid.Units"), TextWrapping = TextWrapping.Wrap
        });
        BuildFluidForces(vm);
        BuildFluidDrawConsumers(vm);

        void Add(string id, double value, Func<Fluid2DSettings, double, Fluid2DSettings> update) =>
            AddNumber(id, value, number => vm.Edit(new()
            {
                Action = "fluid-settings", Fluid2D = update(vm.State.Fluid2D!, number)
            }), compactFloat: true);

        void AddNumber(string id, double value, Action<double> update, bool compactFloat = false)
        {
            NodeInspector.Children.Add(new TextBlock { Text = text.Get("Fluid." + id) });
            var input = new TextBox
            {
                Name = "Fluid" + id, Text = compactFloat ? ((float)value).ToString("R", CultureInfo.InvariantCulture)
                    : value.ToString("R", CultureInfo.InvariantCulture),
                IsEnabled = !vm.Graph.IsReadOnly
            };
            var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 4 };
            row.Children.Add(input);
            var apply = Button(text.Get("Fluid.Apply"), () => update(double.Parse(input.Text ?? "",
                NumberStyles.Float, CultureInfo.InvariantCulture)));
            apply.Name = "ApplyFluid" + id;
            apply.IsEnabled = !vm.Graph.IsReadOnly;
            Grid.SetColumn(apply, 1);
            row.Children.Add(apply);
            NodeInspector.Children.Add(row);
        }
    }

    private void BuildFluidForces(GpuWorkspaceViewModel vm)
    {
        var text = LocalizationService.Shared;
        NodeInspector.Children.Add(new TextBlock { Text = text.Get("Fluid.Forces"), FontWeight = FontWeight.SemiBold });
        NodeInspector.Children.Add(new TextBlock { Text = text.Get("Fluid.ForcesHint"), TextWrapping = TextWrapping.Wrap });
        foreach (var kind in Enum.GetValues<Fluid2DForceKind>())
        {
            var add = Button(text.Get("Fluid.AddForce") + " " + text.Get("Fluid.ForceKind." + kind), () => vm.Edit(new()
            {
                Action = "fluid-force-add", FluidForce = new() { Id = Guid.NewGuid(), Kind = kind, Y = kind is Fluid2DForceKind.Acceleration or Fluid2DForceKind.Smoothstep ? 1 : 0 }
            }));
            add.IsEnabled = !vm.Graph.IsReadOnly && (vm.State.Fluid2D!.Forces?.Count ?? 0) < Fluid2DForceCompiler.MaximumNodes;
            NodeInspector.Children.Add(add);
        }
        foreach (var node in vm.State.Fluid2D!.Forces ?? [])
        {
            var panel = new StackPanel { Spacing = 4 };
            var ordered = vm.State.Fluid2D!.Forces!.ToList();
            var index = ordered.FindIndex(item => item.Id == node.Id);
            var earlier = ordered.Take(index).ToArray();
            panel.Children.Add(new TextBlock { Text = $"{index + 1}. {text.Get("Fluid.ForceKind." + node.Kind)}", FontWeight = FontWeight.SemiBold });
            ComboBox? inputA = null, inputB = null;
            if (node.Kind is Fluid2DForceKind.Noise or Fluid2DForceKind.CurlNoise or Fluid2DForceKind.Add or Fluid2DForceKind.Multiply or Fluid2DForceKind.Sine or Fluid2DForceKind.Smoothstep)
            {
                inputA = Input("InputA", node.InputA);
                if (node.Kind is not (Fluid2DForceKind.Sine or Fluid2DForceKind.Smoothstep)) inputB = Input("InputB", node.InputB);
            }
            var inputs = new Dictionary<string, TextBox>();
            foreach (var (id, value) in new[] { ("X", (double)node.X), ("Y", node.Y), ("Strength", node.Strength),
                ("Frequency", node.Frequency), ("Seed", node.Seed), ("Octaves", node.Octaves),
                ("Roughness", node.Roughness), ("AnimationSpeed", node.AnimationSpeed) })
            {
                if (node.Kind is Fluid2DForceKind.CellPosition or Fluid2DForceKind.Time or Fluid2DForceKind.Density or Fluid2DForceKind.Temperature) continue;
                if (node.Kind is not (Fluid2DForceKind.Noise or Fluid2DForceKind.CurlNoise) && id is not ("X" or "Y" or "Strength")) continue;
                if (node.Kind is not (Fluid2DForceKind.Acceleration or Fluid2DForceKind.Smoothstep) && id is "X" or "Y") continue;
                panel.Children.Add(new TextBlock { Text = text.Get("Fluid.Force." + id) });
                var input = new TextBox { Text = value.ToString("R", CultureInfo.InvariantCulture), IsEnabled = !vm.Graph.IsReadOnly };
                inputs[id] = input; panel.Children.Add(input);
            }
            var apply = Button(text.Get("Fluid.Apply"), () => vm.Edit(new()
            {
                Action = "fluid-force-update", FluidForce = node with
                {
                    InputA = Reference(inputA, node.InputA), InputB = Reference(inputB, node.InputB),
                    X = Value("X", node.X), Y = Value("Y", node.Y), Strength = Value("Strength", node.Strength),
                    Frequency = Value("Frequency", node.Frequency), Seed = Integer(Value("Seed", node.Seed)),
                    Octaves = Integer(Value("Octaves", node.Octaves)), Roughness = Value("Roughness", node.Roughness),
                    AnimationSpeed = Value("AnimationSpeed", node.AnimationSpeed)
                }
            }));
            apply.IsEnabled = !vm.Graph.IsReadOnly; panel.Children.Add(apply);
            var remove = Button(text.Get("Fluid.RemoveForce"), () => vm.Edit(new() { Action = "fluid-force-remove", NodeId = node.Id.ToString("D") }));
            remove.IsEnabled = !vm.Graph.IsReadOnly; panel.Children.Add(remove);
            NodeInspector.Children.Add(FluidCard(panel));

            ComboBox Input(string label, Guid? reference)
            {
                panel.Children.Add(new TextBlock { Text = text.Get("Fluid.Force." + label) });
                var selector = new ComboBox
                {
                    ItemsSource = new[] { text.Get("Fluid.Force.DefaultInput") }.Concat(earlier.Select((item, i) => $"{i + 1}. {text.Get("Fluid.ForceKind." + item.Kind)}")).ToArray(),
                    SelectedIndex = reference is { } id ? 1 + Array.FindIndex(earlier, item => item.Id == id) : 0,
                    IsEnabled = !vm.Graph.IsReadOnly
                };
                panel.Children.Add(selector);
                return selector;
            }
            Guid? Reference(ComboBox? selector, Guid? fallback) => selector is null ? fallback
                : selector.SelectedIndex > 0 ? earlier[selector.SelectedIndex - 1].Id : null;

            float Value(string id, float fallback) => inputs.TryGetValue(id, out var input)
                ? float.Parse(input.Text ?? "", NumberStyles.Float, CultureInfo.InvariantCulture) : fallback;
        }
    }

    private void BuildFluidDrawConsumers(GpuWorkspaceViewModel vm)
    {
        var text = LocalizationService.Shared;
        BindingsPanel.Children.Add(new TextBlock { Text = text.Get("Fluid.DrawConsumers"), FontWeight = FontWeight.SemiBold });
        BindingsPanel.Children.Add(new TextBlock { Text = text.Get("Fluid.DrawConsumersHint"), TextWrapping = TextWrapping.Wrap });
        var targets = GpuProgramDeliveryService.DrawTextureTargets(vm.Graph.CreateCurrentSnapshot());
        if (targets.Count != 0)
        {
            var target = new ComboBox { ItemsSource = targets.Select(item => item.NodeName + " · " +
                (item.ParameterId == "assetPath" ? text.Get("Fluid.MainTexture") : item.ParameterName)).ToArray(), SelectedIndex = 0 };
            var outputs = targets[0].ParameterId == "assetPath" ? new[] { "ColorOutput" } : new[] { "ColorOutput", "DyeOutput", "FieldOutput" };
            var output = new ComboBox { ItemsSource = outputs, SelectedIndex = 0 };
            target.SelectionChanged += (_, _) =>
            {
                if (target.SelectedIndex < 0) return;
                outputs = targets[target.SelectedIndex].ParameterId == "assetPath" ? new[] { "ColorOutput" } : new[] { "ColorOutput", "DyeOutput", "FieldOutput" };
                output.ItemsSource = outputs; output.SelectedIndex = 0;
            };
            target.IsEnabled = output.IsEnabled = !vm.Graph.IsReadOnly;
            BindingsPanel.Children.Add(target); BindingsPanel.Children.Add(output);
            var bind = Button(text.Get("Fluid.BindDrawConsumer"), () => vm.Edit(new()
            {
                Action = "fluid-consumer-bind", GeneratedConsumer = new(targets[target.SelectedIndex].NodeId,
                    targets[target.SelectedIndex].ParameterId, outputs[output.SelectedIndex])
            }));
            bind.IsEnabled = !vm.Graph.IsReadOnly; BindingsPanel.Children.Add(bind);
        }
        foreach (var consumer in vm.State.GeneratedConsumers ?? [])
        {
            var target = targets.FirstOrDefault(item => item.NodeId == consumer.TargetNodeId && item.ParameterId == consumer.ParameterId);
            BindingsPanel.Children.Add(new TextBlock { Text = (target?.NodeName ?? consumer.TargetNodeId.ToString("D")) + " · " + consumer.ParameterId + " ← " + consumer.OutputId,
                TextWrapping = TextWrapping.Wrap });
            var remove = Button(text.Get("Fluid.RemoveDrawConsumer"), () => vm.Edit(new() { Action = "fluid-consumer-remove", GeneratedConsumer = consumer }));
            remove.IsEnabled = !vm.Graph.IsReadOnly; BindingsPanel.Children.Add(remove);
        }
    }

    private static uint Integer(double value)
    {
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value < 0 || value > uint.MaxValue)
        {
            throw new ArgumentException(LocalizationService.Shared.Get("Fluid.IntegerRequired"));
        }
        return (uint)value;
    }
}
