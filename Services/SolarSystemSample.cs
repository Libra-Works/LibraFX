using LiberaFx.Models.Graph;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Project;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiberaFx.Services;

public static class SolarSystemSample
{
    public const string SphereAsset = "/Engine/BasicShapes/Sphere.Sphere";
    public const string CubeAsset = "/Engine/BasicShapes/Cube.Cube";

    public static Task SaveAsync(Stream stream) =>
        SaveSnapshotAsync(stream, Create(), "Solar-system matrix hierarchy");

    public static Task SaveDedicatedAsync(Stream stream) =>
        SaveSnapshotAsync(stream, CreateDedicated(), "Dedicated Matrix solar-system hierarchy");

    private static async Task SaveSnapshotAsync(Stream stream, GraphSnapshot snapshot, string description)
    {
        var historyId = Guid.NewGuid();
        var document = new LiberaFxProjectDocument
        {
            Nodes = snapshot.Nodes.Select(node => new LiberaFxProjectNode(node.Id, node.Name,
                node.Kind.ToString(), node.X, node.Y, Parameters: node.Parameters)).ToList(),
            Connections = snapshot.Connections.Select(connection => new LiberaFxProjectConnection(
                connection.SourceNodeId, connection.TargetNodeId, connection.TargetPortId, connection.SourcePortId)).ToList(),
            History = new(historyId, historyId, Guid.NewGuid(), 1,
                [new(historyId, null, null, description, "Main", false, snapshot)])
        };
        await JsonSerializer.SerializeAsync(stream, document, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        });
    }

    public static GraphSnapshot Create()
    {
        var graph = new MatrixGraphAuthoring();
        var time = graph.Node("Time", "Solar clock: deterministic elapsed seconds (no randomness)");
        var root = graph.Translation(80, -40, 30, "Sun/root local");
        var sunFrame = graph.Multiply(root, graph.RotationZ(
            graph.Scalar(0.25, "Sun/root angle (radians)"), "Sun/root rotation"), "Sun/root world");
        Draw("Sun", sunFrame, 0.75, [1, 0.65, 0.08, 1], 0, SphereAsset);

        Planet("Mercury", 170, 1.6, 0.18, [0.6, 0.5, 0.4, 1], 1.1);
        Planet("Venus", 270, 1.05, 0.28, [1, 0.8, 0.35, 1], -0.35);
        var earth = Planet("Earth", 400, 0.65, 0.35, [0.1, 0.4, 1, 1], 2.4);
        Planet("Mars", 600, 0.4, 0.28, [1, 0.15, 0.06, 1], 1.8);

        // Attach to Earth's translated frame, not its display/self-spin matrix.
        var moonOrbit = graph.Multiply(earth, graph.RotationZ(
            graph.Scale(time, 2.0, "Moon local orbit radians"), "Moon orbit parent"), "Moon orbit world");
        var moon = graph.Multiply(moonOrbit, graph.Translation(100, 0, 0, "Moon local offset"),
            "Moon world = Earth frame * orbit * offset");
        Draw("Moon", moon, 0.13, [0.8, 0.85, 0.9, 1], 0.3, SphereAsset);
        return graph.Snapshot;

        MatrixGraphAuthoring.Matrix Planet(string name, double radius, double speed, double size,
            double[] color, double spin)
        {
            var orbit = graph.RotationZ(graph.Scale(time, speed, $"{name} orbit radians"),
                $"{name} orbit parent local");
            var orbitWorld = graph.Multiply(sunFrame, orbit, $"{name} orbit parent world");
            var world = graph.Multiply(orbitWorld, graph.Translation(radius, 0, 0, $"{name} local offset"),
                $"{name} world = parent * local");
            Draw(name, world, size, color, spin, SphereAsset);
            return world;
        }

        void Draw(string name, MatrixGraphAuthoring.Matrix world, double size, double[] color,
            double spin, string asset)
        {
            var materialAsset = new EngineAsset(asset, name, "model", "Unreal",
                Materials: [new(0, "builtin:unlit-color", "Unlit color",
                    [new("Color", "vector", System.Text.Json.JsonSerializer.SerializeToElement(color))])]);
            var schema = RenderNodeSchema.Encode(materialAsset);
            var colorPin = RenderNodeSchema.Describe(schema).Single().Id;
            var draw = graph.Node("ModelDraw", $"{name} visible mesh", ("assetPath", "string", asset),
                ("materialSchema", "string", schema), (colorPin, "vector4", color),
                ("scale", "vector3", new[] { size, size, size }), ("tint", "vector4", color));
            graph.Connect(graph.Position(world, name), draw, "position");
            var radians = graph.Scale(time, spin, $"{name} independent self-spin radians");
            var parentAngle = graph.Node("ATan2", $"{name} parent orientation");
            graph.Connect(graph.Component(world.Rows[1], 0, $"{name} basis Y"), parentAngle, "a");
            graph.Connect(graph.Component(world.Rows[0], 0, $"{name} basis X"), parentAngle, "b");
            var displayAngle = graph.Node("Add", $"{name} parent angle + independent spin");
            graph.Connect(parentAngle, displayAngle, "a");
            graph.Connect(radians, displayAngle, "b");
            var degrees = graph.Unary("RadToDeg", displayAngle, $"{name} display rotation degrees");
            var zero = graph.Scalar(0, $"{name} spin zero");
            var rotation = graph.Unary("Vector4ToVector3",
                graph.Compose([zero, zero, degrees, zero], $"{name} self-spin Euler Z"), $"{name} renderer rotation");
            graph.Connect(rotation, draw, "rotation");
            // An asymmetric marker makes sphere self-rotation observable using only engine builtin meshes.
            if (name != "Earth") return;
            var display = graph.Multiply(world, graph.RotationZ(radians, "Earth display self-spin"),
                "Earth display world (not Moon parent)");
            var markerWorld = graph.Multiply(display, graph.Translation(28, 0, 12, "Earth spin marker local"),
                "Earth spin marker world");
            var marker = graph.Node("ModelDraw", "Earth yellow self-spin marker", ("assetPath", "string", CubeAsset),
                ("materialSchema", "string", schema), (colorPin, "vector4", new[] { 1d, 1d, 0.1, 1d }),
                ("scale", "vector3", new[] { 0.12, 0.06, 0.08 }), ("tint", "vector4", new[] { 1d, 1d, 0.1, 1d }));
            graph.Connect(graph.Position(markerWorld, "Earth spin marker"), marker, "position");
            graph.Connect(rotation, marker, "rotation");
        }
    }

    public static GraphSnapshot CreateDedicated()
    {
        var graph = new MatrixGraphAuthoring();
        var time = graph.Node("Time", "Solar clock");
        var root = Transform("Sun root", [80, -40, 30], Angle(graph.Scalar(0.25, "Root radians"), "Root"));
        Draw("Sun", root, [0.75, 0.75, 0.75], [1, 0.65, 0.08, 1], SphereAsset);
        Planet("Mercury", 170, 1.6, 0.18, [0.6, 0.5, 0.4, 1], 1.1);
        Planet("Venus", 270, 1.05, 0.28, [1, 0.8, 0.35, 1], -0.35);
        var earth = Planet("Earth", 400, 0.65, 0.35, [0.1, 0.4, 1, 1], 2.4);
        Planet("Mars", 600, 0.4, 0.28, [1, 0.15, 0.06, 1], 1.8);
        var moonOrbit = Multiply(earth, Transform("Moon local orbit", [0, 0, 0],
            Angle(graph.Scale(time, 2, "Moon orbit radians"), "Moon")), "Moon orbit world");
        var moon = Multiply(moonOrbit, Transform("Moon offset", [100, 0, 0]), "Moon world");
        Draw("Moon", moon, [0.13, 0.13, 0.13], [0.8, 0.85, 0.9, 1], SphereAsset);
        return graph.Snapshot;

        MatrixGraphAuthoring.Output Angle(MatrixGraphAuthoring.Output radians, string name)
        {
            var degrees = graph.Unary("RadToDeg", radians, $"{name} degrees");
            var compose = graph.Node("ComposeVector4", $"{name} Z rotation");
            graph.Connect(degrees, compose, "b");
            return graph.Unary("Vector4ToVector3", compose, $"{name} Euler rotation");
        }

        MatrixGraphAuthoring.Output Transform(string name, double[] position, MatrixGraphAuthoring.Output? rotation = null)
        {
            var transform = graph.Node("Transform", name, ("position", "vector3", position));
            if (rotation is { } source) graph.Connect(source, transform, "rotation");
            return transform;
        }

        MatrixGraphAuthoring.Output Multiply(MatrixGraphAuthoring.Output parent, MatrixGraphAuthoring.Output local, string name)
        {
            var multiply = graph.Node("MatrixMultiply", name);
            graph.Connect(parent, multiply, "parent");
            graph.Connect(local, multiply, "local");
            return multiply;
        }

        MatrixGraphAuthoring.Output Planet(string name, double radius, double speed, double size, double[] color, double spin)
        {
            var orbit = Multiply(root, Transform($"{name} orbit", [0, 0, 0],
                Angle(graph.Scale(time, speed, $"{name} orbit radians"), name)), $"{name} orbit world");
            var frame = Multiply(orbit, Transform($"{name} offset", [radius, 0, 0]), $"{name} frame");
            var display = Multiply(frame, Transform($"{name} spin", [0, 0, 0],
                Angle(graph.Scale(time, spin, $"{name} spin radians"), $"{name} spin")), $"{name} display");
            Draw(name, display, [size, size, size], color, SphereAsset);
            if (name == "Earth")
                Draw("Earth spin marker", Multiply(display, Transform("Marker offset", [28, 0, 12]), "Marker world"),
                    [0.12, 0.06, 0.08], [1, 1, 0.1, 1], CubeAsset);
            return frame;
        }

        void Draw(string name, MatrixGraphAuthoring.Output matrix, double[] scale, double[] color, string asset)
        {
            var material = new EngineAsset(asset, name, "model", "Unreal",
                Materials: [new(0, "builtin:unlit-color", "Unlit color",
                        [new("Color", "vector", JsonSerializer.SerializeToElement(color))])]);
            var schema = RenderNodeSchema.Encode(material);
            var colorPin = RenderNodeSchema.Describe(schema).Single().Id;
            var draw = graph.Node("ModelDraw", $"{name} mesh", ("assetPath", "string", asset),
                ("materialSchema", "string", schema), (colorPin, "vector4", color),
                ("scale", "vector3", scale), ("tint", "vector4", color));
            var decompose = graph.Node("DecomposeTransform", $"{name} draw transform");
            graph.Connect(matrix, decompose, "matrix");
            graph.Connect(decompose with { Port = "position" }, draw, "position");
            graph.Connect(decompose with { Port = "rotation" }, draw, "rotation");
        }
    }
}
