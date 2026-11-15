using System.Globalization;
using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;

internal static class SolarSystemSampleTests
{
    public static void Run(string? fixtureDirectory = null)
    {
        using var saved = new MemoryStream();
        SolarSystemSample.SaveAsync(saved).GetAwaiter().GetResult();
        saved.Position = 0;
        var loaded = ProjectDocumentReader.LoadAsync(saved).GetAwaiter().GetResult();
        var graph = loaded.Graph;
        var program = VfxProgram.Compile(graph);
        using var bundled = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Assets", "Samples", "SolarSystem.lfxproj"));
        var bundledGraph = Task.Run(() => ProjectDocumentReader.LoadAsync(bundled)).GetAwaiter().GetResult().Graph;
        Require(VfxProgram.Compile(bundledGraph).TypedInstructions.Count == program.TypedInstructions.Count,
            "The delivered sample must load and compile, not only the in-memory factory.");
        Require(loaded.History is not null && program.BinaryVersion == 3,
            "The saved sample must load with its history and compile with portable math.");
        using var scene = JsonDocument.Parse(RenderProgramBindings.CreateScene(graph, 1));
        var draws = scene.RootElement.GetProperty("nodes").EnumerateArray().ToArray();
        Require(draws.Length == 7, "Sun, four planets, Moon and a visible Earth self-spin marker must render.");
        foreach (var draw in draws)
        {
            Require(draw.GetProperty("materials")[0].GetProperty("path").GetString() == "builtin:unlit-color",
                "Sample colors require the explicit real builtin unlit material override.");
            Require(draw.GetProperty("assetPath").GetString()!.StartsWith("/Engine/BasicShapes/"),
                "The sample must not depend on external project content.");
            var inputs = draw.GetProperty("inputs").EnumerateArray().ToArray();
            foreach (var id in new[] { "position", "rotation" })
            {
                var input = inputs.Single(value => value.GetProperty("id").GetString() == id);
                Require(input.GetProperty("sourceNodeId").ValueKind == JsonValueKind.String,
                    $"Renderer {id} must be a computed numeric binding.");
                var source = input.GetProperty("sourceNodeId").GetGuid();
                Require(program.TypedInstructions.Any(instruction => instruction.NodeId == source),
                    "A renderer transform must resolve to a compiled instruction.");
            }
            Require(inputs.Any(input => input.TryGetProperty("materialParameter", out var material) &&
                material.ValueKind == JsonValueKind.Object && material.GetProperty("name").GetString() == "Color"),
                "Builtin shape colors must bind an actual material parameter, not only the generic Tint pin.");
        }
        var spin = graph.Nodes.Single(node => node.Name == "Earth independent self-spin radians");
        var changed = graph with
        {
            Nodes = graph.Nodes.Select(node => node.Id != spin.Id ? node : node with
            {
                Parameters = [new("b", "float", JsonSerializer.SerializeToElement(-3d))]
            }).ToArray()
        };
        var changedProgram = VfxProgram.Compile(changed);
        var ancestors = Ancestors(graph, graph.Nodes.Single(node => node.Name == "Moon renderer position").Id);
        Require(!ancestors.Contains(spin.Id), "Moon must not inherit Earth's independent display rotation.");
        Require(ancestors.Contains(graph.Nodes.Single(node => node.Name == "Earth orbit radians").Id),
            "Moon must inherit Earth's animated orbital position.");

        // A translated, rotated parent catches reversed multiplication and lost homogeneous W.
        var matrixGraph = new MatrixGraphAuthoring();
        var parent = matrixGraph.Multiply(matrixGraph.Translation(10, 20, 30, "parent translation"),
            matrixGraph.RotationZ(matrixGraph.Scalar(Math.PI / 2, "quarter turn"), "parent rotation"), "parent");
        var child = matrixGraph.Multiply(parent, matrixGraph.Translation(3, 0, 0, "child offset"), "child");
        var position = matrixGraph.Position(child, "child");
        var matrixProgram = VfxProgram.Compile(matrixGraph.Snapshot);
        if (fixtureDirectory is null) return;
        Directory.CreateDirectory(fixtureDirectory);
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "SolarSystem.lfxproj"), saved.ToArray());
        Export("solar-bundled", bundledGraph, VfxProgram.Compile(bundledGraph), 2.4);
        Export("solar", graph, program, 2.4);
        Export("solar-spin-changed", changed, changedProgram, -3);
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "matrix.lfxb"), matrixProgram.ToBinary());
        var index = matrixProgram.TypedInstructions.ToList().FindIndex(item => item.NodeId == position.NodeId);
        File.WriteAllText(Path.Combine(fixtureDirectory, "matrix.checks"), $"0 {index} 3 10 23 30\n");

        void Export(string name, GraphSnapshot snapshot, VfxProgram compiled, double earthSpin)
        {
            File.WriteAllBytes(Path.Combine(fixtureDirectory, name + ".lfxb"), compiled.ToBinary());
            var nativeFixture = Path.Combine(fixtureDirectory, name);
            Directory.CreateDirectory(nativeFixture);
            File.WriteAllBytes(Path.Combine(nativeFixture, "program.lfxb"), compiled.ToBinary());
            File.WriteAllBytes(Path.Combine(nativeFixture, "render-config.json"), RenderProgramBindings.CreateScene(snapshot, 1));
            var frames = new List<object>();
            using var checks = new StreamWriter(Path.Combine(fixtureDirectory, name + ".checks"));
            foreach (var time in new[] { 0d, Math.PI / 2, 10d, 0d })
            {
                var expectedNodes = new List<object>();
                Position("Sun", 80, -40, 30);
                foreach (var planet in new[] { ("Mercury", 170d, 1.6), ("Venus", 270d, 1.05),
                    ("Earth", 400d, 0.65), ("Mars", 600d, 0.4) })
                {
                    var angle = 0.25 + time * planet.Item3;
                    Position(planet.Item1, 80 + planet.Item2 * Math.Cos(angle), -40 + planet.Item2 * Math.Sin(angle), 30);
                }
                var earthAngle = 0.25 + time * 0.65;
                var earthX = 80 + 400 * Math.Cos(earthAngle);
                var earthY = -40 + 400 * Math.Sin(earthAngle);
                Position("Moon", earthX + 100 * Math.Cos(earthAngle + time * 2),
                    earthY + 100 * Math.Sin(earthAngle + time * 2), 30);
                Position("Earth spin marker", earthX + 28 * Math.Cos(earthAngle + time * earthSpin),
                    earthY + 28 * Math.Sin(earthAngle + time * earthSpin), 42);
                Check("Earth renderer rotation", [0, 0,
                    (Math.Atan2(Math.Sin(earthAngle), Math.Cos(earthAngle)) + time * earthSpin) * 180 / Math.PI]);
                frames.Add(new { timeSeconds = time, nodes = expectedNodes });

                void Position(string body, double x, double y, double z)
                {
                    Check(body + " renderer position", [x, y, z]);
                    var draw = snapshot.Nodes.Single(node => node.Name == (body == "Earth spin marker"
                        ? "Earth yellow self-spin marker" : body + " visible mesh"));
                    var speed = body switch { "Mercury" => 1.6, "Venus" => 1.05, "Earth" or "Earth spin marker" => 0.65,
                        "Mars" => 0.4, "Moon" => 2.65, _ => 0d };
                    var spin = body switch { "Mercury" => 1.1, "Venus" => -0.35, "Earth" or "Earth spin marker" => earthSpin,
                        "Mars" => 1.8, "Moon" => 0.3, _ => 0d };
                    var angle = 0.25 + time * speed;
                    var scale = draw.Parameters!.Single(parameter => parameter.Id == "scale").Value;
                    var color = draw.Parameters!.Single(parameter => parameter.Id == "tint").Value;
                    expectedNodes.Add(new
                    {
                        nodeId = draw.Id, position = new[] { x, y, z }, scale,
                        rotation = new[] { 0d, 0d, (Math.Atan2(Math.Sin(angle), Math.Cos(angle)) + time * spin) * 180 / Math.PI },
                        parameters = new[] { new { name = "Color", slot = 0, value = color } }
                    });
                }
                void Check(string nodeName, double[] expected)
                {
                    var node = snapshot.Nodes.Single(node => node.Name == nodeName);
                    var slot = compiled.TypedInstructions.ToList().FindIndex(item => item.NodeId == node.Id);
                    checks.WriteLine(string.Join(" ", new[] { time, slot, expected.Length }.Concat(expected)
                        .Select(value => value.ToString("R", CultureInfo.InvariantCulture))));
                }
                File.WriteAllText(Path.Combine(nativeFixture, "expected.json"), JsonSerializer.Serialize(new { frames }));
            }
        }
    }

    private static HashSet<Guid> Ancestors(GraphSnapshot graph, Guid target)
    {
        var result = new HashSet<Guid>();
        void Visit(Guid node)
        {
            foreach (var connection in graph.Connections.Where(connection => connection.TargetNodeId == node))
                if (result.Add(connection.SourceNodeId)) Visit(connection.SourceNodeId);
        }
        Visit(target);
        return result;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
