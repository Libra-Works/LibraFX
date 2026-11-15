using System.Text.Json;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services;

/// <summary>
/// Column-vector affine matrices, represented by four FloatX4 row nodes.
/// This keeps the portable four-lane ABI: world = parent * local.
/// </summary>
public sealed class MatrixGraphAuthoring
{
    public readonly record struct Output(Guid NodeId, string? Port = null);
    public sealed record Matrix(Output[] Rows);
    private readonly List<GraphNodeSnapshot> _nodes = [];
    private readonly List<GraphConnectionSnapshot> _connections = [];
    private readonly Dictionary<string, GraphNodeDefinition> _definitions =
        GraphNodeDefinition.All.ToDictionary(definition => definition.TypeName);

    public GraphSnapshot Snapshot => new(_nodes.ToArray(), _connections.ToArray());

    public Output Node(string kind, string name, params (string Id, string Type, object Value)[] parameters)
    {
        var definition = _definitions[kind];
        var id = Guid.NewGuid();
        var index = _nodes.Count;
        _nodes.Add(new(id, definition.Kind, name, index % 6 * 300, index / 6 * 180,
            definition.PortType, definition.PortCategory,
            Parameters: parameters.Select(parameter => new NodeParameterSnapshot(parameter.Id, parameter.Type,
                JsonSerializer.SerializeToElement(parameter.Value))).ToArray()));
        return new(id, definition.OutputPorts is { Count: 1 } ports ? ports[0].Id : null);
    }

    public void Connect(Output source, Output target, string input) =>
        _connections.Add(new(source.NodeId, target.NodeId, input, source.Port));

    public Output Scalar(double value, string name) => Node("Float", name, ("value", "float", value));
    public Output Vector(double[] value, string name) => Node($"FloatX{value.Length}", name,
        ("value", $"vector{value.Length}", value));
    public Output Component(Output vector, int lane, string name)
    {
        var split = Node("BreakVector4", name);
        Connect(vector, split, "a");
        return split with { Port = new[] { "x", "y", "z", "w" }[lane] };
    }

    public Output Compose(Output[] lanes, string name)
    {
        if (lanes.Length != 4) throw new ArgumentException("A matrix row requires four lanes.");
        var row = Node("ComposeVector4", name);
        for (var lane = 0; lane < 4; lane++) Connect(lanes[lane], row, new[] { "r", "g", "b", "a" }[lane]);
        return row;
    }

    public Output Unary(string kind, Output input, string name)
    {
        var node = Node(kind, name);
        Connect(input, node, "a");
        return node;
    }

    public Output Scale(Output input, double factor, string name)
    {
        var node = Node("Multiply", name, ("b", "float", factor));
        Connect(input, node, "a");
        return node;
    }

    public Matrix Translation(double x, double y, double z, string name) => new([
        Vector([1, 0, 0, x], $"{name}: row X"),
        Vector([0, 1, 0, y], $"{name}: row Y"),
        Vector([0, 0, 1, z], $"{name}: row Z"),
        Vector([0, 0, 0, 1], $"{name}: homogeneous row")
    ]);

    public Matrix RotationZ(Output radians, string name)
    {
        var cosine = Unary("Cos", radians, $"{name}: cos");
        var sine = Unary("Sin", radians, $"{name}: sin");
        var negativeSine = Scale(sine, -1, $"{name}: -sin");
        var zero = Scalar(0, $"{name}: zero");
        return new([
            Compose([cosine, negativeSine, zero, zero], $"{name}: row X"),
            Compose([sine, cosine, zero, zero], $"{name}: row Y"),
            Vector([0, 0, 1, 0], $"{name}: row Z"),
            Vector([0, 0, 0, 1], $"{name}: homogeneous row")
        ]);
    }

    public Matrix Multiply(Matrix parent, Matrix local, string name)
    {
        if (parent.Rows.Length != 4 || local.Rows.Length != 4)
            throw new ArgumentException("Matrices require four rows.");
        var columns = new Output[4];
        for (var column = 0; column < 4; column++)
            columns[column] = Compose(local.Rows.Select((row, index) =>
                Component(row, column, $"{name}: local [{index},{column}]")).ToArray(),
                $"{name}: local column {column}");
        var rows = new Output[4];
        for (var row = 0; row < 4; row++)
        {
            var lanes = new Output[4];
            for (var column = 0; column < 4; column++)
            {
                lanes[column] = Node("DotVector4", $"{name}: parent row {row} dot local column {column}");
                Connect(parent.Rows[row], lanes[column], "a");
                Connect(columns[column], lanes[column], "b");
            }
            rows[row] = Compose(lanes, $"{name}: world row {row}");
        }
        return new(rows);
    }

    public Output Position(Matrix world, string name)
    {
        var translation = Compose([
            Component(world.Rows[0], 3, $"{name}: world X"),
            Component(world.Rows[1], 3, $"{name}: world Y"),
            Component(world.Rows[2], 3, $"{name}: world Z"),
            Scalar(1, $"{name}: point W")
        ], $"{name}: translation column");
        return Unary("Vector4ToVector3", translation, $"{name} renderer position");
    }
}
