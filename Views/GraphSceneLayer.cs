using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LiberaFx.ViewModels;
using LiberaFx.Services;

namespace LiberaFx.Views;

public sealed class GraphSceneLayer : Control
{
    private const double GridSpacing = 24;
    private const int MajorGridInterval = 5;

    private static readonly Pen MinorGridPen = new(ThemeSettingsService.EditorBrush("GraphMinorGrid"), 1);
    private static readonly Pen MajorGridPen = new(ThemeSettingsService.EditorBrush("GraphMajorGrid"), 1);
    private static readonly Pen ParameterConnectionPen = new(ThemeSettingsService.EditorBrush("GraphParameter"), 2);
    private static readonly Pen SignalConnectionPen = new(ThemeSettingsService.EditorBrush("GraphSignal"), 2);

    private readonly HashSet<GraphConnectionViewModel> _observedConnections = [];
    private GraphWorkspaceViewModel? _workspace;
    private Vector _graphOrigin;
    private Rect _visibleArea;

    public Vector GraphOrigin
    {
        get => _graphOrigin;
        set { _graphOrigin = value; InvalidateVisual(); }
    }

    public Rect VisibleArea
    {
        get => _visibleArea;
        set { if (_visibleArea != value) { _visibleArea = value; InvalidateVisual(); } }
    }

    public GraphSceneLayer()
    {
        IsHitTestVisible = false;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeSettingsService.EditorColorsChanged += OnEditorColorsChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ThemeSettingsService.EditorColorsChanged -= OnEditorColorsChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnEditorColorsChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_workspace is not null)
        {
            _workspace.Connections.CollectionChanged -= OnConnectionsChanged;
            _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        }

        foreach (var connection in _observedConnections)
        {
            connection.PropertyChanged -= OnConnectionPropertyChanged;
        }

        _observedConnections.Clear();
        _workspace = DataContext as GraphWorkspaceViewModel;
        if (_workspace is not null)
        {
            _workspace.Connections.CollectionChanged += OnConnectionsChanged;
            _workspace.PropertyChanged += OnWorkspacePropertyChanged;
            SynchronizeConnections();
        }

        InvalidateVisual();
        base.OnDataContextChanged(e);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        DrawGrid(context);
        if (_workspace is null)
        {
            return;
        }

        foreach (var connection in _workspace.Connections)
        {
            DrawConnection(context, connection);
        }

        if (_workspace.PendingOutputNode is { } source && _workspace.ConnectionPreviewPoint is { } end)
        {
            var start = new Point(
                source.DisplayX + GraphNodeViewModel.NodeWidth - GraphNodeViewModel.PortHorizontalOffset + _graphOrigin.X,
                source.DisplayY + GraphNodeViewModel.PortCenterOffset + _graphOrigin.Y);
            DrawConnection(context, start, end + _graphOrigin, source.Definition.PortCategory);
        }
    }

    private void DrawGrid(DrawingContext context)
    {
        var visible = _visibleArea.Intersect(new Rect(Bounds.Size));
        if (visible.Width <= 0 || visible.Height <= 0) return;
        var startX = (long)Math.Floor((visible.Left - _graphOrigin.X) / GridSpacing);
        var endX = (long)Math.Ceiling((visible.Right - _graphOrigin.X) / GridSpacing);
        for (var index = startX; index <= endX; index++)
        {
            var x = index * GridSpacing + _graphOrigin.X;
            var pen = index % MajorGridInterval == 0 ? MajorGridPen : MinorGridPen;
            context.DrawLine(pen, new Point(x, visible.Top), new Point(x, visible.Bottom));
        }

        var startY = (long)Math.Floor((visible.Top - _graphOrigin.Y) / GridSpacing);
        var endY = (long)Math.Ceiling((visible.Bottom - _graphOrigin.Y) / GridSpacing);
        for (var index = startY; index <= endY; index++)
        {
            var y = index * GridSpacing + _graphOrigin.Y;
            var pen = index % MajorGridInterval == 0 ? MajorGridPen : MinorGridPen;
            context.DrawLine(pen, new Point(visible.Left, y), new Point(visible.Right, y));
        }
    }

    private void DrawConnection(DrawingContext context, GraphConnectionViewModel connection)
    {
        DrawConnection(context, connection.StartPoint + _graphOrigin, connection.EndPoint + _graphOrigin, connection.Source.Definition.PortCategory);
    }

    private static void DrawConnection(DrawingContext context, Point start, Point end, Models.Graph.GraphPortCategory category)
    {
        var controlOffset = Math.Max(48, Math.Abs(end.X - start.X) * 0.5);
        var geometry = new StreamGeometry();
        using (var geometryContext = geometry.Open())
        {
            geometryContext.BeginFigure(start, isFilled: false);
            geometryContext.CubicBezierTo(
                new Point(start.X + controlOffset, start.Y),
                new Point(end.X - controlOffset, end.Y),
                end,
                isStroked: true);
            geometryContext.EndFigure(isClosed: false);
        }

        var pen = category == Models.Graph.GraphPortCategory.Signal
            ? SignalConnectionPen
            : ParameterConnectionPen;
        context.DrawGeometry(null, pen, geometry);
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GraphWorkspaceViewModel.ConnectionPreviewPoint) or
            nameof(GraphWorkspaceViewModel.PendingOutputNode))
        {
            InvalidateVisual();
        }
    }

    private void OnConnectionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SynchronizeConnections();
        InvalidateVisual();
    }

    private void SynchronizeConnections()
    {
        var currentConnections = _workspace?.Connections.ToHashSet() ?? [];
        foreach (var connection in _observedConnections.Except(currentConnections).ToArray())
        {
            connection.PropertyChanged -= OnConnectionPropertyChanged;
            _observedConnections.Remove(connection);
        }

        foreach (var connection in currentConnections.Except(_observedConnections))
        {
            connection.PropertyChanged += OnConnectionPropertyChanged;
            _observedConnections.Add(connection);
        }
    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GraphConnectionViewModel.StartPoint) or
            nameof(GraphConnectionViewModel.EndPoint))
        {
            InvalidateVisual();
        }
    }
}
