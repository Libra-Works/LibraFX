using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Compilation;

/// <summary>Composes per-parameter scalar procedural inputs into vector4 blocks before binding.</summary>
internal static class ProceduralLowering
{
    private static readonly string[] ComponentPorts = ["r", "g", "b", "a"];
    private static readonly GraphNodeKind ComposeKind = new("ComposeVector4");

    public static GraphSnapshot Lower(GraphSnapshot graph)
    {
        var nodes = graph.Nodes.Select(node =>
            node.ChildGraph is { } child ? node with { ChildGraph = Lower(child) } : node).ToList();
        var connections = graph.Connections.ToList();
        var changed = false;

        foreach (var node in nodes.ToArray())
        {
            if (!ProceduralResourceSchema.IsMetadata(node.Kind)) continue;
            var scalarEdges = connections
                .Select((edge, index) => (edge, index))
                .Where(item => item.edge.TargetNodeId == node.Id &&
                    ProceduralResourceSchema.TryParseScalarPort(item.edge.TargetPortId, out _, out _))
                .ToArray();
            if (scalarEdges.Length == 0) continue;

            changed = true;
            var definition = GraphNodeDefinition.All.Single(d => d.Kind == node.Kind);
            var recipe = ProceduralResourceSchema.ReadEffective(definition, node.Parameters);
            var blockPorts = ProceduralResourceSchema.EffectiveBlocks(definition, node.Parameters).InputPorts ?? [];
            var removeEdges = new HashSet<GraphConnectionSnapshot>();

            var groups = scalarEdges
                .Select(item =>
                {
                    ProceduralResourceSchema.TryParseScalarPort(item.edge.TargetPortId, out var stageId, out var paramIndex);
                    return (item.edge, stageId, paramIndex, block: paramIndex / 4, component: paramIndex % 4);
                })
                .GroupBy(item => (item.stageId, item.block));

            foreach (var group in groups)
            {
                var (stageId, block) = group.Key;
                var blockPortId = ProceduralResourceSchema.Port(stageId, block);
                var blockPort = blockPorts.FirstOrDefault(p => p.Id == blockPortId);
                if (blockPort is null) continue;

                var items = group.ToArray();
                foreach (var item in items) removeEdges.Add(item.edge);

                if (blockPort.PortType == "Float" && items.Length == 1)
                {
                    connections.Add(items[0].edge with { TargetPortId = blockPortId });
                    continue;
                }

                var stageValues = recipe.Modifiers.FirstOrDefault(m => m.Id == stageId)?.Values
                    ?? recipe.Operations.FirstOrDefault(o => o.Id == stageId)?.Values
                    ?? new float[16];
                var parameters = new List<NodeParameterSnapshot>(4);
                for (var c = 0; c < 4; ++c)
                {
                    var idx = block * 4 + c;
                    var val = idx < stageValues.Length ? stageValues[idx] : 0f;
                    parameters.Add(new(ComponentPorts[c], "float", JsonSerializer.SerializeToElement((double)val)));
                }

                var composeId = ComposeNodeId(node.Id, stageId, block);
                nodes.Add(new GraphNodeSnapshot(composeId, ComposeKind, "Compose Vector4", node.X - 40, node.Y,
                    "FloatX4", GraphPortCategory.Parameter, Parameters: parameters));

                foreach (var item in items)
                {
                    connections.Add(item.edge with
                    {
                        TargetNodeId = composeId,
                        TargetPortId = ComponentPorts[item.component]
                    });
                }
                connections.Add(new GraphConnectionSnapshot(composeId, node.Id, blockPortId, "value"));
            }

            connections.RemoveAll(removeEdges.Contains);
        }

        return changed ? graph with { Nodes = nodes.ToArray(), Connections = connections.ToArray() } : graph;
    }

    private static Guid ComposeNodeId(Guid nodeId, Guid stageId, int block)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"LiberaFx.Procedural.ScalarCompose.v1:{nodeId:D}:{stageId:D}:{block}"));
        return new Guid(digest.AsSpan(0, 16), bigEndian: true);
    }
}
