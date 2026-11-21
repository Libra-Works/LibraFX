using System.ComponentModel;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

/// <summary>One ordered Flow editor for all procedural producers. Every edit uses document history.</summary>
public sealed partial class ProceduralWorkspaceView : UserControl
{
    private GraphWorkspaceViewModel _workspace = null!;
    private Guid _nodeId;
    private GraphNodeViewModel _node => _workspace.Nodes.Single(n => n.Id == _nodeId);
    private GraphNodeViewModel? _observedNode;
    private bool _queued;
    private readonly StackPanel _flow = new() { Name = "FlowPanel", Spacing = 6, Margin = new Thickness(40, 20), HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 300 };
    private readonly TextBlock _status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private bool _refreshing;
    private string T(string key) => LocalizationService.Shared.Get("Procedural." + key);
    private ProceduralRecipe Recipe => ProceduralResourceSchema.ReadEffective(_node.BaseDefinition, _node.Parameters);
    private bool Model => ProceduralResourceSchema.Name(_node.Definition.Kind) == "ProceduralModel";

    private readonly StackPanel _palette = new() { Name = "Palette", Spacing = 4, Margin = new Thickness(8) };
    private readonly StackPanel _inspector = new() { Name = "NodeInspector", Spacing = 6, Margin = new Thickness(12) };
    private readonly TextBlock _breadcrumb = new() { FontSize = 17, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8) };
    private int _selected = -1;
    private Guid? _selectedStage;
    public string Title => _breadcrumb.Text ?? "";
    public GraphWorkspaceViewModel? Workspace => IsVisible ? _workspace : null;
    public bool UseDocumentNavigation { get; set; }

    public ProceduralWorkspaceView()
    {
        IsVisible = false;
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        _toolbar.Children.Add(_breadcrumb);
        var header = new Border { Child = _toolbar, BorderThickness = new Thickness(0, 0, 0, 1) };
        header.Classes.Add("editor-bar"); grid.Children.Add(header);
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("190,*,280") };
        Grid.SetRow(body, 1);
        var palette = new Border { Child = new ScrollViewer { Content = _palette }, BorderThickness = new Thickness(0, 0, 1, 0) };
        palette.Bind(Border.BorderBrushProperty, palette.GetResourceObservable("EffectBorderBrush"));
        body.Children.Add(palette);
        var flow = new ScrollViewer { Content = _flow };
        Grid.SetColumn(flow, 1); body.Children.Add(flow);
        var inspectorBody = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        inspectorBody.Children.Add(CreateTexturePreview());
        var inspectorScroll = new ScrollViewer { Content = _inspector };
        Grid.SetRow(inspectorScroll, 1); inspectorBody.Children.Add(inspectorScroll);
        var inspector = new Border { Child = inspectorBody, BorderThickness = new Thickness(1, 0, 0, 0) };
        inspector.Bind(Border.BorderBrushProperty, inspector.GetResourceObservable("EffectBorderBrush"));
        Grid.SetColumn(inspector, 2); body.Children.Add(inspector);
        grid.Children.Add(body);
        _status.Margin = new Thickness(10, 5);
        Grid.SetRow(_status, 2); grid.Children.Add(_status);
        Content = grid;
        this.Bind(BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("EffectWindowBackgroundBrush"));
        DetachedFromVisualTree += (_, _) => CloseWorkspace();
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) { CloseWorkspace(); e.Handled = true; }
            if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) && e.Key == Avalonia.Input.Key.Z)
            { _workspace.History.Undo(); e.Handled = true; }
            if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) && e.Key == Avalonia.Input.Key.Y)
            { _workspace.History.Redo(); e.Handled = true; }
        };
    }
    public void Open(GraphWorkspaceViewModel workspace, Guid nodeId)
    {
        CloseWorkspace();
        if (!workspace.Nodes.Any(n => n.Id == nodeId && ProceduralResourceSchema.IsMetadata(n.Definition.Kind))) return;
        _workspace = workspace; _nodeId = nodeId; _selected = -1; _selectedStage = null;
        IsVisible = true;
        LocalizationService.Shared.LanguageChanged += LanguageChanged;
        _workspace.Nodes.CollectionChanged += NodesChanged;
        Refresh(); Focus();
    }
    public void CloseWorkspace()
    {
        ClearTexturePreview();
        LocalizationService.Shared.LanguageChanged -= LanguageChanged;
        if (_workspace is not null) _workspace.Nodes.CollectionChanged -= NodesChanged;
        if (_observedNode is not null) _observedNode.PropertyChanged -= NodeChanged;
        _observedNode = null; IsVisible = false;
    }
    private void Card(string text, int index)
    {
        if (_flow.Children.Count > 0) _flow.Children.Add(new TextBlock { Text = "↓", HorizontalAlignment = HorizontalAlignment.Center });
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 64,
            Padding = new Thickness(16), BorderThickness = new Thickness(_selected == index ? 2 : 1) };
        button.Bind(BackgroundProperty, button.GetResourceObservable("EffectSurfaceBrush"));
        button.Bind(BorderBrushProperty, button.GetResourceObservable(_selected == index ? "EffectSelectionBorderBrush" : "EffectBorderBrush"));
        button.Click += (_, _) => { _selected = index; _selectedStage = null; Refresh(); };
        _flow.Children.Add(button);
    }
    private void LanguageChanged(object? sender, EventArgs e) => Refresh();
    private void NodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            if (!IsVisible) return;
            if (!_workspace.Nodes.Any(n => n.Id == _nodeId)) { CloseWorkspace(); return; }
            Refresh();
        });
    }
    private void NodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_refreshing && e.PropertyName is nameof(GraphNodeViewModel.Parameters) or nameof(GraphNodeViewModel.Definition)) Refresh();
    }
    private void Save(ProceduralRecipe recipe)
    {
        try
        {
            _refreshing = true;
            _node.Workspace.ApplyProceduralRecipe(_node, recipe);
            _status.Text = "";
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException)
        {
            _status.Text = T("Invalid") + " " + error.Message;
        }
        finally { _refreshing = false; }
        Refresh();
    }
    private void Button(Panel parent, string key, Action action)
    {
        var button = new Button { Content = T(key), Padding = new Thickness(7, 4), Margin = new Thickness(2) };
        button.Click += (_, _) => action();
        parent.Children.Add(button);
    }
    private void Choice(Panel parent, string key, IEnumerable<string> names, int selected, Action<int> set)
    {
        var box = new ComboBox { ItemsSource = names.ToArray(), SelectedIndex = selected, MinWidth = 180 };
        box.SelectionChanged += (_, _) => { if (!_refreshing && box.SelectedIndex >= 0) set(box.SelectedIndex); };
        parent.Children.Add(new TextBlock { Text = T(key) });
        parent.Children.Add(box);
    }
    private void Number(Panel parent, string label, float value, Action<float> set)
    {
        var box = new TextBox { Text = value.ToString("G9", CultureInfo.InvariantCulture), MinWidth = 85 };
        box.LostFocus += (_, _) =>
        {
            if (_refreshing || !IsVisible) return;
            if (float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                float.IsFinite(number) && number != value)
            {
                try { set(number); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException)
                {
                    _status.Text = T("Invalid") + " " + error.Message;
                }
            }
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = label, MinWidth = 130, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(box);
        parent.Children.Add(row);
    }
    private void Refresh()
    {
        if (_refreshing || !IsVisible) return;
        _refreshing = true;
        try
        {
            if (!ReferenceEquals(_observedNode, _node))
            {
                if (_observedNode is not null) _observedNode.PropertyChanged -= NodeChanged;
                _observedNode = _node;
                _observedNode.PropertyChanged += NodeChanged;
            }
            var r = Recipe;
            var stageCount = Model ? r.Modifiers.Length : r.Operations.Length;
            if (_selectedStage is { } selectedId)
                _selected = Model ? Array.FindIndex(r.Modifiers, m => m.Id == selectedId) : Array.FindIndex(r.Operations, op => op.Id == selectedId);
            if (_selected >= stageCount) _selected = -1;
            _selectedStage = _selected < 0 ? null : Model ? r.Modifiers[_selected].Id : r.Operations[_selected].Id;
            _palette.IsEnabled = _inspector.IsEnabled = !_workspace.IsReadOnly;
            _breadcrumb.Text = T(ProceduralResourceSchema.Name(_node.Definition.Kind)!) + " / " + T("Flow");
            _flow.Children.Clear(); _palette.Children.Clear(); _inspector.Children.Clear();
            _toolbar.Children.Clear();
            if (!UseDocumentNavigation)
            {
                Button(_toolbar, "Back", CloseWorkspace);
                _toolbar.Children.Add(_breadcrumb);
            }
            Button(_toolbar, "Compile", () => { try { _ = LiberaFx.Services.Compilation.SpawnableGroupPackage.Compile(_workspace.CreateSnapshot()); _status.Text = T("Valid"); } catch (Exception error) { _status.Text = T("Invalid") + " " + error.Message; } });
            _palette.Children.Add(new TextBlock { Text = T("Palette"), FontSize = 17 });
            var choices = Model ? ProceduralResourceSchema.ModifierNames : ProceduralResourceSchema.PixelNames;
            for (var i = 0; i < choices.Length; ++i)
            {
                var kind = i;
                if (!Model && kind == 30 && ProceduralResourceSchema.Name(_node.Definition.Kind) != "ProceduralMaterial") continue;
                var add = new Button { Content = T((Model ? "Modifier." : "Pixel.") + choices[i]), HorizontalAlignment = HorizontalAlignment.Stretch };
                add.Click += (_, _) => { _selected = Model ? Recipe.Modifiers.Length : Recipe.Operations.Length; _selectedStage = null; Add(kind); };
                _palette.Children.Add(add);
            }
            Card(T(Model ? "Shape" : "Resolution"), -1);
            if (_selected == -1)
            {
            if (Model)
            {
                Choice(_inspector, "Shape", ProceduralResourceSchema.ShapeNames.Select(n => T("Shape." + n)), r.Shape, i => Save(Recipe with { Shape = i }));
                Number(_inspector, T("SegmentsU"), r.SegmentsU, v => Save(Recipe with { SegmentsU = Whole(v, 256) }));
                Number(_inspector, T("SegmentsV"), r.SegmentsV, v => Save(Recipe with { SegmentsV = Whole(v, 128) }));
                Vector(_inspector, "Size", r.Size, v => Save(Recipe with { Size = v }));
                Number(_inspector, T("InnerRadius"), r.InnerRadius, v => Save(Recipe with { InnerRadius = v }));
                Number(_inspector, T("Sweep"), r.SweepDegrees, v => Save(Recipe with { SweepDegrees = v }));
                Number(_inspector, T("Turns"), r.Turns, v => Save(Recipe with { Turns = v }));
                Number(_inspector, T("TubeRadius"), r.TubeRadius, v => Save(Recipe with { TubeRadius = v }));
                Toggle(_inspector, "Capped", r.Capped, b => Save(Recipe with { Capped = b }));
                Choice(_inspector, "Pivot", new[] { "Center", "Base", "Tip", "Origin", "Custom", "Start", "End", "Vertex" }.Select(n => T("Pivot." + n)),
                    (int)r.PivotMode, i => Save(Recipe with { PivotMode = (uint)i }));
                Vector(_inspector, "Pivot", r.Pivot, v => Save(Recipe with { Pivot = v }));
                if (r.PivotMode == 7) Number(_inspector, T("PivotVertex"), r.PivotVertex, v => Save(Recipe with { PivotVertex = Whole(v, 65535, true) }));
            }
            else
            {
                Choice(_inspector, "Resolution", new[] { "16", "32", "64", "128", "256", "512", "1024" },
                    (int)Math.Log2(r.Resolution) - 4, i => Save(Recipe with { Resolution = 1 << (i + 4) }));
                if (ProceduralResourceSchema.Name(_node.Definition.Kind) == "ProceduralMaterial")
                {
                    Choice(_inspector, "Blend", new[] { T("Alpha"), T("Additive") }, r.Blend == "alpha" ? 0 : 1,
                        i => Save(Recipe with { Blend = i == 0 ? "alpha" : "additive" }));
                    Number(_inspector, T("Emission"), r.Emission, v => Save(Recipe with { Emission = v }));
                    Toggle(_inspector, "DoubleSided", r.DoubleSided, b => Save(Recipe with { DoubleSided = b }));
                }
            }
            }
            var count = Model ? r.Modifiers.Length : r.Operations.Length;
            for (var index = 0; index < count; ++index)
            {
                var slot = index;
                var names = Model ? ProceduralResourceSchema.ModifierNames : ProceduralResourceSchema.PixelNames;
                var kind = Model ? r.Modifiers[index].Kind : (int)r.Operations[index].Kind;
                var title = $"{index + 1}. {T((Model ? "Modifier." : "Pixel.") + names[kind])}";
                Card(title, index);
                if (_selected != index) continue;
                var stage = _inspector;
                stage.Children.Add(new TextBlock { Text = title, FontSize = 17 });
                var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
                Button(buttons, "Up", () => Move(slot, -1));
                Button(buttons, "Down", () => Move(slot, 1));
                Button(buttons, "Duplicate", () => Duplicate(slot));
                Button(buttons, "Remove", () => Remove(slot));
                stage.Children.Add(buttons);
                if (Model)
                {
                    var m = r.Modifiers[index];
                    Toggle(stage, "Enabled", (m.Flags & 1) != 0, b => Modify(slot, x => x with { Flags = b ? x.Flags | 1u : x.Flags & ~1u }));
                    Toggle(stage, "Animated", (m.Flags & 16) != 0, b => Modify(slot, x => x with { Flags = b ? x.Flags | 16u : x.Flags & ~16u }));
                    if (m.Kind is <= 5 or 9 or 10) Choice(stage, "Axis", new[] { "X", "Y", "Z" }, m.Axis, a => Modify(slot, x => x with { Axis = a }));
                    if (m.Kind is 10 or 11) Number(stage, T("Count"), m.Count, v => Modify(slot, x => x with { Count = Whole(v, 64) }));
                    if (m.Kind == 5) Number(stage, T("Seed"), m.Seed, v => Modify(slot, x => x with { Seed = Whole(v, 1000000, true) }));
                    Toggle(stage, "CustomPivot", (m.Flags & 2) != 0, b => Modify(slot, x => x with { Flags = b ? x.Flags | 2u : x.Flags & ~2u }));
                    if (m.Kind == 9) Toggle(stage, "KeepOriginal", (m.Flags & 8) != 0, b => Modify(slot, x => x with { Flags = b ? x.Flags | 8u : x.Flags & ~8u }));
                    if (m.Kind == 10) Toggle(stage, "RadialArray", (m.Flags & 4) != 0, b => Modify(slot, x => x with { Flags = b ? x.Flags | 4u : x.Flags & ~4u }));
                }
                else
                {
                    var op = r.Operations[index];
                    var sources = new[] { T("Pixel.UV") }.Concat(r.Operations.Take(slot).Select((source, i) =>
                        $"{i + 1}. {T("Pixel." + ProceduralResourceSchema.PixelNames[source.Kind])}")).ToArray();
                    if (op.Kind is not (0 or 1 or 2)) Choice(stage, "InputA", sources, (int)op.InputA, v => Pixel(slot, x => x with { InputA = (uint)v }));
                    if (op.Kind is 16 or >= 17 and <= 22 or 29) Choice(stage, "InputB", sources, (int)op.InputB, v => Pixel(slot, x => x with { InputB = (uint)v }));
                    Number(stage, T("Seed"), op.Seed, v => Pixel(slot, x => x with { Seed = Whole(v, 1000000, true) }));
                }
                var data = Model ? r.Modifiers[index].Values : r.Operations[index].Values;
                for (var j = 0; j < data.Length; ++j)
                {
                    var parameter = Model ? ModelParameter(kind, j) : PixelParameter(kind, j);
                    if (parameter is null || Model && (j == 15 || j >= 12 && (r.Modifiers[index].Flags & 2) == 0)) continue;
                    var component = j;
                    Number(stage, parameter is null ? T("Value") + " " + j : T("Parameter." + parameter), data[j], v =>
                    {
                        if (Model) Modify(slot, x => { var a = (float[])x.Values.Clone(); a[component] = v; return x with { Values = a }; });
                        else Pixel(slot, x => { var a = (float[])x.Values.Clone(); a[component] = v; return x with { Values = a }; });
                    });
                }
            }
            Card(T("Result"), -2);
            if (_selected == -2)
            {
                _inspector.Children.Add(new TextBlock { Text = T("Result"), FontSize = 17 });
                if (!Model) Choice(_inspector, "Output", r.Operations.Select((op, i) => $"{i + 1}. {T("Pixel." + ProceduralResourceSchema.PixelNames[op.Kind])}"),
                    (int)r.OutputRegister - 1, v => Save(Recipe with { OutputRegister = (uint)v + 1 }));
                _inspector.Children.Add(new TextBlock { Text = T("Hint"), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            }
        }
        finally { _refreshing = false; }
        RefreshTexturePreview();
    }
    private void Toggle(Panel parent, string key, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = T(key), IsChecked = value };
        box.IsCheckedChanged += (_, _) => { if (!_refreshing) set(box.IsChecked == true); };
        parent.Children.Add(box);
    }
    private static string? ModelParameter(int kind, int index)
    {
        if (index is >= 12 and <= 14) return new[] { "CenterX", "CenterY", "CenterZ" }[index - 12];
        string[] names = kind switch
        {
            0 or 1 => ["Angle"], 2 => ["EndScale"], 3 => ["Amplitude", "Frequency", "Phase", "Speed"],
            4 => ["Angle", "Radius"], 5 => ["Amplitude", "Frequency", "Phase", "Speed", "Roughness"],
            6 => ["Blend", "Radius"], 7 => ["Distance"],
            8 => ["TranslateX", "TranslateY", "TranslateZ", "RotateX", "RotateY", "RotateZ", "ScaleX", "ScaleY", "ScaleZ"],
            10 => ["TranslateX", "TranslateY", "TranslateZ", "RotateX", "RotateY", "RotateZ", "ScaleX", "ScaleY", "ScaleZ", "Radius", "Sweep"],
            12 => ["Thickness", "Offset"], _ => []
        };
        return index < names.Length ? names[index] : null;
    }
    private static string? PixelParameter(int kind, int index)
    {
        string[] names = kind switch
        {
            1 => ["Red", "Green", "Blue", "Alpha"],
            3 => ["ScaleU", "ScaleV", "OffsetU", "OffsetV", "Angle", "CenterU", "CenterV"],
            4 => ["DirectionU", "DirectionV", "Offset"], 5 => ["CenterU", "CenterV", "Scale"],
            6 => ["CenterU", "CenterV", "Radius", "Width", "Softness"],
            7 => ["FrequencyU", "FrequencyV", "Phase"], 8 => ["RepeatU", "RepeatV"],
            13 or 14 => ["FrequencyU", "FrequencyV", "SpeedU", "SpeedV", "Octaves", "Roughness"],
            >= 9 and <= 15 => ["FrequencyU", "FrequencyV", "SpeedU", "SpeedV"],
            16 => ["Amplitude", "Center"], 22 => ["Blend"], 23 => ["Power"], 26 => ["Lower", "Upper"],
            27 => ["StartRed", "StartGreen", "StartBlue", "StartAlpha", "EndRed", "EndGreen", "EndBlue", "EndAlpha", "Lower", "Upper"],
            28 => ["Channel"], 30 => ["TextureSlot"], _ => []
        };
        return index < names.Length ? names[index] : null;
    }
    private void Vector(Panel parent, string key, float[] values, Action<float[]> set)
    {
        for (var i = 0; i < 3; ++i)
        {
            var axis = i;
            Number(parent, T(key) + " " + "XYZ"[i], values[i], v => { var next = (float[])values.Clone(); next[axis] = v; set(next); });
        }
    }
    private uint Whole(float v, uint maximum, bool zero = false) =>
        v == MathF.Floor(v) && v >= (zero ? 0 : 1) && v <= maximum ? (uint)v : throw new ArgumentException(T("IntegerRange"));
    private void Modify(int index, Func<ProceduralModifier, ProceduralModifier> edit)
    {
        var r = Recipe; var a = (ProceduralModifier[])r.Modifiers.Clone(); a[index] = edit(a[index]); Save(r with { Modifiers = a });
    }
    private void Pixel(int index, Func<ProceduralPixelOperation, ProceduralPixelOperation> edit)
    {
        var r = Recipe; var a = (ProceduralPixelOperation[])r.Operations.Clone(); a[index] = edit(a[index]); Save(r with { Operations = a });
    }
    private void Add(int kind)
    {
        var r = Recipe;
        if (Model)
        {
            var values = new float[16];
            if (kind is 8 or 10) values[6] = values[7] = values[8] = 1;
            if (kind == 2) values[0] = 1;
            if (kind is 3 or 5) { values[0] = .1f; values[1] = 2; values[3] = 1; values[4] = .5f; }
            if (kind == 6) { values[0] = .5f; values[1] = .5f; }
            if (kind == 12) values[0] = .05f;
            Save(r with { Modifiers = [.. r.Modifiers, new() { Kind = kind, Values = values, Flags = kind == 9 ? 9u : 1u }] });
        }
        else
        {
            Save(r with { Operations = [.. r.Operations, ProceduralResourceSchema.CreatePixel((uint)kind, (uint)r.Operations.Length)], OutputRegister = (uint)r.Operations.Length + 1 });
        }
    }
    private void Duplicate(int index)
    {
        var r = Recipe;
        if (Model)
        {
            var stages = r.Modifiers.ToList();
            stages.Insert(index + 1, stages[index] with { Id = Guid.NewGuid(), Values = (float[])stages[index].Values.Clone() });
            Save(r with { Modifiers = stages.ToArray() });
        }
        else
        {
            var stages = r.Operations.ToList();
            stages.Insert(index + 1, stages[index] with { Id = Guid.NewGuid(), Values = (float[])stages[index].Values.Clone() });
            RemapPixels(r, stages.ToArray());
        }
    }
    private void Move(int index, int delta)
    {
        var r = Recipe; var target = index + delta;
        if (target < 0 || target >= (Model ? r.Modifiers.Length : r.Operations.Length)) return;
        if (Model)
        {
            var a = (ProceduralModifier[])r.Modifiers.Clone(); (a[index], a[target]) = (a[target], a[index]); Save(r with { Modifiers = a });
        }
        else
        {
            var a = (ProceduralPixelOperation[])r.Operations.Clone(); (a[index], a[target]) = (a[target], a[index]); RemapPixels(r, a);
        }
    }
    private void Remove(int index)
    {
        var r = Recipe;
        if (Model) { _selected = Math.Min(index, r.Modifiers.Length - 2); Save(r with { Modifiers = r.Modifiers.Where((_, i) => i != index).ToArray() }); }
        else RemapPixels(r, r.Operations.Where((_, i) => i != index).ToArray());
    }
    private void RemapPixels(ProceduralRecipe old, ProceduralPixelOperation[] next)
    {
        uint Map(uint register)
        {
            if (register == 0) return 0;
            var index = Array.FindIndex(next, op => op.Id == old.Operations[register - 1].Id);
            return index < 0 ? 0 : (uint)index + 1;
        }
        var output = Map(old.OutputRegister);
        Save(old with { Operations = next.Select(op => op with { InputA = Map(op.InputA), InputB = Map(op.InputB) }).ToArray(),
            OutputRegister = output != 0 ? output : (uint)next.Length });
    }
}
