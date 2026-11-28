using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;
using LiberaFx.Views.Gpu;
using LiberaFx.Views.Procedural;
using System.Text.Json;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls.Templates;
using Avalonia.Threading;

namespace LiberaFx.Views.Graph;

public sealed class GraphNodeViewTemplate : IDataTemplate
{
    private readonly ConditionalWeakTable<GraphNodeViewModel, GraphNodeView> _cache = new();

    public Control? Build(object? param)
    {
        if (param is not GraphNodeViewModel node) return null;
        if (_cache.TryGetValue(node, out var existing) && existing.Parent is null)
            return existing;
        var view = new GraphNodeView();
        _cache.AddOrUpdate(node, view);
        return view;
    }

    public bool Match(object? data) => data is GraphNodeViewModel;
}

public partial class GraphEditorView : UserControl
{
    private bool _isPanning;
    private bool _panMoved;
    private bool _panCanMove;
    private MouseButton _panButton;
    private Point _panStart;
    private Vector _panOffset;
    private GraphConnectionViewModel? _contextMenuConnection;
    private double _zoom = 1;
    private bool _updatingZoomSlider;
    private const double OriginStep = 2400;
    private Vector _graphOrigin = new(OriginStep, OriginStep);
    public static readonly DirectProperty<GraphEditorView, double> OriginXProperty =
        AvaloniaProperty.RegisterDirect<GraphEditorView, double>(nameof(OriginX), view => view.OriginX);
    public static readonly DirectProperty<GraphEditorView, double> OriginYProperty =
        AvaloniaProperty.RegisterDirect<GraphEditorView, double>(nameof(OriginY), view => view.OriginY);
    public double OriginX => _graphOrigin.X;
    public double OriginY => _graphOrigin.Y;
    private readonly ScaleTransform _graphScale = new(1, 1);
    private bool _viewportInitialized;
    private bool _isSelecting;
    private Point _selectionStart;
    private GraphNodeViewModel[] _selectionBefore = [];
    private GraphConnectionViewModel[] _wireSelectionBefore = [];
    private Point _pastePosition = new(80, 80);
    private Point? _lastGraphPointer;
    private Point _nodeSearchPosition;
    private GraphWorkspaceViewModel? _observedGraph;
    private readonly HashSet<GraphNodeViewModel> _observedNodes = [];
    private const string ClipboardPrefix = "LIBERA-FX-GRAPH-1\n";
    public GraphEditorView()
    {
        InitializeComponent();
        GraphCanvas.RenderTransform = _graphScale;
        SceneLayer.GraphOrigin = _graphOrigin;
        ConnectionLayer.GraphOrigin = _graphOrigin;
        GraphScrollViewer.AddHandler(InputElement.PointerWheelChangedEvent, OnViewportPointerWheelChanged,
            RoutingStrategies.Tunnel);
        GraphScrollViewer.AddHandler(Control.RequestBringIntoViewEvent, (_, e) => e.Handled = true,
            RoutingStrategies.Tunnel);
        AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (OwnsInput(e.Source) && DataContext is GraphWorkspaceViewModel graph)
            {
                graph.ActivateEditingWorkspace();
                var point = e.GetCurrentPoint(GraphCanvas);
                var hitNode = FindHitNode(e.Source);
                if (!graph.IsReadOnly && point.Properties.IsRightButtonPressed && hitNode?.IsBlankCommentArea(e) == true)
                {
                    Focus();
                    _pastePosition = ToGraphPoint(e.GetPosition(GraphCanvas));
                    _contextMenuConnection = GraphSceneLayer.HitTestConnection(graph, _pastePosition, WireHitTolerance());
                    NewGraphGroupMenu.IsVisible = graph.CanCreateNode(GraphNodeKind.GraphGroup, out string? _);
                    NewSpawnableGroupMenu.IsVisible = true;
                    AddRerouteMenu.IsVisible = _contextMenuConnection is not null || graph.SelectedConnections.Count > 0;
                    BeginViewportPan(e, MouseButton.Right,
                        ShortcutService.Shared.GraphPanButton is "Right" or "Right + Middle");
                }
                else if (point.Properties.IsLeftButtonPressed && hitNode?.ShouldFocusGraphOnBlankComment(e) == true)
                {
                    Focus();
                    _lastGraphPointer = ToGraphPoint(point.Position);
                    e.Handled = true;
                }
            }
        }, RoutingStrategies.Tunnel);
        // A wheel reaching an inline scroll limit must not scroll/zoom the outer editor.
        AddHandler(InputElement.PointerWheelChangedEvent, (_, e) =>
        {
            if (DataContext is GraphWorkspaceViewModel { IsInlineWorkspace: true } && OwnsInput(e.Source))
                e.Handled = true;
        }, RoutingStrategies.Bubble, true);
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(InitializeViewport);
        AttachedToVisualTree += (_, _) => LocalizationService.Shared.LanguageChanged += OnLanguageChanged;
        DetachedFromVisualTree += (_, _) => LocalizationService.Shared.LanguageChanged -= OnLanguageChanged;
        AttachedToVisualTree += (_, _) => ObserveDefinitionChanges(true);
        DetachedFromVisualTree += (_, _) => ObserveDefinitionChanges(false);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_specializedNodeId is not null) RefreshSpecializedBreadcrumbs(sender, e);
        _searchEntries = null;
        if (NodeSearchPopup.IsOpen) RefreshNodeSearch();
    }

    private void OnNodeDefinitionsChanged(object? sender, EventArgs e)
    {
        _searchEntries = null;
        if (NodeSearchPopup.IsOpen) RefreshNodeSearch();
    }

    private void ObserveDefinitionChanges(bool subscribe)
    {
        ObserveSpecializedWorkspaces(subscribe);
        if (_observedGraph is not null)
        {
            _observedGraph.FrameSelectionRequested -= OnFrameSelectionRequested;
            _observedGraph.HierarchyChanging -= OnHierarchyChanging;
            _observedGraph.HierarchyChanged -= OnHierarchyChanged;
            if (subscribe)
            {
                _observedGraph.FrameSelectionRequested += OnFrameSelectionRequested;
                _observedGraph.HierarchyChanging += OnHierarchyChanging;
                _observedGraph.HierarchyChanged += OnHierarchyChanged;
            }
        }
        if (_observedGraph?.NodeDefinitions is not { } definitions) return;
        definitions.Changed -= OnNodeDefinitionsChanged;
        if (subscribe) definitions.Changed += OnNodeDefinitionsChanged;
    }

    private void OnFrameSelectionRequested(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(DataContext, sender) || TopLevel.GetTopLevel(this) is null) return;
            InitializeViewport();
            FrameSelection();
        }, DispatcherPriority.Loaded);

    internal Point ToGraphPoint(Point canvasPoint) =>
        new(canvasPoint.X - _graphOrigin.X, canvasPoint.Y - _graphOrigin.Y);

    internal Point? GraphPointToOverlay(Point point, GraphInteractionOverlay overlay) =>
        GraphCanvas.TranslatePoint(new Point(point.X + _graphOrigin.X, point.Y + _graphOrigin.Y), overlay);

    internal void ShowInteractionWires(GraphWorkspaceViewModel graph,
        IEnumerable<GraphConnectionViewModel> connections, bool pending = false)
    {
        var root = this.GetSelfAndVisualAncestors().OfType<GraphEditorView>().Last();
        root.InteractionOverlay.Show(this, graph, connections, pending);
    }

    internal void ClearInteractionWires()
    {
        var root = this.GetSelfAndVisualAncestors().OfType<GraphEditorView>().LastOrDefault();
        root?.InteractionOverlay.Clear(this);
    }

    private double WireHitTolerance()
    {
        var root = TopLevel.GetTopLevel(this);
        if (root is not null && GraphCanvas.TranslatePoint(new Point(0, 0), root) is { } origin &&
            GraphCanvas.TranslatePoint(new Point(1, 0), root) is { } unit)
        {
            return 10.0 / Math.Max(0.001, ((Vector)(unit - origin)).Length);
        }
        return 10.0 / Math.Max(0.001, _zoom);
    }

    internal double DebugZoom => _zoom;
    private bool OwnsInput(object? source)
    {
        if (source is not Visual visual) return true;
        foreach (var ancestor in visual.GetSelfAndVisualAncestors())
        {
            if (ancestor is ProceduralWorkspaceView or GpuWorkspaceView) return false;
            if (ancestor is GraphEditorView editor) return ReferenceEquals(editor, this);
        }
        return true;
    }

    private void OnHierarchyChanging(object? sender, string key)
    {
        if (sender is GraphWorkspaceViewModel graph)
            SaveCameraState(graph, key);
    }

    private void OnHierarchyChanged(object? sender, string key)
    {
        if (sender is not GraphWorkspaceViewModel graph) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(DataContext, graph) || TopLevel.GetTopLevel(this) is null) return;
            RestoreCameraState(graph, key);
        }, DispatcherPriority.Loaded);
    }

    private void SaveCameraState(GraphWorkspaceViewModel graph, string key)
    {
        if (!_viewportInitialized || GraphScrollViewer is null || GraphScrollViewer.Viewport.Width <= 0) return;
        graph.CameraStates[key] = new GraphWorkspaceViewModel.GraphCameraState(
            _zoom,
            GraphScrollViewer.Offset.X - _graphOrigin.X * _zoom,
            GraphScrollViewer.Offset.Y - _graphOrigin.Y * _zoom);
    }

    private void RestoreCameraState(GraphWorkspaceViewModel graph, string key)
    {
        InitializeViewport();
        UpdateCanvasExtent();
        if (graph.CameraStates.TryGetValue(key, out var state))
        {
            _zoom = Math.Clamp(state.Zoom, 0.05, 4);
            UpdateZoomExtent();
            UpdateSceneViewport();
            var offset = new Vector(_graphOrigin.X * _zoom + state.RelativeOffsetX,
                                    _graphOrigin.Y * _zoom + state.RelativeOffsetY);
            EnsureViewportExtent(offset);
            GraphScrollViewer.UpdateLayout();
            GraphScrollViewer.Offset = offset;
        }
        else
        {
            _zoom = 1;
            UpdateZoomExtent();
            UpdateSceneViewport();
            GraphScrollViewer.UpdateLayout();
            GraphScrollViewer.Offset = _graphOrigin * _zoom;
        }
        _updatingZoomSlider = true;
        ZoomSlider.Value = Math.Log2(_zoom);
        _updatingZoomSlider = false;
        ZoomText.Text = $"{_zoom:P0}";
    }

    private void InitializeViewport()
    {
        if (_viewportInitialized) return;
        _viewportInitialized = true;
        UpdateCanvasExtent();
        GraphScrollViewer.UpdateLayout();
        if (_observedGraph is { } graph && graph.CameraStates.TryGetValue(graph.CurrentHierarchyKey, out var state))
        {
            RestoreCameraState(graph, graph.CurrentHierarchyKey);
            return;
        }
        GraphScrollViewer.Offset = _graphOrigin * _zoom;
        UpdateSceneViewport();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_observedGraph is not null)
        {
            SaveCameraState(_observedGraph, _observedGraph.CurrentHierarchyKey);
            _observedGraph.Nodes.CollectionChanged -= OnNodesChanged;
        }
        ObserveDefinitionChanges(false);
        foreach (var node in _observedNodes) node.PropertyChanged -= OnNodePositionChanged;
        _observedNodes.Clear();
        _observedGraph = DataContext as GraphWorkspaceViewModel;
        if (_observedGraph is not null) _observedGraph.Nodes.CollectionChanged += OnNodesChanged;
        ObserveDefinitionChanges(TopLevel.GetTopLevel(this) is not null);
        SynchronizeNodes();
        if (_observedGraph is not null && _viewportInitialized && TopLevel.GetTopLevel(this) is not null)
        {
            var graph = _observedGraph;
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(DataContext, graph) || TopLevel.GetTopLevel(this) is null) return;
                RestoreCameraState(graph, graph.CurrentHierarchyKey);
            }, DispatcherPriority.Loaded);
        }
        base.OnDataContextChanged(e);
    }

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e) => SynchronizeNodes();

    private void SynchronizeNodes()
    {
        if (_specializedNodeId is { } id && _observedGraph?.Nodes.Any(node => node.Id == id) != true)
            CloseSpecializedWorkspace();
        var current = _observedGraph?.Nodes.ToHashSet() ?? [];
        foreach (var node in _observedNodes.Except(current).ToArray())
        {
            node.PropertyChanged -= OnNodePositionChanged;
            _observedNodes.Remove(node);
        }
        foreach (var node in current.Except(_observedNodes))
        {
            node.PropertyChanged += OnNodePositionChanged;
            _observedNodes.Add(node);
        }
        UpdateCanvasExtent();
    }

    private void OnNodePositionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GraphNodeViewModel.DisplayX) or nameof(GraphNodeViewModel.DisplayY) or
            nameof(GraphNodeViewModel.RenderedWidth) or nameof(GraphNodeViewModel.CommentHeight))
            UpdateCanvasExtent();
    }

    internal void UpdateCanvasExtent()
    {
        if (GraphCanvas is null) return;
        var minX = _observedNodes.Select(n => n.DisplayX).DefaultIfEmpty(0).Min();
        var minY = _observedNodes.Select(n => n.DisplayY).DefaultIfEmpty(0).Min();
        var shiftX = Math.Max(0, Math.Ceiling((-minX - _graphOrigin.X + 200) / OriginStep) * OriginStep);
        var shiftY = Math.Max(0, Math.Ceiling((-minY - _graphOrigin.Y + 200) / OriginStep) * OriginStep);
        if (shiftX > 0 || shiftY > 0) ShiftOrigin(shiftX, shiftY);
        GraphCanvas.Width = Math.Max(GraphCanvas.Width,
            _graphOrigin.X + Math.Max(1600, _observedNodes.Select(n => n.DisplayX + n.RenderedWidth + 200).DefaultIfEmpty(1600).Max()));
        GraphCanvas.Height = Math.Max(GraphCanvas.Height,
            _graphOrigin.Y + Math.Max(1000, _observedNodes.Select(n => n.DisplayY + n.RenderedHeight + 200).DefaultIfEmpty(1000).Max()));
        UpdateZoomExtent();
    }

    private void UpdateZoomExtent()
    {
        ZoomHost.Width = GraphCanvas.Width * _zoom;
        ZoomHost.Height = GraphCanvas.Height * _zoom;
    }

    private void ShiftOrigin(double x, double y)
    {
        if (x == 0 && y == 0) return;
        var oldOrigin = _graphOrigin;
        _graphOrigin += new Vector(x, y);
        GraphCanvas.Width += x;
        GraphCanvas.Height += y;
        UpdateZoomExtent();
        RaisePropertyChanged(OriginXProperty, oldOrigin.X, _graphOrigin.X);
        RaisePropertyChanged(OriginYProperty, oldOrigin.Y, _graphOrigin.Y);
        SceneLayer.GraphOrigin = _graphOrigin;
        ConnectionLayer.GraphOrigin = _graphOrigin;
        if (_viewportInitialized)
        {
            var delta = new Vector(x * _zoom, y * _zoom);
            _panOffset += delta;
            GraphScrollViewer.UpdateLayout();
            GraphScrollViewer.Offset += delta;
        }
    }

}
