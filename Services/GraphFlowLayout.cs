using Avalonia;
using LiberaFx.Models.Graph;
using LiberaFx.ViewModels;

namespace LiberaFx.Services;

public sealed record GraphLayoutMove(Guid NodeId, double OldX, double OldY, double X, double Y,
    double Width, double Height, bool Fixed)
{
    public double DeltaX => X - OldX;
    public double DeltaY => Y - OldY;
}

public sealed record GraphLayoutPreview(Guid PreviewId, string Mode, IReadOnlyList<GraphLayoutMove> Moves,
    IReadOnlyList<string> Warnings);

public static class GraphFlowLayout
{
    private const double HorizontalGap = 160;
    private const double VerticalGap = 56;
    public const int MaximumNodes = 256;

    public static GraphLayoutMode ParseMode(string mode) => mode switch
    {
        "manual" => GraphLayoutMode.Manual,
        "flow" => GraphLayoutMode.Flow,
        "compact" => GraphLayoutMode.Compact,
        _ => throw new ArgumentException("Layout must be manual, flow or compact.")
    };

    internal static Rect Bounds(GraphNodeViewModel node)
    {
        // Off-screen/new nodes may not have been measured by a view yet.
        var estimate = node.IsComment ? node.CommentHeight :
            84 + 24 * Math.Max(node.InputPorts.Count, node.OutputPorts.Count) +
            (node.IsLifecycleNode ? 64 : 0) + (node.IsSequence ? 100 : 0) +
            (node.IsPreviewSignal ? 40 : 0) +
            (node.IsDetailsExpanded ? 28 * node.InlineParameters.Count + 16 : 0) +
            (node.IsInlineExpanded ? node.InlineHeight + 28 : 0);
        return new Rect(node.X, node.Y, node.RenderedWidth, Math.Max(estimate, node.RenderedHeight));
    }

    internal static IReadOnlyList<GraphLayoutMove> Arrange(
        IReadOnlyList<GraphNodeViewModel> nodes, IReadOnlyList<GraphConnectionSnapshot> edges,
        IReadOnlyList<GraphNodeViewModel> obstacles, IReadOnlySet<Guid> fixedIds,
        GraphLayoutMode mode, Point origin)
    {
        if (nodes.Count is 0 or > MaximumNodes)
            throw new ArgumentException($"Select between 1 and {MaximumNodes} nodes.");
        if (mode is not (GraphLayoutMode.Flow or GraphLayoutMode.Compact) ||
            !double.IsFinite(origin.X) || !double.IsFinite(origin.Y))
            throw new ArgumentException("A finite origin and flow or compact layout are required.");
        var order = nodes.Select((node, index) => (node.Id, index)).ToDictionary(item => item.Id, item => item.index);
        var byId = nodes.ToDictionary(node => node.Id);
        var bounds = nodes.ToDictionary(node => node.Id, Bounds);
        var fixedNodes = nodes.Where(node => fixedIds.Contains(node.Id) || node.IsComment).Select(node => node.Id).ToHashSet();
        var incoming = nodes.ToDictionary(node => node.Id, _ => new List<GraphConnectionSnapshot>());
        var outgoing = nodes.ToDictionary(node => node.Id, _ => new List<GraphConnectionSnapshot>());
        foreach (var edge in edges.Where(edge => byId.ContainsKey(edge.SourceNodeId) && byId.ContainsKey(edge.TargetNodeId)))
        {
            incoming[edge.TargetNodeId].Add(edge);
            outgoing[edge.SourceNodeId].Add(edge);
        }
        var remaining = incoming.ToDictionary(pair => pair.Key, pair => pair.Value.Count);
        var levels = nodes.ToDictionary(node => node.Id, _ => 0);
        var ready = new SortedSet<int>(nodes.Where(node => remaining[node.Id] == 0).Select(node => order[node.Id]));
        var visited = 0;
        while (ready.Count > 0)
        {
            var index = ready.Min;
            ready.Remove(index);
            var source = nodes[index].Id;
            visited++;
            foreach (var edge in outgoing[source])
            {
                levels[edge.TargetNodeId] = Math.Max(levels[edge.TargetNodeId], levels[source] + 1);
                if (--remaining[edge.TargetNodeId] == 0) ready.Add(order[edge.TargetNodeId]);
            }
        }
        if (visited != nodes.Count) throw new ArgumentException("Cannot lay out a cyclic graph.");
        var placed = fixedNodes.ToDictionary(id => id, id => bounds[id]);
        var blockers = obstacles.Select(node => (Node: node, Bounds: Bounds(node))).ToArray();
        double DesiredY(GraphNodeViewModel node) => incoming[node.Id]
            .Where(edge => placed.ContainsKey(edge.SourceNodeId))
            .Select(edge => placed[edge.SourceNodeId].Y +
                byId[edge.SourceNodeId].OutputAnchor(edge.SourcePortId).Y - node.InputAnchor(edge.TargetPortId).Y)
            .DefaultIfEmpty(origin.Y).Average();
        Rect AvoidObstacles(GraphNodeViewModel node, double x, double y)
        {
            var size = bounds[node.Id].Size;
            var candidate = new Rect(new Point(x, y), size);
            while (true)
            {
                var hits = placed.Where(item => !byId[item.Key].IsComment ||
                        !bounds[item.Key].Contains(bounds[node.Id])).Select(item => item.Value)
                    .Concat(blockers.Where(item =>
                    !item.Node.IsComment || !item.Bounds.Contains(bounds[node.Id])).Select(item => item.Bounds))
                    .Where(box => box.Inflate(VerticalGap / 2).Intersects(candidate)).ToArray();
                if (hits.Length == 0) break;
                var nextY = hits.Max(box => box.Bottom) + VerticalGap;
                if (nextY <= candidate.Y) throw new ArgumentException("Coordinates are too large for safe layout.");
                candidate = new Rect(x, nextY, size.Width, size.Height);
                if (!double.IsFinite(candidate.Bottom)) throw new ArgumentException("Layout exceeds finite coordinates.");
            }
            return candidate;
        }
        var columnX = origin.X;
        var compactTop = origin.Y;
        var compactRowBottom = compactTop;
        var compactColumn = 0;
        foreach (var layer in nodes.GroupBy(node => mode == GraphLayoutMode.Flow ? levels[node.Id] : 0).OrderBy(group => group.Key))
        {
            foreach (var node in layer
                         .OrderBy(node => mode == GraphLayoutMode.Flow
                             ? (node.Definition.PortCategory == GraphPortCategory.Signal || node.IsLifecycleNode ? 0 : 1)
                             : 0)
                         .ThenBy(node => mode == GraphLayoutMode.Flow ? DesiredY(node) : order[node.Id])
                         .ThenBy(node => order[node.Id]).ToArray())
            {
                if (fixedNodes.Contains(node.Id)) continue;
                var x = columnX;
                var y = Math.Max(origin.Y, DesiredY(node));
                if (mode == GraphLayoutMode.Compact)
                {
                    if (compactColumn == 4)
                    {
                        compactColumn = 0;
                        columnX = origin.X;
                        compactTop = compactRowBottom + VerticalGap;
                    }
                    x = columnX;
                    y = compactTop;
                }
                else
                    x = incoming[node.Id].Where(edge => placed.ContainsKey(edge.SourceNodeId))
                        .Select(edge => placed[edge.SourceNodeId].Right + HorizontalGap).Append(x).Max();
                var box = AvoidObstacles(node, x, y);
                if (!double.IsFinite(box.Right) || !double.IsFinite(box.Bottom))
                    throw new ArgumentException("Layout exceeds finite coordinates.");
                placed[node.Id] = box;
                if (mode == GraphLayoutMode.Compact)
                {
                    compactColumn++;
                    columnX = box.Right + HorizontalGap;
                    compactRowBottom = Math.Max(compactRowBottom, box.Bottom);
                }
            }
            if (mode == GraphLayoutMode.Flow)
                columnX = layer.Select(node => placed[node.Id].Right).DefaultIfEmpty(columnX).Max() + HorizontalGap;
        }
        return nodes.Select(node => new GraphLayoutMove(node.Id, node.X, node.Y, placed[node.Id].X, placed[node.Id].Y,
            bounds[node.Id].Width, bounds[node.Id].Height, fixedNodes.Contains(node.Id))).ToArray();
    }
}
