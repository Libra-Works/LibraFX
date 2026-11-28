using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels.Graph;

namespace LiberaFx.Views.Graph;

// Draw only transient interaction wires above the document graph; settled wires stay below nodes.
public sealed class GraphInteractionOverlay : Control
{
    private GraphEditorView? _editor;
    private GraphWorkspaceViewModel? _graph;
    private GraphConnectionViewModel[] _connections = [];
    private bool _pending;

    public GraphInteractionOverlay()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    internal void Show(GraphEditorView editor, GraphWorkspaceViewModel graph,
        IEnumerable<GraphConnectionViewModel> connections, bool pending)
    {
        _editor = editor;
        _graph = graph;
        _connections = connections.ToArray();
        _pending = pending;
        InvalidateVisual();
    }

    internal void Clear(GraphEditorView editor)
    {
        if (!ReferenceEquals(_editor, editor))
        {
            return;
        }
        _editor = null;
        _graph = null;
        _connections = [];
        _pending = false;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_editor is not { } editor || _graph is not { } graph)
        {
            return;
        }
        foreach (var connection in _connections)
        {
            if (editor.GraphPointToOverlay(connection.StartPoint, this) is { } start &&
                editor.GraphPointToOverlay(connection.EndPoint, this) is { } end)
            {
                GraphSceneLayer.DrawConnection(context, start, end,
                    connection.Source.Definition.GetOutputPortCategory(connection.SourcePortId), selected: connection.IsSelected);
            }
        }
        if (_pending && graph.PendingOutputNode is { } source && graph.ConnectionPreviewPoint is { } endPoint)
        {
            var anchor = source.OutputAnchor(graph.PendingOutputPortId);
            var sourcePoint = new Point(source.DisplayX + anchor.X, source.DisplayY + anchor.Y);
            if (editor.GraphPointToOverlay(sourcePoint, this) is { } start &&
                editor.GraphPointToOverlay(endPoint, this) is { } end)
            {
                GraphSceneLayer.DrawConnection(context, start, end,
                    source.Definition.GetOutputPortCategory(graph.PendingOutputPortId));
            }
        }
    }
}
