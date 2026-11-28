using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Assets;

public static class BillboardGraphLibrary
{
    public static readonly Guid AssetId = Guid.Parse("b17b0000-0000-4000-8000-000000000004");
    private static readonly Guid ModeType = Guid.Parse("b17b0000-0000-4000-8000-000000000101");

    public static GraphSnapshot Create(string kind)
    {
        if (kind is not ("Billboard" or "WorldLookAt" or "SphericalBillboard" or "TreeBillboard"))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        var builder = new Builder(kind);
        Output? mode = kind == "Billboard" ? builder.ModeInput() : null;
        var position = builder.Input("Position", new[] { 0d, 0, 0 }, "World anchor; same coordinate system as Target.");
        var target = builder.Input("Target", new[] { 1d, 0, 0 }, "Explicit world target or camera position; no camera singleton.");
        var up = builder.Input("World Up", new[] { 0d, 0, 1 }, "World up direction. Tree preserves this axis.");
        var localForward = builder.Input("Local Forward", new[] { 1d, 0, 0 }, "Mesh forward axis; perpendicular to Local Up.");
        var localUp = builder.Input("Local Up", new[] { 0d, 0, 1 }, "Mesh up axis; perpendicular to Local Forward.");
        var scale = builder.Input("Scale", new[] { 1d, 1, 1 }, "Mesh-local scale; negative scale deliberately flips facing.");
        var pivot = builder.Input("Pivot", new[] { 0d, 0, 0 }, "Mesh-local point mapped onto Position.");
        var tolerance = builder.Input("Tolerance", 1e-6,
            "Positive distance/axis threshold; clamped to at least 1e-12. Invalid output is the zero matrix.");
        var epsilon = builder.Node("Max", "minimum tolerance", ("b", "float", 1e-12));
        builder.Connect(tolerance, epsilon, "a");
        var direction = builder.Binary("SubtractVector3", target, position, "target direction");
        var upUnit = builder.Unary("NormalizeVector3", up, "unit world up");
        if (kind is "Billboard" or "TreeBillboard")
        {
            var dot = builder.Binary("DotVector3", direction, upUnit, "height along world up");
            var height = builder.Binary("MultiplyVector3", upUnit,
                builder.Unary("ScalarToVector3", dot, "broadcast height"), "vertical direction");
            if (mode is { } selectorMode)
            {
                // Guard only a literal, not the shared inputs used by the world frame.
                var axisLocked = builder.Node("Float", "axis locked weight", ("value", "float", 1d));
                var selector = builder.Node($"EnumSelectFloat_{ModeType:N}", "projection weight by mode");
                builder.Connect(selectorMode, selector, "selector");
                builder.Connect(axisLocked, selector, GraphEnumBranchSchema.CasePortId(1));
                height = builder.Binary("MultiplyVector3", height,
                    builder.Unary("ScalarToVector3", selector, "broadcast projection weight"), "selected vertical direction");
            }
            direction = builder.Binary("SubtractVector3", direction, height, "direction in up plane");
        }
        var world = builder.Frame(direction, up, position, epsilon, "world");
        var zero = builder.Node("FloatX3", "origin", ("value", "vector3", new[] { 0d, 0, 0 }));
        var local = builder.Frame(localForward, localUp, zero, epsilon, "local");
        var localInverse = builder.Node("MatrixInverse", "inverse local axes");
        builder.Connect(local.Matrix, localInverse, "matrix");
        var facing = builder.Binary("MatrixMultiply", world.Matrix, localInverse, "world facing");
        var negativePivot = builder.Node("MultiplyVector3", "negative pivot", ("b", "vector3", new[] { -1d, -1, -1 }));
        builder.Connect(pivot, negativePivot, "a");
        var translatedPivot = builder.Binary("MultiplyVector3", negativePivot, scale, "scaled negative pivot");
        var pivotScale = builder.Node("Transform", "local pivot and scale");
        builder.Connect(translatedPivot, pivotScale, "position");
        builder.Connect(scale, pivotScale, "scale");
        var result = builder.Binary("MatrixMultiply", facing, pivotScale, "world with pivot and scale");
        var localDot = builder.Binary("DotVector3",
            builder.Unary("NormalizeVector3", localForward, "unit local forward"),
            builder.Unary("NormalizeVector3", localUp, "unit local up"), "local axes dot");
        var localSkew = builder.Positive(builder.Binary("Subtract",
            builder.Unary("Abs", localDot, "absolute local dot"), epsilon, "local orthogonality error"),
            "local axes skewed");
        var orthogonal = builder.Node("Subtract", "local axes orthogonal", ("a", "float", 1d));
        builder.Connect(localSkew, orthogonal, "b");
        var valid = builder.Binary("Multiply",
            builder.Binary("Multiply", world.Valid, local.Valid, "both frames valid"), orthogonal, "combined validity");
        var split = builder.Node("MatrixBreak", "world rows");
        builder.Connect(result, split, "matrix");
        var mask = builder.Unary("ScalarToVector4", valid, "validity row mask");
        var finite = builder.Node("MatrixCompose", "finite valid world matrix");
        for (var row = 0; row < 4; row++)
            builder.Connect(builder.Binary("MultiplyVector4", split with { Port = $"row{row}" }, mask,
                $"valid world row {row}"), finite, $"row{row}");
        builder.Output("GroupOutputMatrix", "World Matrix", finite);
        builder.Output("GroupOutputFloat", "Valid", valid);
        return builder.Snapshot;
    }

    private readonly record struct Output(Guid Id, string? Port = null);

    private sealed class Builder(string module)
    {
        private readonly List<GraphNodeSnapshot> _nodes = [];
        private readonly List<GraphConnectionSnapshot> _connections = [];
        private readonly List<GroupExposedItem> _exposed = [];
        public GraphSnapshot Snapshot => new(_nodes.ToArray(), _connections.ToArray(), _exposed.ToArray());
        private Guid Id(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"LiberaFx.Billboard.v1:{module}:{name}")).AsSpan(0, 16));

        public Output Node(string kind, string name, params (string Id, string Type, object Value)[] parameters)
        {
            var definition = GraphNodeDefinition.Core.Single(item => item.QualifiedName == $"LiberaFx.{kind}");
            var id = Id($"node:{kind}:{name}");
            _nodes.Add(new(id, definition.Kind, name, _nodes.Count % 6 * 300, _nodes.Count / 6 * 180,
                definition.PortType, definition.PortCategory, Parameters: parameters.Select(parameter =>
                    new NodeParameterSnapshot(parameter.Id, parameter.Type, JsonSerializer.SerializeToElement(parameter.Value))).ToArray()));
            return new(id, definition.OutputPorts?.FirstOrDefault(port => port.Id == "value")?.Id ??
                (definition.OutputPorts is { Count: 1 } ports ? ports[0].Id : null));
        }

        public void Connect(Output source, Output target, string? port) =>
            _connections.Add(new(source.Id, target.Id, port, source.Port));

        public Output Input(string name, object value, string description)
        {
            var vector = value is double[];
            var node = Node(vector ? "GroupInputVector3" : "GroupInputFloat", name,
                ("value", vector ? "vector3" : "float", value));
            _exposed.Add(new(Id($"exposed:{name}"), name, description, "Orientation", _exposed.Count,
                "parameter", [new(node.Id, "value")], Minimum: vector ? null : 1e-12));
            return node;
        }

        public void Output(string kind, string name, Output source) => Connect(source, Node(kind, name), null);
        public Output ModeInput()
        {
            var node = Node(GraphEnumBranchSchema.GroupInputKind(ModeType), "Mode",
                ("value", GraphEnumBranchSchema.EnumPortType(ModeType), 0));
            _exposed.Add(new(Id("exposed:Mode"), "Mode", "Spherical, axis-locked, or explicit look-at.",
                "Orientation", _exposed.Count, "parameter", [new(node.Id, "value")]));
            return node;
        }

        public Output Unary(string kind, Output source, string name)
        {
            var node = Node(kind, name);
            Connect(source, node, "a");
            return node;
        }

        public Output Binary(string kind, Output a, Output b, string name, string inputA = "a", string inputB = "b")
        {
            var node = Node(kind, name);
            if (kind == "MatrixMultiply") { inputA = "parent"; inputB = "local"; }
            Connect(a, node, inputA);
            Connect(b, node, inputB);
            return node;
        }

        public Output Positive(Output source, string name)
        {
            var sign = Unary("Sign", source, $"{name}: sign");
            var node = Node("Max", name, ("b", "float", 0d));
            Connect(sign, node, "a");
            return node;
        }

        private Output Nonzero(Output source, Output epsilon, string name) =>
            Positive(Binary("Subtract", Unary("LengthVector3", source, $"{name}: length"),
                epsilon, $"{name}: excess"), name);

        public (Output Matrix, Output Valid) Frame(Output forward, Output up, Output position, Output epsilon, string name)
        {
            var f = Unary("NormalizeVector3", forward, $"{name}: forward");
            var unitUp = Unary("NormalizeVector3", up, $"{name}: unit up");
            var cross = Binary("CrossVector3", unitUp, f, $"{name}: cross");
            var right = Unary("NormalizeVector3", cross, $"{name}: right");
            var correctedUp = Binary("CrossVector3", f, right, $"{name}: up");
            var components = new[] { f, right, correctedUp, position }.Select((source, index) =>
                Unary("BreakVector3", source, $"{name}: column {index}")).ToArray();
            var matrix = Node("MatrixCompose", $"{name}: matrix");
            for (var row = 0; row < 3; row++)
            {
                var composed = Node("ComposeVector4", $"{name}: row {row}");
                for (var column = 0; column < 4; column++)
                    Connect(components[column] with { Port = new[] { "x", "y", "z" }[row] }, composed,
                        new[] { "r", "g", "b", "a" }[column]);
                Connect(composed, matrix, $"row{row}");
            }
            var valid = Binary("Multiply", Nonzero(forward, epsilon, $"{name}: direction valid"),
                Binary("Multiply", Nonzero(up, epsilon, $"{name}: up valid"),
                    Nonzero(cross, epsilon, $"{name}: cross valid"), $"{name}: up and cross valid"),
                $"{name}: valid");
            return (matrix, valid);
        }
    }
}
