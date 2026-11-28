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
using System.Text.Json;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls.Templates;
using Avalonia.Threading;

namespace LiberaFx.Views.Graph;

public partial class GraphEditorView : UserControl
{
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
        var threshold = WireHitTolerance();
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
