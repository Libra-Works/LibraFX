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
    public GpuWorkspaceViewModel? Workspace { get; private set; }
    public event EventHandler? CloseRequested;
    private string? _selection;
    private string? _wireSource;
    private bool _refreshing;
    private bool _drafting;
    private string _displayedText = "";
    private readonly Dictionary<string, NodeVisual> _nodeVisuals = [];
    private readonly List<WireVisual> _wireVisuals = [];
    private GraphSceneLayer? _grid;
    private readonly GpuGraphViewport _viewport;
    private readonly HashSet<string> _selectedNodes = [];
    private (IPointer Pointer, Point Start, string[] Before, Border Box)? _marquee;
    private Point _pastePosition = new(80, 80);
    private string? _visualKernelId;
    private NodeDrag? _drag;
    private ConnectionDrag? _connectionDrag;
    private bool _framePending;
    private bool _anchorsDirty;
    private readonly Action<TimeSpan> _renderFrame;

    private sealed class NodeVisual(GpuNode model, Border border, TextBlock title, Button output, Button[] inputs,
        TextBlock outputLabel, TextBlock[] inputLabels)
    {
        public GpuNode Model = model;
        public readonly Border Border = border;
        public readonly TextBlock Title = title;
        public readonly Button Output = output;
        public readonly Button[] Inputs = inputs;
        public readonly TextBlock OutputLabel = outputLabel;
        public readonly TextBlock[] InputLabels = inputLabels;
        public readonly TranslateTransform Translation = new();
        public readonly List<WireVisual> Wires = [];
        public Point Position;
        public Vector PreviewOffset;
        public Vector CanvasOrigin;
        public bool AnchorInitialized;
        public bool IsHovered;
        public Point OutputAnchor;
        public readonly Point[] InputAnchors = new Point[inputs.Length];
        public Point Origin => Position + PreviewOffset;
        public Point CanvasPosition => Origin + CanvasOrigin;
        public void SyncTransform()
        {
            if (Border.Bounds.Width == 0) return;
            // Keep logical positions exact even when the layout anchor is rounded at fractional DPI.
            Translation.X = CanvasPosition.X - Border.Bounds.X;
            Translation.Y = CanvasPosition.Y - Border.Bounds.Y;
        }
    }
    private sealed record WireVisual(NodeVisual Source, NodeVisual Target, int Input, GpuConnectionCurve Curve);
    private sealed class NodeDrag(NodeVisual visual, IPointer pointer, Point start, Point startView)
    {
        public readonly NodeVisual Visual = visual;
        public readonly IPointer Pointer = pointer;
        public readonly Point Start = start;
        public readonly Point StartView = startView;
        public readonly Point Original = visual.Position;
        public Point Latest = start;
        public bool IsDragging;
        public (NodeVisual Visual, Point Position)[] Members = [];
    }
    private sealed class ConnectionDrag(NodeVisual source, IPointer pointer, GpuConnectionCurve preview, Point latest)
    {
        public readonly NodeVisual Source = source;
        public readonly IPointer Pointer = pointer;
        public readonly GpuConnectionCurve Preview = preview;
        public Point Latest = latest;
        public readonly Point Start = latest;
    }
    public GpuWorkspaceView()
    {
        InitializeComponent();
        _viewport = new(GpuGraphScrollViewer, GpuZoomHost, NodeCanvas);
        _viewport.Changed += OnViewportChanged;
        _viewport.ContextRequested += OpenGraphContext;
        NodeCanvas.PointerPressed += OnBackgroundPressed;
        NodeCanvas.PointerMoved += OnBackgroundMoved;
        NodeCanvas.PointerReleased += OnBackgroundReleased;
        NodeCanvas.PointerCaptureLost += (_, _) => CancelMarquee();
        _renderFrame = OnRenderFrame;
        NodeCanvas.LayoutUpdated += (_, _) => { if (_anchorsDirty) UpdatePortAnchors(); };
        NodeCanvas.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.RenderTransformProperty) _grid?.InvalidateVisual();
        };
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
        FinishDrag(commit: false);
        CancelConnectionDrag();
        CancelMarquee();
        ThemeSettingsService.EditorColorsChanged -= OnAppearanceChanged;
        LocalizationService.Shared.LanguageChanged -= OnAppearanceChanged;
        base.OnDetachedFromVisualTree(e);
    }
    private void OnAppearanceChanged(object? sender, EventArgs e)
    {
        _grid?.InvalidateVisual();
        Refresh();
    }
    public void Open(GraphWorkspaceViewModel graph, Guid container)
    {
        DisposeWorkspace();
        Workspace = new(graph, container);
        Workspace.Refreshed += OnRefreshed;
        Workspace.LayoutChanged += OnLayoutChanged;
        Workspace.StatusChanged += OnStatusChanged;
        _visualKernelId = null;
        _selection = _wireSource = null;
        _selectedNodes.Clear();
        IsVisible = true;
        Refresh();
        Focus();
    }
    public void CloseWorkspace()
    {
        FinishDrag(commit: true);
        CancelConnectionDrag();
        CancelMarquee();
        DisposeWorkspace(); IsVisible = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
    private void DisposeWorkspace()
    {
        if (Workspace is not { } vm) return;
        FinishDrag(commit: false);
        CancelConnectionDrag();
        CancelMarquee();
        vm.Refreshed -= OnRefreshed;
        vm.LayoutChanged -= OnLayoutChanged;
        vm.StatusChanged -= OnStatusChanged;
        vm.Dispose(); Workspace = null;
    }
    private void OnStatusChanged(object? sender, EventArgs e)
    {
        if (Workspace is { } vm) Status.Text = vm.Status;
    }
    private void OnLayoutChanged(object? sender, EventArgs e)
    {
        FinishDrag(commit: false);
        CancelConnectionDrag();
        ApplySavedPositions();
    }
    private void OnRefreshed(object? sender, EventArgs e)
    {
        if (Workspace is { } vm && !vm.Graph.Nodes.Any(n => n.Id == vm.ContainerId))
        {
            FinishDrag(commit: false);
            CancelConnectionDrag();
            CloseWorkspace();
            return;
        }
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
                    Palette.Children.Add(Button("+ " + op.DisplayName, () => Run(() =>
                    {
                        var type = TypeChoice.SelectedItem is GpuType selected ? selected : GpuType.Float;
                        if (op.Op == GpuOp.ReadUniform && !ParticleAbi.Uniforms.Values.Contains(type))
                            throw new ArgumentException("Uniforms support Float and Float3.");
                        if (op.Op == GpuOp.ReadAttribute && !ParticleAbi.Attributes.Any(a => a.Type == type))
                            throw new ArgumentException("No ABI attribute has this type.");
                        SelectVisualNode(vm.Add(op.Op, type).Id);
                    })));
                Palette.Children.Add(Button("+ Attribute write / 属性出力", () =>
                {
                    Status.Text = "Select a value node, then choose an attribute under Writes / 値ノードを選択し、属性出力を指定";
                }));
                BuildGraph();
                _displayedText = vm.Text;
                KernelText.Text = _displayedText;
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
        var map = GpuFlowVisualization.Create(vm.State);
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
                    Text = "↓ declared flow-list order · not a runtime trace / 保存された順序 · 実行トレースではありません",
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
                    Text = $"Compiled source reference · {step.CompiledMetadata.Kind} / ソースコンパイル済み",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeSettingsService.EditorBrush("GraphSignal")
                });
            if ((flow.Kind is FlowKind.Spawn or FlowKind.Update) && step.Kernel is { } kernel)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"Kernel: {kernel.Id} · {kernel.Stage} / カーネル",
                    TextWrapping = TextWrapping.Wrap
                });
                panel.Children.Add(Button("Open kernel / カーネルを開く", () =>
                {
                    _selection = null;
                    vm.EnterKernel(kernel.Id);
                }));
            }
            else if (flow.Kind == FlowKind.RenderBillboard)
            {
                if (flow.Kernel is not null)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = $"Unexpected kernel reference: {flow.Kernel} / 想定外のカーネル参照",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = ThemeSettingsService.EditorBrush("GraphSignal")
                    });
                    if (step.Kernel is { } referencedKernel)
                        panel.Children.Add(Button("Inspect referenced kernel / 参照カーネルを確認", () =>
                        {
                            _selection = null;
                            vm.EnterKernel(referencedKernel.Id);
                        }));
                }
                panel.Children.Add(new TextBlock
                {
                    Text = "Billboard source binding · engine-side rendering is pending / 描画実行はエンジン側で未対応",
                    TextWrapping = TextWrapping.Wrap
                });
                var texture = new ComboBox
                {
                    ItemsSource = vm.State.Program.Resources.Where(r => r.Kind == ResourceKind.Texture2D).Select(r => r.Id).ToArray(),
                    SelectedItem = flow.Resource
                };
                Avalonia.Automation.AutomationProperties.SetName(
                    texture, "Billboard texture resource / ビルボードのテクスチャリソース");
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
                    Text = $"Kernel reference: {kernelId} / カーネル参照",
                    TextWrapping = TextWrapping.Wrap
                });
                if (step.Kernel is { } referencedKernel)
                    panel.Children.Add(Button("Inspect referenced kernel / 参照カーネルを確認 (unsupported binding)", () =>
                    {
                        _selection = null;
                        vm.EnterKernel(referencedKernel.Id);
                    }));
            }

            AddFlowPorts(panel, "Inputs / 入力ポート", step.Inputs);
            AddFlowPorts(panel, "Outputs / 出力ポート", step.Outputs);
            foreach (var reason in step.UnsupportedReasons)
                panel.Children.Add(new TextBlock
                {
                    Text = "Unsupported relationship / 未対応: " + reason,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeSettingsService.EditorBrush("GraphSignal")
                });

            var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 5 };
            var up = Button("↑", () => vm.Edit(new() { Action = "flow-move", NodeId = flow.Id, Input = index - 1 }));
            Avalonia.Automation.AutomationProperties.SetName(up, "Move flow earlier / フローを前へ移動");
            ToolTip.SetTip(up, "Move earlier in the authored flow list / 保存されたフロー順を前へ");
            up.IsEnabled = i > 0;
            var down = Button("↓", () => vm.Edit(new() { Action = "flow-move", NodeId = flow.Id, Input = index + 1 }));
            Avalonia.Automation.AutomationProperties.SetName(down, "Move flow later / フローを後へ移動");
            ToolTip.SetTip(down, "Move later in the authored flow list / 保存されたフロー順を後へ");
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
            Text = "Cross-flow parameter relationships / フロー間パラメーター関係",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 10, 0, 2)
        };
        FlowPanel.Children.Add(relationTitle);
        if (map.Transfers.Count == 0)
            FlowPanel.Children.Add(new TextBlock
            {
                Text = "No authored cross-flow attribute write/read match. Host uniforms are external inputs, not flow-to-flow transfers. / フロー間で一致する属性の書き込み・読み取りはありません。Uniform は外部入力です。",
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
                (transfer.IsTypeCompatible ? "Static attribute dependency · not observed execution / 静的依存関係 · 実行観測ではありません"
                    : "Type mismatch or unresolved declaration / 型不一致または宣言未解決") +
                (transfer.ProducerPrecedesConsumer
                    ? ""
                    : "\nAuthored producer appears after consumer; transfer order is not valid / 出力側が入力側より後のため、順序は無効です");
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
    private static void AddFlowPorts(StackPanel panel, string heading, IReadOnlyList<GpuFlowPort> ports)
    {
        panel.Children.Add(new TextBlock { Text = heading, FontWeight = FontWeight.SemiBold });
        if (ports.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No authored ports / ポートなし",
                Foreground = ThemeSettingsService.EditorBrush("CurveAxis")
            });
            return;
        }
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
            panel.Children.Add(row);
        }
    }
    private void BuildGraph()
    {
        var vm = Workspace!; var k = vm.Kernel!;
        if (_visualKernelId != k.Id) _selectedNodes.Clear();
        _selectedNodes.IntersectWith(k.Nodes.Select(n => n.Id));
        if (_selection is not null && !_selectedNodes.Contains(_selection)) _selection = _selectedNodes.LastOrDefault();
        var sameTopology = _visualKernelId == k.Id && _nodeVisuals.Count == k.Nodes.Count &&
            k.Nodes.All(n => _nodeVisuals.TryGetValue(n.Id, out var visual) &&
                visual.Model.Op == n.Op && visual.Model.Inputs.SequenceEqual(n.Inputs));
        if (!sameTopology)
        {
            FinishDrag(commit: false);
            CancelConnectionDrag();
            NodeCanvas.Children.Clear();
            _grid = new GraphSceneLayer { GridOnly = true, DataContext = null, IsHitTestVisible = false };
            NodeCanvas.Children.Add(_grid);
            _viewport.SetGrid(_grid);
            _nodeVisuals.Clear();
            _wireVisuals.Clear();
            _visualKernelId = k.Id;
            foreach (var n in k.Nodes)
            {
                var panel = new StackPanel { Spacing = 3 };
                var title = new TextBlock
                {
                    Margin = new Thickness(4, 0), MinHeight = 26, Tag = n.Id,
                    TextWrapping = TextWrapping.Wrap
                };
                panel.Children.Add(title);
                var output = PortButton("gpu-output");
                var outputLabel = PortLabel();
                outputLabel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
                var outputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                outputRow.Children.Add(outputLabel);
                Grid.SetColumn(output, 1);
                outputRow.Children.Add(output);
                panel.Children.Add(outputRow);
                var inputs = new Button[GpuOperationRegistry.Operations[n.Op].MaxInputs];
                var inputLabels = new TextBlock[inputs.Length];
                for (var i = 0; i < inputs.Length; i++)
                {
                    var input = i;
                    inputs[i] = PortButton("gpu-input-" + i);
                    inputs[i].Click += (_, _) => Run(() =>
                    {
                        if (_wireSource is null) { SelectVisualNode(n.Id); return; }
                        vm.Edit(new() { Action = "connect", KernelId = k.Id, NodeId = n.Id, SourceId = _wireSource, Input = input });
                        _wireSource = null;
                    });
                    inputLabels[i] = PortLabel();
                    inputLabels[i].Margin = new Thickness(4, 0, 0, 0);
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                    row.Children.Add(inputs[i]);
                    Grid.SetColumn(inputLabels[i], 1);
                    row.Children.Add(inputLabels[i]);
                    panel.Children.Add(row);
                }
                var border = new Border
                {
                    Width = 230, Child = panel, Padding = new Thickness(6), CornerRadius = new CornerRadius(5),
                    Background = this.FindResource("EffectSurfaceBrush") as IBrush ?? Brushes.DimGray,
                    BorderThickness = new Thickness(1), Tag = n.Id, Cursor = new Cursor(StandardCursorType.SizeAll)
                };
                var visual = new NodeVisual(n, border, title, output, inputs, outputLabel, inputLabels)
                { CanvasOrigin = _viewport.Origin };
                border.RenderTransform = visual.Translation;
                _nodeVisuals.Add(n.Id, visual);
                border.PointerPressed += (_, e) => BeginDrag(visual, e);
                border.PointerMoved += (_, e) => MoveDrag(visual, e);
                border.PointerReleased += (_, e) => ReleaseDrag(visual, e);
                border.PointerCaptureLost += (_, _) =>
                {
                    if (_drag?.Visual == visual) FinishDrag(commit: true);
                };
                output.AddHandler(InputElement.PointerPressedEvent, (_, e) => BeginConnectionDrag(visual, e),
                    RoutingStrategies.Tunnel);
                output.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
                {
                    if (_connectionDrag is not { } connection || connection.Source != visual || connection.Pointer != e.Pointer) return;
                    connection.Latest = e.GetPosition(NodeCanvas);
                    ScheduleFrame();
                    e.Handled = true;
                }, RoutingStrategies.Tunnel);
                output.AddHandler(InputElement.PointerReleasedEvent, (_, e) => ReleaseConnectionDrag(visual, e),
                    RoutingStrategies.Tunnel);
                output.PointerCaptureLost += (_, _) =>
                {
                    if (_connectionDrag?.Source == visual) CancelConnectionDrag();
                };
                border.PointerEntered += (_, _) => { visual.IsHovered = true; UpdateSelectionStyles(); };
                border.PointerExited += (_, _) => { visual.IsHovered = false; UpdateSelectionStyles(); };
                border.SizeChanged += (_, _) => { _anchorsDirty = true; ScheduleFrame(); };
                NodeCanvas.Children.Add(border);
            }
            foreach (var n in k.Nodes)
                for (var input = 0; input < n.Inputs.Count; input++)
                    if (_nodeVisuals.TryGetValue(n.Inputs[input], out var source))
                    {
                        var target = _nodeVisuals[n.Id];
                        var curve = new GpuConnectionCurve { Width = NodeCanvas.Width, Height = NodeCanvas.Height,
                            StrokeThickness = 2, IsHitTestVisible = false };
                        var wire = new WireVisual(source, target, input, curve);
                        _wireVisuals.Add(wire);
                        source.Wires.Add(wire);
                        target.Wires.Add(wire);
                        NodeCanvas.Children.Insert(1, curve);
                    }
            _anchorsDirty = true;
        }
        foreach (var n in k.Nodes)
        {
            var visual = _nodeVisuals[n.Id];
            visual.Model = n;
            visual.Title.Text = NodeDisplayName(n);
            visual.OutputLabel.Text = OutputDisplayName(n) + " : " + n.Type;
            visual.Output.Foreground = TypeBrush(n.Type);
            for (var i = 0; i < visual.Inputs.Length; i++)
            {
                var source = k.Nodes.FirstOrDefault(x => x.Id == n.Inputs.ElementAtOrDefault(i));
                var defaults = GpuOperationRegistry.GetDefaultInputs(n.Op, n.Type);
                var descriptor = defaults.ElementAtOrDefault(i);
                visual.InputLabels[i].Text = $"{InputDisplayName(descriptor?.Id ?? "input")} : {source?.Type ?? descriptor?.DefaultType ?? n.Type}";
                visual.Inputs[i].Foreground = TypeBrush(source?.Type ?? descriptor?.DefaultType ?? n.Type);
            }
        }
        UpdateSelectionStyles();
        if (_drag is null) ApplySavedPositions();
        Dispatcher.UIThread.Post(_viewport.Initialize);
        ScheduleFrame();
    }
    private static Button PortButton(string tag) => new()
    {
        Content = "●", Tag = tag, Width = 24, Height = 24, MinWidth = 24, MinHeight = 24,
        Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        Cursor = new Cursor(StandardCursorType.Hand),
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center
    };
    private static TextBlock PortLabel() => new()
    {
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        IsHitTestVisible = false
    };
    private static string InputDisplayName(string id) => LocalizationService.Shared.Get("Gpu.Input." + id);
    private static string AttributeDisplayName(string? id) => id is null
        ? "?"
        : LocalizationService.Shared.Get("Gpu.Attribute." + id);
    private static string UniformDisplayName(string? id) => id is null
        ? "?"
        : LocalizationService.Shared.Get("Gpu.Uniform." + id);
    private static string NodeDisplayName(GpuNode node)
    {
        var operation = node.Op switch
        {
            GpuOp.ReadAttribute => LocalizationService.Shared.Get("Gpu.ReadAttribute") + " · " +
                AttributeDisplayName(node.Attribute),
            GpuOp.ReadUniform => LocalizationService.Shared.Get("Gpu.ReadUniform") + " · " +
                UniformDisplayName(node.Uniform),
            GpuOp.SampleFlowMap => GpuOperationRegistry.Operations[node.Op].DisplayName + " · " + node.Resource,
            _ => GpuOperationRegistry.Operations[node.Op].DisplayName
        };
        return $"{operation} : {node.Type}";
    }
    private static string OutputDisplayName(GpuNode node) => node.Op switch
    {
        GpuOp.ReadAttribute => AttributeDisplayName(node.Attribute),
        GpuOp.ReadUniform => UniformDisplayName(node.Uniform),
        _ => LocalizationService.Shared.Get("Gpu.Output")
    };
    private static IBrush TypeBrush(GpuType type) => ThemeSettingsService.EditorBrush(
        type == GpuType.Bool ? "GraphSignal" : "GraphParameter");
    private void SelectVisualNode(string id, bool preserveGroup = false)
    {
        if (!preserveGroup || !_selectedNodes.Contains(id)) { _selectedNodes.Clear(); _selectedNodes.Add(id); }
        _selection = id;
        UpdateSelectionStyles();
        BuildInspector();
    }
    private void UpdateSelectionStyles()
    {
        FrameButton.IsEnabled = _selectedNodes.Count > 0;
        var errors = Workspace!.Diagnostics.Where(d => d.KernelId == Workspace.KernelId && d.NodeId is not null)
            .Select(d => d.NodeId!).ToHashSet();
        foreach (var visual in _nodeVisuals.Values)
            visual.Border.BorderBrush = errors.Contains(visual.Model.Id) ? Brushes.Red :
                _selectedNodes.Contains(visual.Model.Id) ? ThemeSettingsService.EditorBrush("NodeSelectedBorder") :
                ThemeSettingsService.EditorBrush(visual.IsHovered ? "CurveAxis" : "NodeBorder");
        foreach (var wire in _wireVisuals)
            wire.Curve.Stroke = errors.Contains(wire.Target.Model.Id) ? Brushes.Red : TypeBrush(wire.Source.Model.Type);
    }
    private void ApplySavedPositions()
    {
        if (Workspace?.Kernel is not { } k || _visualKernelId != k.Id) return;
        for (var i = 0; i < k.Nodes.Count; i++)
        {
            var n = k.Nodes[i];
            if (!_nodeVisuals.TryGetValue(n.Id, out var visual)) continue;
            var p = Workspace.GetPosition(n);
            visual.Position = new(p.X, p.Y);
            visual.PreviewOffset = default;
            if (!visual.AnchorInitialized)
            {
                Canvas.SetLeft(visual.Border, p.X + _viewport.Origin.X);
                Canvas.SetTop(visual.Border, p.Y + _viewport.Origin.Y);
                visual.AnchorInitialized = true;
            }
            visual.SyncTransform();
        }
        _viewport.EnsureWorldBounds(NodeBounds(_nodeVisuals.Values));
        UpdateWireGeometry(_wireVisuals);
    }
    private void BeginDrag(NodeVisual visual, PointerPressedEventArgs e)
    {
        if (IsInteractiveSource(e.Source as Visual, visual.Border) ||
            !e.GetCurrentPoint(visual.Border).Properties.IsLeftButtonPressed) return;
        FinishDrag(commit: false);
        CancelConnectionDrag();
        Focus();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (!_selectedNodes.Add(visual.Model.Id)) _selectedNodes.Remove(visual.Model.Id);
            _selection = _selectedNodes.LastOrDefault();
            UpdateSelectionStyles(); BuildInspector(); e.Handled = true; return;
        }
        SelectVisualNode(visual.Model.Id, preserveGroup: true);
        // Coordinates belong to the stationary canvas, never the translating captured node.
        _drag = new(visual, e.Pointer, _viewport.ToGraphPoint(e.GetPosition(NodeCanvas)), e.GetPosition(this))
        { Members = _selectedNodes.Where(_nodeVisuals.ContainsKey)
            .Select(id => (_nodeVisuals[id], _nodeVisuals[id].Position)).ToArray() };
        e.Pointer.Capture(visual.Border);
        e.Handled = true;
    }
    private void MoveDrag(NodeVisual visual, PointerEventArgs e)
    {
        if (_drag is not { } drag || drag.Visual != visual || drag.Pointer != e.Pointer) return;
        UpdateDragPoint(drag, e);
        if (drag.IsDragging) ScheduleFrame();
        e.Handled = true;
    }
    private void ReleaseDrag(NodeVisual visual, PointerReleasedEventArgs e)
    {
        if (_drag is not { } drag || drag.Visual != visual || drag.Pointer != e.Pointer) return;
        UpdateDragPoint(drag, e);
        FinishDrag(commit: true);
        e.Handled = true;
    }
    private static bool IsInteractiveSource(Visual? source, Border node)
    {
        for (var current = source; current is not null && current != node; current = current.GetVisualParent())
            if (current is Avalonia.Controls.Button or TextBox or NumericUpDown or Slider or Avalonia.Controls.Primitives.SelectingItemsControl)
                return true;
        return false;
    }
    private void UpdateDragPoint(NodeDrag drag, PointerEventArgs e)
    {
        drag.Latest = _viewport.ToGraphPoint(e.GetPosition(NodeCanvas));
        var cursor = e.GetPosition(this);
        var dx = cursor.X - drag.StartView.X;
        var dy = cursor.Y - drag.StartView.Y;
        drag.IsDragging |= dx * dx + dy * dy >= 9;
    }
    private void ScheduleFrame()
    {
        if (_framePending) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) { OnRenderFrame(default); return; }
        _framePending = true;
        top.RequestAnimationFrame(_renderFrame);
    }
    private void OnRenderFrame(TimeSpan time)
    {
        _framePending = false;
        if (_anchorsDirty) UpdatePortAnchors();
        if (_drag is { } drag) ApplyDrag(drag);
        if (_connectionDrag is { } connection)
            connection.Preview.SetEndpoints(connection.Source.CanvasPosition + (Vector)connection.Source.OutputAnchor, connection.Latest);
    }
    private void UpdatePortAnchors()
    {
        _anchorsDirty = false;
        foreach (var visual in _nodeVisuals.Values)
        {
            visual.SyncTransform();
            visual.OutputAnchor = visual.Output.TranslatePoint(
                new Point(visual.Output.Bounds.Width / 2, visual.Output.Bounds.Height / 2), visual.Border) ?? new(230, 45);
            for (var i = 0; i < visual.Inputs.Length; i++)
                visual.InputAnchors[i] = visual.Inputs[i].TranslatePoint(
                    new Point(visual.Inputs[i].Bounds.Width / 2, visual.Inputs[i].Bounds.Height / 2), visual.Border) ?? new(0, 70 + i * 27);
        }
        UpdateWireGeometry(_wireVisuals);
    }
    private static void UpdateWireGeometry(List<WireVisual> wires)
    {
        foreach (var wire in wires)
        {
            wire.Curve.SetEndpoints(wire.Source.CanvasPosition + (Vector)wire.Source.OutputAnchor,
                wire.Target.CanvasPosition + (Vector)wire.Target.InputAnchors[wire.Input]);
        }
    }
    private void ApplyDrag(NodeDrag drag)
    {
        if (!drag.IsDragging) return;
        var delta = (Vector)(drag.Latest - drag.Start);
        foreach (var (visual, _) in drag.Members)
        {
            visual.PreviewOffset = delta;
            visual.SyncTransform();
            UpdateWireGeometry(visual.Wires);
        }
        _viewport.EnsureWorldBounds(NodeBounds(drag.Members.Select(m => m.Visual)));
    }
    private void FinishDrag(bool commit)
    {
        if (_drag is not { } drag) return;
        ApplyDrag(drag);
        var position = drag.Visual.Origin;
        _drag = null;
        drag.Pointer.Capture(null);
        if (commit && position != drag.Original && Workspace?.KernelId is { } kernel)
        {
            var edits = drag.Members.Select(m => new GpuEdit { Action = "move", KernelId = kernel,
                NodeId = m.Visual.Model.Id, Position = new(m.Visual.Origin.X, m.Visual.Origin.Y) }).ToArray();
            Run(() => Workspace.Edit(edits[0], edits[1..]));
            ApplySavedPositions();
        }
        else
        {
            foreach (var (visual, _) in drag.Members)
            {
                visual.PreviewOffset = default;
                visual.SyncTransform();
                UpdateWireGeometry(visual.Wires);
            }
        }
    }
    private void BeginConnectionDrag(NodeVisual source, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(source.Output).Properties.IsLeftButtonPressed) return;
        FinishDrag(commit: false);
        CancelConnectionDrag();
        SelectVisualNode(source.Model.Id);
        _wireSource = null;
        var preview = new GpuConnectionCurve
        {
            Width = NodeCanvas.Width, Height = NodeCanvas.Height,
            Stroke = TypeBrush(source.Model.Type), StrokeThickness = 2, StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 4 },
            IsHitTestVisible = false
        };
        _connectionDrag = new(source, e.Pointer, preview, e.GetPosition(NodeCanvas));
        NodeCanvas.Children.Insert(1, preview);
        e.Pointer.Capture(source.Output);
        ScheduleFrame();
        e.Handled = true;
    }
    private void ReleaseConnectionDrag(NodeVisual source, PointerReleasedEventArgs e)
    {
        if (_connectionDrag is not { } connection || connection.Source != source || connection.Pointer != e.Pointer) return;
        var point = e.GetPosition(NodeCanvas);
        NodeVisual? target = null;
        var input = -1;
        foreach (var candidate in _nodeVisuals.Values)
            for (var i = 0; i < candidate.Inputs.Length; i++)
            {
                var pin = candidate.Inputs[i];
                var local = NodeCanvas.TranslatePoint(point, pin);
                if (local is { } p && new Rect(-3, -3, pin.Bounds.Width + 6, pin.Bounds.Height + 6).Contains(p))
                { target = candidate; input = i; }
            }
        var clicked = Math.Abs(point.X - connection.Start.X) < 4 && Math.Abs(point.Y - connection.Start.Y) < 4;
        CancelConnectionDrag();
        if (target is { } targetNode && Workspace?.KernelId is { } kernel)
        {
            Run(() => Workspace.Edit(new() { Action = "connect", KernelId = kernel,
                NodeId = targetNode.Model.Id, SourceId = source.Model.Id, Input = input }));
            _wireSource = null;
        }
        else
        {
            _wireSource = clicked ? source.Model.Id : null;
            if (clicked) Status.Text = "Select a target input / 接続先を選択";
        }
        e.Handled = true;
    }
    private void CancelConnectionDrag()
    {
        if (_connectionDrag is not { } connection) return;
        _connectionDrag = null;
        NodeCanvas.Children.Remove(connection.Preview);
        connection.Pointer.Capture(null);
    }
    internal Point ToGraphPoint(Point point) => _viewport.ToGraphPoint(point);
    internal double DebugZoom => _viewport.Zoom;
    private static Rect NodeBounds(IEnumerable<NodeVisual> nodes)
    {
        var all = nodes.Select(n => new Rect(n.Origin,
            new Size(n.Border.Bounds.Width > 0 ? n.Border.Bounds.Width : 230,
                n.Border.Bounds.Height > 0 ? n.Border.Bounds.Height : 80))).ToArray();
        if (all.Length == 0) return new Rect(0, 0, 1, 1);
        var left = all.Min(r => r.Left); var top = all.Min(r => r.Top);
        return new Rect(left, top, all.Max(r => r.Right) - left, all.Max(r => r.Bottom) - top);
    }
    private void OnViewportChanged()
    {
        foreach (var visual in _nodeVisuals.Values) { visual.CanvasOrigin = _viewport.Origin; visual.SyncTransform(); }
        foreach (var wire in _wireVisuals) { wire.Curve.Width = NodeCanvas.Width; wire.Curve.Height = NodeCanvas.Height; }
        if (_connectionDrag is { } connection)
        { connection.Preview.Width = NodeCanvas.Width; connection.Preview.Height = NodeCanvas.Height; }
        UpdateWireGeometry(_wireVisuals);
        ZoomLabel.Content = $"{_viewport.Zoom:P0}";
    }
    private void OnZoomIn(object? sender, RoutedEventArgs e) => _viewport.SetZoom(_viewport.Zoom * 1.25);
    private void OnZoomOut(object? sender, RoutedEventArgs e) => _viewport.SetZoom(_viewport.Zoom / 1.25);
    private void OnResetZoom(object? sender, RoutedEventArgs e) => _viewport.SetZoom(1);
    private void OnArrange(object? sender, RoutedEventArgs e) => Run(() =>
    {
        FinishDrag(commit: false);
        Workspace!.ArrangeNodes();
        _viewport.Frame(NodeBounds(_nodeVisuals.Values));
    });
    private void OnFrame(object? sender, RoutedEventArgs e)
    {
        if (_selectedNodes.Count > 0)
            _viewport.Frame(NodeBounds(_selectedNodes.Where(_nodeVisuals.ContainsKey).Select(id => _nodeVisuals[id])));
    }
    private void OpenGraphContext(Point position)
    {
        if (Workspace is not { Kernel: not null } vm) return;
        Focus(); _pastePosition = position;
        var add = new MenuItem { Header = LocalizationService.Shared.Get("Gpu.AddNode") };
        add.ItemsSource = vm.Palette.Select(op =>
        {
            var item = new MenuItem { Header = op.DisplayName };
            item.Click += (_, _) => Run(() => SelectVisualNode(vm.Add(op.Op,
                TypeChoice.SelectedItem is GpuType type ? type : GpuType.Float, new(position.X, position.Y)).Id));
            return item;
        }).ToArray();
        var remove = new MenuItem { Header = LocalizationService.Shared.Get("Gpu.Delete"), IsEnabled = _selectedNodes.Count > 0 };
        remove.Click += (_, _) => DeleteSelection();
        var copy = new MenuItem { Header = LocalizationService.Shared.Get("Gpu.Copy"), IsEnabled = _selectedNodes.Count > 0 };
        copy.Click += async (_, _) => await CopySelectionAsync(false);
        var cut = new MenuItem { Header = LocalizationService.Shared.Get("Gpu.Cut"), IsEnabled = _selectedNodes.Count > 0 };
        cut.Click += async (_, _) => await CopySelectionAsync(true);
        var paste = new MenuItem { Header = LocalizationService.Shared.Get("Gpu.Paste") };
        paste.Click += async (_, _) => await PasteAsync();
        NodeCanvas.ContextMenu = new ContextMenu { ItemsSource = new Control[] { add, new Separator(), copy, cut, paste, remove } };
        NodeCanvas.ContextMenu.Open(NodeCanvas);
    }
    private void DeleteSelection()
    {
        if (Workspace?.KernelId is not { } kernel || _selectedNodes.Count == 0) return;
        var edits = _selectedNodes.Select(id => new GpuEdit { Action = "remove", KernelId = kernel, NodeId = id }).ToArray();
        Run(() => Workspace.Edit(edits[0], edits[1..]));
    }
    private async Task CopySelectionAsync(bool cut)
    {
        if (Workspace is not { } vm || _selectedNodes.Count == 0) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("Clipboard unavailable.");
            var text = vm.CopyNodes(_selectedNodes);
            var revision = vm.Graph.History.CurrentNodeId;
            var kernel = vm.KernelId;
            var ids = _selectedNodes.ToArray();
            await clipboard.SetTextAsync(text);
            if (cut && Workspace == vm && vm.KernelId == kernel && vm.Graph.History.CurrentNodeId == revision)
            {
                var edits = ids.Select(id => new GpuEdit { Action = "remove", KernelId = vm.KernelId, NodeId = id }).ToArray();
                Run(() => vm.Edit(edits[0], edits[1..]));
            }
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or ArgumentException)
        { Status.Text = error.Message; }
    }
    private async Task PasteAsync()
    {
        if (Workspace is not { } vm) return;
        var kernel = vm.KernelId;
        try
        {
            var text = await (TopLevel.GetTopLevel(this)?.Clipboard?.TryGetTextAsync() ?? Task.FromResult<string?>(null));
            if (Workspace != vm || vm.KernelId != kernel || text is null) return;
            var ids = vm.PasteNodes(text, new(_pastePosition.X, _pastePosition.Y));
            _selectedNodes.Clear(); _selectedNodes.UnionWith(ids); _selection = ids.FirstOrDefault();
            UpdateSelectionStyles(); BuildInspector();
            _pastePosition += new Vector(24, 24);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or ArgumentException or System.Text.Json.JsonException)
        { Status.Text = error.Message; }
    }
    private void OnBackgroundPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source != NodeCanvas || !e.GetCurrentPoint(NodeCanvas).Properties.IsLeftButtonPressed) return;
        Focus(); FinishDrag(false); CancelConnectionDrag(); CancelMarquee(); _wireSource = null;
        var start = _viewport.ToGraphPoint(e.GetPosition(NodeCanvas)); _pastePosition = start;
        var box = new Border { IsHitTestVisible = false, BorderThickness = new Thickness(1),
            BorderBrush = ThemeSettingsService.EditorBrush("NodeSelectedBorder"),
            Background = this.FindResource("EffectSelectionFillBrush") as IBrush };
        NodeCanvas.Children.Add(box);
        _marquee = (e.Pointer, start, e.KeyModifiers.HasFlag(KeyModifiers.Control) ? _selectedNodes.ToArray() : [], box);
        e.Pointer.Capture(NodeCanvas); e.Handled = true;
    }
    private void OnBackgroundMoved(object? sender, PointerEventArgs e)
    {
        if (_marquee is not { } marquee || marquee.Pointer != e.Pointer) return;
        var rect = SelectionRect(marquee.Start, _viewport.ToGraphPoint(e.GetPosition(NodeCanvas)));
        Canvas.SetLeft(marquee.Box, rect.X + _viewport.Origin.X); Canvas.SetTop(marquee.Box, rect.Y + _viewport.Origin.Y);
        marquee.Box.Width = rect.Width; marquee.Box.Height = rect.Height; e.Handled = true;
    }
    private void OnBackgroundReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_marquee is not { } marquee || marquee.Pointer != e.Pointer) return;
        var rect = SelectionRect(marquee.Start, _viewport.ToGraphPoint(e.GetPosition(NodeCanvas)));
        _selectedNodes.Clear(); _selectedNodes.UnionWith(marquee.Before);
        if (rect.Width >= 3 || rect.Height >= 3)
            _selectedNodes.UnionWith(_nodeVisuals.Values.Where(n => rect.Intersects(NodeBounds([n]))).Select(n => n.Model.Id));
        _selection = _selectedNodes.LastOrDefault();
        CancelMarquee(); UpdateSelectionStyles(); BuildInspector(); e.Handled = true;
    }
    private void CancelMarquee()
    {
        if (_marquee is not { } marquee) return;
        _marquee = null; NodeCanvas.Children.Remove(marquee.Box); marquee.Pointer.Capture(null);
    }
    private static Rect SelectionRect(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    private void BuildInspector()
    {
        NodeInspector.Children.Clear();
        var vm = Workspace!;
        var n = vm.Kernel?.Nodes.FirstOrDefault(n => n.Id == _selection);
        SelectionLabel.Text = n is null ? "Select a visual node / ノードを選択" : NodeDisplayName(n);
        ToolTip.SetTip(SelectionLabel, n?.Id);
        if (n is null) return;
        var type = new ComboBox { ItemsSource = Enum.GetValues<GpuType>().Where(t => t != GpuType.Quaternion).ToArray(), SelectedItem = n.Type };
        if (n.Op == GpuOp.SampleFlowMap)
            NodeInspector.Children.Add(new TextBlock { Text = LocalizationService.Shared.Get("Gpu.FlowMapTypeFromInput") });
        else
        {
            NodeInspector.Children.Add(new TextBlock { Text = "Result type / 出力型" });
            NodeInspector.Children.Add(type);
        }
        var value = new TextBox { Text = string.Join(", ", n.Value.Select(v => v.ToString("G", CultureInfo.InvariantCulture))) };
        if (n.Op == GpuOp.Constant) { NodeInspector.Children.Add(new TextBlock { Text = "Constant components / 定数" }); NodeInspector.Children.Add(value); }
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
        var component = new TextBox { Text = (n.ComponentIndex ?? 0).ToString(CultureInfo.InvariantCulture) };
        if (n.Op == GpuOp.Component) { NodeInspector.Children.Add(new TextBlock { Text = "Component 0…3" }); NodeInspector.Children.Add(component); }
        NodeInspector.Children.Add(Button("Apply properties / 適用", () =>
        {
            var updated = n with
            {
                Type = n.Op == GpuOp.SampleFlowMap ? n.Type : (GpuType)type.SelectedItem!,
                Value = n.Op == GpuOp.Constant
                    ? value.Text!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToList() : n.Value,
                Attribute = n.Op == GpuOp.ReadAttribute ? attr.SelectedItem as string : n.Attribute,
                Uniform = n.Op == GpuOp.ReadUniform ? uniform.SelectedItem as string : n.Uniform,
                ComponentIndex = n.Op == GpuOp.Component ? int.Parse(component.Text!, CultureInfo.InvariantCulture) : n.ComponentIndex,
                Resource = n.Op == GpuOp.SampleFlowMap ? flowMap.SelectedItem as string : n.Resource,
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
            var producer = k.Nodes.FirstOrDefault(node => node.Id == write.Value);
            var target = LocalizationService.Shared.Get("Gpu.Attribute." + write.Attribute);
            WritesPanel.Children.Add(new TextBlock
            {
                Text = $"@{target} : {attribute?.Type} ← {(producer is null ? write.Value : NodeDisplayName(producer))}",
                TextWrapping = TextWrapping.Wrap
            });
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
            BindingsPanel.Children.Add(Button(LocalizationService.Shared.Get("Gpu.ApplyRenderer"), () =>
                vm.Edit(new()
                {
                    Action = "renderer", NodeId = flow.Id, Material = presets[material.SelectedIndex],
                    SizeScale = double.Parse(scale.Text!, CultureInfo.InvariantCulture)
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
                                WorldMinimumU = double.Parse(minimumU.Text!, CultureInfo.InvariantCulture),
                                WorldMaximumU = double.Parse(maximumU.Text!, CultureInfo.InvariantCulture),
                                WorldMinimumV = double.Parse(minimumV.Text!, CultureInfo.InvariantCulture),
                                WorldMaximumV = double.Parse(maximumV.Text!, CultureInfo.InvariantCulture)
                            }
                        },
                        AssetPath = vm.State.ResourceAssetPaths.GetValueOrDefault(resource.Id)
                    })));
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
    private void OnCompile(object? sender, RoutedEventArgs e) => Workspace?.TryCompile(out _);
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
