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
    public void UseDocumentNavigation()
    {
        BackButton.IsVisible = Breadcrumb.IsVisible = false;
    }
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
    private Button? _paletteDragSource;
    private PointerPressedEventArgs? _palettePressed;
    private Point _paletteDragStart;
    private NodeDrag? _drag;
    private ConnectionDrag? _connectionDrag;
    private readonly PortSnapFeedback _connectionFeedback = new();
    private bool _framePending;
    private bool _anchorsDirty;
    private readonly Action<TimeSpan> _renderFrame;

    private sealed class NodeVisual(GpuNode model, Border border, TextBlock title, Button output, Button[] inputs,
        TextBlock outputLabel, TextBlock destinationLabel, TextBlock[] inputLabels)
    {
        public GpuNode Model = model;
        public readonly Border Border = border;
        public readonly TextBlock Title = title;
        public readonly Button Output = output;
        public readonly Button[] Inputs = inputs;
        public readonly TextBlock OutputLabel = outputLabel;
        public readonly TextBlock DestinationLabel = destinationLabel;
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
        public IReadOnlySet<(string Node, int Input)> Compatible = new HashSet<(string, int)>();
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
        ExportFormat.ItemsSource = new[] { "json", "usf", "hlsl", "compute", "resources" };
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
            var fluid = vm.State.Fluid2D is not null;
            if (vm.State.Fluid2D is { } settings)
            {
                Capacity.Text = ((ulong)settings.Width * settings.Height).ToString(CultureInfo.InvariantCulture);
            }
            CapacityLabel.Text = LocalizationService.Shared.Get(fluid ? "Fluid.CellCount" : "Gpu.Capacity");
            Capacity.IsEnabled = ApplyCapacityButton.IsEnabled = !fluid;
            TypeChoice.IsVisible = !fluid && vm.Kernel is not null;
            FutureTextureButton.IsVisible = !fluid;
            BackButton.Content = LocalizationService.Shared.Get(vm.Kernel is null ? "Gpu.Close" : "Gpu.FlowBack");
            FlowScroll.IsVisible = fluid || vm.Kernel is null;
            KernelTabs.IsVisible = !fluid && vm.Kernel is not null;
            Palette.Children.Clear();
            AddSimulationModeControl(vm);
            if (fluid)
            {
                BuildFluidFlow();
            }
            else if (vm.Kernel is null)
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
                {
                    var opButton = Button("+ " + OperationDisplayName(op.Op), () => AddPaletteNode(vm, op.Op));
                    ToolTip.SetTip(opButton, OpDescription(op.Op));
                    AttachPaletteDrag(opButton, op.Op);
                    Palette.Children.Add(opButton);
                }
                Palette.Children.Add(Button(LocalizationService.Shared.Get("Gpu.AddAttributeWrite"), () =>
                {
                    Status.Text = LocalizationService.Shared.Get("Gpu.SelectValueNodeHint");
                }));
                BuildGraph();
                _displayedText = vm.Text;
                KernelText.Text = _displayedText;
            }
            if (fluid)
            {
                BuildFluidInspector();
            }
            else
            {
                BuildInspector();
                BuildWrites();
                BuildBindings();
            }
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
                ToolTip.SetTip(title, OpDescription(n.Op));
                panel.Children.Add(title);
                var output = PortButton("gpu-output");
                var outputLabel = PortLabel();
                outputLabel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
                var outputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                outputRow.Background = Brushes.Transparent;
                outputRow.Children.Add(outputLabel);
                Grid.SetColumn(output, 1);
                outputRow.Children.Add(output);
                panel.Children.Add(outputRow);
                var destinationLabel = PortLabel();
                destinationLabel.Margin = new Thickness(4, 0, 4, 0);
                panel.Children.Add(destinationLabel);
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
                var visual = new NodeVisual(n, border, title, output, inputs, outputLabel, destinationLabel, inputLabels)
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
        var destinations = _wireVisuals.ToLookup(wire => wire.Source.Model.Id);
        var modelsById = k.Nodes.ToDictionary(node => node.Id);
        var writes = k.Writes.ToLookup(write => write.Value);
        foreach (var n in k.Nodes)
        {
            var visual = _nodeVisuals[n.Id];
            visual.Model = n;
            visual.Title.Text = NodeDisplayName(n);
            var targets = destinations[n.Id].Select(wire =>
            {
                var target = modelsById[wire.Target.Model.Id];
                var input = GpuOperationRegistry.GetDefaultInputs(target.Op, target.Type).ElementAtOrDefault(wire.Input);
                return $"{NodeDisplayName(target)} / {InputDisplayName(input?.Id ?? "input")}";
            }).Concat(writes[n.Id].Select(write =>
                LocalizationService.Shared.Get("Gpu.OutputAttributeTarget", AttributeDisplayName(write.Attribute)))).ToArray();
            var outputText = $"{OutputDisplayName(n)} : {n.Type}";
            visual.OutputLabel.Text = outputText;
            visual.DestinationLabel.IsVisible = targets.Length != 0;
            visual.DestinationLabel.Text = targets.Length switch
            {
                0 => "",
                1 => $"→ {targets[0]}",
                _ => $"→ {LocalizationService.Shared.Get("Gpu.OutputTargetCount", targets.Length)}"
            };
            var outputDetails = targets.Length == 0 ? outputText : outputText + Environment.NewLine +
                string.Join(Environment.NewLine, targets.Select(target => $"→ {target}"));
            ToolTip.SetTip(visual.Output, outputDetails);
            if (visual.OutputLabel.Parent is Control outputRow) ToolTip.SetTip(outputRow, outputDetails);
            Avalonia.Automation.AutomationProperties.SetName(visual.Output, outputDetails);
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
            GpuOp.ReadAttribute => LocalizationService.Shared.GetNodeLabel("Gpu.ReadAttribute", "Read Attribute") + " · " +
                AttributeDisplayName(node.Attribute),
            GpuOp.ReadUniform => LocalizationService.Shared.GetNodeLabel("Gpu.ReadUniform", "Read Uniform") + " · " +
                UniformDisplayName(node.Uniform),
            GpuOp.SampleFlowMap => GpuOperationRegistry.Operations[node.Op].DisplayName + " · " + node.Resource,
            _ => OperationDisplayName(node.Op)
        };
        return $"{operation} : {node.Type}";
    }
    private static string OperationDisplayName(GpuOp operation) => operation is GpuOp.GreaterThan or GpuOp.LessThan
        ? LocalizationService.Shared.Get("Fluid.Operation." + operation) : GpuOperationRegistry.Operations[operation].DisplayName;
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
        {
            var target = FindConnectionTarget(connection, connection.Latest);
            _connectionFeedback.Show(target?.Node.Inputs[target.Value.Input]);
            var end = target is { } snap
                ? snap.Node.CanvasPosition + (Vector)snap.Node.InputAnchors[snap.Input] : connection.Latest;
            connection.Preview.SetEndpoints(connection.Source.CanvasPosition + (Vector)connection.Source.OutputAnchor, end);
        }
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
        _connectionDrag = new(source, e.Pointer, preview, e.GetPosition(NodeCanvas))
        { Compatible = Workspace!.CompatibleInputs(source.Model.Id) };
        NodeCanvas.Children.Insert(1, preview);
        e.Pointer.Capture(source.Output);
        ScheduleFrame();
        e.Handled = true;
    }
    private void ReleaseConnectionDrag(NodeVisual source, PointerReleasedEventArgs e)
    {
        if (_connectionDrag is not { } connection || connection.Source != source || connection.Pointer != e.Pointer) return;
        var point = e.GetPosition(NodeCanvas);
        var target = FindConnectionTarget(connection, point);
        var clicked = Math.Abs(point.X - connection.Start.X) < 4 && Math.Abs(point.Y - connection.Start.Y) < 4;
        CancelConnectionDrag();
        if (target is { } snap && Workspace?.KernelId is { } kernel)
        {
            Run(() => Workspace.Edit(new() { Action = "connect", KernelId = kernel,
                NodeId = snap.Node.Model.Id, SourceId = source.Model.Id, Input = snap.Input }));
            _wireSource = null;
        }
        else
        {
            _wireSource = clicked ? source.Model.Id : null;
            if (clicked) Status.Text = LocalizationService.Shared.Get("Gpu.SelectTargetInput");
        }
        e.Handled = true;
    }
    private void CancelConnectionDrag()
    {
        _connectionFeedback.Dispose();
        if (_connectionDrag is not { } connection) return;
        _connectionDrag = null;
        NodeCanvas.Children.Remove(connection.Preview);
        connection.Pointer.Capture(null);
    }
    private (NodeVisual Node, int Input)? FindConnectionTarget(ConnectionDrag connection, Point point)
    {
        var root = TopLevel.GetTopLevel(this);
        if (root is null || NodeCanvas.TranslatePoint(point, root) is not { } pointer) return null;
        (NodeVisual Node, int Input)? best = null;
        var distance = PortSnapFeedback.AcquireRadius;
        foreach (var (id, input) in connection.Compatible)
        {
            if (!_nodeVisuals.TryGetValue(id, out var visual) || input >= visual.Inputs.Length) continue;
            var pin = visual.Inputs[input];
            if (!pin.IsEffectivelyVisible || pin.TranslatePoint(new Point(pin.Bounds.Width / 2, pin.Bounds.Height / 2), root) is not { } center) continue;
            var next = ((Vector)(center - pointer)).Length;
            if (next <= distance || best is null && ReferenceEquals(pin, _connectionFeedback.Pin) && next <= PortSnapFeedback.ReleaseRadius)
            { best = (visual, input); distance = next; }
        }
        return best;
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
    private void OnFrameAll(object? sender, RoutedEventArgs e)
    {
        if (_nodeVisuals.Count > 0)
            _viewport.Frame(NodeBounds(_nodeVisuals.Values));
    }
    private static string OpDescription(GpuOp op) =>
        LocalizationService.Shared.GetOrDefault($"Gpu.OpDesc.{op}", op.ToString());

    private void AddPaletteNode(GpuWorkspaceViewModel vm, GpuOp op, GpuPosition? position = null) => Run(() =>
    {
        var type = TypeChoice.SelectedItem is GpuType selected ? selected : GpuType.Float;
        if (op == GpuOp.ReadUniform && !ParticleAbi.Uniforms.Values.Contains(type))
            throw new ArgumentException("Uniforms support Float and Float3.");
        if (op == GpuOp.ReadAttribute && !ParticleAbi.Attributes.Any(a => a.Type == type))
            throw new ArgumentException("No ABI attribute has this type.");
        SelectVisualNode(vm.Add(op, type, position).Id);
    });

    private void AttachPaletteDrag(Button button, GpuOp op)
    {
        button.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
            _paletteDragSource = button;
            _palettePressed = e;
            _paletteDragStart = e.GetPosition(this);
        }, RoutingStrategies.Tunnel);

        button.AddHandler(InputElement.PointerMovedEvent, async (_, e) =>
        {
            if (_paletteDragSource != button || _palettePressed is not { } pressed ||
                !e.GetCurrentPoint(button).Properties.IsLeftButtonPressed)
                return;
            var current = e.GetPosition(this);
            if (Math.Abs(current.X - _paletteDragStart.X) < 5 && Math.Abs(current.Y - _paletteDragStart.Y) < 5)
                return;
            _paletteDragSource = null;
            _palettePressed = null;
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText("gpu-op:" + op));
            await DragDrop.DoDragDropAsync(pressed, transfer, DragDropEffects.Copy);
        }, RoutingStrategies.Tunnel);

        button.AddHandler(InputElement.PointerReleasedEvent, (_, _) =>
        {
            if (_paletteDragSource == button) { _paletteDragSource = null; _palettePressed = null; }
        }, RoutingStrategies.Tunnel);

        button.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) =>
        {
            if (_paletteDragSource == button) { _paletteDragSource = null; _palettePressed = null; }
        }, RoutingStrategies.Tunnel);
    }

    private void OnNodeCanvasDragOver(object? sender, DragEventArgs e)
    {
        if (Workspace is { Kernel: not null } &&
            e.DataTransfer.TryGetText() is { } text &&
            text.StartsWith("gpu-op:", StringComparison.Ordinal))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnNodeCanvasDrop(object? sender, DragEventArgs e)
    {
        if (Workspace is not { Kernel: not null } vm ||
            e.DataTransfer.TryGetText() is not { } text ||
            !text.StartsWith("gpu-op:", StringComparison.Ordinal) ||
            !Enum.TryParse<GpuOp>(text["gpu-op:".Length..], out var op))
            return;

        var point = _viewport.ToGraphPoint(e.GetPosition(NodeCanvas));
        AddPaletteNode(vm, op, new(point.X, point.Y));
        e.Handled = true;
    }
    private void OpenGraphContext(Point position)
    {
        if (Workspace is not { Kernel: not null } vm) return;
        Focus(); _pastePosition = position;
        var add = new MenuItem { Header = LocalizationService.Shared.Get("Gpu.AddNode") };
        add.ItemsSource = vm.Palette.Select(op =>
        {
            var item = new MenuItem { Header = op.DisplayName };
            ToolTip.SetTip(item, OpDescription(op.Op));
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
        SelectionLabel.Text = n is null ? LocalizationService.Shared.Get("Gpu.SelectVisualNode") : NodeDisplayName(n);
        ToolTip.SetTip(SelectionLabel, n?.Id);
        if (n is null) return;
        var type = new ComboBox { ItemsSource = Enum.GetValues<GpuType>().Where(t => t != GpuType.Quaternion).ToArray(), SelectedItem = n.Type };
        if (n.Op == GpuOp.SampleFlowMap)
            NodeInspector.Children.Add(new TextBlock { Text = LocalizationService.Shared.Get("Gpu.FlowMapTypeFromInput") });
        else
        {
            NodeInspector.Children.Add(new TextBlock { Text = LocalizationService.Shared.Get("Gpu.ResultType") });
            NodeInspector.Children.Add(type);
        }
        var value = new TextBox { Text = string.Join(", ", n.Value.Select(v => v.ToString("G", CultureInfo.InvariantCulture))) };
        if (n.Op == GpuOp.Constant) { NodeInspector.Children.Add(new TextBlock { Text = LocalizationService.Shared.Get("Gpu.ConstantComponents") }); NodeInspector.Children.Add(value); }
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
            if (updated != n)
                vm.Edit(new() { Action = "update", KernelId = vm.KernelId, Node = updated });
        });

        type.SelectionChanged += (_, _) => CommitInspector();
        attr.SelectionChanged += (_, _) => CommitInspector();
        uniform.SelectionChanged += (_, _) => CommitInspector();
        flowMap.SelectionChanged += (_, _) => CommitInspector();
        value.LostFocus += (_, _) => CommitInspector();
        value.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitInspector(); e.Handled = true; } };
        component.LostFocus += (_, _) => CommitInspector();
        component.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitInspector(); e.Handled = true; } };

        NodeInspector.Children.Add(Button(LocalizationService.Shared.Get("Gpu.ApplyProperties"), CommitInspector));
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
        WritesPanel.Children.Add(Button(LocalizationService.Shared.Get("Gpu.WriteSelectedNode"), () =>
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
        BindingsPanel.Children.Add(new TextBlock { Text = LocalizationService.Shared.Get("Gpu.FormatManaged"), TextWrapping = TextWrapping.Wrap });
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
