using System.Text.Json;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Resources;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;

internal static class SpriteCompositionTests
{
    public static void Run(string? fixtureDirectory)
    {
        var a = new SpriteAnimationCell(Guid.NewGuid(), "Wide", 2, 4, 20, 10);
        var b = new SpriteAnimationCell(Guid.NewGuid(), "Tall", 24, 8, 8, 24);
        SpriteAnimationFrame Frame(SpriteAnimationCell cell, double duration) => new(Guid.NewGuid(), cell.Id, duration);
        var back = new SpriteCompositionTrack { Id = Guid.NewGuid(), Name = "Back", Frames = [Frame(a, 0.5), Frame(b, 1)] };
        var front = new SpriteCompositionTrack
        {
            Id = Guid.NewGuid(), Name = "Front", Frames = [Frame(a, 4)],
            Keys = [new(Guid.NewGuid(), 0.5, 2, 4, 10, 1, 2), new(Guid.NewGuid(), 2.5, 10, -4, 90, 3, 0.5)]
        };
        var hidden = new SpriteCompositionTrack { Id = Guid.NewGuid(), Name = "Hidden", Visible = false };
        var data = new SpriteCompositionData { TextureWidth = 64, TextureHeight = 32, Fps = 2,
            Cells = [a, b], Tracks = [back, hidden, front] };
        Require(data.DurationFrames == 4 && data.SampleFrame(2) == 0 && data.SampleFrame(0.75) == 1.5, "Shared timing");
        Require((data with { Loop = false }).SampleFrame(10) == 4, "Non-loop clamp");
        Require(front.PoseAt(1.5) == new SpritePose(6, 0, 50, 2, 1.25) &&
            front.PoseAt(0).X == 2 && front.PoseAt(10).X == 10 && back.PoseAt(0) == new SpritePose(0, 0, 0, 1, 1),
            "Linear pose and holds");
        foreach (var invalid in new[]
        {
            data with { Fps = double.NaN },
            data with { Tracks = [hidden with { Visible = true }] },
            data with { Tracks = [back, front with { Id = back.Id }] },
            data with { Tracks = [back, front with { Name = " back " }] },
            data with { Tracks = [front with { Keys = [front.Keys[1], front.Keys[0]] }] },
            data with { Tracks = [front with { Keys = [front.Keys[0] with { ScaleX = double.PositiveInfinity }] }] },
            data with { Cells = [a] }
        })
        {
            try { invalid.Validate(); }
            catch (InvalidDataException) { continue; }
            throw new Exception("Invalid sprite composition passed validation.");
        }

        var asset = new EngineAsset("/Engine/EngineResources/WhiteSquareTexture.WhiteSquareTexture", "Fixture", "texture", "Unreal");
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var node = SpriteCompositionAuthoring.Apply(graph, null, asset, data);
        var original = graph.CreateSnapshot();
        Require(graph.History.Undo() && graph.Nodes.Count == 0 && graph.History.Redo(), "Creation undo/redo");
        SpriteCompositionAuthoring.Apply(graph, graph.GetNode(node.Id), asset, data with { Fps = 6 });
        Require(graph.History.Undo() && GraphSnapshot.ContentEquals(original, graph.CreateSnapshot()) &&
            graph.History.Redo() && graph.History.Undo(), "Atomic edit undo/redo");
        using var stream = new MemoryStream();
        new ProjectFileService().SaveAsync(stream, graph).GetAwaiter().GetResult();
        stream.Position = 0;
        var restored = ProjectDocumentReader.LoadAsync(stream).GetAwaiter().GetResult().Graph;
        Require(GraphSnapshot.ContentEquals(original, restored), "Project JSON roundtrip");
        var serialized = original.Nodes.Single().Parameters!.Single(x => x.Id == "compositionData").Value;
        Require(SpriteCompositionData.Deserialize(serialized)?.Tracks[2].PoseAt(1.5) == front.PoseAt(1.5), "Composition JSON roundtrip");
        graph.PasteSnapshot(original, 100, 100);
        var copied = ReadDraws(RenderProgramBindings.CreateScene(graph.CreateSnapshot(), 0));
        Require(copied.Length == 4 && copied.Select(x => x.GetProperty("nodeId").GetGuid()).Distinct().Count() == 4,
            "Copied compositions have independent draw IDs");

        var scene = RenderProgramBindings.CreateScene(restored, 0);
        var directProgram = VfxProgram.Compile(restored, out var directScene);
        var package = SpawnableGroupPackage.Compile(restored);
        Require(scene.SequenceEqual(directScene) && scene.SequenceEqual(package.RootRenderConfiguration),
            "Direct scene, bytecode and package use the same draw identities");
        var packageBytes = SpawnableGroupPackageBinary.Serialize(package);
        var draws = ReadDraws(SpawnableGroupPackageBinary.Deserialize(packageBytes).RootRenderConfiguration);
        Require(draws.Length == 2, "Two visible real draws");
        for (var index = 0; index < draws.Length; index++)
        {
            var draw = draws[index];
            var animation = draw.GetProperty("spriteAnimation");
            Require(!draw.GetProperty("billboard").GetBoolean() && draw.GetProperty("assetPath").GetString() == asset.Path &&
                animation.GetProperty("version").GetInt32() == 2 && animation.GetProperty("drawOrder").GetInt32() == index * 2 &&
                animation.GetProperty("durationFrames").GetDouble() == 4 &&
                animation.GetProperty("keys").GetArrayLength() == index * 2, "Generated layered draw metadata");
            VerifySpriteInputs(draw, data.Fps);
        }
        VerifyScopedPackage(restored, draws, node.Id);
        var legacy = SpriteEditorAuthoring.Apply(graph, null, asset, new(1, 1, 2, 0, true));
        try { SpriteCompositionAuthoring.Apply(graph, legacy, asset, data); }
        catch (InvalidOperationException) { legacy = null!; }
        Require(legacy is null, "Legacy sprite isn't overwritten");

        if (fixtureDirectory is null) return;
        Directory.CreateDirectory(fixtureDirectory);
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "program.lfxb"), directProgram.ToBinary());
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "composition.lfxp"), packageBytes);
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "composition.lfxproj"), stream.ToArray());
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "render-config.json"), RenderProgramBindings.CreateScene(restored, 1));
        var times = new[] { 0d, 0.25, 0.75, 1.75, 2d };
        var frames = times.Select(time =>
        {
            var t = data.SampleFrame(time);
            return new
            {
                timeSeconds = time,
                nodes = new[] { back, front }.Select((track, i) =>
                {
                    var cell = i == 0 && t >= 0.5 ? b : a;
                    var pose = track.PoseAt(t);
                    return new
                    {
                        nodeId = draws[i].GetProperty("nodeId").GetGuid(), drawOrder = i * 2,
                        frameIndex = i == 0 && t >= 0.5 ? 1 : 0,
                        position = new[] { pose.X, pose.Y, 0 }, rotation = new[] { 0d, 0, pose.RotationDegrees },
                        scale = new[] { pose.ScaleX * cell.Width / 100, pose.ScaleY * cell.Height / 100, 1 },
                        parameters = new[]
                        {
                            new { name = "AtlasScale", slot = 0, value = new[] { cell.Width / 64d, cell.Height / 32d, 0, 0 } },
                            new { name = "AtlasOffset", slot = 0, value = new[] { cell.X / 64d, cell.Y / 32d, 0, 0 } }
                        }

                    };
                })
            };
        });
        File.WriteAllText(Path.Combine(fixtureDirectory, "expected.json"), JsonSerializer.Serialize(new { frames }));
    }

    private static void VerifySpriteInputs(JsonElement draw, double fps)
    {
        var inputs = draw.GetProperty("inputs").EnumerateArray().ToDictionary(input => input.GetProperty("id").GetString()!);
        void Check(string id, params double[] defaults)
        {
            Require(inputs.TryGetValue(id, out var input) && input.GetProperty("width").GetInt32() == defaults.Length &&
                input.GetProperty("defaults").EnumerateArray().Select(value => value.GetDouble()).SequenceEqual(defaults) &&
                input.GetProperty("sourceNodeId").ValueKind == JsonValueKind.Null &&
                input.GetProperty("sourceComponent").GetInt32() == 0 &&
                input.GetProperty("materialParameter").ValueKind == JsonValueKind.Null,
                $"Packaged Unity/Unreal sprite input '{id}' is present with the expected default");
        }
        Check("position", 0, 0, 0);
        Check("rotation", 0, 0, 0);
        Check("scale", 1, 1, 1);
        Check("tint", 1, 1, 1, 1);
        Check("frame", -1);
        Check("columns", 1);
        Check("rows", 1);
        Check("fps", fps);
        Check("playing", 1);
    }

    private static void VerifyScopedPackage(GraphSnapshot snapshot, JsonElement[] draws, Guid nodeId)
    {
        var groupId = Guid.NewGuid();
        GraphSnapshot Wrap(GraphNodeKind kind)
        {
            var definition = GraphNodeDefinition.All.Single(item => item.Kind == kind);
            return new([new(groupId, kind, "Containing group", 0, 0,
                definition.PortType, definition.PortCategory, ChildGraph: snapshot)], []);
        }
        var group = Wrap(GraphNodeKind.GraphGroup);
        Require(RenderProgramBindings.CreateScene(group, 0).SequenceEqual(SpawnableGroupPackage.Compile(group).RootRenderConfiguration),
            "Root group lowering retains draw IDs");
        var spawnable = Wrap(GraphNodeKind.SpawnableGroup);
        var textureId = Guid.NewGuid();
        var project = new EngineProjectIdentity("Unreal", Guid.NewGuid());
        var mapped = new OpaqueBinding("unreal.object-path", "/Game/Moved/Atlas.Atlas");
        var resources = new ResourceIdentityDocument(1, [new(textureId, "texture", "Atlas")],
            [new(project, textureId, "texture", mapped)], [], [new(nodeId, "assetPath", textureId, [groupId])], []);
        var bindings = new ResourceBindingSnapshot(1, project, true,
            [new(mapped, ResourceResolutionStatus.Resolved, "texture")], []);
        var context = new ResourceIdentityRenderContext(new ResourceIdentityResolver(resources, project,
            new ResourceIdentitySnapshotProbe(bindings)), requireMappings: true, graph: spawnable);
        var template = SpawnableGroupPackage.Compile(spawnable, resourceContext: context).Templates.Single();
        var scoped = ReadDraws(template.RenderConfiguration);
        Require(scoped.Length == draws.Length &&
            scoped.Select(draw => draw.GetProperty("nodeId").GetGuid()).SequenceEqual(
                draws.Select(draw => draw.GetProperty("nodeId").GetGuid())) &&
            scoped.All(draw => draw.GetProperty("assetPath").GetString() == "/Game/Moved/Atlas.Atlas"),
            "Both child draws resolve the original node's scoped texture without changing draw identity");
    }

    private static JsonElement[] ReadDraws(byte[] scene)
    {
        using var json = JsonDocument.Parse(scene);
        return json.RootElement.GetProperty("nodes").EnumerateArray().Select(x => x.Clone()).ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception($"Sprite composition: {message}.");
    }
}
