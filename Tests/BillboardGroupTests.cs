using System.Globalization;
using System.Text.Json;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Project;
using LiberaFx.Services.Samples;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;
using LiberaFx.Services.Compilation;

internal static class BillboardGroupTests
{
    public static void Run(string? directory)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        foreach (var kind in new[] { "WorldLookAt", "SphericalBillboard", "TreeBillboard" })
        {
            Case("canonical", kind, [0, 0, 0], [1, 0, 0], [0, 0, 1]);
            Case("elevated", kind, [3, -4, 2], [-2, 5, 9], [0, 0, 1]);
            Case("y-up", kind, [3, -4, 2], [-2, 5, 9], [0, 1, 0]);
            Case("coincident", kind, [3, -4, 2], [3, -4, 2], [0, 0, 1]);
            Case("parallel", kind, [0, 0, 0], [0, 0, 9], [0, 0, 1]);
            Case("zero-up", kind, [0, 0, 0], [1, 0, 0], [0, 0, 0]);
            Case("near-zero", kind, [0, 0, 0], [1e-10, 0, 0], [0, 0, 1]);
            Case("mesh-z", kind, [3, -4, 2], [-2, 5, 9], [0, 0, 1], [0, 0, 1], [0, 1, 0]);
            Case("pivot-scale", kind, [3, -4, 2], [-2, 5, 9], [0, 0, 1], scale: [-2, 3, 0.5], pivot: [1, -2, 3]);
            Case("invalid-local-axes", kind, [0, 0, 0], [1, 0, 0], [0, 0, 1], [1, 0, 0], [1, 0, 1]);
        }
        Case("spherical-elevated", "Billboard", [3, -4, 2], [-2, 5, 9], [0, 0, 1], mode: 0);
        Case("axial-elevated", "Billboard", [3, -4, 2], [-2, 5, 9], [0, 0, 1], mode: 1);
        Case("look-at-elevated", "Billboard", [3, -4, 2], [-2, 5, 9], [0, 0, 1], mode: 2);
        VerifyAuthoring();
        VerifyWorldRotationMetadata();
        if (directory is not null) ExportMovingCamera(directory);
        Console.WriteLine("Billboard ordinary-Group axes, invalid inputs, project history, copying and compiler fixtures passed.");

        void Case(string name, string kind, double[] position, double[] target, double[] up,
            double[]? forward = null, double[]? localUp = null, double[]? scale = null, double[]? pivot = null,
            int? mode = null)
        {
            forward ??= [1, 0, 0]; localUp ??= [0, 0, 1]; scale ??= [1, 1, 1]; pivot ??= [0, 0, 0];
            var values = new Dictionary<string, double[]> { ["Position"] = position, ["Target"] = target,
                ["World Up"] = up, ["Local Forward"] = forward, ["Local Up"] = localUp, ["Scale"] = scale, ["Pivot"] = pivot };
            var child = BillboardGraphLibrary.Create(kind);
            child = child with { Nodes = child.Nodes.Select(node => values.TryGetValue(node.Name, out var value)
                ? node with { Parameters = [new("value", "vector3", JsonSerializer.SerializeToElement(value))] } : node).ToArray() };
            var groupDefinition = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.GraphGroup);
            var group = new GraphNodeSnapshot(Guid.NewGuid(), groupDefinition.Kind, kind, 0, 0,
                groupDefinition.PortType, groupDefinition.PortCategory, ChildGraph: child,
                GroupOverrides: mode is { } selected
                    ? [new(child.ExposedItems!.Single(item => item.Name == "Mode").Id,
                        [new(JsonSerializer.SerializeToElement(selected))])]
                    : null);
            var graph = new GraphSnapshot([group], []);
            GraphValidator.Validate(graph);
            Require(GraphTypeDiagnostics.Analyze(graph).Count == 0, "Billboards must use compatible ordinary primitive connections.");
            var program = VfxProgram.Compile(graph);
            var direction = Sub(target, position);
            if (kind == "TreeBillboard" || mode == 1)
            {
                var axis = Unit(up);
                direction = Sub(direction, Scale(axis, Dot(direction, axis)));
            }
            var expected = Frame(direction, up, position);
            var local = Frame(forward, localUp, [0, 0, 0]);
            var valid = expected is not null && local is not null && Math.Abs(Dot(Unit(forward), Unit(localUp))) <= 1e-6;
            var world = new double[16];
            if (valid)
            {
                var inverseLocal = Enumerable.Range(0, 16).Select(index => local![index % 4 * 4 + index / 4]).ToArray();
                var adjustment = new double[] { scale[0], 0, 0, -pivot[0] * scale[0],
                    0, scale[1], 0, -pivot[1] * scale[1], 0, 0, scale[2], -pivot[2] * scale[2], 0, 0, 0, 1 };
                world = Multiply(Multiply(expected!, inverseLocal), adjustment);
            }
            if (directory is null) return;
            var stem = Path.Combine(directory, $"{kind}-{name}");
            File.WriteAllBytes(stem + ".lfxp", EffectBinary.Compile(graph));
            var lowered = GraphPolymorphism.LowerWithOutputs(GroupBoundaryLowering.Lower(GroupExposedItems.Materialize(graph)));
            using var writer = new StreamWriter(stem + ".checks");
            foreach (var (label, output) in new[] { ("World Matrix", world), ("Valid", new[] { valid ? 1d : 0 }) })
            {
                var boundary = child.Nodes.Single(node => node.Name == label);
                var bindings = lowered.Outputs[(boundary.Id, label == "World Matrix" ? "value" : null)];
                for (var row = 0; row < bindings.Count; row++)
                {
                    var index = program.TypedInstructions.ToList().FindIndex(item => item.NodeId == bindings[row].NodeId);
                    Require(index >= 0, "Each Group output must resolve into compiled native instructions.");
                    var lanes = bindings.Count == 4 ? output.Skip(row * 4).Take(4) : output;
                    writer.WriteLine($"0 {index} {lanes.Count()} " + string.Join(" ",
                        lanes.Select(value => value.ToString("R", CultureInfo.InvariantCulture))));
                }
            }
        }
    }

    private static void ExportMovingCamera(string directory)
    {
        var author = new MatrixGraphAuthoring();
        var time = author.Node("Time", "camera time");
        var x = author.Scale(author.Unary("Cos", time, "camera cosine"), 8, "camera X");
        var y = author.Scale(author.Unary("Sin", time, "camera sine"), 8, "camera Y");
        var height = author.Scale(author.Unary("Sin", author.Scale(time, 0.5, "half time"), "camera height sine"), 3, "camera height wave");
        var z = author.Node("Add", "camera Z", ("b", "float", 5d));
        author.Connect(height, z, "a");
        var camera = author.Unary("Vector4ToVector3",
            author.Compose([x, y, z, author.Scalar(1, "camera W")], "moving camera"), "explicit camera");
        using var catalog = new GroupAssetCatalog(new TeamProfilesService(Path.Combine(directory, "engine-profiles.json")));
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager(), catalog);
        var groupDefinition = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.GraphGroup);
        var groups = new[] { "SphericalBillboard", "TreeBillboard" }.Select((kind, index) =>
        {
            var child = BillboardGraphLibrary.Create(kind);
            child = child with { Nodes = child.Nodes.Select(node => node.Name is "Position" or "Scale" ?
                node with { Parameters = [new("value", "vector3", JsonSerializer.SerializeToElement(
                    node.Name == "Position" ? new[] { index == 0 ? -3d : 3d, 0d, 0d } : new[] { 1.2, 0.25, 0.5 }))] } : node).ToArray() };
            return new GraphNodeSnapshot(Guid.NewGuid(), groupDefinition.Kind, kind, 100, index * 300,
                groupDefinition.PortType, groupDefinition.PortCategory, ChildGraph: child);
        }).ToArray();
        workspace.InitializeFromSnapshot(new([.. author.Snapshot.Nodes, .. groups], author.Snapshot.Connections));
        var draws = new List<Guid>();
        foreach (var group in groups)
        {
            var source = workspace.GetNode(group.Id);
            var targetInput = group.ChildGraph!.Nodes.Single(node => node.Name == "Target");
            Require(workspace.TryConnectNodes(workspace.GetNode(camera.NodeId), source, out _,
                GroupBoundarySchema.InputPortId(targetInput.Id), camera.Port), "Explicit moving-camera input must connect.");
            var decomposition = workspace.CreateNode(new("DecomposeTransform"));
            Require(workspace.TryConnectNodes(source, decomposition, out _, "matrix",
                GroupBoundarySchema.OutputPortId(group.ChildGraph.Nodes.Single(node => node.Name == "World Matrix").Id)),
                "Billboard world matrix must connect through its ordinary Group boundary.");
            var draw = RenderNodeAuthoring.CreateModel(workspace,
                new("/Engine/BasicShapes/Cube.Cube", group.Name, "model", "Unreal"));
            foreach (var port in new[] { "position", "rotation", "scale" })
                Require(workspace.TryConnectNodes(decomposition, draw, out _, port, port), "World decomposition must drive rendering.");
            draws.Add(draw.Id);
        }
        var ordered = workspace.CreateNode(new("Transform"));
        workspace.SetParameter(ordered, "position", JsonSerializer.SerializeToElement(new[] { 3d, -5, 7 }));
        workspace.SetParameter(ordered, "rotation", JsonSerializer.SerializeToElement(new[] { 17d, -31, 43 }));
        workspace.SetParameter(ordered, "scale", JsonSerializer.SerializeToElement(new[] { 2d, 3, 0.5 }));
        workspace.SetParameter(ordered, "order", JsonSerializer.SerializeToElement(3));
        var orderedDecomposition = workspace.CreateNode(new("DecomposeTransform"));
        Require(workspace.TryConnectNodes(ordered, orderedDecomposition, out _, "matrix", "value"), "Ordered transform must decompose.");
        var orderedDraw = RenderNodeAuthoring.CreateModel(workspace,
            new("/Engine/BasicShapes/Cube.Cube", "RST transform", "model", "Unreal"));
        foreach (var port in new[] { "position", "rotation", "scale" })
            Require(workspace.TryConnectNodes(orderedDecomposition, orderedDraw, out _, port, port), "Ordered render pose must connect.");
        var positionStep = workspace.CreateGroupAssetNode(Guid.Parse("29000000-0000-4000-8000-000000000001"), 0, 900);
        foreach (var (name, value) in new (string, object)[]
        {
            ("Previous Position", new[] { 0d, -5, 0 }),
            ("Velocity", new[] { 4d, 5, 6 }),
            ("Delta Time (seconds)", 0.25d)
        })
        {
            var vector = value is double[];
            var input = workspace.CreateNode(vector ? GraphNodeKind.FloatX3 : GraphNodeKind.Float);
            workspace.SetParameter(input, "value", JsonSerializer.SerializeToElement(value));
            Require(workspace.TryConnectNodes(input, positionStep, out _,
                GroupBoundarySchema.InputPortId(positionStep.ChildGraph!.Nodes.Single(node => node.Name == name).Id)),
                "A standard update-step module must accept caller-owned previous state and Delta Time.");
        }
        workspace.SetGroupInstanceValue(positionStep, new(positionStep.ChildGraph!.ExposedItems!.Single().Id,
            [new(JsonSerializer.SerializeToElement(1.5d))]));
        var moduleDraw = RenderNodeAuthoring.CreateModel(workspace,
            new("/Engine/BasicShapes/Cube.Cube", "caller-owned position step", "model", "Unreal"));
        Require(workspace.TryConnectNodes(positionStep, moduleDraw, out _, "position",
            GroupBoundarySchema.OutputPortId(positionStep.ChildGraph.Nodes.Single(node =>
                GroupBoundarySchema.IsOutput(node.Kind)).Id)), "A shipped ordinary module must drive a real renderer.");
        var graph = workspace.CreateSnapshot();
        var package = SpawnableGroupPackage.Compile(graph);
        using (var scene = JsonDocument.Parse(package.RootRenderConfiguration))
            Require(scene.RootElement.GetProperty("nodes").EnumerateArray().All(node =>
                !node.GetProperty("billboard").GetBoolean() &&
                (node.GetProperty("nodeId").GetGuid() == moduleDraw.Id ||
                 node.GetProperty("rotationConvention").GetString() == "liberafx-world")),
                "Precomputed orientation must retain its world convention without automatic Billboard.");
        File.WriteAllBytes(Path.Combine(directory, "moving-camera.lfxp"), SpawnableGroupPackageBinary.Serialize(package));
        using var project = new MemoryStream();
        new ProjectFileService().SaveAsync(project, workspace).GetAwaiter().GetResult();
        File.WriteAllBytes(Path.Combine(directory, "moving-camera.lfxproj"), project.ToArray());
        var frames = new[] { 0d, 0.5, 2 }.Select(seconds => new
        {
            timeSeconds = seconds,
            nodes = draws.Select((id, index) =>
            {
                var position = new[] { index == 0 ? -3d : 3d, 0, 0 };
                var direction = Sub([8 * Math.Cos(seconds), 8 * Math.Sin(seconds), 5 + 3 * Math.Sin(seconds * 0.5)], position);
                if (index == 1) direction[2] = 0;
                var forward = Unit(direction);
                return (object)new { nodeId = id, position, scale = new[] { 1.2, 0.25, 0.5 },
                    rotation = new[] { 0d, Math.Asin(forward[2]) * 180 / Math.PI,
                        Math.Atan2(forward[1], forward[0]) * 180 / Math.PI }, forward,
                    up = Unit(Sub([0, 0, 1], Scale(forward, forward[2]))),
                    tree = index == 1 };
            }).Append(OrderedPose()).Append(new
            {
                nodeId = moduleDraw.Id, position = new[] { 1.5, -3.125, 2.25 }, scale = new[] { 1d, 1, 1 },
                rotation = new[] { 0d, 0, 0 }, forward = new[] { 1d, 0, 0 }, up = new[] { 0d, 0, 1 }, tree = false
            }).ToArray()
        }).ToArray();
        File.WriteAllText(Path.Combine(directory, "moving-camera.expected.json"), JsonSerializer.Serialize(new { frames }));

        object OrderedPose()
        {
            var roll = 17 * Math.PI / 180;
            var pitch = -31 * Math.PI / 180;
            var yaw = 43 * Math.PI / 180;
            var cp = Math.Cos(pitch); var sp = Math.Sin(pitch);
            var cr = Math.Cos(roll); var sr = Math.Sin(roll);
            var cy = Math.Cos(yaw); var sy = Math.Sin(yaw);
            double[] f = [cy * cp, sy * cp, sp];
            double[] r = [cy * sp * sr - sy * cr, sy * sp * sr + cy * cr, -cp * sr];
            double[] u = [-cy * sp * cr - sy * sr, -sy * sp * cr + cy * sr, cp * cr];
            return new { nodeId = orderedDraw.Id,
                position = Enumerable.Range(0, 3).Select(lane => f[lane] * 6 - r[lane] * 15 + u[lane] * 3.5).ToArray(),
                scale = new[] { 2d, 3, 0.5 }, rotation = new[] { 17d, -31, 43 }, forward = f, up = u, tree = false };
        }
    }

    private static void VerifyWorldRotationMetadata()
    {
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        var transform = workspace.CreateNode(new("Transform"));
        var decomposition = workspace.CreateNode(new("DecomposeTransform"));
        var sprite = RenderNodeAuthoring.CreateSprite(workspace,
            new("/Engine/EngineResources/DefaultTexture.DefaultTexture", "Sprite", "texture", "Unreal"), fps: 0);
        workspace.SetParameter(sprite, "billboard", JsonSerializer.SerializeToElement(true));
        Require(workspace.TryConnectNodes(transform, decomposition, out _, "matrix", "value") &&
            workspace.TryConnectNodes(decomposition, sprite, out _, "rotation", "rotation"), "Matrix rotation must connect to a Sprite.");
        try { _ = RenderProgramBindings.CreateScene(workspace.CreateSnapshot(), 1); throw new Exception("Double Billboard accepted."); }
        catch (InvalidDataException error) when (error.Message.Contains("Disable renderer Billboard", StringComparison.Ordinal)) { }
        workspace.SetParameter(sprite, "billboard", JsonSerializer.SerializeToElement(false));
        using var scene = JsonDocument.Parse(RenderProgramBindings.CreateScene(workspace.CreateSnapshot(), 1));
        Require(scene.RootElement.GetProperty("nodes")[0].GetProperty("rotationConvention").GetString() == "liberafx-world",
            "Matrix-derived rotation must be marked separately from legacy raw Unity Euler inputs.");
    }

    private static void VerifyAuthoring()
    {
        var definition = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.GraphGroup);
        var snapshot = new GraphSnapshot([new(Guid.NewGuid(), definition.Kind, "Tree", 0, 0,
            definition.PortType, definition.PortCategory, ChildGraph: BillboardGraphLibrary.Create("TreeBillboard"))], []);
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        workspace.InitializeFromSnapshot(snapshot);
        var group = workspace.Nodes.Single();
        workspace.SetSelection([group]);
        workspace.PasteSnapshot(workspace.CreateSelectionSnapshot(), 500, 300);
        Require(workspace.Nodes.Count == 2 && workspace.History.Undo() && workspace.Nodes.Count == 1 &&
            workspace.History.Redo() && workspace.Nodes.Count == 2, "A Billboard Group must copy, Undo and Redo as one reusable unit.");
        using var stream = new MemoryStream();
        new ProjectFileService().SaveAsync(stream, workspace).GetAwaiter().GetResult();
        stream.Position = 0;
        Require(GraphSnapshot.ContentEquals(workspace.CreateSnapshot(), ProjectDocumentReader.LoadAsync(stream).GetAwaiter().GetResult().Graph),
            "Billboard exposed axes and Group history must survive project storage.");
        _ = VfxProgram.Compile(workspace.CreateSnapshot());
        var nested = new GraphSnapshot([new(Guid.NewGuid(), definition.Kind, "Parent", 0, 0,
            definition.PortType, definition.PortCategory, ChildGraph: workspace.CreateSnapshot())], []);
        GraphValidator.Validate(nested);
        _ = VfxProgram.Compile(nested);
    }

    private static double Dot(double[] a, double[] b) => a.Zip(b).Sum(pair => pair.First * pair.Second);
    private static double[] Scale(double[] value, double factor) => value.Select(lane => lane * factor).ToArray();
    private static double[] Sub(double[] a, double[] b) => a.Zip(b).Select(pair => pair.First - pair.Second).ToArray();
    private static double[] Unit(double[] value) => Dot(value, value) == 0 ? new double[3] : Scale(value, 1 / Math.Sqrt(Dot(value, value)));
    private static double[] Cross(double[] a, double[] b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
    private static double[]? Frame(double[] forward, double[] up, double[] position)
    {
        var f = Unit(forward);
        var cross = Cross(Unit(up), f);
        if (Math.Sqrt(Dot(forward, forward)) <= 1e-6 || Math.Sqrt(Dot(up, up)) <= 1e-6 ||
            Math.Sqrt(Dot(cross, cross)) <= 1e-6) return null;
        var r = Unit(cross);
        var u = Cross(f, r);
        return [f[0], r[0], u[0], position[0], f[1], r[1], u[1], position[1],
            f[2], r[2], u[2], position[2], 0, 0, 0, 1];
    }
    private static double[] Multiply(double[] a, double[] b) => Enumerable.Range(0, 16)
        .Select(index => Enumerable.Range(0, 4).Sum(k => a[index / 4 * 4 + k] * b[k * 4 + index % 4])).ToArray();
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
