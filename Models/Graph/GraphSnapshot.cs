namespace LiberaFx.Models.Graph;

public sealed record GraphSnapshot(
    IReadOnlyList<GraphNodeSnapshot> Nodes,
    IReadOnlyList<GraphConnectionSnapshot> Connections)
{
    public static bool ContentEquals(GraphSnapshot left, GraphSnapshot right) =>
        left.Nodes.Count == right.Nodes.Count &&
        left.Nodes.Zip(right.Nodes).All(pair =>
            pair.First with { Curve = null, ChildGraph = null, Parameters = null } ==
                pair.Second with { Curve = null, ChildGraph = null, Parameters = null } &&
            ParametersEqual(pair.First.Parameters, pair.Second.Parameters) &&
            (pair.First.Curve is null && pair.Second.Curve is null ||
             pair.First.Curve is not null && pair.Second.Curve is not null &&
             pair.First.Curve.Keys.SequenceEqual(pair.Second.Curve.Keys)) &&
            (pair.First.ChildGraph is null && pair.Second.ChildGraph is null ||
             pair.First.ChildGraph is not null && pair.Second.ChildGraph is not null &&
             ContentEquals(pair.First.ChildGraph, pair.Second.ChildGraph))) &&
        left.Connections.SequenceEqual(right.Connections);

    public static bool EquivalentIgnoringIds(GraphSnapshot left, GraphSnapshot right)
    {
        if (left.Nodes.Count != right.Nodes.Count || left.Connections.Count != right.Connections.Count) return false;
        var ids = new Dictionary<Guid, Guid>();
        for (var index = 0; index < left.Nodes.Count; index++)
        {
            var a = left.Nodes[index];
            var b = right.Nodes[index];
            if (!ids.TryAdd(a.Id, b.Id) ||
                a with { Id = Guid.Empty, Curve = null, ChildGraph = null, Parameters = null } !=
                b with { Id = Guid.Empty, Curve = null, ChildGraph = null, Parameters = null } ||
                !ParametersEqual(a.Parameters, b.Parameters) ||
                (a.Curve is null) != (b.Curve is null) ||
                (a.Curve is not null && !a.Curve.Keys.SequenceEqual(b.Curve!.Keys)) ||
                (a.ChildGraph is null) != (b.ChildGraph is null) ||
                (a.ChildGraph is not null && !EquivalentIgnoringIds(a.ChildGraph, b.ChildGraph!)))
                return false;
        }
        return left.Connections.Zip(right.Connections).All(pair =>
            ids.TryGetValue(pair.First.SourceNodeId, out var source) && source == pair.Second.SourceNodeId &&
            ids.TryGetValue(pair.First.TargetNodeId, out var target) && target == pair.Second.TargetNodeId &&
            pair.First.TargetPortId == pair.Second.TargetPortId);
    }

    private static bool ParametersEqual(IReadOnlyList<NodeParameterSnapshot>? left, IReadOnlyList<NodeParameterSnapshot>? right) =>
        left is null && right is null ||
        left is not null && right is not null && left.Count == right.Count &&
        left.Zip(right).All(pair => pair.First.Id == pair.Second.Id && pair.First.Type == pair.Second.Type &&
                                     NodeParameter.ValuesEqual(pair.First.Value, pair.Second.Value));
}

public sealed record GraphNodeSnapshot(
    Guid Id,
    GraphNodeKind Kind,
    string Name,
    double X,
    double Y,
    string PortType,
    GraphPortCategory PortCategory,
    FCurveSnapshot? Curve = null,
    GraphSnapshot? ChildGraph = null,
    IReadOnlyList<NodeParameterSnapshot>? Parameters = null,
    GroupAssetReference? GroupAsset = null);

public sealed record GroupAssetReference(Guid Id, long Revision, bool Locked);

public sealed record GraphConnectionSnapshot(Guid SourceNodeId, Guid TargetNodeId, string? TargetPortId = null);
