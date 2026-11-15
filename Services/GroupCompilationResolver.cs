using System.Security.Cryptography;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services;

public sealed class GroupCompilationResolver(IReadOnlyList<GroupAssetLibrary> libraries, bool useEmbedded = false)
{
    private readonly Dictionary<Guid, GroupAsset> _assets = [];

    public GraphSnapshot Resolve(GraphSnapshot graph)
    {
        GraphValidator.Validate(graph);
        _assets.Clear();
        var result = ResolveGraph(graph, new HashSet<Guid>(), 0);
        GraphValidator.Validate(result);
        return result;
    }

    private GraphSnapshot ResolveGraph(GraphSnapshot graph, HashSet<Guid> ancestors, int depth)
    {
        if (depth > 12) throw new InvalidDataException("The graph hierarchy is too deep.");
        var nodes = new List<GraphNodeSnapshot>(graph.Nodes.Count);
        foreach (var node in graph.Nodes)
        {
            if (node.ChildGraph is null) { nodes.Add(node); continue; }
            if (!useEmbedded && node.GroupAsset is { } reference)
            {
                if (!ancestors.Add(reference.Id))
                    throw new InvalidDataException($"Cyclic linked group asset: {reference.Id}.");
                try
                {
                    var asset = Load(reference.Id);
                    var child = ResolveGraph(Clone(asset.Graph, node.Id), ancestors, depth + 1);
                    nodes.Add(node with
                    {
                        ChildGraph = child,
                        GroupAsset = new(asset.Id, asset.Revision, asset.Locked)
                    });
                }
                finally { ancestors.Remove(reference.Id); }
            }
            else nodes.Add(node with { ChildGraph = ResolveGraph(node.ChildGraph, ancestors, depth + 1) });
        }
        var connections = graph.Connections.Select(connection =>
        {
            if (useEmbedded || connection.TargetPortId is null or "spawn") return connection;
            var original = graph.Nodes.First(node => node.Id == connection.TargetNodeId);
            if (original.Kind != GraphNodeKind.SpawnableGroup || original.GroupAsset is null || original.ChildGraph is null)
                return connection;
            var input = original.ChildGraph.Nodes.FirstOrDefault(node =>
                SpawnableGroupSchema.IsInput(node.Kind) && SpawnableGroupSchema.PortId(node.Id) == connection.TargetPortId);
            if (input is null) return connection;
            var baseline = LoadRevision(original.GroupAsset.Id, original.GroupAsset.Revision).Graph;
            var source = baseline.Nodes.FirstOrDefault(node => node.Id == input.Id && SpawnableGroupSchema.IsInput(node.Kind));
            if (source is null && GraphSnapshot.EquivalentIgnoringIds(baseline, original.ChildGraph))
            {
                var index = original.ChildGraph.Nodes.ToList().FindIndex(node => node.Id == input.Id);
                if (index >= 0) source = baseline.Nodes[index];
            }
            if (source is null || !SpawnableGroupSchema.IsInput(source.Kind))
                throw new InvalidDataException($"Linked Spawnable Group input '{input.Name}' cannot be matched to its saved revision.");
            var resolved = nodes.First(node => node.Id == original.Id);
            var derived = DeriveId(original.Id, source.Id);
            if (!resolved.ChildGraph!.Nodes.Any(node => node.Id == derived && SpawnableGroupSchema.IsInput(node.Kind)))
                throw new InvalidDataException($"Linked Spawnable Group input '{input.Name}' no longer exists.");
            return connection with { TargetPortId = SpawnableGroupSchema.PortId(derived) };
        }).ToArray();
        return graph with { Nodes = nodes.AsReadOnly(), Connections = connections };
    }

    private GroupAsset Load(Guid id)
    {
        if (_assets.TryGetValue(id, out var cached)) return cached;
        GroupAsset? found = null;
        foreach (var library in libraries)
        {
            GroupAsset candidate;
            try { candidate = library.Load(id); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if (found is not null &&
                (found.Revision != candidate.Revision || !GraphSnapshot.ContentEquals(found.Graph, candidate.Graph)))
                throw new InvalidDataException($"Multiple group libraries contain conflicting asset {id}.");
            found = candidate;
        }
        if (found is null)
            throw new InvalidDataException($"Latest group asset {id} was not found. Configure --groups, or explicitly use --embedded-groups for frozen versions.");
        _assets.Add(id, found);
        return found;
    }

    private GroupAsset LoadRevision(Guid id, long revision)
    {
        foreach (var library in libraries)
        {
            try { return library.LoadRevision(id, revision); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        throw new InvalidDataException($"Group asset {id} revision {revision} was not found.");
    }

    private static GraphSnapshot Clone(GraphSnapshot graph, Guid scope)
    {
        var ids = graph.Nodes.ToDictionary(node => node.Id, node => DeriveId(scope, node.Id));
        var nodes = graph.Nodes.Select(node => node with
        {
            Id = ids[node.Id],
            ChildGraph = node.ChildGraph is null ? null : Clone(node.ChildGraph, ids[node.Id])
        }).ToArray();
        return new GraphSnapshot(nodes, graph.Connections.Select(connection => connection with
        {
            SourceNodeId = ids[connection.SourceNodeId],
            TargetNodeId = ids[connection.TargetNodeId],
            TargetPortId = RemapPort(graph.Nodes.Single(node => node.Id == connection.TargetNodeId),
                nodes.Single(node => node.Id == ids[connection.TargetNodeId]).ChildGraph, connection.TargetPortId)
        }).ToArray());
    }

    private static string? RemapPort(GraphNodeSnapshot original, GraphSnapshot? clone, string? port)
    {
        if (original.Kind != GraphNodeKind.SpawnableGroup || original.ChildGraph is null || clone is null) return port;
        for (var i = 0; i < original.ChildGraph.Nodes.Count; ++i)
            if (SpawnableGroupSchema.IsInput(original.ChildGraph.Nodes[i].Kind) &&
                SpawnableGroupSchema.PortId(original.ChildGraph.Nodes[i].Id) == port)
                return SpawnableGroupSchema.PortId(clone.Nodes[i].Id);
        return port;
    }

    private static Guid DeriveId(Guid scope, Guid source)
    {
        Span<byte> input = stackalloc byte[32];
        scope.TryWriteBytes(input[..16], bigEndian: true, out _);
        source.TryWriteBytes(input[16..], bigEndian: true, out _);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        hash[6] = (byte)((hash[6] & 15) | 0x80);
        hash[8] = (byte)((hash[8] & 63) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }
}
