using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LibraGpu.Core;
using LiberaFx.Services;

namespace LiberaFx.Views;

public partial class GpuWorkspaceView
{
    private Expander BuildResourcePlanEditor()
    {
        var vm = Workspace!;
        var text = LocalizationService.Shared;
        var plan = GpuParticleEditorService.ResourcePlan(vm.State);
        var compiled = GpuResourcePlanCompiler.Compile(plan);
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = text.Get("Fluid.ResourcePlanHint"), TextWrapping = TextWrapping.Wrap
        });
        var source = new TextBox
        {
            Name = "ResourcePlanSource", Text = GpuResourcePlanCompiler.Serialize(plan),
            AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap,
            Height = 300, MaxLength = 4 * 1024 * 1024,
            FontFamily = new FontFamily("Consolas"), IsReadOnly = vm.Graph.IsReadOnly
        };
        panel.Children.Add(source);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var apply = Button(text.Get("Fluid.ResourcePlanApply"), () =>
            vm.Edit(new() { Action = "resource-plan", Text = source.Text }));
        apply.Name = "ApplyResourcePlan";
        apply.IsEnabled = !vm.Graph.IsReadOnly;
        actions.Children.Add(apply);
        var reset = Button(text.Get("Fluid.ResourcePlanReset"), () =>
            vm.Edit(new() { Action = "resource-plan-reset" }));
        reset.Name = "ResetResourcePlan";
        reset.IsEnabled = !vm.Graph.IsReadOnly && vm.State.ResourcePlanDraft is not null;
        actions.Children.Add(reset);
        panel.Children.Add(actions);
        panel.Children.Add(new TextBlock
        {
            Text = text.Get("Fluid.ResourcePlanSummary", compiled.Allocations.Count,
                compiled.Passes.Count, compiled.OwnedBytes, compiled.Transitions.Count),
            TextWrapping = TextWrapping.Wrap
        });
        foreach (var allocation in compiled.Allocations.Take(32))
        {
            var resource = allocation.Resource;
            panel.Children.Add(new TextBlock
            {
                Text = $"{resource.Id} · {resource.Kind} · {resource.Format} · " +
                    $"{resource.Width} × {resource.Height} × {resource.Depth} · " +
                    $"{resource.Lifetime} · {resource.Initialization} · {resource.ExternalBinding}",
                TextWrapping = TextWrapping.Wrap
            });
        }
        foreach (var pass in compiled.Passes.Take(32))
        {
            panel.Children.Add(new TextBlock
            {
                Text = pass.Id + " · " + pass.Operation + "\n" + string.Join("\n", pass.Uses.Select(use =>
                    $"{use.Binding}: {use.Resource}@{use.Version} · {use.Access} · {use.Frame}")),
                TextWrapping = TextWrapping.Wrap
            });
        }
        if (compiled.Allocations.Count > 32 || compiled.Passes.Count > 32)
            panel.Children.Add(new TextBlock { Text = text.Get("Fluid.ResourcePlanPreviewLimit"), TextWrapping = TextWrapping.Wrap });
        return new Expander
        {
            Name = "ResourcePlanEditor", Header = text.Get("Fluid.ResourcePlanTitle"),
            Content = panel, IsExpanded = vm.State.ResourcePlanDraft is not null,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }
}
