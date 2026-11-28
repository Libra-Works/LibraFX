using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using System.ComponentModel;
using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels.Graph;

namespace LiberaFx.Views.Graph;

public partial class GraphNodeView : UserControl
{
    private void OnOutputPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not GraphNodeViewModel node ||
            sender is not Control port ||
            !e.GetCurrentPoint(port).Properties.IsLeftButtonPressed ||
            this.FindAncestorOfType<GraphEditorView>()?.FindControl<Grid>("GraphCanvas") is not { } canvas)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            var outputPortId = port.Tag is GraphOutputPortDefinition op ? op.Id : null;
            var matches = node.Workspace.Connections
                .Where(c => c.Source == node && (outputPortId is null || c.SourcePortId == outputPortId))
                .ToArray();
            if (matches.Length > 0)
            {
                node.Workspace.RunAuthoringAction(() =>
                {
                    foreach (var connection in matches)
                        node.Workspace.DisconnectConnection(connection);
                });
            }
            e.Handled = true;
            return;
        }

        _connectionSource = node;
        _connectionCanvas = canvas;
        _connectionStart = e.GetPosition(canvas);
        _connectionMoved = false;
        _connectionSourcePortId = port.Tag is GraphOutputPortDefinition outputPort ? outputPort.Id : null;
        _connectionTargets = canvas.GetVisualDescendants().OfType<Button>()
            .Where(button => button.IsEffectivelyVisible &&
                button.FindAncestorOfType<GraphEditorView>()?.DataContext == node.Workspace)
            .Select(button => (Pin: button, Node: button.FindAncestorOfType<GraphNodeView>()?.DataContext as GraphNodeViewModel,
                PortId: (button.Tag as GraphInputPortDefinition)?.Id))
            .Where(item => item.Node is not null && (item.Pin.Tag is GraphInputPortDefinition or "InputPort") &&
                node.Workspace.CanConnectNodes(node, item.Node, out _, item.PortId, _connectionSourcePortId))
            .Select(item => (item.Pin, item.Node!, item.PortId)).ToArray();
        if (_connectionSourcePortId is null) node.SelectOutputPortCommand.Execute(null);
        else node.Workspace.BeginConnection(node, _connectionSourcePortId);
        e.Pointer.Capture(port);
        e.Handled = true;
    }

    private void OnOutputPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_connectionSource is not { } source || _connectionCanvas is not { } canvas)
        {
            return;
        }

        var position = e.GetPosition(canvas);
        if (!_connectionMoved &&
            (Math.Abs(position.X - _connectionStart.X) >= 5 ||
             Math.Abs(position.Y - _connectionStart.Y) >= 5))
        {
            _connectionMoved = true;
        }

        if (_connectionMoved)
        {
            var editor = this.FindAncestorOfType<GraphEditorView>();
            var target = FindInputAt(canvas, position);
            _connectionFeedback.Show(target?.Pin);
            if (target is { } snap && snap.Pin.TranslatePoint(new Point(snap.Pin.Bounds.Width / 2, snap.Pin.Bounds.Height / 2), canvas) is { } anchor)
                position = anchor;
            source.Workspace.PreviewConnection(editor?.ToGraphPoint(position) ?? position);
            editor?.ShowInteractionWires(source.Workspace, [], pending: true);
        }

        e.Handled = true;
    }

    private void OnOutputPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_connectionSource is not { } source || _connectionCanvas is not { } canvas)
        {
            return;
        }

        this.FindAncestorOfType<GraphEditorView>()?.ClearInteractionWires();
        var moved = _connectionMoved;
        var position = e.GetPosition(canvas);
        var target = moved ? FindInputAt(canvas, position) : null;
        _connectionFeedback.Dispose();
        _connectionTargets = [];
        _connectionSource = null;
        var sourcePortId = _connectionSourcePortId;
        _connectionSourcePortId = null;
        _connectionCanvas = null;
        e.Pointer.Capture(null);
        if (target is { } input)
        {
            input.Node.Workspace.RunAuthoringAction(() =>
                input.Node.Workspace.CompleteConnection(input.Node, input.PortId, sourcePortId));
        }
        else if (moved)
        {
            source.Workspace.CancelConnection();
        }

        e.Handled = true;
    }

    private void OnCustomInputClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GraphNodeViewModel node &&
            sender is Button { Tag: GraphInputPortDefinition port })
            node.Workspace.RunAuthoringAction(() => node.Workspace.CompleteConnection(node, port.Id));
    }

    private (Button Pin, GraphNodeViewModel Node, string? PortId)? FindInputAt(Grid canvas, Point position)
    {
        var root = TopLevel.GetTopLevel(canvas);
        if (root is null || canvas.TranslatePoint(position, root) is not { } pointer) return null;
        (Button Pin, GraphNodeViewModel Node, string? PortId)? best = null;
        var distance = PortSnapFeedback.AcquireRadius;
        foreach (var candidate in _connectionTargets)
        {
            if (!candidate.Pin.IsEffectivelyVisible || candidate.Pin.TranslatePoint(
                    new Point(candidate.Pin.Bounds.Width / 2, candidate.Pin.Bounds.Height / 2), root) is not { } center) continue;
            var next = ((Vector)(center - pointer)).Length;
            var current = ReferenceEquals(candidate.Pin, _connectionFeedback.Pin);
            if (next <= distance || best is null && current && next <= PortSnapFeedback.ReleaseRadius)
            {
                best = candidate;
                distance = next;
            }
        }
        return best;
    }

    private void OnToggleInlineClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GraphNodeViewModel node)
            node.Workspace.RunAuthoringAction(() =>
                node.Workspace.SetInlinePresentation(node, !node.IsInlineExpanded, node.InlineWidth, node.InlineHeight));
        e.Handled = true;
    }

    private void OnMaximizeInlineClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GraphNodeViewModel node) node.Workspace.MaximizeInlineGraph(node);
        e.Handled = true;
    }

    private void OnOutputPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        this.FindAncestorOfType<GraphEditorView>()?.ClearInteractionWires();
        _connectionFeedback.Dispose();
        _connectionTargets = [];
        _connectionSource?.Workspace.CancelConnection();
        _connectionSource = null;
        _connectionSourcePortId = null;
        _connectionCanvas = null;
    }
}
