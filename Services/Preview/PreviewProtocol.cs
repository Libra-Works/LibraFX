using System.Text.Json;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services;

public sealed record PreviewSignalRequest(long Sequence, Guid NodeId);

public sealed class PreviewPublication(long sequence, string documentId, GraphSnapshot graph,
    IReadOnlyList<GraphNodeDefinition> nodeTypes, PreviewPlaybackState? playback = null)
{
    public long Sequence { get; } = sequence;
    public string DocumentId { get; } = documentId;
    public GraphSnapshot Graph { get; } = graph;
    public IReadOnlyList<GraphNodeDefinition> NodeTypes { get; } = nodeTypes;
    public PreviewPlaybackState? Playback { get; } = playback;
    public IReadOnlyList<PreviewSignalRequest> SignalRequests { get; init; } = [];
    public long SignalRequestSequence { get; init; }
    public bool IncludePreviewDebugDraw { get; init; }
}

public sealed record PreviewParameterChange(IReadOnlyList<Guid> GraphPath, Guid NodeId,
    string ParameterId, string Type, JsonElement? Value, bool Remove);

internal sealed record PreviewCurveChange(IReadOnlyList<Guid> GraphPath, Guid NodeId, FCurveSnapshot Curve);

public static class PreviewProtocol
{
    internal static bool Collect(GraphSnapshot previous, GraphSnapshot current, Guid[] path,
        List<PreviewParameterChange> changes, List<PreviewCurveChange> curves)
    {
        if (ReferenceEquals(previous, current)) return true;
        if (previous.AutoDestroyWhenIdle != current.AutoDestroyWhenIdle ||
            !GroupExposedItems.Equal(previous.ExposedItems, current.ExposedItems) ||
            JsonSerializer.Serialize(previous.ResourceIdentities) != JsonSerializer.Serialize(current.ResourceIdentities))
        {
            return false;
        }
        if (previous.Nodes.Count != current.Nodes.Count || !previous.Connections.SequenceEqual(current.Connections)) return false;
        foreach (var (oldNode, node) in previous.Nodes.Zip(current.Nodes))
        {
            if (oldNode with { Parameters = null, Curve = null, ChildGraph = null } !=
                node with { Parameters = null, Curve = null, ChildGraph = null } ||
                (oldNode.Curve is null) != (node.Curve is null) ||
                (oldNode.ChildGraph is null) != (node.ChildGraph is null))
                return false;
            if (oldNode.Curve is not null && !oldNode.Curve.Keys.SequenceEqual(node.Curve!.Keys))
            {
                if (node.Kind != GraphNodeKind.FloatFCurve)
                {
                    return false;
                }
                curves.Add(new(path, node.Id, node.Curve));
            }
            if (oldNode.ChildGraph is not null && !Collect(oldNode.ChildGraph, node.ChildGraph!, [.. path, node.Id], changes, curves))
                return false;
            var oldValues = (oldNode.Parameters ?? []).ToDictionary(parameter => parameter.Id, StringComparer.Ordinal);
            var values = (node.Parameters ?? []).ToDictionary(parameter => parameter.Id, StringComparer.Ordinal);
            foreach (var old in oldValues.Values)
            {
                if (!values.ContainsKey(old.Id))
                    changes.Add(new(path, node.Id, old.Id, old.Type, null, true));
            }
            foreach (var value in values.Values)
            {
                if (oldValues.TryGetValue(value.Id, out var old))
                {
                    if (old.Type != value.Type) return false;
                    if (NodeParameter.ValuesEqual(old.Value, value.Value)) continue;
                }
                changes.Add(new(path, node.Id, value.Id, value.Type, value.Value, false));
            }
        }
        return true;
    }
}
