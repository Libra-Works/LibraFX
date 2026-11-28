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
        Focusable = true;
        ContextRequested += (_, e) => e.Handled = true;
        AddFlowCardStyles();
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
            if (e.Key == Avalonia.Input.Key.Delete && _selected >= 0 && e.Source is not TextBox)
            { Remove(_selected); e.Handled = true; }
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
    private readonly List<Button> _cards = [];
    private int _dragSourceIndex = -1;
    private int _dragTargetIndex = -1;
    private Point _dragStart;
    private bool _dragActive;

    private void AddFlowCardStyles()
    {
        Avalonia.Styling.Selector Build(Func<Avalonia.Styling.Selector, Avalonia.Styling.Selector> step) =>
            step(Avalonia.Styling.Selectors.Class(
                Avalonia.Styling.Selectors.Class(
                    Avalonia.Styling.Selectors.OfType<Button>(null), "flow-card"), "selected"));

        foreach (var selector in new[]
        {
            Build(s => Avalonia.Styling.Selectors.OfType<Avalonia.Controls.Presenters.ContentPresenter>(Avalonia.Styling.Selectors.Template(s))),
            Build(s => Avalonia.Styling.Selectors.OfType<Avalonia.Controls.Presenters.ContentPresenter>(Avalonia.Styling.Selectors.Template(Avalonia.Styling.Selectors.Class(s, ":pointerover")))),
            Build(s => Avalonia.Styling.Selectors.OfType<Avalonia.Controls.Presenters.ContentPresenter>(Avalonia.Styling.Selectors.Template(Avalonia.Styling.Selectors.Class(s, ":pressed")))),
            Build(s => Avalonia.Styling.Selectors.OfType<Avalonia.Controls.Presenters.ContentPresenter>(Avalonia.Styling.Selectors.Template(Avalonia.Styling.Selectors.Class(s, ":focus"))))
        })
        {
            var style = new Avalonia.Styling.Style(_ => selector);
            style.Setters.Add(new Avalonia.Styling.Setter(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("EffectSelectionBorderBrush")));
            style.Setters.Add(new Avalonia.Styling.Setter(Border.BorderThicknessProperty, new Thickness(2)));
            style.Setters.Add(new Avalonia.Styling.Setter(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("EffectSurfaceBrush")));
            Styles.Add(style);
        }
    }

    private void Card(string text, int index)
    {
        if (_flow.Children.Count > 0) _flow.Children.Add(new TextBlock { Text = "↓", HorizontalAlignment = HorizontalAlignment.Center });
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 64,
            Padding = new Thickness(16), BorderThickness = new Thickness(_selected == index ? 2 : 1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        button.Classes.Add("flow-card");
        if (_selected == index) button.Classes.Add("selected");
        button.Bind(BackgroundProperty, button.GetResourceObservable("EffectSurfaceBrush"));
        button.Bind(BorderBrushProperty, button.GetResourceObservable(_selected == index ? "EffectSelectionBorderBrush" : "EffectBorderBrush"));
        button.Click += (_, _) =>
        {
            _selected = index;
            _selectedStage = null;
            Refresh(restoreFocus: true);
        };
        button.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
            if (index >= 0)
            {
                _dragSourceIndex = index;
                _dragTargetIndex = index;
                _dragStart = e.GetPosition(_flow);
                _dragActive = false;
            }
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        button.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (index < 0 || _dragSourceIndex != index) return;
            var pos = e.GetPosition(_flow);
            if (!_dragActive && Math.Abs(pos.Y - _dragStart.Y) > 6)
            {
                _dragActive = true;
                e.Pointer.Capture(button);
                button.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeNorthSouth);
            }
            if (!_dragActive) return;
            var target = _dragSourceIndex;
            for (var i = 0; i < _cards.Count; i++)
            {
                var bounds = _cards[i].Bounds;
                if (pos.Y >= bounds.Top && pos.Y <= bounds.Bottom) { target = i; break; }
                if (pos.Y > bounds.Bottom) target = i;
            }
            _dragTargetIndex = target;
            for (var i = 0; i < _cards.Count; i++)
            {
                _cards[i].Opacity = i == _dragSourceIndex ? 0.55 : i == _dragTargetIndex ? 0.85 : 1.0;
            }
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        button.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != Avalonia.Input.MouseButton.Left) return;
            if (index < 0)
            {
                _selected = index;
                _selectedStage = null;
                Refresh(restoreFocus: true);
                e.Handled = true;
                return;
            }
            if (_dragSourceIndex != index) return;
            var from = _dragSourceIndex;
            var to = _dragTargetIndex;
            var wasDrag = _dragActive;
            _dragSourceIndex = -1;
            _dragTargetIndex = -1;
            _dragActive = false;
            button.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            foreach (var c in _cards) c.Opacity = 1.0;
            if (wasDrag) e.Pointer.Capture(null);
            e.Handled = true;
            if (wasDrag && from != to)
            {
                MoveTo(from, to);
            }
            else
            {
                _selected = index;
                _selectedStage = null;
                Refresh(restoreFocus: true);
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        if (index >= 0 && !_workspace.IsReadOnly)
        {
            var r = Recipe;
            var count = Model ? r.Modifiers.Length : r.Operations.Length;
            var up = new MenuItem { Header = T("Up"), IsEnabled = index > 0 };
            up.Click += (_, _) => MoveTo(index, index - 1);
            var down = new MenuItem { Header = T("Down"), IsEnabled = index < count - 1 };
            down.Click += (_, _) => MoveTo(index, index + 1);
            var remove = new MenuItem { Header = T("Remove"), IsEnabled = Model || count > 1 };
            remove.Click += (_, _) => Remove(index);
            button.ContextMenu = new ContextMenu { ItemsSource = new Control[] { up, down, new Separator(), remove } };
        }
        if (index >= 0) _cards.Add(button);
        _flow.Children.Add(button);
    }
    private void LanguageChanged(object? sender, EventArgs e)
    {
        _previewCheckerToggle.Content = T("PreviewCheckerboard");
        Refresh();
    }
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
    private bool _suppressInspectorRebuild;

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
        var committed = value;
        void Commit(bool live)
        {
            if (_refreshing || !IsVisible) return;
            if (float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                float.IsFinite(number) && number != committed)
            {
                var prev = _suppressInspectorRebuild;
                _suppressInspectorRebuild = live;
                try
                {
                    set(number);
                    committed = number;
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException)
                {
                    _status.Text = T("Invalid") + " " + error.Message;
                }
                finally
                {
                    _suppressInspectorRebuild = prev;
                }
            }
        }
        box.TextChanged += (_, _) => Commit(live: true);
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Commit(live: false); e.Handled = true; } };
        box.LostFocus += (_, _) => Commit(live: false);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = label, MinWidth = 130, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(box);
        parent.Children.Add(row);
    }
    private void Refresh(bool restoreFocus = false)
    {
        if (_refreshing || !IsVisible) return;
        if (_suppressInspectorRebuild)
        {
            RefreshTexturePreview();
            return;
        }
        var hadFocus = restoreFocus || IsKeyboardFocusWithin && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not TextBox;
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
            _cards.Clear();
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
                var shapeIndices = Enumerable.Range(1, ProceduralResourceSchema.ShapeNames.Length - 1).ToArray();
                if (r.Shape == 0) shapeIndices = [0, .. shapeIndices];
                Choice(_inspector, "Shape", shapeIndices.Select(idx => T("Shape." + ProceduralResourceSchema.ShapeNames[idx])),
                    Math.Max(0, Array.IndexOf(shapeIndices, r.Shape)), i => Save(Recipe with { Shape = shapeIndices[i] }));
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
        if (hadFocus)
        {
            if (_selected >= 0 && _selected < _cards.Count) _cards[_selected].Focus();
            else Focus();
        }
        RefreshTexturePreview();
    }
    private void Toggle(Panel parent, string key, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = T(key), IsChecked = value };
        box.IsCheckedChanged += (_, _) => { if (!_refreshing) set(box.IsChecked == true); };
        parent.Children.Add(box);
    }
    private static string? ModelParameter(int kind, int index) => ProceduralResourceSchema.ModelParameterName(kind, index);
    private static string? PixelParameter(int kind, int index) => ProceduralResourceSchema.PixelParameterName(kind, index);
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
    private void MoveTo(int from, int to)
    {
        var r = Recipe;
        var count = Model ? r.Modifiers.Length : r.Operations.Length;
        if (from < 0 || from >= count || to < 0 || to >= count || from == to) return;
        _selected = to;
        if (Model)
        {
            var list = r.Modifiers.ToList();
            var item = list[from];
            list.RemoveAt(from);
            list.Insert(to, item);
            Save(r with { Modifiers = list.ToArray() });
        }
        else
        {
            var list = r.Operations.ToList();
            var item = list[from];
            list.RemoveAt(from);
            list.Insert(to, item);
            RemapPixels(r, list.ToArray());
        }
    }
    private void Remove(int index)
    {
        var r = Recipe;
        if (Model)
        {
            _selected = Math.Min(index, r.Modifiers.Length - 2);
            _selectedStage = null;
            Save(r with { Modifiers = r.Modifiers.Where((_, i) => i != index).ToArray() });
        }
        else
        {
            _selected = r.Operations.Length > 1 ? Math.Min(index, r.Operations.Length - 2) : -1;
            _selectedStage = null;
            RemapPixels(r, r.Operations.Where((_, i) => i != index).ToArray());
        }
        if (_selected >= 0 && _selected < _cards.Count) _cards[_selected].Focus();
        else Focus();
    }
    private void RemapPixels(ProceduralRecipe old, ProceduralPixelOperation[] next)
    {
        if (next.Length == 0)
        {
            _status.Text = T("AtLeastOneOperation");
            return;
        }
        uint Map(uint register, uint limit)
        {
            if (register == 0) return 0;
            var index = Array.FindIndex(next, op => op.Id == old.Operations[register - 1].Id);
            if (index < 0) return 0;
            var mapped = (uint)index + 1;
            return mapped <= limit ? mapped : 0u;
        }
        var output = Map(old.OutputRegister, (uint)next.Length);
        Save(old with
        {
            Operations = next.Select((op, i) => op with
            {
                InputA = Map(op.InputA, (uint)i),
                InputB = Map(op.InputB, (uint)i)
            }).ToArray(),
            OutputRegister = output != 0 ? output : (uint)next.Length
        });
    }
}
