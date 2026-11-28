using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LibraGpu.Core;
using LiberaFx.Services.Compilation;
using LiberaFx.Services.Settings;

namespace LiberaFx.Views.Gpu;

public partial class GpuWorkspaceView
{
    private Control BuildResourcePlanForm(GpuResourcePlan initial)
    {
        var vm = Workspace!;
        var text = LocalizationService.Shared;
        var host = new StackPanel { Spacing = 8, Name = "ResourcePlanForm" };
        var draft = initial;
        Func<GpuResourcePlan> read = () => draft;
        Rebuild();
        return host;

        string Label(string key) => text.Get("Fluid.Plan." + key);
        void Change(Action change)
        {
            draft = read();
            change();
            Rebuild();
        }
        Button ActionButton(string key, string name, Action action)
        {
            var button = Button(Label(key), action);
            button.Name = name;
            button.IsEnabled = !vm.Graph.IsReadOnly;
            return button;
        }
        Expander Entry(string title, Func<Control> build)
        {
            var entry = new Expander { Header = title, HorizontalAlignment = HorizontalAlignment.Stretch };
            entry.PropertyChanged += (_, change) =>
            {
                if (change.Property == Expander.IsExpandedProperty && entry.IsExpanded && entry.Content is null)
                    entry.Content = build();
            };
            return entry;
        }
        void Rebuild()
        {
            host.Children.Clear();
            var resources = new List<Func<GpuFlowResource>>();
            var passes = new List<Func<GpuResourcePass>>();
            host.Children.Add(new TextBlock { Text = Label("Resources"), FontWeight = FontWeight.SemiBold });
            foreach (var resource in draft.Resources)
            {
                var index = resources.Count;
                resources.Add(() => resource);
                host.Children.Add(Entry(resource.Id, () =>
                {
                var panel = new StackPanel { Spacing = 4 };
                var prefix = "PlanResource" + index;
                var id = StringField(panel, "Id", resource.Id, prefix + "Id");
                var kind = Choice(panel, "Kind", resource.Kind, prefix + "Kind");
                var format = Choice(panel, "Format", resource.Format, prefix + "Format");
                var width = Number(panel, "Width", resource.Width, prefix + "Width", 1);
                var height = Number(panel, "Height", resource.Height, prefix + "Height", 1);
                var depth = Number(panel, "Depth", resource.Depth, prefix + "Depth", 1);
                var lifetime = Choice(panel, "Lifetime", resource.Lifetime, prefix + "Lifetime");
                var initialization = Choice(panel, "Initialization", resource.Initialization, prefix + "Initialization");
                var external = StringField(panel, "ExternalBinding", resource.ExternalBinding ?? "", prefix + "ExternalBinding");
                resources[index] = () => resource with
                {
                    Id = id.Text ?? "", Kind = Selected<GpuFlowResourceKind>(kind), Format = Selected<GpuResourceFormat>(format),
                    Width = Value(width), Height = Value(height), Depth = Value(depth),
                    Lifetime = Selected<GpuResourceLifetime>(lifetime), Initialization = Selected<GpuResourceInitialization>(initialization),
                    ExternalBinding = string.IsNullOrWhiteSpace(external.Text) ? null : external.Text
                };
                panel.Children.Add(ActionButton("Remove", prefix + "Remove", () => Change(() => draft.Resources.RemoveAt(index))));
                return panel;
                }));
            }
            host.Children.Add(ActionButton("AddResource", "AddPlanResource", () => Change(() =>
            {
                var id = Unique("resource", draft.Resources.Select(r => r.Id));
                draft.Resources.Add(new() { Id = id, Width = 128, Initialization = GpuResourceInitialization.Zero });
            })));
            host.Children.Add(new TextBlock { Text = Label("Passes"), FontWeight = FontWeight.SemiBold });
            foreach (var pass in draft.Passes)
            {
                var index = passes.Count;
                passes.Add(() => pass);
                host.Children.Add(Entry(pass.Id, () =>
                {
                var panel = new StackPanel { Spacing = 4 };
                var prefix = "PlanPass" + index;
                var id = StringField(panel, "Id", pass.Id, prefix + "Id");
                var operation = StringField(panel, "Operation", pass.Operation, prefix + "Operation");
                var after = new List<(string Id, CheckBox Check)>();
                panel.Children.Add(new TextBlock { Text = Label("After") });
                foreach (var dependency in draft.Passes.Where(p => p.Id != pass.Id))
                {
                    var check = new CheckBox { Content = dependency.Id, IsChecked = pass.After.Contains(dependency.Id), IsEnabled = !vm.Graph.IsReadOnly };
                    panel.Children.Add(check);
                    after.Add((dependency.Id, check));
                }
                var uses = new List<Func<GpuResourceUse>>();
                foreach (var use in pass.Uses)
                {
                    var row = new StackPanel { Spacing = 4 };
                    var usePrefix = prefix + "Use" + uses.Count;
                    var binding = StringField(row, "Binding", use.Binding, usePrefix + "Binding");
                    row.Children.Add(new TextBlock { Text = Label("Resource") });
                    var names = draft.Resources.Select(r => r.Id).Append(use.Resource).Distinct().ToArray();
                    var target = new ComboBox { Name = usePrefix + "Resource", ItemsSource = names, SelectedItem = use.Resource, IsEnabled = !vm.Graph.IsReadOnly };
                    row.Children.Add(target);
                    var version = Number(row, "Version", use.Version, usePrefix + "Version", 0);
                    var access = Choice(row, "Access", use.Access, usePrefix + "Access");
                    var frame = Choice(row, "Frame", use.Frame, usePrefix + "Frame");
                    uses.Add(() => new(binding.Text ?? "", (string?)target.SelectedItem ?? "", Value(version), Selected<GpuResourceAccess>(access), Selected<GpuResourceFrame>(frame)));
                    var useIndex = uses.Count - 1;
                    row.Children.Add(ActionButton("Remove", usePrefix + "Remove", () => Change(() => draft.Passes[index].Uses.RemoveAt(useIndex))));
                    panel.Children.Add(new Expander { Header = use.Binding + " → " + use.Resource, Content = row });
                }
                // Keep missing dependencies so applying a form never silently repairs a broken contract.
                var missing = pass.After.Except(draft.Passes.Select(p => p.Id)).ToArray();
                passes[index] = () => pass with { Id = id.Text ?? "", Operation = operation.Text ?? "",
                    After = after.Where(a => a.Check.IsChecked == true).Select(a => a.Id).Concat(missing).ToList(),
                    Uses = uses.Select(get => get()).ToList() };
                panel.Children.Add(ActionButton("AddUse", prefix + "AddUse", () => Change(() => draft.Passes[index].Uses.Add(
                    new(Unique("binding", draft.Passes[index].Uses.Select(u => u.Binding)), draft.Resources.FirstOrDefault()?.Id ?? "", 0, GpuResourceAccess.StorageRead)))));
                panel.Children.Add(ActionButton("Remove", prefix + "Remove", () => Change(() => draft.Passes.RemoveAt(index))));
                return panel;
                }));
            }
            host.Children.Add(ActionButton("AddPass", "AddPlanPass", () => Change(() => draft.Passes.Add(new()
                { Id = Unique("pass", draft.Passes.Select(p => p.Id)), Operation = "CustomCompute" }))));
            read = () => draft with { Resources = resources.Select(get => get()).ToList(), Passes = passes.Select(get => get()).ToList() };
            host.Children.Add(ActionButton("Apply", "ApplyResourcePlanForm", () => vm.Edit(new()
                { Action = "resource-plan", Text = GpuResourcePlanCompiler.Serialize(read()) })));
        }

        TextBox StringField(StackPanel panel, string key, string value, string name)
        {
            panel.Children.Add(new TextBlock { Text = Label(key) });
            var input = new TextBox { Name = name, Text = value, IsReadOnly = vm.Graph.IsReadOnly };
            panel.Children.Add(input);
            return input;
        }
        NumericUpDown Number(StackPanel panel, string key, uint value, string name, uint minimum)
        {
            panel.Children.Add(new TextBlock { Text = Label(key) });
            var input = new NumericUpDown { Name = name, Value = value, Minimum = minimum, Maximum = uint.MaxValue,
                Increment = 1, FormatString = "0", IsEnabled = !vm.Graph.IsReadOnly };
            panel.Children.Add(input);
            return input;
        }
        ComboBox Choice<T>(StackPanel panel, string key, T value, string name) where T : struct, Enum
        {
            panel.Children.Add(new TextBlock { Text = Label(key) });
            var values = Enum.GetValues<T>();
            var input = new ComboBox { Name = name, ItemsSource = values.Select(v => Label(v.ToString())).ToArray(),
                SelectedIndex = Array.IndexOf(values, value), IsEnabled = !vm.Graph.IsReadOnly };
            panel.Children.Add(input);
            return input;
        }
    }

    private static uint Value(NumericUpDown input) => input.Value is { } value && decimal.Truncate(value) == value
        ? checked((uint)value) : throw new ArgumentException("Enter a whole number.");
    private static T Selected<T>(ComboBox input) where T : struct, Enum => Enum.GetValues<T>()[input.SelectedIndex];
    private static string Unique(string prefix, IEnumerable<string> existing)
    {
        var names = existing.ToHashSet(StringComparer.Ordinal);
        for (var i = 1; ; i++) if (!names.Contains(prefix + i)) return prefix + i;
    }
}
