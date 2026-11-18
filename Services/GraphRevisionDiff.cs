using System.Text.Json;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services;

public enum GraphDiffKind { Unchanged, Added, Removed, Modified }
public sealed record GraphNodeDifference(string Path, GraphNodeSnapshot? Before, GraphNodeSnapshot? After,
    GraphDiffKind Kind);

public static class GraphRevisionDiff
{
    public static IReadOnlyList<GraphNodeDifference> Compare(GraphSnapshot before, GraphSnapshot after)
    {
        var result = new List<GraphNodeDifference>();
        CompareLevel(before, after, "", result);
        return result;
    }

    private static void CompareLevel(GraphSnapshot before, GraphSnapshot after, string prefix,
        List<GraphNodeDifference> result)
    {
        if (!GraphSnapshot.ContentEquals(before with { Nodes = [], Connections = [] },
                after with { Nodes = [], Connections = [] }))
            result.Add(new(prefix + "$graph", null, null, GraphDiffKind.Modified));
        var old = before.Nodes.ToDictionary(node => node.Id);
        var current = after.Nodes.ToDictionary(node => node.Id);
        foreach (var id in old.Keys.Concat(current.Keys).Distinct())
        {
            old.TryGetValue(id, out var left);
            current.TryGetValue(id, out var right);
            var kind = left is null ? GraphDiffKind.Added : right is null ? GraphDiffKind.Removed :
                EqualNode(left, right) && Edges(before, id).SequenceEqual(Edges(after, id))
                    ? GraphDiffKind.Unchanged : GraphDiffKind.Modified;
            result.Add(new(prefix + id.ToString("D"), left, right, kind));
            if (left?.ChildGraph is not null || right?.ChildGraph is not null)
                CompareLevel(left?.ChildGraph ?? new([], []), right?.ChildGraph ?? new([], []),
                    prefix + id.ToString("D") + "/", result);
        }
    }

    private static bool EqualNode(GraphNodeSnapshot left, GraphNodeSnapshot right) =>
        (left.ChildGraph is null) == (right.ChildGraph is null) &&
        GraphSnapshot.ContentEquals(new([left with { ChildGraph = null }], []),
            new([right with { ChildGraph = null }], []));

    private static IEnumerable<string> Edges(GraphSnapshot graph, Guid id) =>
        graph.Connections.Where(edge => edge.SourceNodeId == id || edge.TargetNodeId == id)
            .Select(edge => JsonSerializer.Serialize(edge)).Order(StringComparer.Ordinal);
}
