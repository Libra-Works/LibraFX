using System.Text.Json;
using System.Text.Json.Serialization;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Project;
using LiberaFx.Services.Assets;

namespace LiberaFx.Services.Samples;

/// <summary>Two sibling templates, not a flattened animation or nested generator.</summary>
public static class FireworksSample
{
    public const int BurstParticleCount = 12;
    public const string PreviewSetupTitle = "Fireworks preview setup";
    public const double PreviewSetupWidth = 560;
    public const double PreviewSetupHeight = 170;
    public static (double X, double Y)? RootPosition(string name) => name switch
    {
        PreviewSetupTitle => (0, 0),
        "Start once" => (0, 200),
        "Launch initial position" => (0, 320),
        "Launch" => (280, 200),
        "Burst" => (320, 400),
        _ => null
    };
    public const string PreviewSetupText =
        "Before Play: Timeline End = 240 (4s), FPS = 60.\n" +
        "再生前：Timeline 終了 = 240（4秒）、FPS = 60 に設定。\n" +
        "Default End=120 loops at Launch expiry and hides Burst.\n" +
        "既定の終了120では Burst を見る前にループします。\n" +
        "Execution: editor-native C++ lifecycle; UE renders evaluated values.";
    public static Task SaveAsync(Stream stream)
    {
        var graph = Create();
        var stateId = Guid.NewGuid();
        var document = new LiberaFxProjectDocument
        {
            Nodes = graph.Nodes.Select(node => new LiberaFxProjectNode(node.Id, node.Name, node.Kind.Value,
                node.X, node.Y, node.Curve, node.ChildGraph, node.Parameters, node.GroupAsset, node.IsPolymorphic)).ToList(),
            Connections = graph.Connections.Select(edge => new LiberaFxProjectConnection(edge.SourceNodeId,
                edge.TargetNodeId, edge.TargetPortId, edge.SourcePortId)).ToList(),
            History = new(stateId, stateId, Guid.NewGuid(), 1,
                [new(stateId, null, null, "Fireworks: native Launch -> Destroy -> Burst", "Main", false, graph)])
        };
        return JsonSerializer.SerializeAsync(stream, document, new JsonSerializerOptions
        {
            WriteIndented = true, Converters = { new JsonStringEnumConverter() }
        });
    }
    public static GraphSnapshot Create()
    {
        var root = new Builder();
        var start = root.Node("OnStart", "Start once");
        var origin = root.Node("FloatX3", "Launch initial position", ("value", "vector3", new[] { 0d, 0d, 20d }));
        var launch = new Builder();
        var initial = launch.Node("SpawnInputVector3", "Initial position (captured)");
        launch.Node("LifeTime", "Launch life: 2 seconds", ("seconds", "float", 2d));
        var launchTime = launch.Node("Time", "Launch local seconds");
        var position = launch.Move(initial, launchTime, [0, 0, 120], "Launch rising position");
        var final = launch.Node("SpawnOutputVector3", "Final position");
        launch.Connect(position, final, "a");
        launch.Draw(position, "Launch particle", [1, 0.75, 0.1, 1], 0.05);
        var launchGroup = root.Group("Launch", launch.Graph);

        var burst = new Builder();
        var center = burst.Node("SpawnInputVector3", "Burst center (captured from Destroy)");
        burst.Node("LifeTime", "Burst life: 1.5 seconds", ("seconds", "float", 1.5d));
        var burstTime = burst.Node("Time", "Burst local seconds");
        for (var index = 0; index < BurstParticleCount; ++index)
        {
            var angle = index * Math.PI * 2 / BurstParticleCount;
            var point = burst.Move(center, burstTime, [Math.Cos(angle) * 110, Math.Sin(angle) * 110,
                (index % 3 - 1) * 60], $"Burst {index + 1} expanding position");
            burst.Draw(point, $"Burst particle {index + 1}", [1, 0.15 + index * 0.05, 0.05, 1], 0.035);
        }
        var burstGroup = root.Group("Burst", burst.Graph);
        root.Connect(start, launchGroup, "spawn", "then");
        root.Connect(origin, launchGroup, SpawnableGroupSchema.PortId(initial));
        root.Connect(launchGroup, burstGroup, "spawn", "destroy");
        root.Connect(launchGroup, burstGroup, SpawnableGroupSchema.PortId(center), SpawnableGroupSchema.OutputPortId(final));
        root.Node("Comment", PreviewSetupTitle, ("text", "string", PreviewSetupText),
            ("width", "float", PreviewSetupWidth), ("height", "float", PreviewSetupHeight));
        foreach (var node in root.Graph.Nodes)
            if (RootPosition(node.Name) is { } placement)
                root.Position(node.Id, placement.X, placement.Y);
        GraphValidator.Validate(root.Graph);
        return root.Graph;
    }

    private sealed class Builder
    {
        private readonly List<GraphNodeSnapshot> _nodes = [];
        private readonly List<GraphConnectionSnapshot> _edges = [];
        public GraphSnapshot Graph => new(_nodes.ToArray(), _edges.ToArray());
        public Guid Node(string kind, string name, params (string Id, string Type, object Value)[] parameters)
        {
            var definition = GraphNodeDefinition.All.Single(item => item.Kind.Value == kind);
            var id = Guid.NewGuid();
            var index = _nodes.Count;
            _nodes.Add(new(id, definition.Kind, name, index % 5 * 240, index / 5 * 180,
                definition.PortType, definition.PortCategory, Parameters: parameters.Select(parameter =>
                    new NodeParameterSnapshot(parameter.Id, parameter.Type, JsonSerializer.SerializeToElement(parameter.Value))).ToArray()));
            return id;
        }
        public Guid Group(string name, GraphSnapshot child)
        {
            var id = Node("SpawnableGroup", name);
            _nodes[^1] = _nodes[^1] with { ChildGraph = child };
            return id;
        }
        public void Connect(Guid source, Guid target, string? input, string? output = null) =>
            _edges.Add(new(source, target, input, output));
        public void Position(Guid id, double x, double y)
        {
            var index = _nodes.FindIndex(node => node.Id == id);
            _nodes[index] = _nodes[index] with { X = x, Y = y };
        }
        public Guid Move(Guid center, Guid time, double[] velocity, string name)
        {
            var localTime = Node("ScalarToVector3", name + " time lanes");
            Connect(time, localTime, "a");
            var displacement = Node("MultiplyVector3", name + " velocity * time",
                ("b", "vector3", velocity));
            Connect(localTime, displacement, "a");
            var position = Node("AddVector3", name);
            Connect(center, position, "a");
            Connect(displacement, position, "b");
            return position;
        }
        public void Draw(Guid position, string name, double[] color, double size)
        {
            const string asset = "/Engine/BasicShapes/Sphere.Sphere";
            var schema = RenderNodeSchema.Encode(new EngineAsset(asset, name, "model", "Unreal",
                Materials: [new(0, "builtin:unlit-color", "Unlit color",
                    [new("Color", "vector", JsonSerializer.SerializeToElement(color))])]));
            var colorPin = RenderNodeSchema.Describe(schema).Single().Id;
            var draw = Node("ModelDraw", name, ("assetPath", "string", asset),
                ("materialSchema", "string", schema), (colorPin, "vector4", color),
                ("scale", "vector3", new[] { size, size, size }), ("tint", "vector4", color));
            Connect(position, draw, "position");
        }
    }
}
