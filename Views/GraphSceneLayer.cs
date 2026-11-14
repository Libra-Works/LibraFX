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
                source.DisplayX + GraphNodeViewModel.NodeWidth - GraphNodeViewModel.PortHorizontalOffset,
                source.DisplayY + GraphNodeViewModel.PortCenterOffset);
            DrawConnection(context, start, end, source.Definition.PortCategory);
        }
    }

    private void DrawGrid(DrawingContext context)
    {
        var minorIndex = 0;
        for (var x = 0d; x <= Bounds.Width; x += GridSpacing)
        {
            var pen = minorIndex++ % MajorGridInterval == 0 ? MajorGridPen : MinorGridPen;
            context.DrawLine(pen, new Point(x, 0), new Point(x, Bounds.Height));
        }

        var majorIndex = 0;
        for (var y = 0d; y <= Bounds.Height; y += GridSpacing)
        {
            var pen = majorIndex++ % MajorGridInterval == 0 ? MajorGridPen : MinorGridPen;
            context.DrawLine(pen, new Point(0, y), new Point(Bounds.Width, y));
        }
    }

    private static void DrawConnection(DrawingContext context, GraphConnectionViewModel connection)
    {
        DrawConnection(context, connection.StartPoint, connection.EndPoint, connection.Source.Definition.PortCategory);
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
