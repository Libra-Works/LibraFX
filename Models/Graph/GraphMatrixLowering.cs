using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiberaFx.Models.Graph;

public readonly record struct GraphRuntimeOutput(Guid NodeId, string? PortId);
public sealed record MatrixLoweringResult(GraphSnapshot Graph,
    IReadOnlyDictionary<(Guid NodeId, string? PortId), IReadOnlyList<GraphRuntimeOutput>> Outputs);

public static class GraphMatrixLowering
{
    private static readonly HashSet<string> Kinds =
        ["MatrixCompose", "MatrixBreak", "Transform", "MatrixMultiply", "TransformPoint",
            "TransformDirection", "MatrixInverse", "DecomposeTransform", "MatrixRuntimeBoundary",
            "RotatePoint", "TranslatePoint", "OwnerMatrix"];

    public static bool IsMatrixNode(GraphNodeKind kind) => Kinds.Contains(kind.Value);
    private static bool IsMatrixNode(GraphNodeSnapshot node) => IsMatrixNode(node.Kind) ||
        (node.Kind == GraphNodeKind.InputProperty || node.Kind == GraphNodeKind.OutputProperty) &&
        GameIoSchema.Effective(GraphNodeDefinition.All.Single(item => item.Kind == node.Kind),
            node.Parameters).PortType == "Matrix";

    public static MatrixLoweringResult Lower(GraphSnapshot graph)
    {
        var definitions = GraphNodeDefinition.All.ToDictionary(item => item.Kind);
        var byName = GraphNodeDefinition.All.ToDictionary(item => item.QualifiedName);
        GraphNodeDefinition Definition(GraphNodeSnapshot node) =>
            DynamicPinSchema.Effective(
                SequenceSchema.Effective(GameIoSchema.Effective(definitions[node.Kind], node.Parameters), node.Parameters),
                node.Parameters);
        var occupied = new HashSet<Guid>();
        var outputs = new Dictionary<(Guid, string?), IReadOnlyList<GraphRuntimeOutput>>();
        void Collect(GraphSnapshot current)
        {
            foreach (var node in current.Nodes)
            {
                if (!occupied.Add(node.Id)) throw new InvalidDataException("Duplicate graph node ID.");
                if (node.ChildGraph is not null) Collect(node.ChildGraph);
            }
        }
        Collect(graph);

        GraphSnapshot Rewrite(GraphSnapshot current)
        {
            var original = current.Nodes.ToDictionary(node => node.Id);
            var incoming = current.Connections.ToLookup(edge => edge.TargetNodeId);
            var lowered = new List<GraphNodeSnapshot>();
            var edges = new List<GraphConnectionSnapshot>();
            var visiting = new HashSet<Guid>();
            var built = new HashSet<Guid>();

            void Build(GraphNodeSnapshot node)
            {
                if (built.Contains(node.Id)) return;
                if (!visiting.Add(node.Id)) throw new InvalidDataException("The graph contains a cycle.");
                var definition = Definition(node);
                if (!IsMatrixNode(node))
                {
                    lowered.Add(node with { ChildGraph = node.ChildGraph is null ? null : Rewrite(node.ChildGraph) });
                    if (definition.OutputPorts is null)
                        outputs[(node.Id, null)] = [new(node.Id, null)];
                    else
                        foreach (var port in definition.OutputPorts)
                            outputs[(node.Id, port.Id)] = [new(node.Id, port.Id)];
                }
                else
                {
                    GraphRuntimeOutput Emit(string kind, string key,
                        IEnumerable<(string Id, string Type, object Value)>? parameters = null,
                        IEnumerable<(string Port, GraphRuntimeOutput Source)>? inputs = null,
                        bool keepId = false)
                    {
                        var type = byName[$"LiberaFx.{kind}"];
                        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"LiberaFx.matrix:{node.Id:D}:{key}"));
                        var id = keepId ? node.Id : new Guid(hash.AsSpan(0, 16));
                        if (!keepId && (id == Guid.Empty || !occupied.Add(id)))
                            throw new InvalidDataException($"Matrix expansion ID conflicts at node {node.Id}.");
                        lowered.Add(new(id, type.Kind, $"{node.Name}: {key}", node.X, node.Y,
                            type.PortType, type.PortCategory,
                            Parameters: parameters?.Select(parameter => new NodeParameterSnapshot(
                                parameter.Id, parameter.Type, JsonSerializer.SerializeToElement(parameter.Value))).ToArray()));
                        foreach (var (port, source) in inputs ?? [])
                            edges.Add(new(source.NodeId, id, port, source.PortId));
                        return new(id, type.OutputPorts is { Count: 1 } ports ? ports[0].Id : null);
                    }

                    IReadOnlyList<GraphRuntimeOutput>? Connected(string port, string expectedType)
                    {
                        var matches = incoming[node.Id].Where(edge => edge.TargetPortId == port).ToArray();
                        if (matches.Length > 1) throw new InvalidDataException("A matrix input has multiple sources.");
                        if (matches.Length == 0) return null;
                        var edge = matches[0];
                        var source = original[edge.SourceNodeId];
                        if (Definition(source).GetOutputPortType(edge.SourcePortId) != expectedType)
                            throw new InvalidDataException($"Matrix input '{port}' expects {expectedType}.");
                        Build(source);
                        return outputs[(source.Id, edge.SourcePortId)];
                    }

                    GraphRuntimeOutput Numeric(string port, int width)
                    {
                        var connection = Connected(port, $"FloatX{width}");
                        if (connection is not null) return connection.Single();
                        var parameter = definition.Parameters.Single(item => item.Id == port && item.Type == $"vector{width}");
                        var saved = node.Parameters?.FirstOrDefault(item => item.Id == port);
                        var value = saved?.Value ?? parameter.DefaultValue;
                        if (saved is not null && saved.Type != parameter.Type ||
                            !NodeParameter.IsValidValue(parameter.Type, value))
                            throw new InvalidDataException($"Invalid matrix input default '{port}'.");
                        return Emit($"FloatX{width}", $"default-{port}",
                            [("value", parameter.Type, value)]);
                    }

                    GraphRuntimeOutput[] Matrix(string port)
                    {
                        var connection = Connected(port, "Matrix");
                        if (connection is not null)
                        {
                            if (connection.Count != 4) throw new InvalidDataException("A Matrix requires four row values.");
                            return connection.ToArray();
                        }
                        return Enumerable.Range(0, 4).Select(row => Emit("FloatX4", $"identity-{port}-{row}",
                            [("value", "vector4", Enumerable.Range(0, 4).Select(column => row == column ? 1d : 0d).ToArray())])).ToArray();
                    }

                    GraphRuntimeOutput Lane(GraphRuntimeOutput source, int lane, string key)
                    {
                        var split = Emit("BreakVector4", key, inputs: [("a", source)]);
                        return split with { PortId = new[] { "x", "y", "z", "w" }[lane] };
                    }

                    GraphRuntimeOutput Compose(GraphRuntimeOutput[] lanes, string key) =>
                        Emit("ComposeVector4", key, inputs: lanes.Select((source, index) =>
                            (new[] { "r", "g", "b", "a" }[index], source)));

                    GraphRuntimeOutput[] MultiplyMatrices(GraphRuntimeOutput[] parent, GraphRuntimeOutput[] local, string prefix)
                    {
                        var columns = Enumerable.Range(0, 4).Select(column =>
                            Compose(local.Select((source, row) => Lane(source, column, $"{prefix}local-{row}-{column}")).ToArray(),
                                $"{prefix}column{column}")).ToArray();
                        return Enumerable.Range(0, 4).Select(row => Compose(
                            columns.Select((column, index) => Emit("DotVector4", $"{prefix}dot-{row}-{index}",
                                inputs: [("a", parent[row]), ("b", column)])).ToArray(), $"{prefix}row{row}")).ToArray();
                    }

                    void Output(string port, params GraphRuntimeOutput[] values) => outputs[(node.Id, port)] = values;
                    (string, GraphRuntimeOutput)[] RowInputs(GraphRuntimeOutput[] rows) =>
                        rows.Select((source, index) => ($"row{index}", source)).ToArray();

                    switch (node.Kind.Value)
                    {
                        case "MatrixRuntimeBoundary":
                            Output("value", Matrix("matrix"));
                            break;
                        case "OwnerMatrix":
                        case "InputProperty":
                            Output("value", Enumerable.Range(0, 4).Select(row =>
                                Emit("FloatX4", $"input-row{row}",
                                    [("value", "vector4", Enumerable.Range(0, 4)
                                        .Select(column => row == column ? 1d : 0d).ToArray())])).ToArray());
                            break;
                        case "OutputProperty":
                            Output("value", Matrix("value"));
                            break;
                        case "MatrixCompose":
                            Output("value", Enumerable.Range(0, 4).Select(row => Numeric($"row{row}", 4)).ToArray());
                            break;
                        case "MatrixBreak":
                            {
                                var matrix = Matrix("matrix");
                                for (var row = 0; row < 4; row++) Output($"row{row}", matrix[row]);
                                break;
                            }
                        case "Transform":
                            {
                                var inputs = new List<(string, GraphRuntimeOutput)> { ("position", Numeric("position", 3)),
                                ("rotation", Numeric("rotation", 3)), ("scale", Numeric("scale", 3)) };
                                var order = node.Parameters?.FirstOrDefault(parameter => parameter.Id == "order");
                                if (order is not null && (order.Type != "enum:b0af61d2-29f6-4cc9-a71e-000000000201" ||
                                    order.Value.ValueKind != JsonValueKind.Number ||
                                    !order.Value.TryGetInt32(out _) || order.Value.GetInt32() is < 0 or > 5))
                                    throw new InvalidDataException("Transform order must be a declared TransformOrder value.");
                                var value = order?.Value.GetInt32() ?? 0;
                                if (value != 0)
                                    inputs.Add(("order", Emit("Float", "order", [("value", "float", (double)value)])));
                                var local = Enumerable.Range(0, 4).Select(row =>
                                    Emit(value == 0 ? $"MatrixRuntimeTransformRow{row}" : $"MatrixRuntimeOrderedTransformRow{row}",
                                        $"row{row}", inputs: inputs)).ToArray();
                                var parent = Connected("parent", "Matrix");
                                if (parent is not null && parent.Count != 4)
                                {
                                    throw new InvalidDataException("A Matrix requires four row values.");
                                }
                                Output("value", parent is null ? local : MultiplyMatrices(parent.ToArray(), local, "parented-"));
                                break;
                            }
                        case "MatrixMultiply":
                            {
                                var parent = Matrix("parent");
                                var local = Matrix("local");
                                Output("value", MultiplyMatrices(parent, local, ""));
                                break;
                            }
                        case "TransformPoint":
                        case "TransformDirection":
                            {
                                var point = node.Kind.Value == "TransformPoint";
                                var matrix = Matrix("matrix");
                                var vector = Numeric(point ? "point" : "direction", 3);
                                var homogeneous = Emit("Vector3ToVector4", "homogeneous",
                                    [("fill", "float", point ? 1d : 0d)], [("a", vector)]);
                                var components = matrix.Select((row, index) =>
                                    Emit("DotVector4", $"component{index}", inputs: [("a", row), ("b", homogeneous)])).ToArray();
                                var packed = Compose(components, "packed");
                                Output("value", Emit("Vector4ToVector3", "point-result", inputs: [("a", packed)], keepId: true));
                                Output("w", components[3]);
                                break;
                            }
                        case "RotatePoint":
                            {
                                var rotation = Numeric("rotation", 3);
                                var vector = Numeric("point", 3);
                                var homogeneous = Emit("Vector3ToVector4", "homogeneous",
                                    [("fill", "float", 1d)], [("a", vector)]);
                                var rows = Enumerable.Range(0, 4).Select(row =>
                                    Emit($"MatrixRuntimeTransformRow{row}", $"row{row}", inputs: [("rotation", rotation)])).ToArray();
                                var components = rows.Select((row, index) =>
                                    Emit("DotVector4", $"component{index}", inputs: [("a", row), ("b", homogeneous)])).ToArray();
                                Output("value", Emit("Vector4ToVector3", "point-result",
                                    inputs: [("a", Compose(components, "packed"))], keepId: true));
                                break;
                            }
                        case "TranslatePoint":
                            Output("value", Emit("AddVector3", "point-result",
                                inputs: [("a", Numeric("point", 3)), ("b", Numeric("translation", 3))], keepId: true));
                            break;
                        case "MatrixInverse":
                            {
                                var inputs = RowInputs(Matrix("matrix"));
                                Output("value", Enumerable.Range(0, 4).Select(row =>
                                    Emit($"MatrixRuntimeInverseRow{row}", $"row{row}", inputs: inputs)).ToArray());
                                Output("valid", Emit("MatrixRuntimeInverseValid", "valid", inputs: inputs));
                                break;
                            }
                        case "DecomposeTransform":
                            {
                                var inputs = RowInputs(Matrix("matrix"));
                                foreach (var port in new[] { "position", "rotation", "scale", "valid" })
                                    Output(port, Emit($"MatrixRuntimeDecompose{char.ToUpperInvariant(port[0])}{port[1..]}",
                                        port, inputs: inputs, keepId: port == "position"));
                                break;
                            }
                    }
                }
                visiting.Remove(node.Id);
                built.Add(node.Id);
            }
            foreach (var node in current.Nodes) Build(node);
            foreach (var edge in current.Connections)
            {
                if (IsMatrixNode(original[edge.TargetNodeId])) continue;
                var source = outputs[(edge.SourceNodeId, edge.SourcePortId)];
                if (original[edge.TargetNodeId].Kind.Value is "DebugText" or "DebugModel" &&
                    edge.TargetPortId == "matrix")
                {
                    if (source.Count != 4) throw new InvalidDataException("Debug world Matrix requires four rows.");
                    for (var row = 0; row < 4; row++)
                        edges.Add(new(source[row].NodeId, edge.TargetNodeId, $"row{row}", source[row].PortId));
                    continue;
                }
                if (original[edge.TargetNodeId].Kind.Value == "GpuParticle" && edge.TargetPortId is "drawMatrix" or "emitterMatrix")
                {
                    var role = edge.TargetPortId == "emitterMatrix" ? "emitter" : "draw";
                    if (source.Count != 4)
                    {
                        throw new InvalidDataException("A GPU Draw Matrix requires four rows.");
                    }
                    var target = original[edge.TargetNodeId];
                    var definition = byName["LiberaFx.FloatX4"];
                    for (var row = 0; row < 4; row++)
                    {
                        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"LiberaFx.gpu-{role}.v1:{target.Id:D}:{row}"));
                        var rowId = new Guid(hash.AsSpan(0, 16));
                        if (rowId == Guid.Empty || !occupied.Add(rowId))
                        {
                            throw new InvalidDataException($"GPU Draw Matrix row ID conflicts at node {target.Id}.");
                        }
                        lowered.Add(new(rowId, definition.Kind, $"{target.Name}: draw row {row}",
                            target.X, target.Y, definition.PortType, definition.PortCategory));
                        edges.Add(new(source[row].NodeId, rowId, SourcePortId: source[row].PortId));
                        edges.Add(new(rowId, target.Id, $"{role}Row{row}"));
                    }
                    continue;
                }
                if (source.Count != 1)
                    throw new InvalidDataException("A Matrix output requires a Matrix-aware input.");
                edges.Add(new(source[0].NodeId, edge.TargetNodeId, edge.TargetPortId, source[0].PortId));
            }
            return new(lowered, edges);
        }
        return new(Rewrite(graph), outputs);
    }
}
