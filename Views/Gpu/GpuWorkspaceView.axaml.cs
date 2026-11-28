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
using LiberaFx.Services.Compilation;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;
using LiberaFx.ViewModels.Panels;
using LiberaFx.Views.Graph;

namespace LiberaFx.Views.Gpu;

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
        TypeChoice.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<GpuType>((t, _) => new TextBlock { Text = GpuTypeLabel(t) });
        TypeChoice.SelectedItem = GpuType.Float;
        ExportFormat.ItemsSource = new[] { "json", "usf", "hlsl", "compute", "resources" };
        ExportFormat.SelectedItem = "usf";
        GpuInspectorContainer.Child = null;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty) SyncExternalInspector(IsVisible);
        };
    }
    private ActiveGraphProvider? _inspectorProvider;
    private void SyncExternalInspector(bool attach)
    {
        if (attach && TopLevel.GetTopLevel(this)?.DataContext is LiberaFx.ViewModels.Main.MainViewModel main)
            _inspectorProvider = main.ActiveGraphProvider;
        if (_inspectorProvider is null) return;
        if (attach && IsVisible && Workspace is not null)
        {
            _inspectorProvider.ActiveGpuInspector = GpuInspectorHost;
        }
        else if (ReferenceEquals(_inspectorProvider.ActiveGpuInspector, GpuInspectorHost))
        {
            _inspectorProvider.ActiveGpuInspector = null;
        }
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeSettingsService.EditorColorsChanged += OnAppearanceChanged;
        LocalizationService.Shared.LanguageChanged += OnAppearanceChanged;
        SyncExternalInspector(true);
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SyncExternalInspector(false);
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
        SyncExternalInspector(true);
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
        SyncExternalInspector(false);
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
            TypeChoiceLabel.IsVisible = TypeChoice.IsVisible = !fluid && vm.Kernel is not null;
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
                    if (op.Op is GpuOp.ReadAttribute or GpuOp.ReadUniform) continue;
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
}
