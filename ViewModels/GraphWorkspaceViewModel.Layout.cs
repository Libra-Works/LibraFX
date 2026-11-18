using Avalonia;
using LiberaFx.Models.Graph;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed partial class GraphWorkspaceViewModel
{
    private readonly Dictionary<Guid, (GraphSnapshot Before, GraphLayoutPreview Preview, Guid[] Path, long Created)> _layoutPreviews = [];

    public GraphLayoutPreview PreviewLayout(IReadOnlyList<Guid> nodeIds, IReadOnlyList<Guid>? fixedNodeIds = null,
        string mode = "flow", double? originX = null, double? originY = null)
    {
        if (nodeIds is null || nodeIds.Count is 0 or > GraphFlowLayout.MaximumNodes ||
            nodeIds.Distinct().Count() != nodeIds.Count)
            throw new ArgumentException("Supply distinct node IDs from the active editing level.");
        var selectedIds = nodeIds.ToHashSet();
        var nodes = Nodes.Where(node => selectedIds.Contains(node.Id)).ToArray();
        if (nodes.Length != selectedIds.Count) throw new ArgumentException("A selected node is not in the active graph.");
        var fixedIds = (fixedNodeIds ?? []).ToHashSet();
        if (!fixedIds.IsSubsetOf(selectedIds)) throw new ArgumentException("Fixed nodes must be in the requested scope.");
        var layout = GraphFlowLayout.ParseMode(mode);
        var snapshot = CreateCurrentSnapshot();
        var moves = GraphFlowLayout.Arrange(nodes, snapshot.Connections,
            Nodes.Where(node => !selectedIds.Contains(node.Id)).ToArray(), fixedIds, layout,
            new Point(originX ?? nodes.Min(node => node.X), originY ?? nodes.Min(node => node.Y)));
        var warnings = new List<string>();
        if (Nodes.Any(node => node.IsComment)) warnings.Add("Comment frames stay fixed and are not resized. Review their coverage.");
        if (fixedIds.Count > 0) warnings.Add("Fixed positions may prevent all connections from flowing left to right.");
        var preview = new GraphLayoutPreview(Guid.NewGuid(), mode, moves, warnings);
        if (_layoutPreviews.Count >= 4) _layoutPreviews.Clear();
        _layoutPreviews.Add(preview.PreviewId, (snapshot, preview, _graphPath.ToArray(), Environment.TickCount64));
        return preview;
    }

    public GraphLayoutPreview ApplyLayout(Guid previewId)
    {
        if (!_layoutPreviews.TryGetValue(previewId, out var prepared) ||
            Environment.TickCount64 - prepared.Created > 5 * 60 * 1000)
            throw new ArgumentException("Layout preview is missing or expired. Request a new preview.");
        if (!_graphPath.SequenceEqual(prepared.Path) ||
            !GraphSnapshot.ContentEquals(prepared.Before, CreateCurrentSnapshot()))
            throw new InvalidOperationException("The graph changed after this preview. Request a new preview.");
        foreach (var move in prepared.Preview.Moves)
        {
            var bounds = GraphFlowLayout.Bounds(GetNode(move.NodeId));
            if (Math.Abs(bounds.Width - move.Width) > 0.01 || Math.Abs(bounds.Height - move.Height) > 0.01)
                throw new InvalidOperationException("Node geometry changed. Request a new layout preview.");
        }
        MoveNodes(prepared.Preview.Moves.Where(move => !move.Fixed)
            .Select(move => (GetNode(move.NodeId), move.X, move.Y)));
        _layoutPreviews.Remove(previewId);
        return prepared.Preview;
    }
}
