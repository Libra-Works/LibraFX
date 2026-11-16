using System.Globalization;
using System.Text.Json;
using Avalonia.Threading;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;

internal static class MatrixNodeTests
{
    public static void Run(string? fixtureDirectory = null)
    {
        VerifyAuthoring();
        VerifyLowering(fixtureDirectory);
        VerifyDefaultsAndFailures(fixtureDirectory);
        VerifyPointOperations(fixtureDirectory);
        var dedicated = SolarSystemSample.CreateDedicated();
        Require(dedicated.Nodes.Count < SolarSystemSample.Create().Nodes.Count / 2,
            "The dedicated Matrix sample must substantially reduce authoring graph complexity.");
        using var scene = JsonDocument.Parse(RenderProgramBindings.CreateScene(dedicated, 1));
        Require(scene.RootElement.GetProperty("nodes").GetArrayLength() == 7,
            "The compact sample must retain all seven rendered objects.");
        _ = VfxProgram.Compile(dedicated);
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Assets", "Samples", "SolarSystemMatrix.lfxproj"));
        var bundled = Task.Run(() => ProjectDocumentReader.LoadAsync(stream)).GetAwaiter().GetResult().Graph;
        Require(bundled.Nodes.Count == dedicated.Nodes.Count, "The delivered compact sample must match the dedicated graph.");
        _ = VfxProgram.Compile(bundled);
        VerifySolarFixtures(bundled, fixtureDirectory);
    }

    private static void VerifyPointOperations(string? directory)
    {
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        var rotate = workspace.CreateNode(new("RotatePoint"));
        var translate = workspace.CreateNode(new("TranslatePoint"));
        workspace.SetParameter(rotate, "point", JsonSerializer.SerializeToElement(new[] { 1d, 0, 0 }));
        workspace.SetParameter(rotate, "rotation", JsonSerializer.SerializeToElement(new[] { 0d, 0, 90 }));
        workspace.SetParameter(translate, "translation", JsonSerializer.SerializeToElement(new[] { 10d, 20, 30 }));
        Require(workspace.TryConnectNodes(rotate, translate, out _, "point", "value"),
            "Rotate Point must connect directly to Translate Point.");
        Require(workspace.History.Undo() && workspace.Connections.Count == 0 &&
            workspace.History.Redo() && workspace.Connections.Count == 1,
            "Point operation connections must support undo and redo.");
        var graph = workspace.CreateSnapshot();
        using var stream = new MemoryStream();
        new ProjectFileService().SaveAsync(stream, workspace).GetAwaiter().GetResult();
        stream.Position = 0;
        var loaded = ProjectDocumentReader.LoadAsync(stream).GetAwaiter().GetResult().Graph;
        Require(GraphSnapshot.ContentEquals(graph, loaded),
            "Point operations and their named wiring must survive project save/load.");

        var defaultRotation = Node("RotatePoint", "default rotation", ("point", "vector3", new[] { 2d, 3, 4 }));
        var defaultTranslation = Node("TranslatePoint", "default translation", ("point", "vector3", new[] { -2d, 3, 4 }));
        var angles = Node("FloatX3", "connected angles", ("value", "vector3", new[] { 0d, 0, -90 }));
        var connectedRotation = Node("RotatePoint", "connected rotation", ("point", "vector3", new[] { 1d, 0, 0 }));
        graph = new GraphSnapshot([.. graph.Nodes, defaultRotation, defaultTranslation, angles, connectedRotation],
            [.. graph.Connections, new(angles.Id, connectedRotation.Id, "rotation")]);
        GraphValidator.Validate(graph);
        Require(GraphTypeDiagnostics.Analyze(graph).Count == 0, "Point operations must retain typed connections.");
        var lowered = GraphPolymorphism.LowerWithOutputs(graph);
        GraphValidator.Validate(lowered.Graph);
        Require(GraphSnapshot.ContentEquals(lowered.Graph, GraphPolymorphism.Lower(lowered.Graph)),
            "Point operation lowering must be idempotent.");
        Export(directory, "point-operations", graph, [
            (rotate.Id, "value", new[] { 0d, 1, 0 }),
            (translate.Id, "value", new[] { 10d, 21, 30 }),
            (defaultRotation.Id, "value", new[] { 2d, 3, 4 }),
            (defaultTranslation.Id, "value", new[] { -2d, 3, 4 }),
            (connectedRotation.Id, "value", new[] { 0d, -1, 0 })
        ]);
        _ = VfxProgram.Compile(graph);
        if (directory is null) return;
        var asset = new EngineAsset(SolarSystemSample.SphereAsset, "Point operation target", "model", "Unreal");
        var draw = RenderNodeAuthoring.CreateModel(workspace, asset);
        translate = workspace.GetNode(translate.Id);
        Require(workspace.TryConnectNodes(translate, draw, out _, "position", "value"),
            "The point operation result must drive a rendered position.");
        var renderGraph = workspace.CreateSnapshot();
        var fixture = Path.Combine(directory, "point-operations-unreal");
        Directory.CreateDirectory(fixture);
        File.WriteAllBytes(Path.Combine(fixture, "program.lfxb"), VfxProgram.Compile(renderGraph).ToBinary());
        File.WriteAllBytes(Path.Combine(fixture, "render-config.json"), RenderProgramBindings.CreateScene(renderGraph, 1));
        File.WriteAllText(Path.Combine(fixture, "expected.json"), JsonSerializer.Serialize(new
        {
            frames = new[] { new { timeSeconds = 0d, nodes = new[] { new
            {
                nodeId = draw.Id, position = new[] { 10d, 21d, 30d },
                scale = new[] { 1d, 1d, 1d }, rotation = new[] { 0d, 0d, 0d }
            } } } }
        }));
    }

    private static void VerifySolarFixtures(GraphSnapshot graph, string? directory)
    {
        if (directory is null) return;
        var program = VfxProgram.Compile(graph);
        File.WriteAllBytes(Path.Combine(directory, "solar-system-matrix.lfxb"), program.ToBinary());
        var nativeDirectory = Path.Combine(directory, "solar-system-matrix");
        Directory.CreateDirectory(nativeDirectory);
        File.WriteAllBytes(Path.Combine(nativeDirectory, "program.lfxb"), program.ToBinary());
        File.WriteAllBytes(Path.Combine(nativeDirectory, "render-config.json"), RenderProgramBindings.CreateScene(graph, 1));
        var frames = new List<object>();
        using var writer = new StreamWriter(Path.Combine(directory, "solar-system-matrix.checks"));
        foreach (var seconds in new[] { 0d, 0.5, 2 })
        {
            var rendered = new List<object>();
            Check("Sun", 0, 0);
            Check("Mercury", 170, 1.6);
            Check("Venus", 270, 1.05);
            var earth = Check("Earth", 400, 0.65);
            Check("Mars", 600, 0.4);
            Position("Moon", [earth[0] + 100 * Math.Cos(0.25 + seconds * (0.65 + 2)),
                earth[1] + 100 * Math.Sin(0.25 + seconds * (0.65 + 2)), 30]);
            var markerAngle = 0.25 + seconds * (0.65 + 2.4);
            Position("Earth spin marker", [earth[0] + 28 * Math.Cos(markerAngle),
                earth[1] + 28 * Math.Sin(markerAngle), 42]);
            frames.Add(new { timeSeconds = seconds, nodes = rendered });

            double[] Check(string name, double radius, double speed)
            {
                var angle = 0.25 + seconds * speed;
                double[] value = [80 + radius * Math.Cos(angle), -40 + radius * Math.Sin(angle), 30];
                Position(name, value);
                return value;
            }

            void Position(string name, double[] value)
            {
                var node = graph.Nodes.Single(item => item.Name == $"{name} draw transform");
                var index = program.TypedInstructions.ToList().FindIndex(item => item.NodeId == node.Id);
                Require(index >= 0, "The renderer's transformed position must exist in native bytecode.");
                writer.WriteLine($"{seconds.ToString("R", CultureInfo.InvariantCulture)} {index} 3 " +
                    string.Join(" ", value.Select(lane => lane.ToString("R", CultureInfo.InvariantCulture))));
                var draw = graph.Nodes.Single(item => item.Name == $"{name} mesh");
                var speed = name switch
                {
                    "Mercury" => 2.7,
                    "Venus" => 0.7,
                    "Earth" or "Earth spin marker" => 3.05,
                    "Mars" => 2.2,
                    "Moon" => 2.65,
                    _ => 0d
                };
                var color = draw.Parameters!.Single(item => item.Id == "tint").Value;
                rendered.Add(new
                {
                    nodeId = draw.Id,
                    position = value,
                    scale = draw.Parameters!.Single(item => item.Id == "scale").Value,
                    rotation = new[] { 0d, 0d, (0.25 + seconds * speed) * 180 / Math.PI },
                    parameters = new[] { new { name = "Color", slot = 0, value = color } }
                });
            }
        }
        File.WriteAllText(Path.Combine(nativeDirectory, "expected.json"), JsonSerializer.Serialize(new { frames }));
    }

    private static void VerifyAuthoring()
    {
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        Require(workspace.AvailableNodeDefinitions.All(item => !item.IsInternalRuntime),
            "Internal Matrix operations must not appear in authoring/MCP creation menus.");
        var transform = workspace.CreateNode(new("Transform"));
        var decomposition = workspace.CreateNode(new("DecomposeTransform"));
        Require(workspace.TryConnectNodes(transform, decomposition, out _, "matrix", "value"),
            "A dedicated Matrix pin must connect directly to a Matrix consumer.");
        var scalar = workspace.CreateNode(GraphNodeKind.Float);
        Require(!workspace.TryConnectNodes(transform, scalar, out _, sourcePortId: "value"),
            "A Matrix must not be accepted as a scalar.");
        workspace.SetParameter(transform, "position", JsonSerializer.SerializeToElement(new[] { 10d, 20d, 30d }));
        Require(workspace.History.Undo(), "Transform parameter edits must be undoable.");
        Require(workspace.History.Redo(), "Transform parameter edits must be redoable.");
        var snapshot = workspace.CreateSnapshot();
        using var stream = new MemoryStream();
        new ProjectFileService().SaveAsync(stream, workspace).GetAwaiter().GetResult();
        stream.Position = 0;
        var loaded = ProjectDocumentReader.LoadAsync(stream).GetAwaiter().GetResult();
        Require(GraphSnapshot.ContentEquals(snapshot, loaded.Graph),
            "Project persistence must retain Matrix node IDs, parameters and named ports.");
        var provider = new ActiveGraphProvider(workspace);
        var task = LiberaFxMcpTools.DescribeRuntimeOutputs(provider, transform.Id.ToString("D"));
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var registration = timeout.Token.Register(() => Dispatcher.UIThread.Post(() => frame.Continue = false));
            _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
        }
        Require(task.IsCompleted, "Runtime binding description must not hang.");
        using var mapping = JsonDocument.Parse(task.GetAwaiter().GetResult());
        Require(mapping.RootElement.GetProperty("outputs")[0].GetProperty("bindings").GetArrayLength() == 4,
            "MCP must expose four runtime row bindings for one authoring Matrix pin.");
        workspace.SetSelection([workspace.GetNode(transform.Id), workspace.GetNode(decomposition.Id)]);
        var selection = workspace.CreateSelectionSnapshot();
        workspace.PasteSnapshot(selection, 1200, 600);
        var pasted = workspace.CreateSelectionSnapshot();
        Require(pasted.Nodes.All(item => selection.Nodes.All(original => original.Id != item.Id)) &&
                pasted.Connections.Count == 1 && pasted.Connections[0].SourcePortId == "value" &&
                pasted.Connections[0].TargetPortId == "matrix",
            "Copy/paste must clone IDs while retaining Matrix named-pin wiring.");
        _ = VfxProgram.Compile(workspace.CreateSnapshot());
        Require(workspace.History.Undo() && workspace.Nodes.Count == 3 &&
                workspace.History.Redo() && workspace.Nodes.Count == 5,
            "A copied Matrix chain must paste and undo/redo as one history operation.");
    }

    private static void VerifyLowering(string? directory)
    {
        var parent = Node("Transform", "parent", ("position", "vector3", new[] { 10d, 20d, 30d }),
            ("rotation", "vector3", new[] { 0d, 0d, 90d }));
        var local = Node("Transform", "local", ("position", "vector3", new[] { 3d, 0d, 0d }));
        var multiply = Node("MatrixMultiply", "world");
        var point = Node("TransformPoint", "point", ("point", "vector3", new[] { 1d, 0d, 0d }));
        var direction = Node("TransformDirection", "direction", ("direction", "vector3", new[] { 1d, 0d, 0d }));
        var inverse = Node("MatrixInverse", "inverse");
        var decompose = Node("DecomposeTransform", "TRS");
        var graph = new GraphSnapshot([parent, local, multiply, point, direction, inverse, decompose],
        [
            new(parent.Id, multiply.Id, "parent", "value"), new(local.Id, multiply.Id, "local", "value"),
            new(multiply.Id, point.Id, "matrix", "value"), new(multiply.Id, direction.Id, "matrix", "value"),
            new(multiply.Id, inverse.Id, "matrix", "value"), new(multiply.Id, decompose.Id, "matrix", "value")
        ]);
        var lowered = GraphPolymorphism.LowerWithOutputs(graph);
        GraphValidator.Validate(lowered.Graph);
        Require(GraphTypeDiagnostics.Analyze(lowered.Graph).Count == 0,
            "Composite expansion must produce valid, typed numeric wiring.");
        Require(lowered.Graph.Nodes.All(node => node.PortType != "Matrix"), "Matrix values must lower to four complete rows.");
        var program = VfxProgram.Compile(graph);
        Require(program.ToBinary().SequenceEqual(VfxProgram.Compile(graph).ToBinary()),
            "Compilation must produce stable generated IDs and bytecode.");
        Require(GraphSnapshot.ContentEquals(lowered.Graph, GraphPolymorphism.Lower(lowered.Graph)),
            "Lowering must be idempotent for render binding and package callers.");
        var group = Node("GraphGroup", "nested") with { ChildGraph = graph };
        var nested = VfxProgram.Compile(new([group], []));
        Require(program.TypedInstructions.Select(item => item.NodeId).SequenceEqual(
            nested.TypedInstructions.Select(item => item.NodeId)), "Nested graph expansion must use the same runtime identities.");
        Export(directory, "matrix-nodes", graph, [
            (multiply.Id, "value", new[] { 0d,-1,0,10, 1,0,0,23, 0,0,1,30, 0,0,0,1 }),
            (point.Id, "value", new[] { 10d,24,30 }), (point.Id, "w", new[] { 1d }),
            (direction.Id, "value", new[] { 0d,1,0 }), (direction.Id, "w", new[] { 0d }),
            (inverse.Id, "valid", new[] { 1d }),
            (inverse.Id, "value", new[] { 0d,1,0,-23, -1,0,0,10, 0,0,1,-30, 0,0,0,1 }),
            (decompose.Id, "position", new[] { 10d,23,30 }),
            (decompose.Id, "rotation", new[] { 0d,0,90 }),
            (decompose.Id, "scale", new[] { 1d,1,1 }), (decompose.Id, "valid", new[] { 1d })
        ]);
        var draw = Node("ModelDraw", "render", ("assetPath", "string", SolarSystemSample.CubeAsset));
        var renderGraph = graph with
        {
            Nodes = [.. graph.Nodes, draw],
            Connections = [.. graph.Connections, new(decompose.Id, draw.Id, "position", "position"),
                new(decompose.Id, draw.Id, "rotation", "rotation"), new(decompose.Id, draw.Id, "scale", "scale")]
        };
        using var scene = JsonDocument.Parse(RenderProgramBindings.CreateScene(renderGraph, 1));
        var renderProgram = VfxProgram.Compile(renderGraph);
        foreach (var input in scene.RootElement.GetProperty("nodes")[0].GetProperty("inputs").EnumerateArray())
            if (input.GetProperty("sourceNodeId").ValueKind == JsonValueKind.String)
                Require(renderProgram.TypedInstructions.Any(item => item.NodeId == input.GetProperty("sourceNodeId").GetGuid()),
                    "Every independent render_config binding must resolve to the exact compiled output.");
    }

    private static void VerifyDefaultsAndFailures(string? directory)
    {
        var defaults = Node("MatrixInverse", "default identity");
        var point = Node("TransformPoint", "identity point", ("point", "vector3", new[] { 2d, 3, 4 }));
        var perspective = Node("MatrixCompose", "perspective",
            ("row3", "vector4", new[] { 0d, 0, 2, 1 }));
        var transformed = Node("TransformPoint", "homogeneous", ("point", "vector3", new[] { 2d, 3, 4 }));
        var singular = Node("Transform", "zero scale", ("scale", "vector3", new[] { 1d, 0, 1 }));
        var invalidInverse = Node("MatrixInverse", "singular inverse");
        var invalidTRS = Node("DecomposeTransform", "singular TRS");
        var rows = Node("MatrixBreak", "rows");
        var rowConsumer = Node("Vector4ToVector3", "second row");
        var graph = new GraphSnapshot([defaults, point, perspective, transformed, singular, invalidInverse, invalidTRS, rows, rowConsumer],
        [
            new(perspective.Id, transformed.Id, "matrix", "value"),
            new(singular.Id, invalidInverse.Id, "matrix", "value"),
            new(singular.Id, invalidTRS.Id, "matrix", "value"),
            new(perspective.Id, rows.Id, "matrix", "value"), new(rows.Id, rowConsumer.Id, "a", "row1")
        ]);
        GraphValidator.Validate(GraphPolymorphism.Lower(graph));
        _ = VfxProgram.Compile(graph);
        Export(directory, "matrix-defaults", graph, [
            (defaults.Id, "valid", new[] { 1d }), (point.Id, "value", new[] { 2d,3,4 }),
            (transformed.Id, "value", new[] { 2d,3,4 }), (transformed.Id, "w", new[] { 9d }),
            (invalidInverse.Id, "valid", new[] { 0d }), (invalidInverse.Id, "value", new double[16]),
            (invalidTRS.Id, "valid", new[] { 0d }), (invalidTRS.Id, "position", new double[3]),
            (rowConsumer.Id, null, new[] { 0d,1,0 })
        ]);
        var cycle = new GraphSnapshot([defaults], [new(defaults.Id, defaults.Id, "matrix", "value")]);
        try { VfxProgram.Compile(cycle); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("A composite self-cycle must be rejected.");
    }

    private static void Export(string? directory, string name, GraphSnapshot graph,
        (Guid Node, string? Port, double[] Expected)[] checks)
    {
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        var lowered = GraphPolymorphism.LowerWithOutputs(graph);
        var program = VfxProgram.Compile(graph);
        File.WriteAllBytes(Path.Combine(directory, name + ".lfxb"), program.ToBinary());
        using var writer = new StreamWriter(Path.Combine(directory, name + ".checks"));
        foreach (var (node, port, expected) in checks)
        {
            var bindings = lowered.Outputs[(node, port)];
            for (var row = 0; row < bindings.Count; row++)
            {
                var index = program.TypedInstructions.ToList().FindIndex(item => item.NodeId == bindings[row].NodeId);
                Require(index >= 0, "Expected runtime binding must exist.");
                var values = bindings.Count == 4 ? expected.Skip(row * 4).Take(4).ToArray() : expected;
                writer.WriteLine($"0 {index} {values.Length} {string.Join(" ", values.Select(value => value.ToString("R", CultureInfo.InvariantCulture)))}");
            }
        }
    }

    private static GraphNodeSnapshot Node(string kind, string name, params (string Id, string Type, object Value)[] parameters)
    {
        var definition = GraphNodeDefinition.All.Single(item => item.TypeName == kind);
        return new(Guid.NewGuid(), definition.Kind, name, 0, 0, definition.PortType, definition.PortCategory,
            Parameters: parameters.Select(parameter => new NodeParameterSnapshot(
                parameter.Id, parameter.Type, JsonSerializer.SerializeToElement(parameter.Value))).ToArray());
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
