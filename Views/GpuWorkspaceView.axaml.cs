using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Line = Avalonia.Controls.Shapes.Line;
using LibraGpu.Core;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class GpuWorkspaceView : UserControl
{
    public GpuWorkspaceViewModel? Workspace { get; private set; }
    public event EventHandler? CloseRequested;
    private string? _selection;
    private string? _wireSource;
    private bool _refreshing;
    private bool _drafting;
    public GpuWorkspaceView()
    {
        InitializeComponent();
        TypeChoice.ItemsSource = Enum.GetValues<GpuType>().Where(t => t != GpuType.Quaternion).ToArray();
        TypeChoice.SelectedItem = GpuType.Float;
        ExportFormat.ItemsSource = new[] { "json", "usf", "hlsl", "compute" };
        ExportFormat.SelectedItem = "usf";
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeSettingsService.EditorColorsChanged += OnAppearanceChanged;
        LocalizationService.Shared.LanguageChanged += OnAppearanceChanged;
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ThemeSettingsService.EditorColorsChanged -= OnAppearanceChanged;
        LocalizationService.Shared.LanguageChanged -= OnAppearanceChanged;
        base.OnDetachedFromVisualTree(e);
    }
    private void OnAppearanceChanged(object? sender, EventArgs e) => Refresh();
    public void Open(GraphWorkspaceViewModel graph, Guid container)
    {
        DisposeWorkspace();
        Workspace = new(graph, container);
        Workspace.Refreshed += OnRefreshed;
        _selection = _wireSource = null;
        IsVisible = true;
        Refresh();
        Focus();
    }
    public void CloseWorkspace()
    {
        DisposeWorkspace(); IsVisible = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
    private void DisposeWorkspace()
    {
        if (Workspace is not { } vm) return;
        vm.Refreshed -= OnRefreshed; vm.Dispose(); Workspace = null;
    }
    private void OnRefreshed(object? sender, EventArgs e)
    {
        if (Workspace is { } vm && !vm.Graph.Nodes.Any(n => n.Id == vm.ContainerId)) { CloseWorkspace(); return; }
        if (!_drafting) Refresh();
        else RefreshDiagnostics();
    }
    private void Run(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or GpuValidationException or IOException or FormatException or OverflowException)
        { Status.Text = e is GpuValidationException gpu ? string.Join("\n", gpu.Diagnostics.Select(d => d.Message)) : e.Message; }
    }
    private void Refresh()
    {
        if (Workspace is not { } vm) return;
        _refreshing = true;
        try
        {
            Breadcrumb.Text = vm.Breadcrumb;
            Capacity.Text = vm.State.Program.Capacity.ToString(CultureInfo.InvariantCulture);
            BackButton.Content = vm.Kernel is null ? LocalizationService.Shared.Get("Gpu.Close") : "Flow / フローへ";
            FlowScroll.IsVisible = vm.Kernel is null;
            KernelTabs.IsVisible = vm.Kernel is not null;
            Palette.Children.Clear();
            if (vm.Kernel is null)
            {
                foreach (var kind in new[] { FlowKind.Spawn, FlowKind.Update, FlowKind.RenderBillboard })
                {
                    var button = Button(kind.ToString(), () => vm.AddFlow(kind));
                    button.IsEnabled = !vm.State.Program.Flow.Any(f => f.Kind == kind);
                    Palette.Children.Add(button);
                }
                BuildFlow();
            }
            else
            {
                foreach (var op in vm.Palette)
                    Palette.Children.Add(Button("+ " + op.Op, () => Run(() =>
                    {
                        var type = TypeChoice.SelectedItem is GpuType selected ? selected : GpuType.Float;
                        if (op.Op == GpuOp.ReadUniform && !ParticleAbi.Uniforms.Values.Contains(type))
                            throw new ArgumentException("Uniforms support Float and Float3.");
                        if (op.Op == GpuOp.ReadAttribute && !ParticleAbi.Attributes.Any(a => a.Type == type))
                            throw new ArgumentException("No ABI attribute has this type.");
                        _selection = vm.Add(op.Op, type).Id; Refresh();
                    })));
                Palette.Children.Add(Button("+ Attribute write / 属性出力", () =>
                {
                    Status.Text = "Select a value node, then choose an attribute under Writes / 値ノードを選択し、属性出力を指定";
                }));
                BuildGraph();
                KernelText.Text = vm.Text;
            }
            BuildInspector();
            BuildWrites();
            BuildBindings();
            RefreshDiagnostics();
            Status.Text = vm.Status;
        }
        finally { _refreshing = false; }
    }
    private Button Button(string label, Action action)
    {
        var button = new Button { Content = label, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        button.Click += (_, _) => Run(action);
        return button;
    }
    private void BuildFlow()
    {
        var vm = Workspace!;
        FlowPanel.Children.Clear();
        for (var i = 0; i < vm.State.Program.Flow.Count; i++)
        {
            var index = i; var flow = vm.State.Program.Flow[i];
            if (i != 0) FlowPanel.Children.Add(new TextBlock { Text = "↓", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center });
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(new TextBlock { Text = $"{i + 1}. {flow.Kind}  ·  {flow.Id}" });
            if (flow.Kernel is { } kernel)
                panel.Children.Add(Button("Open kernel / カーネルを開く", () => { _selection = null; vm.EnterKernel(kernel); }));
            else
            {
                panel.Children.Add(new TextBlock { Text = "Billboard source pass · engine execution pending", TextWrapping = TextWrapping.Wrap });
                var texture = new ComboBox
                {
                    ItemsSource = vm.State.Program.Resources.Where(r => r.Kind == ResourceKind.Texture2D).Select(r => r.Id).ToArray(),
                    SelectedItem = flow.Resource
                };
                texture.SelectionChanged += (_, _) =>
                {
                    if (texture.SelectedItem is string resource && resource != flow.Resource)
                        Run(() => vm.Edit(new() { Action = "flow-update", Flow = flow with { Resource = resource } }));
                };
                panel.Children.Add(texture);
            }
            var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 5 };
            var up = Button("↑", () => vm.Edit(new() { Action = "flow-move", NodeId = flow.Id, Input = index - 1 }));
            up.IsEnabled = i > 0;
            var down = Button("↓", () => vm.Edit(new() { Action = "flow-move", NodeId = flow.Id, Input = index + 1 }));
            down.IsEnabled = i + 1 < vm.State.Program.Flow.Count;
            actions.Children.Add(up); actions.Children.Add(down);
            actions.Children.Add(Button("Remove / 削除", () => vm.Edit(new() { Action = "flow-remove", NodeId = flow.Id })));
            panel.Children.Add(actions);
            var border = new Border
            {
                Child = panel, Padding = new Thickness(14), CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1), BorderBrush = ThemeSettingsService.EditorBrush("NodeBorder"),
                Background = this.FindResource("EffectSurfaceBrush") as IBrush ?? Brushes.DimGray
            };
            border.PointerPressed += (_, e) =>
            {
                if (e.ClickCount >= 2 && flow.Kernel is { } id) { _selection = null; vm.EnterKernel(id); e.Handled = true; }
            };
            FlowPanel.Children.Add(border);
        }
    }
    private GpuPosition Position(KernelGraph k, GpuNode n, int index) =>
        Workspace!.State.Positions.GetValueOrDefault(GpuParticleEditorService.Key(k.Id, n.Id))
        ?? new(30 + index % 4 * 280, 30 + index / 4 * 160);
    private void BuildGraph()
    {
        NodeCanvas.Children.Clear();
        var vm = Workspace!; var k = vm.Kernel!;
        var positions = k.Nodes.Select((n, i) => (n.Id, P: Position(k, n, i))).ToDictionary(p => p.Id, p => p.P);
        foreach (var n in k.Nodes)
            for (var input = 0; input < n.Inputs.Count; input++)
                if (positions.TryGetValue(n.Inputs[input], out var source))
                {
                    var target = positions[n.Id];
                    var line = new Line
                    {
                        StartPoint = new(source.X + 230, source.Y + 45),
                        EndPoint = new(target.X, target.Y + 70 + input * 27),
                        Stroke = Brushes.CornflowerBlue, StrokeThickness = 2, IsHitTestVisible = false
                    };
                    NodeCanvas.Children.Add(line);
                }
        foreach (var n in k.Nodes)
        {
            var p = positions[n.Id];
            var panel = new StackPanel { Spacing = 3 };
            var title = new TextBlock { Text = $"{n.Op} : {n.Type}", Margin = new Thickness(4), Cursor = new Cursor(StandardCursorType.SizeAll) };
            panel.Children.Add(title);
            var output = Button("● output : " + n.Type, () => { _wireSource = n.Id; Status.Text = "Select a target input / 接続先を選択"; });
            panel.Children.Add(output);
            var maxInputs = GpuOperationRegistry.Operations[n.Op].MaxInputs;
            for (var i = 0; i < maxInputs; i++)
            {
                var input = i;
                var sourceId = n.Inputs.ElementAtOrDefault(input);
                var sourceNode = k.Nodes.FirstOrDefault(x => x.Id == sourceId);
                panel.Children.Add(Button($"○ in {input}  [{sourceNode?.Type.ToString() ?? "unconnected"}]", () =>
                {
                    if (_wireSource is null) { _selection = n.Id; BuildInspector(); return; }
                    vm.Edit(new() { Action = "connect", KernelId = k.Id, NodeId = n.Id, SourceId = _wireSource, Input = input });
                    _wireSource = null;
                }));
            }
            var error = vm.Diagnostics.Any(d => d.KernelId == k.Id && d.NodeId == n.Id);
            var border = new Border
            {
                Width = 230, Child = panel, Padding = new Thickness(6), CornerRadius = new CornerRadius(5),
                Background = this.FindResource("EffectSurfaceBrush") as IBrush ?? Brushes.DimGray,
                BorderBrush = error ? Brushes.Red : _selection == n.Id ? Brushes.CornflowerBlue : ThemeSettingsService.EditorBrush("NodeBorder"),
                BorderThickness = new Thickness(1)
            };
            Canvas.SetLeft(border, p.X); Canvas.SetTop(border, p.Y);
            Point? start = null;
            title.PointerPressed += (_, e) =>
            {
                _selection = n.Id; BuildInspector();
                if (!e.GetCurrentPoint(title).Properties.IsLeftButtonPressed) return;
                start = e.GetPosition(NodeCanvas); e.Pointer.Capture(title); e.Handled = true;
            };
            title.PointerMoved += (_, e) =>
            {
                if (start is not { } s) return;
                var delta = e.GetPosition(NodeCanvas) - s;
                Canvas.SetLeft(border, Math.Max(0, p.X + delta.X)); Canvas.SetTop(border, Math.Max(0, p.Y + delta.Y));
            };
            title.PointerReleased += (_, e) =>
            {
                if (start is null) return;
                start = null; e.Pointer.Capture(null);
                var position = new GpuPosition(Canvas.GetLeft(border), Canvas.GetTop(border));
                Run(() => vm.Edit(new() { Action = "move", KernelId = k.Id, NodeId = n.Id, Position = position }));
            };
            border.PointerPressed += (_, _) => { _selection = n.Id; BuildInspector(); };
            NodeCanvas.Children.Add(border);
        }
    }
    private void BuildInspector()
    {
        NodeInspector.Children.Clear();
        var vm = Workspace!;
        var n = vm.Kernel?.Nodes.FirstOrDefault(n => n.Id == _selection);
        SelectionLabel.Text = n?.Id ?? "Select a visual node / ノードを選択";
        if (n is null) return;
        var type = new ComboBox { ItemsSource = Enum.GetValues<GpuType>().Where(t => t != GpuType.Quaternion).ToArray(), SelectedItem = n.Type };
        NodeInspector.Children.Add(new TextBlock { Text = "Result type / 出力型" });
        NodeInspector.Children.Add(type);
        var value = new TextBox { Text = string.Join(", ", n.Value.Select(v => v.ToString("G", CultureInfo.InvariantCulture))) };
        if (n.Op == GpuOp.Constant) { NodeInspector.Children.Add(new TextBlock { Text = "Constant components / 定数" }); NodeInspector.Children.Add(value); }
        var attr = new ComboBox { ItemsSource = ParticleAbi.Attributes.Select(a => a.Id).ToArray(), SelectedItem = n.Attribute };
        if (n.Op == GpuOp.ReadAttribute) NodeInspector.Children.Add(attr);
        var uniform = new ComboBox { ItemsSource = ParticleAbi.Uniforms.Keys.ToArray(), SelectedItem = n.Uniform };
        if (n.Op == GpuOp.ReadUniform) NodeInspector.Children.Add(uniform);
        var component = new TextBox { Text = (n.ComponentIndex ?? 0).ToString(CultureInfo.InvariantCulture) };
        if (n.Op == GpuOp.Component) { NodeInspector.Children.Add(new TextBlock { Text = "Component 0…3" }); NodeInspector.Children.Add(component); }
        NodeInspector.Children.Add(Button("Apply properties / 適用", () =>
        {
            var updated = n with
            {
                Type = (GpuType)type.SelectedItem!,
                Value = n.Op == GpuOp.Constant
                    ? value.Text!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToList() : n.Value,
                Attribute = n.Op == GpuOp.ReadAttribute ? attr.SelectedItem as string : n.Attribute,
                Uniform = n.Op == GpuOp.ReadUniform ? uniform.SelectedItem as string : n.Uniform,
                ComponentIndex = n.Op == GpuOp.Component ? int.Parse(component.Text!, CultureInfo.InvariantCulture) : n.ComponentIndex,
                SourceSpan = null
            };
            vm.Edit(new() { Action = "update", KernelId = vm.KernelId, Node = updated });
        }));
        for (var i = 0; i < n.Inputs.Count; i++)
        {
            var input = i;
            NodeInspector.Children.Add(Button("Disconnect input " + input, () =>
                vm.Edit(new() { Action = "disconnect", KernelId = vm.KernelId, NodeId = n.Id, Input = input })));
        }
        NodeInspector.Children.Add(Button("Remove node / ノード削除", () =>
            vm.Edit(new() { Action = "remove", KernelId = vm.KernelId, NodeId = n.Id })));
    }
    private void BuildWrites()
    {
        WritesPanel.Children.Clear();
        var vm = Workspace!;
        if (vm.Kernel is not { } k) return;
        foreach (var write in k.Writes)
        {
            var attribute = ParticleAbi.Attributes.SingleOrDefault(a => a.Id == write.Attribute);
            WritesPanel.Children.Add(new TextBlock { Text = $"@{write.Attribute} : {attribute?.Type} ← {write.Value}", TextWrapping = TextWrapping.Wrap });
            WritesPanel.Children.Add(Button("Remove " + write.Id, () => vm.Edit(new() { Action = "write-remove", KernelId = k.Id, NodeId = write.Id })));
        }
        var attributes = new ComboBox { ItemsSource = ParticleAbi.Attributes.Select(a => a.Id).ToArray(), SelectedIndex = 0 };
        WritesPanel.Children.Add(attributes);
        WritesPanel.Children.Add(Button("Write selected node / 選択ノードを出力", () =>
        {
            if (_selection is null) throw new ArgumentException("Select a node first.");
            var attribute = (string)attributes.SelectedItem!;
            var existing = k.Writes.FirstOrDefault(w => w.Attribute == attribute);
            vm.Edit(new() { Action = "write", KernelId = k.Id,
                Write = new(existing?.Id ?? "w_" + Guid.NewGuid().ToString("N"), attribute, _selection) });
        }));
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
        foreach (var resource in vm.State.Program.Resources)
        {
            BindingsPanel.Children.Add(new TextBlock { Text = $"Texture binding / テクスチャ : {resource.Id}", TextWrapping = TextWrapping.Wrap });
            var path = new TextBox { Text = vm.State.ResourceAssetPaths.GetValueOrDefault(resource.Id) ?? "",
                PlaceholderText = "Engine asset object path (UI metadata)" };
            BindingsPanel.Children.Add(path);
            BindingsPanel.Children.Add(Button("Bind / バインド", () => vm.Edit(new()
            {
                Action = "resource", Resource = resource, AssetPath = path.Text ?? ""
            })));
        }
    }
    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_refreshing || Workspace is not { Kernel: { } k } vm) return;
        _drafting = true;
        try { Run(() => vm.Edit(new() { Action = "draft", KernelId = k.Id, Text = KernelText.Text ?? "" })); }
        finally { _drafting = false; }
    }
    private void OnApplyText(object? sender, RoutedEventArgs e) => Run(() =>
        Workspace!.Edit(new() { Action = "text", KernelId = Workspace.KernelId, Text = KernelText.Text ?? "" }));
    private void OnEmitText(object? sender, RoutedEventArgs e) => Run(() =>
        Workspace!.Edit(new() { Action = "discard-draft", KernelId = Workspace.KernelId }));
    private void OnCapacity(object? sender, RoutedEventArgs e) => Run(() =>
        Workspace!.Edit(new() { Action = "capacity", Capacity = uint.Parse(Capacity.Text!, CultureInfo.InvariantCulture) }));
    private void OnCompile(object? sender, RoutedEventArgs e) => Workspace?.TryCompile(out _);
    private void OnUndo(object? sender, RoutedEventArgs e) => Workspace?.Graph.History.Undo();
    private void OnRedo(object? sender, RoutedEventArgs e) => Workspace?.Graph.History.Redo();
    private void OnClose(object? sender, RoutedEventArgs e) => CloseWorkspace();
    private void OnBack(object? sender, RoutedEventArgs e)
    {
        if (Workspace?.Kernel is null) CloseWorkspace(); else { _selection = _wireSource = null; Workspace.Back(); }
    }
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { OnBack(sender, new()); e.Handled = true; }
        else if (e.Key == Key.Delete && KernelText.IsFocused == false && _selection is { } node && Workspace?.KernelId is { } kernel)
        { Run(() => Workspace.Edit(new() { Action = "remove", KernelId = kernel, NodeId = node })); e.Handled = true; }
        else if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        { Workspace?.Graph.History.Undo(); e.Handled = true; }
        else if (e.Key == Key.Y && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        { Workspace?.Graph.History.Redo(); e.Handled = true; }
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
                var shaderFormat = format switch { "usf" => ShaderFormat.Usf, "hlsl" => ShaderFormat.HlslInclude, _ => ShaderFormat.UnityCompute };
                var metadata = GpuParticleEditorService.Compile(vm.State).ExportMetadataJson(shaderFormat);
                await GpuParticleEditorService.WriteAtomicAsync(Path.ChangeExtension(destination, ".metadata.json"), metadata);
            }
            Status.Text = "Exported source · GPU not executed / ソース出力完了（GPU未実行）";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or GpuValidationException or ArgumentException)
        { Status.Text = error is GpuValidationException gpu ? string.Join("\n", gpu.Diagnostics.Select(d => d.Message)) : error.Message; }
    }
}
