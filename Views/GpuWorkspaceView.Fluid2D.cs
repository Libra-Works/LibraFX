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
            Text = text.Get("Fluid.Title"), FontSize = 20, FontWeight = FontWeight.SemiBold
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
        Add("Width", settings.Width, (s, v) => s with { Width = Integer(v) });
        Add("Height", settings.Height, (s, v) => s with { Height = Integer(v) });
        Add("DomainWidth", settings.DomainWidth, (s, v) => s with { DomainWidth = (float)v });
        Add("DomainHeight", settings.DomainHeight, (s, v) => s with { DomainHeight = (float)v });
        Add("PressureIterations", settings.PressureIterations, (s, v) => s with { PressureIterations = Integer(v) });
        Add("MaximumStep", settings.MaximumStep, (s, v) => s with { MaximumStep = (float)v });
        Add("MaximumSubsteps", settings.MaximumSubsteps, (s, v) => s with { MaximumSubsteps = Integer(v) });
        Add("VelocityDissipation", settings.VelocityDissipation, (s, v) => s with { VelocityDissipation = (float)v });
        Add("DensityDissipation", settings.DensityDissipation, (s, v) => s with { DensityDissipation = (float)v });
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
        Add("Buoyancy", settings.Buoyancy, (s, v) => s with { Buoyancy = (float)v });
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

    private static uint Integer(double value)
    {
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value < 0 || value > uint.MaxValue)
        {
            throw new ArgumentException(LocalizationService.Shared.Get("Fluid.IntegerRequired"));
        }
        return (uint)value;
    }
}
