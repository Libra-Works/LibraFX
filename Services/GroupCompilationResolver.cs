using System.Security.Cryptography;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services;

public sealed class GroupCompilationResolver(IReadOnlyList<GroupAssetLibrary> libraries, bool useEmbedded = false)
{
    private readonly IReadOnlyList<GroupAssetLibrary> _libraries =
        libraries.Append(new GroupAssetLibrary(StandardGroupAssetLibrary.DefaultDirectory)).ToArray();
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
                    if (node.GroupOverrides is { Count: > 0 } && asset.Revision != reference.Revision)
                        throw new InvalidDataException($"Linked group '{node.Name}' has instance values and a newer revision; review the update before compiling.");
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
        string? MapBoundary(Guid nodeId, string? port, bool output)
        {
            if (useEmbedded || port is null or "spawn" or "then" or "destroy") return port;
            var original = graph.Nodes.First(node => node.Id == nodeId);
            var kind = original.Kind;
            if ((kind != GraphNodeKind.SpawnableGroup && kind != GraphNodeKind.GraphGroup) ||
                original.GroupAsset is null || original.ChildGraph is null)
                return port;
            bool IsBoundary(GraphNodeSnapshot node) => kind == GraphNodeKind.GraphGroup
                ? output ? GroupBoundarySchema.IsOutput(node.Kind) : GroupBoundarySchema.IsInput(node.Kind)
                : output ? SpawnableGroupSchema.IsOutput(node.Kind) : SpawnableGroupSchema.IsInput(node.Kind);
            string Port(Guid id) => kind == GraphNodeKind.GraphGroup
                ? output ? GroupBoundarySchema.OutputPortId(id) : GroupBoundarySchema.InputPortId(id)
                : output ? SpawnableGroupSchema.OutputPortId(id) : SpawnableGroupSchema.PortId(id);
            var input = original.ChildGraph.Nodes.FirstOrDefault(node =>
                IsBoundary(node) && Port(node.Id) == port);
            if (input is null) return port;
            var baseline = LoadRevision(original.GroupAsset.Id, original.GroupAsset.Revision).Graph;
            var source = baseline.Nodes.FirstOrDefault(node => node.Id == input.Id && IsBoundary(node));
            if (source is null && GraphSnapshot.EquivalentIgnoringIds(baseline, original.ChildGraph))
            {
                var index = original.ChildGraph.Nodes.ToList().FindIndex(node => node.Id == input.Id);
                if (index >= 0) source = baseline.Nodes[index];
            }
            if (source is null || !IsBoundary(source))
                throw new InvalidDataException($"Linked group boundary '{input.Name}' cannot be matched to its saved revision.");
            var resolved = nodes.First(node => node.Id == original.Id);
            var derived = DeriveId(original.Id, source.Id);
            if (!resolved.ChildGraph!.Nodes.Any(node => node.Id == derived && IsBoundary(node)))
                throw new InvalidDataException($"Linked group boundary '{input.Name}' no longer exists.");
            return Port(derived);
        }
        var connections = graph.Connections.Select(connection => connection with
        {
            TargetPortId = MapBoundary(connection.TargetNodeId, connection.TargetPortId, false),
            SourcePortId = MapBoundary(connection.SourceNodeId, connection.SourcePortId, true)
        }).ToArray();
        return graph with { Nodes = nodes.AsReadOnly(), Connections = connections };
    }

    private GroupAsset Load(Guid id)
    {
        if (_assets.TryGetValue(id, out var cached)) return cached;
        GroupAsset? found = null;
        foreach (var library in _libraries)
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
        foreach (var library in _libraries)
        {
            try { return library.LoadRevision(id, revision); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        throw new InvalidDataException($"Group asset {id} revision {revision} was not found.");
    }

    private static GraphSnapshot Clone(GraphSnapshot graph, Guid scope)
    {
        var ids = new Dictionary<Guid, Guid>();
        void Collect(GraphSnapshot current, Guid owner)
        {
            foreach (var node in current.Nodes)
            {
                ids.Add(node.Id, DeriveId(owner, node.Id));
                if (node.ChildGraph is { } child) Collect(child, ids[node.Id]);
            }
        }
        Collect(graph, scope);
        GraphSnapshot Copy(GraphSnapshot current)
        {
            var nodes = current.Nodes.Select(node => node with
            {
                Id = ids[node.Id], Parameters = GpuParticleEditorService.RemapReferences(node, ids),
                ChildGraph = node.ChildGraph is null ? null : Copy(node.ChildGraph)
            }).ToArray();
            return current with
            {
                Nodes = nodes, ExposedItems = GroupExposedItems.Remap(current.ExposedItems, ids),
                Connections = current.Connections.Select(connection => connection with
                {
                    SourceNodeId = ids[connection.SourceNodeId], TargetNodeId = ids[connection.TargetNodeId],
                    SourcePortId = SpawnableGroupSchema.RemapPort(current.Nodes.Single(node => node.Id == connection.SourceNodeId),
                        nodes.Single(node => node.Id == ids[connection.SourceNodeId]).ChildGraph, connection.SourcePortId),
                    TargetPortId = SpawnableGroupSchema.RemapPort(current.Nodes.Single(node => node.Id == connection.TargetNodeId),
                        nodes.Single(node => node.Id == ids[connection.TargetNodeId]).ChildGraph, connection.TargetPortId)
                }).ToArray()
            };
        }
        return Copy(graph);
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
