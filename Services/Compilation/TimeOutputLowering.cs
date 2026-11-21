using System.Security.Cryptography;
using System.Text;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Compilation;

internal static class TimeOutputLowering
{
    internal static Guid RealOutputId(Guid id) => new(SHA256.HashData(
        Encoding.UTF8.GetBytes("LiberaFx.real-time.v1:" + id.ToString("D"))).AsSpan(0, 16), true);

    internal static GraphSnapshot Lower(GraphSnapshot graph)
    {
        var nodes = graph.Nodes.Select(node => node with { ChildGraph = node.ChildGraph is { } child ? Lower(child) : null }).ToList();
        var edges = graph.Connections.ToList();
        for (var index = 0; index < graph.Nodes.Count; index++)
        {
            var node = nodes[index];
            if (node.Kind.Value is not ("EffectAge" or "InstanceAge")) continue;
            var effect = node.Kind.Value == "EffectAge";
            var scaled = GraphNodeDefinition.Find(new(effect ? "RuntimeEffectAge" : "Time"))!;
            var real = GraphNodeDefinition.Find(new(effect ? "RealEffectAge" : "RealInstanceAge"))!;
            nodes[index] = node with { Kind = scaled.Kind };
            var realId = RealOutputId(node.Id);
            if (nodes.Any(item => item.Id == realId)) throw new InvalidDataException("Time output identity collision.");
            nodes.Add(new(realId, real.Kind, real.Name, node.X, node.Y, real.PortType, real.PortCategory));
            for (var edge = 0; edge < edges.Count; edge++)
                if (edges[edge].SourceNodeId == node.Id)
                    edges[edge] = edges[edge].SourcePortId == "real"
                        ? edges[edge] with { SourceNodeId = realId, SourcePortId = "value" }
                        : edges[edge] with { SourcePortId = effect ? "value" : null };
        }
        return graph with { Nodes = nodes, Connections = edges };
    }
}
