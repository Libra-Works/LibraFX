using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Compilation;

public static class SignalTimelineLowering
{
    public static GraphSnapshot Lower(GraphSnapshot graph, bool includePreview = false) =>
        !SignalTimeline.ContainsKeys(graph) ? graph : LowerScope(graph, Guid.Empty, includePreview);

    private static GraphSnapshot LowerScope(GraphSnapshot graph, Guid scope, bool includePreview)
    {
        ValidateNestedLifecycle(graph);
        var flat = GroupBoundaryLowering.Lower(graph);
        var entries = flat.Nodes.SelectMany(node => (node.SignalKeys ?? [])
            .Where(key => includePreview || key.Mode == SignalTimelineMode.Runtime)
            .Select(key => (Node: node, Key: key)))
            .OrderBy(item => item.Key.TimeSeconds).ToArray();
        var nodes = flat.Nodes.Select(node => node with
        {
            SignalKeys = null,
            ChildGraph = node.Kind == GraphNodeKind.SpawnableGroup && node.ChildGraph is { } child
                ? LowerScope(child, node.Id, includePreview) : node.ChildGraph
        }).ToList();
        var edges = flat.Connections.ToList();
        var ids = nodes.Select(node => node.Id).ToHashSet();
        var scheduled = new List<(Guid Node, string? Port)>();
        GraphNodeSnapshot Create(Guid id, string kind, IReadOnlyList<NodeParameterSnapshot>? parameters = null)
        {
            if (!ids.Add(id)) throw new InvalidDataException("A generated Signal timeline ID collides with a graph node.");
            var definition = GraphNodeDefinition.All.Single(item => item.Kind.Value == kind);
            var result = new GraphNodeSnapshot(id, definition.Kind, "Signal timeline " + kind,
                0, 0, definition.PortType, definition.PortCategory, Parameters: parameters);
            nodes.Add(result);
            return result;
        }
        foreach (var (node, key) in entries)
        {
            var outgoing = flat.Connections.Where(edge => edge.SourceNodeId == node.Id).ToArray();
            if (outgoing.Length == 0) continue;
            if (outgoing.Length != 1)
                throw new InvalidDataException("A Signal timeline entry requires one outgoing connection; use Sequence to branch.");
            var delay = Create(Id(node.Id, key.Id, "delay"), "Delay",
                [new("seconds", "float", JsonSerializer.SerializeToElement(key.TimeSeconds))]);
            scheduled.Add((delay.Id, "signal"));
            edges.Add(new(delay.Id, outgoing[0].TargetNodeId, outgoing[0].TargetPortId, "then"));
        }
        if (scheduled.Count != 0)
        {
            var starts = nodes.Where(node => node.Kind.Value == "OnStart").ToArray();
            if (starts.Length > 1) throw new InvalidDataException("A Signal timeline scope supports one On Start entry.");
            var start = starts.SingleOrDefault() ?? Create(Id(scope, Guid.Empty, "start"), "OnStart");
            var existing = edges.Where(edge => edge.SourceNodeId == start.Id).ToArray();
            edges.RemoveAll(edge => edge.SourceNodeId == start.Id);
            var targets = existing.Select(edge => (Node: edge.TargetNodeId, Port: edge.TargetPortId))
                .Concat(scheduled).ToArray();
            var offset = 0;
            var source = start.Id;
            var sourcePort = "then";
            while (offset < targets.Length)
            {
                var remaining = targets.Length - offset;
                var take = Math.Min(remaining, SequenceSchema.MaximumOutputs - 1);
                var more = remaining > take;
                var sequence = Create(Id(start.Id, Guid.Empty, "sequence:" + offset), "Sequence",
                    [new("outputs", "int", JsonSerializer.SerializeToElement(take + (more ? 1 : 0)))]);
                edges.Add(new(source, sequence.Id, "signal", sourcePort));
                for (var index = 0; index < take; ++index)
                    edges.Add(new(sequence.Id, targets[offset + index].Node,
                        targets[offset + index].Port, SequenceSchema.OutputId(index)));
                offset += take;
                source = sequence.Id;
                sourcePort = SequenceSchema.OutputId(take);
            }
        }
        return new(nodes, edges) { AutoDestroyWhenIdle = graph.AutoDestroyWhenIdle };
    }

    private static Guid Id(Guid owner, Guid key, string role) => new(
        SHA256.HashData(Encoding.UTF8.GetBytes($"LiberaFx.SignalTimeline.v1:{owner:D}:{key:D}:{role}")).AsSpan(0, 16),
        bigEndian: true);

    private static void ValidateNestedLifecycle(GraphSnapshot graph, bool nested = false)
    {
        foreach (var node in graph.Nodes)
        {
            if (nested && (node.Kind.Value is "OnStart" or "OnDestroy" or "OwnerDestroy" or "LifeTime" ||
                SpawnableGroupSchema.IsInput(node.Kind) || SpawnableGroupSchema.IsOutput(node.Kind) ||
                SpawnableGroupSchema.IsSignalInput(node.Kind) || SpawnableGroupSchema.IsSignalOutput(node.Kind)))
                throw new InvalidDataException("Lifecycle and Spawn boundary nodes must remain directly in their owning scope.");
            if (node.Kind == GraphNodeKind.GraphGroup && node.ChildGraph is { } child)
                ValidateNestedLifecycle(child, true);
        }
    }
}
