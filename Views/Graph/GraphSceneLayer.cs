using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
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
    private static readonly Pen SignalOrderPen = new(ThemeSettingsService.EditorBrush("GraphSignal"), 1);
    private static readonly IBrush SignalOrderBackground = ThemeSettingsService.EditorBrush("CurveBackground");
    private static readonly IBrush SignalOrderForeground = ThemeSettingsService.EditorBrush("GraphSignal");
    private static readonly IBrush DebugBrush = ThemeSettingsService.EditorBrush("GraphSignal");
    private static readonly Pen DebugFlowPen = new(DebugBrush, 5);
    private static readonly Pen SelectedConnectionPen = new(ThemeSettingsService.EditorBrush("SelectionBorder"), 5);

    private readonly HashSet<GraphConnectionViewModel> _observedConnections = [];
    private GraphWorkspaceViewModel? _workspace;
    private Vector _graphOrigin;
    private Rect _visibleArea;
    public bool GridOnly { get; set; }
    public bool ConnectionsOnly { get; set; }

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
        if (!ConnectionsOnly) DrawGrid(context);
        if (_workspace is null || GridOnly)
        {
            return;
        }

        foreach (var connection in _workspace.Connections)
        {
            DrawConnection(context, connection);
        }
        foreach (var connection in _workspace.Connections.Where(item => item.SignalOrder > 0))
            DrawSignalOrder(context, connection);
        foreach (var node in _workspace.Nodes)
        {
            var active = node.Id == _workspace.ExecutionDebugFrame?.Node;
            var breakpoint = _workspace.ExecutionDebugBreakpoints.Contains(node.Id);
            if (!active && !breakpoint) continue;
            var corner = new Point(node.DisplayX, node.DisplayY) + _graphOrigin;
            if (active)
                context.DrawLine(DebugFlowPen, corner + new Vector(0, -4),
                    corner + new Vector(node.RenderedWidth, -4));
            if (breakpoint)
                context.DrawEllipse(DebugBrush, null, corner + new Vector(-8, 8), 5, 5);
        }

        if (_workspace.PendingOutputNode is { } source && _workspace.ConnectionPreviewPoint is { } end)
        {
            var start = new Point(
                source.DisplayX + source.OutputAnchor(_workspace.PendingOutputPortId).X + _graphOrigin.X,
                source.DisplayY + source.OutputAnchor(_workspace.PendingOutputPortId).Y + _graphOrigin.Y);
            DrawConnection(context, start, end + _graphOrigin,
                source.Definition.GetOutputPortCategory(_workspace.PendingOutputPortId));
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
        var isSignal = connection.Source.Definition.GetOutputPortCategory(connection.SourcePortId) ==
            Models.Graph.GraphPortCategory.Signal;
        var current = _workspace?.ExecutionDebugFrame;
        // A selected signal predecessor is not necessarily the last executed
        // node (parameter refreshes can occur in between signal visits).
        var active = current is not null && connection.Target.Id == current.Node &&
            (isSignal ? connection.Source.Id == current.SignalSource :
                current.Inputs.Any(input => input.Source == connection.Source.Id)) &&
            // Avoid reporting an ambiguous pair of parallel authored routes
            // as two actual traversals when the wire only identifies endpoints.
            _workspace!.Connections.Count(other => other.Source == connection.Source &&
                other.Target == connection.Target &&
                (other.Source.Definition.GetOutputPortCategory(other.SourcePortId) == Models.Graph.GraphPortCategory.Signal) == isSignal) == 1;
        DrawConnection(context, connection.StartPoint + _graphOrigin, connection.EndPoint + _graphOrigin,
            connection.Source.Definition.GetOutputPortCategory(connection.SourcePortId), active, connection.IsSelected);
    }

    private static Point EvaluateCubicBezier(Point start, Point end, double t)
    {
        var controlOffset = Math.Max(48, Math.Abs(end.X - start.X) * 0.5);
        var u = 1 - t;
        var p1x = start.X + controlOffset;
        var p2x = end.X - controlOffset;
        var x = u * u * u * start.X + 3 * u * u * t * p1x + 3 * u * t * t * p2x + t * t * t * end.X;
        var y = (u * u * u + 3 * u * u * t) * start.Y + (3 * u * t * t + t * t * t) * end.Y;
        return new Point(x, y);
    }

    internal static GraphConnectionViewModel? HitTestConnection(
        GraphWorkspaceViewModel workspace, Point graphPoint, double tolerance = 10)
    {
        var maxDistanceSquared = tolerance * tolerance;
        GraphConnectionViewModel? best = null;
        var bestDistance = maxDistanceSquared;
        foreach (var connection in workspace.Connections)
        {
            var start = connection.StartPoint;
            var end = connection.EndPoint;
            const int segments = 24;
            var prev = start;
            for (var step = 1; step <= segments; step++)
            {
                var curr = EvaluateCubicBezier(start, end, step / (double)segments);
                var dx = curr.X - prev.X;
                var dy = curr.Y - prev.Y;
                var lenSq = dx * dx + dy * dy;
                var proj = lenSq > 1e-6
                    ? Math.Clamp(((graphPoint.X - prev.X) * dx + (graphPoint.Y - prev.Y) * dy) / lenSq, 0, 1)
                    : 0;
                var px = prev.X + proj * dx - graphPoint.X;
                var py = prev.Y + proj * dy - graphPoint.Y;
                var distSq = px * px + py * py;
                if (distSq <= bestDistance)
                {
                    bestDistance = distSq;
                    best = connection;
                }
                prev = curr;
            }
        }
        return best;
    }

    internal static bool IntersectsConnection(GraphConnectionViewModel connection, Rect rect)
    {
        var previous = connection.StartPoint;
        for (var step = 1; step <= 32; step++)
        {
            var current = EvaluateCubicBezier(connection.StartPoint, connection.EndPoint, step / 32.0);
            if (rect.Contains(previous) || rect.Contains(current) || SegmentIntersectsRect(previous, current, rect))
            {
                return true;
            }
            previous = current;
        }
        return false;
    }

    private static bool SegmentIntersectsRect(Point a, Point b, Rect rect)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var low = 0.0;
        var high = 1.0;
        bool Clip(double p, double q)
        {
            if (Math.Abs(p) < 1e-12)
            {
                return q >= 0;
            }
            var r = q / p;
            if (p < 0)
            {
                low = Math.Max(low, r);
            }
            else
            {
                high = Math.Min(high, r);
            }
            return low <= high;
        }
        return Clip(-dx, a.X - rect.Left) && Clip(dx, rect.Right - a.X) &&
            Clip(-dy, a.Y - rect.Top) && Clip(dy, rect.Bottom - a.Y);
    }

    private void DrawSignalOrder(DrawingContext context, GraphConnectionViewModel connection)
    {
        var start = connection.StartPoint + _graphOrigin;
        var end = connection.EndPoint + _graphOrigin;
        var offset = Math.Max(48, Math.Abs(end.X - start.X) * 0.5);
        const double t = 0.72;
        var u = 1 - t;
        var point = new Point(
            u * u * u * start.X + 3 * u * u * t * (start.X + offset) +
            3 * u * t * t * (end.X - offset) + t * t * t * end.X,
            (u * u * u + 3 * u * u * t) * start.Y +
            (3 * u * t * t + t * t * t) * end.Y);
        var text = new FormattedText(connection.SignalOrder.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Arial"), 10, SignalOrderForeground);
        context.DrawEllipse(SignalOrderBackground, SignalOrderPen, point, Math.Max(8, text.Width / 2 + 4), 8);
        context.DrawText(text, new Point(point.X - text.Width / 2, point.Y - text.Height / 2));
    }

    internal static void DrawConnection(DrawingContext context, Point start, Point end, Models.Graph.GraphPortCategory category,
        bool debugFlow = false, bool selected = false)
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
        context.DrawGeometry(null, selected ? SelectedConnectionPen : debugFlow ? DebugFlowPen : pen, geometry);
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GraphWorkspaceViewModel.ConnectionPreviewPoint) or
            nameof(GraphWorkspaceViewModel.PendingOutputNode) or
            nameof(GraphWorkspaceViewModel.PendingOutputPortId) or
            nameof(GraphWorkspaceViewModel.ExecutionDebugFrame))
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
            nameof(GraphConnectionViewModel.EndPoint) or nameof(GraphConnectionViewModel.SignalOrder) or
            nameof(GraphConnectionViewModel.IsSelected))
        {
            InvalidateVisual();
        }
    }
}
