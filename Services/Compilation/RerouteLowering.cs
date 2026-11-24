using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Compilation;

internal static class RerouteLowering
{
    public static GraphSnapshot Lower(GraphSnapshot graph)
    {
        var nodes = graph.Nodes.Select(node => node.ChildGraph is { } child
            ? node with { ChildGraph = Lower(child) }
            : node).ToArray();
        var rerouteIds = nodes.Where(node => node.Kind.Value == "Reroute").Select(node => node.Id).ToHashSet();
        if (rerouteIds.Count == 0)
        {
            return ReferenceEquals(nodes, graph.Nodes) ? graph : graph with { Nodes = nodes };
        }

        var incomingByReroute = new Dictionary<Guid, GraphConnectionSnapshot>();
        foreach (var edge in graph.Connections)
        {
            if (rerouteIds.Contains(edge.TargetNodeId))
            {
                incomingByReroute[edge.TargetNodeId] = edge;
            }
        }

        (Guid SourceNodeId, string? SourcePortId)? ResolveUpstream(Guid nodeId, string? portId)
        {
            var visited = new HashSet<Guid>();
            var currentId = nodeId;
            var currentPort = portId;
            while (rerouteIds.Contains(currentId))
            {
                if (!visited.Add(currentId))
                {
                    throw new InvalidDataException("Reroute nodes cannot form a cycle.");
                }
                if (!incomingByReroute.TryGetValue(currentId, out var edge))
                {
                    return null;
                }
                currentId = edge.SourceNodeId;
                currentPort = edge.SourcePortId;
            }
            return (currentId, currentPort);
        }

        var resolvedConnections = new List<GraphConnectionSnapshot>(graph.Connections.Count);
        foreach (var edge in graph.Connections)
        {
            if (rerouteIds.Contains(edge.TargetNodeId)) continue;
            if (rerouteIds.Contains(edge.SourceNodeId))
            {
                if (ResolveUpstream(edge.SourceNodeId, edge.SourcePortId) is { } upstream)
                {
                    resolvedConnections.Add(edge with
                    {
                        SourceNodeId = upstream.SourceNodeId,
                        SourcePortId = upstream.SourcePortId
                    });
                }
                continue;
            }
            resolvedConnections.Add(edge);
        }

        return graph with
        {
            Nodes = nodes.Where(node => !rerouteIds.Contains(node.Id)).ToArray(),
            Connections = resolvedConnections.ToArray()
        };
    }
}
