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
        return graph with { Nodes = nodes.AsReadOnly() };
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

    private static GraphSnapshot Clone(GraphSnapshot graph, Guid scope)
    {
        var ids = graph.Nodes.ToDictionary(node => node.Id, node => DeriveId(scope, node.Id));
        return new GraphSnapshot(graph.Nodes.Select(node => node with
        {
            Id = ids[node.Id],
            ChildGraph = node.ChildGraph is null ? null : Clone(node.ChildGraph, ids[node.Id])
        }).ToArray(), graph.Connections.Select(connection => connection with
        {
            SourceNodeId = ids[connection.SourceNodeId],
            TargetNodeId = ids[connection.TargetNodeId]
        }).ToArray());
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
