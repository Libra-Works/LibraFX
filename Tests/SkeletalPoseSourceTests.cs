using System.Text;
using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;

namespace LiberaFx.Tests;

internal static class SkeletalPoseSourceTests
{
    public static void Run(string? fixtureDirectory = null)
    {
        var source = Node("Source",
            ("target", "enum:b0af61d2-29f6-4cc9-a71e-000000000202", 1),
            ("key", "string", "hero.skeleton"),
            ("bone", "string", "Root/Spine/Hand_R"),
            ("space", "enum:b0af61d2-29f6-4cc9-a71e-000000000203", 2));
        var matrixBreak = Node("MatrixBreak");
        var decompose = Node("DecomposeTransform");
        var graph = new GraphSnapshot([source, matrixBreak, decompose],
        [
            new(source.Id, matrixBreak.Id, "matrix", "value"),
            new(source.Id, decompose.Id, "matrix", "value")
        ]);
        GraphValidator.Validate(graph);

        var lowered = SkeletalPoseSchema.Lower(graph);
        Require(lowered.Nodes.All(node => node.Kind.Value != "Source") &&
                lowered.Nodes.Count(node => node.Kind == GraphNodeKind.InputProperty) == 2,
            "SOURCE must lower to ordinary hidden Game I/O inputs.");
        var matrixInput = lowered.Nodes.Single(node => node.Kind == GraphNodeKind.InputProperty &&
            node.Parameters!.Single(parameter => parameter.Id == "valueType").Value.GetInt32() == 5);
        var validInput = lowered.Nodes.Single(node => node.Kind == GraphNodeKind.InputProperty &&
            node.Parameters!.Single(parameter => parameter.Id == "valueType").Value.GetInt32() == 0);
        Require(lowered.Connections.Single(edge => edge.TargetNodeId == matrixBreak.Id).SourceNodeId == matrixInput.Id &&
                lowered.Connections.Single(edge => edge.TargetNodeId == decompose.Id).SourceNodeId == matrixInput.Id,
            "SOURCE Matrix consumers must bind to the generated Matrix property.");

        var encodedMatrixName = matrixInput.Parameters!.Single(parameter => parameter.Id == "name").Value.GetString();
        var encodedValidName = validInput.Parameters!.Single(parameter => parameter.Id == "name").Value.GetString();
        Require(SkeletalPoseSchema.TryDecodePortName(encodedMatrixName, out var matrixBinding, out var isValidity) &&
                !isValidity && matrixBinding is
                {
                    SourceNodeId: var id,
                    Target: SkeletalPoseTarget.RegisteredKey,
                    Key: "hero.skeleton",
                    BonePath: "Root/Spine/Hand_R",
                    Space: SkeletalPoseSpace.World
                } && id == source.Id,
            "The Matrix input name must preserve target, bone, space, and stable source identity.");
        Require(SkeletalPoseSchema.TryDecodePortName(encodedValidName, out var validityBinding, out isValidity) &&
                isValidity && validityBinding == matrixBinding,
            "The explicit validity property must carry the same source binding.");
        Require(Encoding.UTF8.GetByteCount(encodedMatrixName!) <= 128 &&
                Encoding.UTF8.GetByteCount(encodedValidName!) <= 128,
            "Hidden binding names must fit the existing named-I/O limit.");

        var package = SpawnableGroupPackage.Compile(graph);
        Require(package.GameIoPorts.Count == 2 &&
                package.GameIoPorts[0].Width == 16 &&
                package.GameIoPorts[0].RuntimeNodeIds?.Count == 4 &&
                package.GameIoPorts[1].Width == 1,
            "Root SOURCE must compile into the package Game I/O consumed by engine Spawnable hosts.");
        RequireNotSupported(() => VfxProgram.Compile(graph), "Raw CPU program compilation",
            "Raw CPU compilation must not emit SOURCE inputs with unrefreshed defaults.");
        RequireNotSupported(() => RenderProgramBindings.CreateScene(graph, 1), "Desktop render_scene",
            "A desktop render_scene must not pretend to resolve engine bones.");
        RequireNotSupported(() => RenderProgramBindings.CreateScene(lowered, 1), "Desktop render_scene",
            "Manually lowered SOURCE metadata must not bypass the render_scene boundary.");
        RequireNotSupported(() => _ = new LiberaFx.Services.EditorNativePreviewSession(graph),
            "Editor-native live preview",
            "Editor-native live preview must reject SOURCE without an engine pose adapter.");
        RequireNotSupported(() => LiberaFx.Services.LiveGameGraphCompiler.Compile(new(graph, "SOURCE")),
            "Live game transfer",
            "Live game transfer must reject SOURCE rather than send default-valued inputs.");

        var ownerWithoutKey = Node("Source",
            ("target", "enum:b0af61d2-29f6-4cc9-a71e-000000000202", 0),
            ("key", "string", "must-not-fall-back"),
            ("bone", "string", "Root/Spine"),
            ("space", "enum:b0af61d2-29f6-4cc9-a71e-000000000203", 0));
        RequireInvalid(() => SkeletalPoseSchema.Lower(new GraphSnapshot([ownerWithoutKey], [])),
            "Owner selection must reject an accidental registered-key fallback.");
        var missingBone = Node("Source",
            ("target", "enum:b0af61d2-29f6-4cc9-a71e-000000000202", 1),
            ("key", "string", "hero.skeleton"),
            ("bone", "string", ""),
            ("space", "enum:b0af61d2-29f6-4cc9-a71e-000000000203", 0));
        RequireInvalid(() => SkeletalPoseSchema.Lower(new GraphSnapshot([missingBone], [])),
            "An absent bone path must be a compile error, not an identity transform.");
        var nestedSource = Node("Source",
            ("target", "enum:b0af61d2-29f6-4cc9-a71e-000000000202", 0),
            ("key", "string", ""),
            ("bone", "string", "Root/Spine"),
            ("space", "enum:b0af61d2-29f6-4cc9-a71e-000000000203", 0));
        var groupDefinition = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.GraphGroup);
        var group = new GraphNodeSnapshot(Guid.NewGuid(), groupDefinition.Kind, "Group", 0, 0,
            groupDefinition.PortType, groupDefinition.PortCategory,
            ChildGraph: new GraphSnapshot([nestedSource], []));
        RequireInvalid(() => SkeletalPoseSchema.Lower(new GraphSnapshot([group], [])),
            "Nested SOURCE nodes must be rejected until root input forwarding is explicit.");

        var spawnableDefinition = GraphNodeDefinition.All.Single(item =>
            item.Kind == GraphNodeKind.SpawnableGroup);
        var templateSource = Node("Source",
            ("target", "enum:b0af61d2-29f6-4cc9-a71e-000000000202", 0),
            ("key", "string", ""),
            ("bone", "string", "Root/Spine"),
            ("space", "enum:b0af61d2-29f6-4cc9-a71e-000000000203", 0));
        var spawnable = new GraphNodeSnapshot(Guid.NewGuid(), spawnableDefinition.Kind,
            "Pose Template", 0, 0, spawnableDefinition.PortType, spawnableDefinition.PortCategory,
            ChildGraph: new GraphSnapshot([templateSource], []));
        var templateError = InvalidMessage(() => SpawnableGroupPackage.Compile(
            new GraphSnapshot([spawnable], [])));
        Require(templateError.Contains("SOURCE", StringComparison.Ordinal) &&
                templateError.Contains("instance-scoped", StringComparison.Ordinal),
            "SOURCE in a Spawnable template must fail explicitly until its inputs are updated per instance.");

        Require(!SkeletalPoseSchema.TryDecodePortName("__LFXS1M_not-a-descriptor", out _, out _),
            "Malformed source metadata must not be interpreted as a different target.");
        if (fixtureDirectory is not null)
        {
            WriteEngineFixture(fixtureDirectory, "AnimatedSkeletalSpaces", "hand_r");
            if (Environment.GetEnvironmentVariable("LIBERAFX_UNREAL_TEST_BONE_PATH") is { Length: > 0 } unrealBone)
            {
                WriteEngineFixture(fixtureDirectory, "UnrealAnimatedSkeletalSpaces", unrealBone);
            }
        }
    }

    private static void WriteEngineFixture(string directory, string name, string bonePath)
    {
        var nodes = new List<GraphNodeSnapshot>();
        var connections = new List<GraphConnectionSnapshot>();
        foreach (var target in new[] { "Owner", "Registered" })
        foreach (var (space, index) in new[] { ("BoneLocal", 0), ("Component", 1), ("World", 2) })
        {
            var source = Node("Source",
                ("target", "enum:b0af61d2-29f6-4cc9-a71e-000000000202", target == "Owner" ? 0 : 1),
                ("key", "string", target == "Owner" ? "" : "hero.skeleton"),
                ("bone", "string", bonePath),
                ("space", "enum:b0af61d2-29f6-4cc9-a71e-000000000203", index));
            var matrix = Node("OutputProperty", ("name", "string", target + space),
                ("valueType", "enum:1e309707-1fa0-4d38-aed9-42d000000001", 5));
            var valid = Node("OutputProperty", ("name", "string", target + space + "Valid"),
                ("valueType", "enum:1e309707-1fa0-4d38-aed9-42d000000001", 0));
            nodes.AddRange([source, matrix, valid]);
            connections.Add(new(source.Id, matrix.Id, "value", "value"));
            connections.Add(new(source.Id, valid.Id, "value", "valid"));
        }
        var package = SpawnableGroupPackage.Compile(new GraphSnapshot(nodes, connections)
            { AutoDestroyWhenIdle = false });
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, name + ".lfxp"),
            SpawnableGroupPackageBinary.Serialize(package));
    }

    private static GraphNodeSnapshot Node(string kind, params (string Id, string Type, object Value)[] values)
    {
        var definition = GraphNodeDefinition.All.Single(item => item.Kind.Value == kind);
        var parameters = values.Select(item =>
        {
            var parameter = definition.Parameters.Single(candidate => candidate.Id == item.Id);
            if (item.Type != parameter.Type)
                throw new InvalidOperationException($"Unexpected test parameter type for {kind}.{item.Id}.");
            return new NodeParameterSnapshot(item.Id, parameter.Type,
                JsonSerializer.SerializeToElement(item.Value));
        }).ToArray();
        return new(Guid.NewGuid(), definition.Kind, definition.Name, 0, 0,
            definition.PortType, definition.PortCategory, Parameters: parameters);
    }

    private static void RequireInvalid(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }

    private static string InvalidMessage(Action action)
    {
        try { action(); }
        catch (InvalidDataException error) { return error.Message; }
        throw new InvalidOperationException("Expected an InvalidDataException.");
    }

    private static void RequireNotSupported(Action action, string expectedMessage, string message)
    {
        try { action(); }
        catch (NotSupportedException error)
        {
            if (error.Message.Contains(expectedMessage, StringComparison.Ordinal)) return;
            throw new InvalidOperationException(message + " Unexpected rejection: " + error.Message, error);
        }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
