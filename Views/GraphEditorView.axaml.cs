using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using LiberaFx.Models.Graph;
using LiberaFx.ViewModels;
using LiberaFx.Services;
using System.Text.Json;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Threading;

namespace LiberaFx.Views;

public partial class GraphEditorView : UserControl
{
    private bool _isPanning;
    private bool _panMoved;
    private bool _panCanMove;
    private MouseButton _panButton;
    private Point _panStart;
    private Vector _panOffset;
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
                    NewSpawnableGroupMenu.IsVisible = true;
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
            if (subscribe) _observedGraph.FrameSelectionRequested += OnFrameSelectionRequested;
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

    internal double DebugZoom => _zoom;
    private bool OwnsInput(object? source) =>
        source is not Visual visual ||
        visual.GetSelfAndVisualAncestors().OfType<GraphEditorView>().FirstOrDefault() is not { } editor ||
        ReferenceEquals(editor, this);

    private void InitializeViewport()
    {
        if (_viewportInitialized) return;
        _viewportInitialized = true;
        UpdateCanvasExtent();
        GraphScrollViewer.UpdateLayout();
        GraphScrollViewer.Offset = _graphOrigin * _zoom;
        UpdateSceneViewport();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        ObserveDefinitionChanges(false);
        if (_observedGraph is not null) _observedGraph.Nodes.CollectionChanged -= OnNodesChanged;
        foreach (var node in _observedNodes) node.PropertyChanged -= OnNodePositionChanged;
        _observedNodes.Clear();
        _observedGraph = DataContext as GraphWorkspaceViewModel;
        if (_observedGraph is not null) _observedGraph.Nodes.CollectionChanged += OnNodesChanged;
        ObserveDefinitionChanges(TopLevel.GetTopLevel(this) is not null);
        SynchronizeNodes();
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

    private void OnGraphCanvasDragOver(object? sender, DragEventArgs e)
    {
        if (!OwnsInput(e.Source)) return;
        e.DragEffects = TryGetNodeKind(e, out _) || TryGetGroupId(e, out _)
            ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnGraphCanvasDrop(object? sender, DragEventArgs e)
    {
        if (!OwnsInput(e.Source)) return;
        if (DataContext is not GraphWorkspaceViewModel graph) return;

        var position = ToGraphPoint(e.GetPosition(GraphCanvas));
        try
        {
            if (FindHitNode(e.Source)?.DataContext is GraphNodeViewModel { ChildGraph: not null } owner)
            {
                graph = graph.OpenInlineGraph(owner);
                position = new Point(80, 80);
            }
            if (TryGetGroupId(e, out var assetId)) graph.CreateGroupAssetNode(assetId, position.X, position.Y);
            else if (TryGetNodeKind(e, out var kind)) graph.CreateNode(kind, position.X, position.Y,
                e.DataTransfer.TryGetText()?.StartsWith(NodeCatalogEntryViewModel.PolymorphicPrefix, StringComparison.Ordinal) == true);
            else return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   InvalidOperationException or ArgumentException)
        {
            graph.ReportStatus(ex.Message);
        }
        e.Handled = true;
    }

    private bool TryGetNodeKind(DragEventArgs e, out GraphNodeKind kind)
    {
        var kindName = e.DataTransfer.TryGetText();
        if (kindName?.StartsWith(NodeCatalogEntryViewModel.PolymorphicPrefix, StringComparison.Ordinal) == true)
            kindName = kindName[NodeCatalogEntryViewModel.PolymorphicPrefix.Length..];
        if (GraphNodeDefinition.TryFind(kindName, out var definition,
            (DataContext as GraphWorkspaceViewModel)?.AvailableNodeDefinitions ?? GraphNodeDefinition.Core))
        {
            kind = definition!.Kind;
            return true;
        }

        kind = default;
        return false;
    }

    private static bool TryGetGroupId(DragEventArgs e, out Guid id)
    {
        var text = e.DataTransfer.TryGetText();
        id = Guid.Empty;
        return text is not null && text.StartsWith(NodeCatalogEntryViewModel.GroupPrefix, StringComparison.Ordinal) &&
               Guid.TryParse(text[NodeCatalogEntryViewModel.GroupPrefix.Length..], out id) && id != Guid.Empty;
    }

    private void OnBackgroundPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(GraphCanvas).Properties.IsLeftButtonPressed &&
            sender is Control background && DataContext is GraphWorkspaceViewModel graph)
        {
            Focus();
            var clickPoint = ToGraphPoint(e.GetPosition(GraphCanvas));
            if (e.ClickCount >= 2 &&
                GraphSceneLayer.HitTestConnection(graph, clickPoint) is { } hitConnection)
            {
                graph.CancelConnection();
                graph.InsertRerouteOnConnection(hitConnection, clickPoint.X, clickPoint.Y);
                e.Handled = true;
                return;
            }
            graph.CancelConnection();
            _isSelecting = true;
            _selectionStart = e.GetPosition(GraphCanvas);
            _selectionBefore = e.KeyModifiers.HasFlag(KeyModifiers.Control)
                ? graph.SelectedNodes.ToArray()
                : [];
            _pastePosition = clickPoint;
            e.Pointer.Capture(background);
            e.Handled = true;
        }
    }

    private void OnBackgroundPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isSelecting)
        {
            return;
        }

        var rect = MakeSelectionRect(_selectionStart, e.GetPosition(GraphCanvas));
        SelectionBox.IsVisible = rect.Width >= 3 || rect.Height >= 3;
        Canvas.SetLeft(SelectionBox, rect.X);
        Canvas.SetTop(SelectionBox, rect.Y);
        SelectionBox.Width = rect.Width;
        SelectionBox.Height = rect.Height;
        e.Handled = true;
    }

    private void OnBackgroundPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isSelecting || DataContext is not GraphWorkspaceViewModel graph)
        {
            return;
        }

        var rect = MakeSelectionRect(_selectionStart, e.GetPosition(GraphCanvas));
        var selected = _selectionBefore.AsEnumerable();
        if (rect.Width >= 3 || rect.Height >= 3)
        {
            var matches = GraphCanvas.GetVisualDescendants().OfType<GraphNodeView>()
                .Where(view => ReferenceEquals(view.FindAncestorOfType<GraphEditorView>(), this) &&
                               view.DataContext is GraphNodeViewModel &&
                               view.TranslatePoint(default, GraphCanvas) is { } origin &&
                               rect.Intersects(new Rect(origin, view.Bounds.Size)))
                .Select(view => (GraphNodeViewModel)view.DataContext!);
            selected = selected.Concat(matches);
        }

        _isSelecting = false;
        SelectionBox.IsVisible = false;
        e.Pointer.Capture(null);
        graph.SetSelection(selected.ToArray());
        e.Handled = true;
    }

    private void OnBackgroundPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _isSelecting = false;
        SelectionBox.IsVisible = false;
    }

    private static Rect MakeSelectionRect(Point start, Point end) =>
        new(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
            Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));

    private void OnGraphCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!OwnsInput(e.Source)) return;
        if (!e.GetCurrentPoint(GraphCanvas).Properties.IsRightButtonPressed ||
            DataContext is not GraphWorkspaceViewModel graph)
        {
            return;
        }

        _pastePosition = ToGraphPoint(e.GetPosition(GraphCanvas));
        var hitNode = FindHitNode(e.Source);
        NewSpawnableGroupMenu.IsVisible = hitNode is null;
        AddRerouteMenu.IsVisible = hitNode is null &&
            GraphSceneLayer.HitTestConnection(graph, _pastePosition) is not null;
        PopulateAddNodeMenu(graph);
        if (hitNode?.DataContext is GraphNodeViewModel node && !node.IsSelected && graph.SelectedNodes.Count < 2)
        {
            graph.SelectNode(node);
        }
    }

    private GraphNodeView? FindHitNode(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<GraphNodeView>()
            .FirstOrDefault(view => ReferenceEquals(view.FindAncestorOfType<GraphEditorView>(), this));

    internal GraphNodeViewModel? FindDropContainer(Point point, IReadOnlyCollection<GraphNodeViewModel> dragged)
    {
        return this.GetVisualDescendants().OfType<GraphNodeView>()
            .Where(view => view.IsEffectivelyVisible && ReferenceEquals(view.FindAncestorOfType<GraphEditorView>(), this) &&
                view.DataContext is GraphNodeViewModel { ChildGraph: not null } node && !dragged.Contains(node))
            .Reverse().FirstOrDefault(view => view.TranslatePoint(default, this) is { } origin &&
                new Rect(origin, view.Bounds.Size).Contains(point))?.DataContext as GraphNodeViewModel;
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e) => await CopySelectionAsync(false);

    private async void OnCutClick(object? sender, RoutedEventArgs e) => await CopySelectionAsync(true);

    private async void OnPasteClick(object? sender, RoutedEventArgs e) => await PasteAsync();
    private void PopulateAddNodeMenu(GraphWorkspaceViewModel graph)
    {
        AddNodeMenu.Items.Clear();
        var entries = NodeCatalogViewModel.BuildEntries(graph, graph.GroupAssets, graph.NodeDefinitions);
        foreach (var group in NodeCatalogViewModel.GroupEntries(entries))
        {
            var categoryItem = new MenuItem { Header = group.Name };
            foreach (var entry in group.Nodes)
            {
                var nodeItem = new MenuItem
                {
                    Header = entry.Name,
                    IsEnabled = entry.CanCreate
                };
                if (!string.IsNullOrEmpty(entry.ToolTipText))
                {
                    ToolTip.SetTip(nodeItem, entry.ToolTipText);
                }
                var captured = entry;
                nodeItem.Click += (_, _) => AddCatalogEntryAt(graph, captured, _pastePosition);
                categoryItem.Items.Add(nodeItem);
            }
            AddNodeMenu.Items.Add(categoryItem);
        }
    }

    private void AddCatalogEntryAt(GraphWorkspaceViewModel graph, NodeCatalogEntryViewModel entry, Point position)
    {
        try
        {
            if (entry.GroupAssetId is { } id) graph.CreateGroupAssetNode(id, position.X, position.Y);
            else if (GraphNodeDefinition.TryFind(entry.KindName, out var definition, graph.AvailableNodeDefinitions))
                graph.CreateNode(definition!.Kind, position.X, position.Y, entry.IsPolymorphic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   InvalidOperationException or ArgumentException)
        {
            graph.ReportStatus(ex.Message);
        }
        Focus();
    }

    private void OnAddRerouteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GraphWorkspaceViewModel graph) return;
        if (GraphSceneLayer.HitTestConnection(graph, _pastePosition) is { } connection)
        {
            graph.InsertRerouteOnConnection(connection, _pastePosition.X, _pastePosition.Y);
        }
    }
    private void OnAddCommentClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GraphWorkspaceViewModel graph) return;
        graph.RunAuthoringAction(() =>
        {
            var node = graph.CreateCommentAroundSelection(_pastePosition.X, _pastePosition.Y);
            graph.SelectNode(node);
        });
    }

    private void OnAddSpawnableGroupClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GraphWorkspaceViewModel graph) return;
        try { graph.CreateNode(GraphNodeKind.SpawnableGroup, _pastePosition.X, _pastePosition.Y); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { graph.ReportStatus(ex.Message); }
    }
    private void OnAddCpuParticleClick(object? sender, RoutedEventArgs e) => AddParticle("CpuParticle");
    private void OnAddGpuParticleClick(object? sender, RoutedEventArgs e) => AddParticle("GpuParticle");
    private void AddParticle(string kind)
    {
        if (DataContext is GraphWorkspaceViewModel graph)
            graph.RunAuthoringAction(() => graph.CreateNode(new(kind), _pastePosition.X, _pastePosition.Y));
    }

    private void OnBreadcrumbClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int depth } && DataContext is GraphWorkspaceViewModel graph)
        {
            CloseSpecializedWorkspace();
            graph.NavigateToGraph(depth);
            graph.Document.ActivateEditingWorkspace();
        }
    }

    private async void OnGraphKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || !OwnsInput(e.Source)) return;
        if (DataContext is not GraphWorkspaceViewModel graph)
        {
            return;
        }

        var shortcuts = ShortcutService.Shared;
        if (graph.IsCpuParticleOverview &&
            !shortcuts.Matches("undo", e) && !shortcuts.Matches("redo", e))
        {
            return;
        }
        if (shortcuts.Matches("graph.search", e) && e.Source is Visual source &&
            source.GetSelfAndVisualAncestors().OfType<TextBox>().Any())
        {
            return;
        }
        if (graph.IsReadOnly && new[] { "graph.cut", "graph.paste", "graph.delete", "graph.group", "graph.search" }
            .Any(id => shortcuts.Matches(id, e)))
        {
            graph.SetConnectionStatus("Inline.Locked");
            e.Handled = true;
            return;
        }
        if (shortcuts.Matches("undo", e))
        {
            graph.History.Undo();
            e.Handled = true;
        }
        else if (shortcuts.Matches("redo", e))
        {
            graph.History.Redo();
            e.Handled = true;
        }
        else if (shortcuts.Matches("graph.copy", e))
        {
            e.Handled = true;
            await CopySelectionAsync(false);
        }
        else if (shortcuts.Matches("graph.cut", e))
        {
            e.Handled = true;
            await CopySelectionAsync(true);
        }
        else if (shortcuts.Matches("graph.paste", e))
        {
            e.Handled = true;
            await PasteAsync();
        }
        else if (shortcuts.Matches("graph.all", e))
        {
            graph.SetSelection(graph.Nodes);
            e.Handled = true;
        }
        else if (shortcuts.Matches("graph.delete", e))
        {
            graph.RunAuthoringAction(() => graph.DeleteNodes(graph.SelectedNodes.ToArray()));
            e.Handled = true;
        }
        else if (shortcuts.Matches("graph.search", e))
        {
            var center = new Point(
                (GraphScrollViewer.Offset.X + GraphScrollViewer.Viewport.Width / 2) / _zoom - _graphOrigin.X,
                (GraphScrollViewer.Offset.Y + GraphScrollViewer.Viewport.Height / 2) / _zoom - _graphOrigin.Y);
            ShowNodeSearch(_lastGraphPointer ?? center);
            e.Handled = true;
        }
        else if (shortcuts.Matches("graph.frame", e))
        {
            FrameSelection();
            e.Handled = true;
        }
        else if (shortcuts.Matches("graph.group", e))
        {
            graph.RunAuthoringAction(() => graph.GroupSelection());
            e.Handled = true;
        }
        else if (shortcuts.Matches("graph.zoomIn", e) || shortcuts.Matches("graph.zoomOut", e))
        {
            ZoomBy(shortcuts.Matches("graph.zoomIn", e) ? 1.25 : 1 / 1.25);
            e.Handled = true;
        }
    }

    private NodeCatalogEntryViewModel[]? _searchEntries;

    private void ShowNodeSearch(Point position)
    {
        if (DataContext is GraphWorkspaceViewModel { IsReadOnly: true }) return;
        _nodeSearchPosition = position;
        _searchEntries = BuildNodeSearchEntries();
        if (string.IsNullOrEmpty(NodeSearchBox.Text))
            RefreshNodeSearch();
        else
            NodeSearchBox.Text = string.Empty;
        NodeSearchPopup.IsOpen = true;
        Dispatcher.UIThread.Post(() => NodeSearchBox.Focus());
    }

    private NodeCatalogEntryViewModel[] BuildNodeSearchEntries()
    {
        var workspace = DataContext as GraphWorkspaceViewModel;
        var projectName = workspace?.NodeDefinitions?.ProjectName;
        var allowedKinds = workspace?.CreatableNodeKinds();
        var definitions = (workspace?.AvailableNodeDefinitions ?? GraphNodeDefinition.Core)
            .Where(definition => !GraphPolymorphism.IsHiddenVariant(definition.Kind) &&
                                 (allowedKinds is null || allowedKinds.Contains(definition.Kind)))
            .Select(definition => new NodeCatalogEntryViewModel(definition, projectName));
        var groups = (workspace is null || workspace.CanCreateNode(GraphNodeKind.GraphGroup, out _)
            ? workspace?.GroupAssets?.Assets
                .Select(asset => new NodeCatalogEntryViewModel(asset, workspace?.GroupAssets))
            : null) ?? [];
        return [.. definitions, .. groups];
    }

    private void OnNodeSearchTextChanged(object? sender, TextChangedEventArgs e) => RefreshNodeSearch();

    private void RefreshNodeSearch()
    {
        if (NodeSearchResults is null || NodeSearchBox is null) return;
        var allEntries = _searchEntries ??= BuildNodeSearchEntries();
        var search = NodeSearchBox.Text?.Trim() ?? string.Empty;
        var entries = search.Length == 0
            ? allEntries
            : allEntries.Where(entry =>
                entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                entry.OriginalName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                entry.KindName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                entry.QualifiedName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                entry.Namespace.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                entry.Group.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                entry.SearchTerms.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        entries = entries.OrderByDescending(entry => entry.IsFavorite)
            .ThenByDescending(entry => string.Equals(entry.Name, search, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(entry => entry.Name.StartsWith(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        NodeSearchResults.ItemsSource = entries;
        NodeSearchResults.SelectedIndex = entries.Length > 0 ? 0 : -1;
    }

    private void OnNodeSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            NodeSearchPopup.IsOpen = false;
            Focus();
        }
        else if (e.Key == Key.Enter) AddSearchSelection();
        else if (e.Key is Key.Down or Key.Up && NodeSearchResults.ItemCount > 0)
        {
            var delta = e.Key == Key.Down ? 1 : -1;
            NodeSearchResults.SelectedIndex = Math.Clamp(NodeSearchResults.SelectedIndex + delta, 0, NodeSearchResults.ItemCount - 1);
            NodeSearchResults.ScrollIntoView(NodeSearchResults.SelectedIndex);
        }
        else return;
        e.Handled = true;
    }

    private void OnNodeSearchDoubleTapped(object? sender, TappedEventArgs e) => AddSearchSelection();

    private void AddSearchSelection()
    {
        if (DataContext is not GraphWorkspaceViewModel graph ||
            NodeSearchResults.SelectedItem is not NodeCatalogEntryViewModel entry) return;
        AddCatalogEntryAt(graph, entry, _nodeSearchPosition);
        NodeSearchPopup.IsOpen = false;
    }

    private async Task CopySelectionAsync(bool cut)
    {
        if (DataContext is not GraphWorkspaceViewModel graph || graph.SelectionCount == 0)
        {
            return;
        }

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard
                ?? throw new InvalidOperationException(LocalizationService.Shared.Get("Editor.ClipboardUnavailable"));
            var nodes = graph.SelectedNodes.ToArray();
            var revision = graph.History.CurrentNodeId;
            var text = ClipboardPrefix + JsonSerializer.Serialize(graph.CreateSelectionSnapshot());
            await clipboard.SetTextAsync(text);
            if (cut)
            {
                if (graph.History.CurrentNodeId != revision || nodes.Any(node => !graph.Nodes.Contains(node)))
                {
                    graph.SetConnectionStatus("Editor.CutCancelled");
                    return;
                }

                graph.DeleteNodes(nodes);
            }
            graph.SetConnectionStatus(cut ? "Editor.NodesCut" : "Editor.NodesCopied");
        }
        catch (Exception ex)
        {
            graph.SetConnectionStatus("Editor.ClipboardFailed", MessageLocalization.Error(ex.Message));
        }
    }

    private async Task PasteAsync()
    {
        if (DataContext is not GraphWorkspaceViewModel graph)
        {
            return;
        }

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard
                ?? throw new InvalidOperationException(LocalizationService.Shared.Get("Editor.ClipboardUnavailable"));
            var text = await clipboard.TryGetTextAsync();
            if (text is null || text.Length > 1_000_000 ||
                !text.StartsWith(ClipboardPrefix, StringComparison.Ordinal))
            {
                graph.SetConnectionStatus("Editor.ClipboardNotNodes");
                return;
            }

            var snapshot = JsonSerializer.Deserialize<GraphSnapshot>(text[ClipboardPrefix.Length..])
                ?? throw new InvalidDataException(LocalizationService.Shared.Get("Editor.ClipboardEmpty"));
            graph.PasteSnapshot(snapshot, _pastePosition.X, _pastePosition.Y);
            _pastePosition = new Point(_pastePosition.X + 24, _pastePosition.Y + 24);
        }
        catch (Exception ex)
        {
            graph.SetConnectionStatus("Editor.PasteFailed", MessageLocalization.Error(ex.Message));
        }
    }

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !OwnsInput(e.Source)) return;
        var properties = e.GetCurrentPoint(GraphScrollViewer).Properties;
        if (properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Alt) &&
            DataContext is GraphWorkspaceViewModel graph &&
            FindHitConnection(ToGraphPoint(e.GetPosition(GraphCanvas)), graph) is { } hit)
        {
            graph.RunAuthoringAction(() => graph.DisconnectConnection(hit));
            e.Handled = true;
            return;
        }
        var panButton = ShortcutService.Shared.GraphPanButton;
        var right = properties.IsRightButtonPressed && panButton is "Right" or "Right + Middle";
        var middle = properties.IsMiddleButtonPressed && panButton is "Middle" or "Right + Middle";
        if (!right && !middle)
        {
            return;
        }

        BeginViewportPan(e, right ? MouseButton.Right : MouseButton.Middle, true);
    }

    private GraphConnectionViewModel? FindHitConnection(Point point, GraphWorkspaceViewModel graph)
    {
        var threshold = 10.0 / Math.Max(0.25, _zoom);
        var thresholdSq = threshold * threshold;
        GraphConnectionViewModel? best = null;
        var bestDistSq = thresholdSq;

        foreach (var connection in graph.Connections)
        {
            var start = connection.StartPoint;
            var end = connection.EndPoint;
            var offset = Math.Max(48, Math.Abs(end.X - start.X) * 0.5);
            var c1 = new Point(start.X + offset, start.Y);
            var c2 = new Point(end.X - offset, end.Y);
            var prev = start;
            for (var step = 1; step <= 20; step++)
            {
                var t = step / 20.0;
                var u = 1.0 - t;
                var cur = new Point(
                    u * u * u * start.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * end.X,
                    u * u * u * start.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * end.Y);
                var distSq = PointToSegmentDistanceSq(point, prev, cur);
                if (distSq <= bestDistSq)
                {
                    bestDistSq = distSq;
                    best = connection;
                }
                prev = cur;
            }
        }
        return best;
    }

    private static double PointToSegmentDistanceSq(Point p, Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lenSq = dx * dx + dy * dy;
        if (lenSq <= 1e-6) return (p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y);
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0, 1);
        var projX = a.X + t * dx;
        var projY = a.Y + t * dy;
        return (p.X - projX) * (p.X - projX) + (p.Y - projY) * (p.Y - projY);
    }

    private void BeginViewportPan(PointerPressedEventArgs e, MouseButton button, bool canMove)
    {
        _isPanning = true;
        _panMoved = false;
        _panCanMove = canMove;
        _panButton = button;
        _panStart = e.GetPosition(GraphScrollViewer);
        _panOffset = GraphScrollViewer.Offset;
        e.Pointer.Capture(GraphScrollViewer);
        e.Handled = true;
    }

    private void OnViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!OwnsInput(e.Source) && !_isPanning) return;
        var graphPoint = e.GetPosition(GraphCanvas);
        if (graphPoint.X >= 0 && graphPoint.Y >= 0 &&
            graphPoint.X <= GraphCanvas.Width && graphPoint.Y <= GraphCanvas.Height)
            _lastGraphPointer = ToGraphPoint(graphPoint);
        if (!_isPanning)
        {
            return;
        }

        var position = e.GetPosition(GraphScrollViewer);
        var target = new Vector(
            _panOffset.X + _panStart.X - position.X,
            _panOffset.Y + _panStart.Y - position.Y);
        if (Math.Abs(position.X - _panStart.X) >= 4 || Math.Abs(position.Y - _panStart.Y) >= 4)
            _panMoved = true;
        if (!_panMoved) return;
        if (!_panCanMove)
        {
            e.Handled = true;
            return;
        }
        var shiftX = target.X < 200 ? OriginStep : 0;
        var shiftY = target.Y < 200 ? OriginStep : 0;
        if (shiftX > 0 || shiftY > 0)
        {
            ShiftOrigin(shiftX, shiftY);
            target += new Vector(shiftX * _zoom, shiftY * _zoom);
        }
        EnsureViewportExtent(target);
        GraphScrollViewer.Offset = target;
        e.Handled = true;
    }

    private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            var openMenu = _panButton == MouseButton.Right && !_panMoved;
            _isPanning = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            if (openMenu) GraphCanvas.ContextMenu?.Open(GraphCanvas);
        }
    }

    private void OnViewportPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _isPanning = false;
    }

    private void OnViewportPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!OwnsInput(e.Source)) return;
        if (e.Delta.Y != 0 && FindHitNode(e.Source)?.TryScrollComment(e) == true)
        {
            e.Handled = true;
            return;
        }
        if (e.Delta.Y == 0 ||
            (ShortcutService.Shared.GraphZoomModifier == "Control" &&
             !e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            return;
        }

        SetZoom(_zoom * Math.Pow(1.15, e.Delta.Y), e.GetPosition(GraphScrollViewer));
        e.Handled = true;
    }

    private void EnsureViewportExtent(Vector offset)
    {
        var width = (offset.X + GraphScrollViewer.Viewport.Width) / _zoom + OriginStep;
        var height = (offset.Y + GraphScrollViewer.Viewport.Height) / _zoom + OriginStep;
        if (width > GraphCanvas.Width || height > GraphCanvas.Height)
        {
            GraphCanvas.Width = Math.Max(GraphCanvas.Width, width);
            GraphCanvas.Height = Math.Max(GraphCanvas.Height, height);
            UpdateZoomExtent();
        }
    }

    private void OnViewportScrollChanged(object? sender, ScrollChangedEventArgs e) => UpdateSceneViewport();

    private void UpdateSceneViewport()
    {
        SceneLayer.VisibleArea = new Rect(
            GraphScrollViewer.Offset.X / _zoom,
            GraphScrollViewer.Offset.Y / _zoom,
            GraphScrollViewer.Viewport.Width / _zoom,
            GraphScrollViewer.Viewport.Height / _zoom);
    }

    private void OnResetZoomClick(object? sender, RoutedEventArgs e)
    {
        SetZoom(1, new Point(GraphScrollViewer.Viewport.Width / 2, GraphScrollViewer.Viewport.Height / 2));
    }

    private void OnZoomInClick(object? sender, RoutedEventArgs e) => ZoomBy(1.25);
    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => ZoomBy(1 / 1.25);
    private void OnZoomSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingZoomSlider || !_viewportInitialized) return;
        SetZoom(Math.Pow(2, e.NewValue),
            new Point(GraphScrollViewer.Viewport.Width / 2, GraphScrollViewer.Viewport.Height / 2));
    }
    private void OnFrameSelectionClick(object? sender, RoutedEventArgs e) => FrameSelection();
    private void OnGroupSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GraphWorkspaceViewModel { IsReadOnly: false } graph)
            graph.RunAuthoringAction(() => graph.GroupSelection());
    }

    private void OnDisconnectConnectionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GraphWorkspaceViewModel { IsReadOnly: false } graph &&
            sender is Button { Tag: GraphConnectionViewModel connection })
            graph.RunAuthoringAction(() => graph.DisconnectConnection(connection));
        e.Handled = true;
    }

    private void ZoomBy(double factor) =>
        SetZoom(_zoom * factor, new Point(GraphScrollViewer.Viewport.Width / 2, GraphScrollViewer.Viewport.Height / 2));

    private void FrameSelection()
    {
        if (DataContext is not GraphWorkspaceViewModel graph || graph.SelectionCount == 0) return;
        FrameNodes(graph.SelectedNodes);
    }

    internal void FrameAll()
    {
        if (DataContext is not GraphWorkspaceViewModel graph) return;
        InitializeViewport();
        FrameNodes(graph.Nodes);
    }

    private void FrameNodes(IEnumerable<GraphNodeViewModel> nodes)
    {
        var selected = nodes.ToHashSet();
        if (selected.Count == 0) return;
        var views = GraphCanvas.GetVisualDescendants().OfType<GraphNodeView>()
            .Where(view => view.DataContext is GraphNodeViewModel node && selected.Contains(node)).ToArray();
        var left = selected.Min(node => node.DisplayX);
        var top = selected.Min(node => node.DisplayY);
        var right = selected.Max(node => node.DisplayX + node.RenderedWidth);
        var bottom = selected.Max(node => node.DisplayY +
            (views.FirstOrDefault(view => ReferenceEquals(view.DataContext, node))?.Bounds.Height ?? 80));
        var viewport = GraphScrollViewer.Viewport;
        if (viewport.Width <= 0 || viewport.Height <= 0) return;
        var zoom = Math.Min(Math.Max(1, viewport.Width - 80) / Math.Max(1, right - left),
                            Math.Max(1, viewport.Height - 80) / Math.Max(1, bottom - top));
        SetZoom(zoom, new Point(viewport.Width / 2, viewport.Height / 2));
        var offset = new Vector(((left + right) / 2 + _graphOrigin.X) * _zoom - viewport.Width / 2,
                                ((top + bottom) / 2 + _graphOrigin.Y) * _zoom - viewport.Height / 2);
        EnsureViewportExtent(offset);
        GraphScrollViewer.UpdateLayout();
        GraphScrollViewer.Offset = offset;
    }

    private void SetZoom(double zoom, Point anchor)
    {
        zoom = Math.Clamp(zoom, 0.05, 4);
        var oldOffset = GraphScrollViewer.Offset;
        var graphX = (oldOffset.X + anchor.X) / _zoom - _graphOrigin.X;
        var graphY = (oldOffset.Y + anchor.Y) / _zoom - _graphOrigin.Y;
        _zoom = zoom;
        _graphScale.ScaleX = zoom;
        _graphScale.ScaleY = zoom;
        var offset = new Vector((graphX + _graphOrigin.X) * zoom - anchor.X,
                                (graphY + _graphOrigin.Y) * zoom - anchor.Y);
        var shiftX = offset.X < 0 ? Math.Ceiling(-offset.X / (OriginStep * zoom)) * OriginStep : 0;
        var shiftY = offset.Y < 0 ? Math.Ceiling(-offset.Y / (OriginStep * zoom)) * OriginStep : 0;
        if (shiftX > 0 || shiftY > 0)
        {
            ShiftOrigin(shiftX, shiftY);
            offset += new Vector(shiftX * zoom, shiftY * zoom);
        }
        else
        {
            UpdateZoomExtent();
        }
        EnsureViewportExtent(offset);
        if (offset.X > GraphScrollViewer.Extent.Width - GraphScrollViewer.Viewport.Width ||
            offset.Y > GraphScrollViewer.Extent.Height - GraphScrollViewer.Viewport.Height)
        {
            GraphScrollViewer.UpdateLayout();
        }
        GraphScrollViewer.Offset = offset;
        UpdateSceneViewport();
        _updatingZoomSlider = true;
        ZoomSlider.Value = Math.Log2(zoom);
        _updatingZoomSlider = false;
        ZoomText.Text = $"{zoom:P0}";
    }
}
