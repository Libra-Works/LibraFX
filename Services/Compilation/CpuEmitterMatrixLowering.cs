using System.Security.Cryptography;
using System.Text;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Compilation;

internal static class CpuEmitterMatrixLowering
{
    internal sealed record Binding(GraphSnapshot Graph, Guid[] Rows);
    private static Guid Id(Guid owner, string role) => new(SHA256.HashData(
        Encoding.UTF8.GetBytes($"LiberaFx.cpu-emitter.v1:{owner:D}:{role}")).AsSpan(0, 16), true);
    private static GraphNodeSnapshot Node(string kind, Guid id)
    {
        var definition = GraphNodeDefinition.All.Single(d => d.Kind.Value == kind);
        return new(id, definition.Kind, definition.Name, 0, 0, definition.PortType, definition.PortCategory);
    }
    internal static Guid[] RowIds(Guid system, string role) => Enumerable.Range(0, 4).Select(i => Id(system, role + "-row" + i)).ToArray();
    internal static Binding Bind(GraphSnapshot graph, GraphNodeSnapshot system, string role = "emitter")
    {
        var source = graph.Connections.SingleOrDefault(e => e.TargetNodeId == system.Id && e.TargetPortId == role + "Matrix");
        if (source is null) return new(graph, []);
        var split = Node("MatrixBreak", Id(system.Id, role + "-matrix"));
        var rows = RowIds(system.Id, role);
        var nodes = graph.Nodes.Append(split).Concat(rows.Select(id => Node("FloatX4", id))).ToArray();
        if (nodes.Select(n => n.Id).Distinct().Count() != nodes.Length) throw new InvalidDataException("Emitter Matrix ID collision.");
        var edges = graph.Connections.Where(e => e != source).Append(source with { TargetNodeId = split.Id, TargetPortId = "matrix" })
            .Concat(rows.Select((id, i) => new GraphConnectionSnapshot(split.Id, id, SourcePortId: "row" + i))).ToArray();
        return new(graph with { Nodes = nodes, Connections = edges }, rows);
    }
    internal static GraphSnapshot TransformShapes(GraphSnapshot graph, Guid stage, Guid[] rows,
        out IReadOnlyList<(Guid Inner, Guid Outer)> bindings)
    {
        if (rows.Length == 0) { bindings = []; return graph; }
        var compose = Node("MatrixCompose", Id(stage, "matrix"));
        var innerRows = Enumerable.Range(0, 4).Select(i => Id(stage, "row" + i)).ToArray();
        bindings = innerRows.Zip(rows, (inner, outer) => (inner, outer)).ToArray();
        GraphSnapshot Rewrite(GraphSnapshot current, bool root)
        {
            var nodes = current.Nodes.Select(n => n with { ChildGraph = n.ChildGraph is { } child ? Rewrite(child, false) : null }).ToList();
            var edges = current.Connections.ToList();
            // Ordinary groups are flattened before invoking this method, so all shapes share these row inputs.
            foreach (var shape in current.Nodes.Where(n => CpuParticleSchema.Name(n.Kind) is
                         "CpuParticleEmitterBox" or "CpuParticleEmitterSphere" or "CpuParticleEmitterCircle"))
            {
                var transform = Node("TransformPoint", Id(shape.Id, "point"));
                for (var i = 0; i < edges.Count; i++)
                    if (edges[i].SourceNodeId == shape.Id)
                        edges[i] = edges[i] with { SourceNodeId = transform.Id, SourcePortId = "value" };
                nodes.Add(transform);
                edges.Add(new(shape.Id, transform.Id, "point", "value"));
                edges.Add(new(compose.Id, transform.Id, "matrix", "value"));
            }
            if (root)
            {
                nodes.Add(compose);
                nodes.AddRange(innerRows.Select(id => Node("FloatX4", id)));
                edges.AddRange(innerRows.Select((id, i) => new GraphConnectionSnapshot(id, compose.Id, "row" + i)));
            }
            return current with { Nodes = nodes, Connections = edges };
        }
        return Rewrite(GroupBoundaryLowering.Lower(graph), true);
    }
}
