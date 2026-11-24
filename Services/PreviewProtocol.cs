using System.Text.Json;
using System.Text.Json.Serialization;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services;

public sealed record PreviewSignalRequest(long Sequence, Guid NodeId);

public sealed class PreviewPublication(long sequence, string documentId, GraphSnapshot graph,
    IReadOnlyList<GraphNodeDefinition> nodeTypes, PreviewPlaybackState? playback = null)
{
    private readonly Lazy<byte[]> _legacy = new(() => PreviewProtocol.EncodeLegacy(sequence, documentId, graph, nodeTypes));
    public long Sequence { get; } = sequence;
    public string DocumentId { get; } = documentId;
    public GraphSnapshot Graph { get; } = graph;
    public IReadOnlyList<GraphNodeDefinition> NodeTypes { get; } = nodeTypes;
    public PreviewPlaybackState? Playback { get; } = playback;
    public IReadOnlyList<PreviewSignalRequest> SignalRequests { get; init; } = [];
    public long SignalRequestSequence { get; init; }
    public bool IncludePreviewDebugDraw { get; init; }
    public byte[] LegacyPayload => _legacy.Value;
}

public sealed record PreviewParameterChange(IReadOnlyList<Guid> GraphPath, Guid NodeId,
    string ParameterId, string Type, JsonElement? Value, bool Remove);

public static class PreviewProtocol
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static byte[] Encode(PreviewPublication publication, PreviewPublication? baseline, bool forceSnapshot = false)
    {
        var changes = new List<PreviewParameterChange>();
        if (!forceSnapshot && baseline is not null && baseline.DocumentId == publication.DocumentId &&
            baseline.Sequence < publication.Sequence &&
            Collect(baseline.Graph, publication.Graph, [], changes))
        {
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "parameters", protocolVersion = 2,
                sequence = publication.Sequence, baseSequence = baseline.Sequence,
                documentId = publication.DocumentId, changes, playback = publication.Playback
            }, Options);
        }
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "snapshot", protocolVersion = 2, sequence = publication.Sequence,
            documentId = publication.DocumentId, graph = publication.Graph,
            nodeTypes = publication.NodeTypes,
            enumTypes = GraphEnumDefinition.All.Values,
            playback = publication.Playback
        }, Options);
    }

    internal static byte[] EncodeLegacy(long sequence, string documentId, GraphSnapshot graph,
        IReadOnlyList<GraphNodeDefinition> nodeTypes) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        type = "snapshot", protocolVersion = 1, sequence, documentId, graph,
        nodeTypes = nodeTypes.Select(type => new { kind = type.Kind.Value, type.TypeId, type.Namespace, type.TypeName })
    }, Options);

    internal static bool Collect(GraphSnapshot previous, GraphSnapshot current, Guid[] path,
        List<PreviewParameterChange> changes)
    {
        if (ReferenceEquals(previous, current)) return true;
        if (previous.Nodes.Count != current.Nodes.Count || !previous.Connections.SequenceEqual(current.Connections)) return false;
        foreach (var (oldNode, node) in previous.Nodes.Zip(current.Nodes))
        {
            if (oldNode with { Parameters = null, Curve = null, ChildGraph = null } !=
                node with { Parameters = null, Curve = null, ChildGraph = null } ||
                (oldNode.Curve is null) != (node.Curve is null) ||
                oldNode.Curve is not null && !oldNode.Curve.Keys.SequenceEqual(node.Curve!.Keys) ||
                (oldNode.ChildGraph is null) != (node.ChildGraph is null))
                return false;
            if (oldNode.ChildGraph is not null && !Collect(oldNode.ChildGraph, node.ChildGraph!, [.. path, node.Id], changes))
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
