using System.Text.Json;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Samples;

/// <summary>Genre vocabulary mapped to editable ordinary particle/procedural graphs.</summary>
public static class GameEffectTemplates
{
    public static IReadOnlyList<string> Names { get; } =
        ["HitImpact", "HitSlash", "FireMagic", "MagicCircle", "DarkMagic", "LightMagic", "HealingMagic",
         "DustPuff", "Explosion", "Rain", "Snow", "Lightning"];

    public static double DefaultDuration(string archetype) => archetype switch
    { "Rain" => 2, "Snow" => 12, "Lightning" => .2, "HitImpact" or "HitSlash" => .3, "Explosion" => .7, _ => 2 };

    public static GraphSnapshot Create(string archetype, double duration = 1, string theme = "auto")
    {
        if (!Names.Contains(archetype, StringComparer.Ordinal) || !double.IsFinite(duration) || duration is < .05 or > 30)
            throw new ArgumentException("Choose a supported effect template and a duration from 0.05 to 30 seconds.");
        var palette = theme == "auto" ? archetype switch
        { "FireMagic" or "Explosion" => "fire", "DarkMagic" => "dark", "HealingMagic" => "heal",
          "DustPuff" => "earth", "Rain" or "Snow" or "Lightning" => "ice", _ => "light" } : theme;
        double[] rgb = palette switch
        {
            "fire" => [2, .35, .03], "dark" => [.65, .08, 1.4], "heal" => [.2, 1.8, .5],
            "light" => [1.8, 1.5, .55], "ice" => [.15, .6, 1.8],
            "earth" => [.55, .38, .2],
            _ => throw new ArgumentException("Theme must be auto, fire, dark, heal, light, ice or earth.")
        };
        if (archetype is "HitSlash" or "MagicCircle") return Procedural(archetype, duration, rgb);
        if (archetype == "Lightning") return Lightning(duration, rgb);
        var weather = archetype is "Rain" or "Snow";
        var fountain = weather || archetype is "FireMagic" or "HealingMagic" or "LightMagic";
        var graph = ParticleSamples.Create(fountain ? "CpuFountain" : "CpuRadial");
        GraphSnapshot Visit(GraphSnapshot source) => source with { Nodes = source.Nodes.Select(node =>
        {
            var values = node.Parameters?.ToList() ?? [];
            void Set(string id, string type, object value)
            {
                values.RemoveAll(item => item.Id == id); values.Add(new(id, type, JsonSerializer.SerializeToElement(value)));
            }
            switch (node.Kind.Value)
            {
                case "CpuParticle":
                    Set("rate", "float", weather ? archetype == "Rain" ? 900d : 220d : fountain ? 150d : 0d);
                    Set("burst", "int", fountain ? 0 : archetype == "HitImpact" ? 96 : archetype == "Explosion" ? 384 : 256);
                    break;
                case "Float" when node.Name == "Particle lifetime": Set("value", "float", duration); break;
                case "FloatX2" when node.Name == "Glow diameter": Set("value", "vector2", archetype switch
                { "Rain" => new[] { .015, .22 }, "Snow" => new[] { .045, .045 }, "DustPuff" => new[] { .35, .35 },
                  "Explosion" => new[] { .22, .22 }, _ => new[] { .12, .12 } }); break;
                case "Color" when node.Name == "Initial tint": Set("value", "color", new[] { rgb[0], rgb[1], rgb[2], 1 }); break;
                case "ComposeVector4": Set("r", "float", rgb[0]); Set("g", "float", rgb[1]); Set("b", "float", rgb[2]); break;
                case "CpuParticleMove": Set("acceleration", "vector3", fountain ? new double[] { 0, .3, 0 } : new double[] { 0, -.5, 0 }); break;
                case "MultiplyVector3" when node.Name == "Outward speed":
                    var speed = archetype == "Explosion" ? 8d : archetype == "DustPuff" ? 1d : 3d;
                    Set("b", "vector3", new[] { speed, archetype == "DustPuff" ? speed * .25 : speed, speed }); break;
                case "CpuParticleSpriteDraw" when archetype == "DustPuff": Set("blendMode", "string", "Alpha"); break;
                case "AddVector3" when node.Name == "Upward velocity":
                    Set("b", "vector3", new double[] { 0, archetype == "FireMagic" ? 1.5 : .8, 0 }); break;
                case "Comment": Set("text", "string", archetype + " · " + palette +
                    " · Y-up / metres\nEditable CPU particles, generated glow texture, per-particle fade.\n" +
                    "Prototype template: adjust timing, silhouette, scale and material for the game."); break;
            }
            var childGraph = node.ChildGraph is { } child ? Visit(child) : null;
            if (weather && node.Kind.Value == "CpuParticleSpawn" && childGraph is not null)
                childGraph = WeatherSpawn(childGraph, archetype);
            if (weather && node.Kind.Value == "CpuParticleMove") Set("acceleration", "vector3", new double[] { .1, 0, 0 });
            return node with { Name = node.Kind.Value == "CpuParticle" ? archetype : node.Name,
                Parameters = values, ChildGraph = childGraph };
        }).ToArray() };
        var result = Visit(graph);
        return weather ? FollowCamera(result) : result;
    }

    private static GraphSnapshot WeatherSpawn(GraphSnapshot graph, string archetype)
    {
        var shape = graph.Nodes.Single(node => node.Kind.Value == "CpuParticleEmitterSphere");
        var box = GraphNodeDefinition.All.Single(item => item.Kind.Value == "CpuParticleEmitterBox");
        var floatX3 = GraphNodeDefinition.All.Single(item => item.Kind.Value == "FloatX3");
        var spawnOut = graph.Nodes.Single(node => node.Kind.Value == "CpuParticleAttributesOutput");
        var velocityConst = new GraphNodeSnapshot(Guid.NewGuid(), floatX3.Kind, "Weather velocity", -80, 120,
            floatX3.PortType, floatX3.PortCategory, Parameters:
            [new("value", "vector3", JsonSerializer.SerializeToElement(archetype == "Rain"
                ? new double[] { 0, -8, 0 } : new double[] { .2, -.6, 0 }))]);
        var discarded = graph.Nodes.Where(node => node.Name is "Horizontal spread" or "Upward velocity").Select(node => node.Id).ToHashSet();
        return graph with
        {
            Nodes = graph.Nodes.Where(node => !discarded.Contains(node.Id)).Select(node => node.Id == shape.Id ?
                node with { Kind = box.Kind, Name = "Weather emission area", Parameters =
                    [new("extent", "vector3", JsonSerializer.SerializeToElement(new double[] { 5, 0, 5 })),
                     new("center", "vector3", JsonSerializer.SerializeToElement(new double[] { 0, 4, 0 }))] } : node)
                .Append(velocityConst).ToArray(),
            Connections = graph.Connections.Where(edge => !discarded.Contains(edge.SourceNodeId) &&
                !discarded.Contains(edge.TargetNodeId) && !(edge.TargetNodeId == spawnOut.Id && edge.TargetPortId == "velocity"))
                .Append(new(shape.Id, spawnOut.Id, "position", "value"))
                .Append(new(velocityConst.Id, spawnOut.Id, "velocity", "value")).ToArray()
        };
    }

    private static GraphSnapshot Lightning(double duration, double[] rgb)
    {
        var graph = ProceduralSamples.Create("Beam");
        var bolt = graph with { Nodes = graph.Nodes.Select(node =>
        {
            if (ProceduralResourceSchema.Name(node.Kind) == "ProceduralModel")
            {
                var recipe = ProceduralResourceSchema.ReadEffective(GraphNodeDefinition.Find(node.Kind)!, node.Parameters) with
                {
                    Shape = 0, Size = [.08f, 5, .08f], SegmentsU = 24, SegmentsV = 1,
                    Modifiers = [new() { Kind = 5, Axis = 1, Seed = 31, Flags = 1,
                        Values = [.18f, 9, 0, .8f, .5f, 0,0,0,0,0,0,0,0,0,0,0] }]
                };
                return node with { Name = "Lightning zigzag", Parameters =
                    [new("proceduralState", "proceduralState", JsonSerializer.SerializeToElement(ProceduralResourceSchema.Encode(recipe)))] };
            }
            return node;
        }).ToArray() };
        return Procedural("Lightning", duration, rgb, bolt);
    }

    private static GraphSnapshot FollowCamera(GraphSnapshot graph)
    {
        var nodes = graph.Nodes.ToList(); var edges = graph.Connections.ToList();
        GraphNodeSnapshot Node(string kind, string name, double y, params NodeParameterSnapshot[] parameters)
        {
            var definition = GraphNodeDefinition.All.Single(item => item.Kind.Value == kind);
            return new(Guid.NewGuid(), definition.Kind, name, -600, y, definition.PortType,
                definition.PortCategory, Parameters: parameters);
        }
        var camera = Node("CurrentCamera", "Weather camera position", 0);
        var emitter = Node("Transform", "Camera-following emitter origin", 200);
        var world = Node("MatrixCompose", "World-space drawing (existing particles stay put)", 400);
        var system = nodes.Single(node => node.Kind.Value == "CpuParticle");
        nodes.AddRange([camera, emitter, world]);
        edges.AddRange([new(camera.Id, emitter.Id, "position", "position"),
            new(emitter.Id, system.Id, "emitterMatrix", "value"), new(world.Id, system.Id, "drawMatrix", "value")]);
        return graph with { Nodes = nodes, Connections = edges };
    }

    private static GraphSnapshot Procedural(string archetype, double duration, double[] rgb, GraphSnapshot? source = null)
    {
        var graph = source ?? ProceduralSamples.Create(archetype == "HitSlash" ? "Slash" : "Shockwave");
        var nodes = graph.Nodes.ToList(); var edges = graph.Connections.ToList();
        Guid Add(string kind, params (string Id, string Type, object Value)[] values)
        {
            var definition = GraphNodeDefinition.All.Single(item => item.Kind.Value == kind);
            var id = Guid.NewGuid();
            nodes.Add(new(id, definition.Kind, definition.Name, 320, 300 + nodes.Count * 150,
                definition.PortType, definition.PortCategory, Parameters: values.Select(value =>
                    new NodeParameterSnapshot(value.Id, value.Type, JsonSerializer.SerializeToElement(value.Value))).ToArray()));
            return id;
        }
        var draw = nodes.Single(node => GraphNodeDefinition.Find(node.Kind)?.TypeName == "ModelDraw");
        var time = Add("Time");
        var progress = Add("Divide", ("b", "float", duration));
        var clamp = Add("Clamp"); var fade = Add("Subtract", ("a", "float", 1d));
        var tint = Add("ComposeVector4", ("r", "float", rgb[0]), ("g", "float", rgb[1]), ("b", "float", rgb[2]));
        edges.AddRange([new(time, progress, "a"), new(progress, clamp, "a"), new(clamp, fade, "b", "value"),
            new(fade, tint, "a"), new(tint, draw.Id, "tint", "value")]);
        Add("LifeTime", ("seconds", "float", duration));
        var groupDefinition = GraphNodeDefinition.Find(GraphNodeKind.SpawnableGroup)!;
        var group = new GraphNodeSnapshot(Guid.NewGuid(), groupDefinition.Kind, archetype, 320, 80,
            groupDefinition.PortType, groupDefinition.PortCategory, ChildGraph: graph with { Nodes = nodes, Connections = edges });
        var startDefinition = GraphNodeDefinition.Find(new("OnStart"))!;
        var start = new GraphNodeSnapshot(Guid.NewGuid(), startDefinition.Kind, "Play once", 0, 80,
            startDefinition.PortType, startDefinition.PortCategory);
        return new([start, group], [new(start.Id, group.Id, "spawn", "then")]);
    }
}
