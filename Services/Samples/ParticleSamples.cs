using System.Text.Json;
using System.Text.Json.Serialization;
using LibraGpu.Core;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Project;
using LiberaFx.Services.Gpu;

namespace LiberaFx.Services.Samples;

/// <summary>Editable Y-up, metre-based source graphs with no imported assets.</summary>
public static class ParticleSamples
{
    public static IReadOnlyList<string> Names { get; } =
        ["CpuRadial", "CpuFountain", "CpuMeshSurface", "GpuCurl100k", "GpuCurlTurbulence", "GpuFluidVelocity", "FluidSmoke", "FluidInk", "FluidWater", "FluidShallowWater", "FluidFire", "Fireworks"];

    public static GraphSnapshot Create(string name)
    {
        if (!Names.Contains(name, StringComparer.Ordinal)) throw new ArgumentException("Unknown particle/fluid sample.", nameof(name));
        if (name == "Fireworks") return Fireworks();
        if (name == "GpuFluidVelocity") return FluidVelocity();
        var root = new Builder();
        if (name is "CpuRadial" or "CpuFountain" or "CpuMeshSurface")
        {
            var system = root.System(name == "CpuFountain", name == "CpuFountain" ? 0 : 256,
                name == "CpuFountain" ? 300 : 0, 3, [0.2, 0.65, 1, 1], mesh: name == "CpuMeshSurface");
            if (name == "CpuMeshSurface")
            {
                var mesh = root.Add("ProceduralModel", "Emitter sphere", ("proceduralState", "proceduralState",
                    ProceduralResourceSchema.Encode(new ProceduralRecipe { Shape = 11, Size = [2, 2, 2], SegmentsU = 24, SegmentsV = 12 })));
                root.Wire(mesh, system.Node, "emitterMesh", "value");
            }
        }
        else
        {
            var state = new GpuEditorState
            {
                ResourceAssetPaths = new() { ["billboardTexture"] = "builtin:white" },
                RendererSettings = new() { ["renderStep"] = new(SizeScale: 1) }
            };
            if (name == "GpuCurl100k")
            {
                state = state with { Program = GpuSamples.CreateCurlCloud() };
            }
            else if (name == "GpuCurlTurbulence")
            {
                state = state with { Program = GpuSamples.CreateCurlTurbulence() };
            }
            else
            {
                var model = name switch
                {
                    "FluidWater" => Fluid2DModel.FreeSurface,
                    "FluidShallowWater" => Fluid2DModel.ShallowWater,
                    "FluidFire" => Fluid2DModel.Combustion,
                    _ => Fluid2DModel.Smoke
                };
                var fluid = Fluid2DPresets.Create(model);
                if (name == "FluidInk") fluid = fluid with
                {
                    ColorR = .12f, ColorG = .3f, ColorB = 1, Buoyancy = 0,
                    SourceVelocityY = .8f, Vorticity = .4f, DensityDissipation = .05f
                };
                state = state with { Fluid2D = fluid,
                    FluidRenderer = new(Fluid2DPresets.RendererMaterial(model), 1) };
            }
            var gpuNode = root.Add("GpuParticle", name, (GpuParticleEditorService.ParameterId, "gpuState",
                JsonSerializer.SerializeToElement(state, GpuParticleEditorService.Json)));
            if (name == "GpuCurlTurbulence")
            {
                var tex = root.Add("ProceduralTexture", "Glow texture");
                var mat = root.Add("ProceduralMaterial", "Glow material");
                root.Wire(tex, mat, "texture0", "value");
                root.Wire(mat, gpuNode, "proceduralMaterial", "value");
            }
        }
        root.Comment(name == "GpuCurlTurbulence"
            ? "GpuCurlTurbulence · X right / Y up / Z forward · metres\n" +
              "Curl Noise moves the particles; Turbulence varies its strength.\n" +
              "Capacity 4,096; 240 particles per second. Preview: 60 FPS, frames 0–1200.\n" +
              "カールノイズで動かし、タービュランスで強さを変化。実 GPU の接続先で確認してください。"
            : name + " · X right / Y up / Z forward · metres\n" +
            "CPU samples use a generated radial glow texture; no image import is required.\n" +
            "GPU Curl: capacity 100,000; particles fill gradually. Fluid2D: edit typed Flow controls.\n" +
            "Suggested preview: 60 FPS, frames 0–1200.\n" +
            "Yアップ・メートル単位。CPUは生成グロー画像を使用。GPU/フルイドは実GPUの接続先で確認してください。");
        return root.Graph;
    }

    private static GraphSnapshot FluidVelocity()
    {
        var root = new Builder();
        var fluid = new GpuEditorState
        {
            Fluid2D = Fluid2DPresets.Create(Fluid2DModel.Smoke, 64, 64),
            FluidRenderer = new(Fluid2DPresets.RendererMaterial(Fluid2DModel.Smoke), 1),
            FluidAutomaticPlane = false
        };
        var producer = root.Add("GpuParticle", "Fluid2D velocity source", (GpuParticleEditorService.ParameterId, "gpuState",
            JsonSerializer.SerializeToElement(fluid, GpuParticleEditorService.Json)));
        var particles = new GpuEditorState
        {
            Program = GpuSamples.CreateFluidVelocityParticles(),
            ResourceAssetPaths = new() { ["billboardTexture"] = "builtin:white" },
            RendererSettings = new() { ["renderStep"] = new(SizeScale: 1) },
            GeneratedResources = new() { ["fluidVelocity"] = new(producer, "VelocityOutput") }
        };
        var gpuParticles = root.Add("GpuParticle", "Particles following fluid", (GpuParticleEditorService.ParameterId, "gpuState",
            JsonSerializer.SerializeToElement(particles, GpuParticleEditorService.Json)));
        var gpuTex = root.Add("ProceduralTexture", "Glow texture");
        var gpuMat = root.Add("ProceduralMaterial", "Glow material");
        root.Wire(gpuTex, gpuMat, "texture0", "value");
        root.Wire(gpuMat, gpuParticles, "proceduralMaterial", "value");
        root.Comment("GpuFluidVelocity · Fluid2D VelocityOutput → GPU particles\n" +
            "The smoke solver runs first. Its velocity moves the particles; its plane is hidden.\n" +
            "64 × 64 cells; 100 particles per second. Preview: 60 FPS, frames 0–600.\n" +
            "フルイドの速度を粒子が読み取ります。対応する GPU 接続先で確認してください。");
        return root.Graph;
    }

    public static Task SaveAsync(Stream stream, string name)
    {
        var graph = Create(name);
        var state = Guid.NewGuid();
        var document = new LiberaFxProjectDocument
        {
            AutoDestroyWhenIdle = graph.AutoDestroyWhenIdle,
            Nodes = graph.Nodes.Select(node => new LiberaFxProjectNode(node.Id, node.Name, node.Kind.Value,
                node.X, node.Y, node.Curve, node.ChildGraph, node.Parameters)).ToList(),
            Connections = graph.Connections.Select(edge => new LiberaFxProjectConnection(edge.SourceNodeId,
                edge.TargetNodeId, edge.TargetPortId, edge.SourcePortId)).ToList(),
            History = new(state, state, Guid.NewGuid(), 1, [new(state, null, null, name, "Main", false, graph)])
        };
        return JsonSerializer.SerializeAsync(stream, document, new JsonSerializerOptions
        { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
    }

    private static GraphSnapshot Fireworks()
    {
        var root = new Builder();
        var start = root.Add("OnStart", "Launch once");
        var launch = new Builder();
        launch.System(true, 1, 0, 1.2, [2, .8, .1, 1], launch: true);
        launch.Add("LifeTime", "Launch duration", ("seconds", "float", 1.2));
        var final = launch.Add("SpawnOutputVector3", "Burst centre",
            ("value", "vector3", new double[] { 0, 5.04, 0 }));
        var launchGroup = root.AddChild("SpawnableGroup", "Launch", launch.Graph);
        var burst = new Builder();
        var center = burst.Add("SpawnInputVector3", "Captured burst centre");
        var system = burst.System(false, 384, 0, 2.5, [1, .3, .08, 1], externalPosition: true);
        burst.Wire(center, system.Node, CpuParticleSchema.PortId(system.PositionInput!.Value));
        burst.Add("LifeTime", "Burst duration", ("seconds", "float", 2.5));
        var burstGroup = root.AddChild("SpawnableGroup", "Burst", burst.Graph);
        root.Wire(start, launchGroup, "spawn", "then");
        root.Wire(launchGroup, burstGroup, "spawn", "destroy");
        root.Wire(launchGroup, burstGroup, SpawnableGroupSchema.PortId(center), SpawnableGroupSchema.OutputPortId(final));
        root.Comment("Fireworks · Y-up / metres · generated radial glow sprites\n" +
            "Launch 1.2 s → capture centre → burst 384 CPU particles, gravity and fade.\n" +
            "Preview: 60 FPS, frames 0–300.\n打ち上げ後に384粒子を放射。外部テクスチャーは不要です。");
        return root.Graph;
    }

    private sealed class Builder
    {
        private readonly List<GraphNodeSnapshot> _nodes = [];
        private readonly List<GraphConnectionSnapshot> _edges = [];
        public GraphSnapshot Graph
        {
            get
            {
                var nonComments = _nodes.Where(n => n.Kind != GraphNodeKind.Comment).ToArray();
                var depth = nonComments.ToDictionary(n => n.Id, _ => 0);
                for (var pass = 0; pass < nonComments.Length; pass++)
                {
                    var changed = false;
                    foreach (var edge in _edges)
                    {
                        if (depth.TryGetValue(edge.SourceNodeId, out var srcDepth) &&
                            depth.TryGetValue(edge.TargetNodeId, out var dstDepth) &&
                            dstDepth <= srcDepth)
                        {
                            depth[edge.TargetNodeId] = srcDepth + 1;
                            changed = true;
                        }
                    }
                    if (!changed) break;
                }
                var rowByDepth = new Dictionary<int, int>();
                var laidOut = new List<GraphNodeSnapshot>(_nodes.Count);
                var maxDepth = depth.Count > 0 ? depth.Values.Max() : 0;
                var commentRow = 0;
                foreach (var n in _nodes)
                {
                    if (n.Kind == GraphNodeKind.Comment)
                    {
                        laidOut.Add(n with { X = (maxDepth + 1) * 320, Y = commentRow++ * 240 });
                    }
                    else
                    {
                        var col = depth[n.Id];
                        var row = rowByDepth.GetValueOrDefault(col);
                        rowByDepth[col] = row + 1;
                        laidOut.Add(n with { X = col * 320, Y = row * 210 });
                    }
                }
                return new(laidOut.ToArray(), _edges.ToArray());
            }
        }
        public Guid Add(string kind, string name, params (string Id, string Type, object Value)[] values)
        {
            var definition = GraphNodeDefinition.All.Single(item => item.Kind.Value == kind);
            var id = Guid.NewGuid(); var index = _nodes.Count;
            _nodes.Add(new(id, definition.Kind, name, index % 4 * 300, index / 4 * 210,
                definition.PortType, definition.PortCategory, Parameters: values.Select(value =>
                    new NodeParameterSnapshot(value.Id, value.Type, value.Value is JsonElement element
                        ? element.Clone() : JsonSerializer.SerializeToElement(value.Value))).ToArray()));
            return id;
        }
        public Guid AddChild(string kind, string name, GraphSnapshot child,
            params (string Id, string Type, object Value)[] values)
        {
            var id = Add(kind, name, values); _nodes[^1] = _nodes[^1] with { ChildGraph = child }; return id;
        }
        public void Wire(Guid source, Guid target, string? input, string? output = null) =>
            _edges.Add(new(source, target, input, output));
        public void Comment(string text) => Add("Comment", "Sample guide",
            ("text", "string", text), ("width", "float", 680d), ("height", "float", 220d));

        public (Guid Node, Guid? PositionInput) System(bool fountain, int burst, double rate, double life,
            double[] color, bool launch = false, bool externalPosition = false, bool mesh = false)
        {
            var spawn = new Builder();
            var entry = spawn.Add("CpuParticleStageEvent", "Spawn");
            Guid? positionInput = externalPosition ? spawn.Add("CpuParticleInputVector3", "Emission centre") : null;
            var position = spawn.Add("CpuParticleSetPosition", "Initial position");
            if (positionInput is { } center) spawn.Wire(center, position, "value");
            if (mesh)
            {
                var meshPosition = spawn.Add("CpuParticleMeshPosition", "Mesh surface position");
                spawn.Wire(meshPosition, position, "value", "value");
            }
            var shape = spawn.Add("CpuParticleEmitterSphere", "Deterministic random direction", ("radius", "float", 1d));
            Guid direction;
            if (fountain)
            {
                var horizontal = spawn.Add("MultiplyVector3", "Horizontal spread", ("b", "vector3",
                    launch ? new double[] { 0, 0, 0 } : new double[] { .7, 0, .7 }));
                spawn.Wire(shape, horizontal, "a", "value");
                direction = spawn.Add("AddVector3", "Upward velocity", ("b", "vector3",
                    new double[] { 0, launch ? 6 : 5, 0 }));
                spawn.Wire(horizontal, direction, "a");
            }
            else
            {
                var normalized = spawn.Add("NormalizeVector3", "Sphere direction");
                var source = mesh ? spawn.Add("CpuParticleMeshNormal", "Mesh surface normal") : shape;
                spawn.Wire(source, normalized, "a", "value");
                direction = spawn.Add("MultiplyVector3", "Outward speed", ("b", "vector3", new double[] { 3, 3, 3 }));
                spawn.Wire(normalized, direction, "a", "value");
            }
            var velocity = spawn.Add("CpuParticleSetVelocity", "Initial velocity");
            spawn.Wire(direction, velocity, "value");
            var lifetime = spawn.Add("CpuParticleSetLife", "Particle lifetime", ("value", "float", life));
            var size = spawn.Add("CpuParticleSetSize", "Glow diameter", ("value", "vector2", new double[] { .14, .14 }));
            var tint = spawn.Add("CpuParticleSetColor", "Initial tint", ("value", "color", color));
            var chain = new[] { entry, position, velocity, lifetime, size, tint };
            for (var i = 1; i < chain.Length; i++) spawn.Wire(chain[i - 1], chain[i], "signal", "then");

            var update = new Builder();
            var tick = update.Add("CpuParticleStageEvent", "Update");
            var move = update.Add("CpuParticleMove", "Velocity + gravity", ("acceleration", "vector3",
                new double[] { 0, launch ? -3 : fountain ? -9.81 : -1.5, 0 }));
            update.Wire(tick, move, "signal", "then");
            var age = update.Add("CpuParticleAge", "Age");
            var duration = update.Add("CpuParticleLife", "Lifetime");
            var normalizedAge = update.Add("Divide", "Age / lifetime");
            update.Wire(age, normalizedAge, "a", "value");
            update.Wire(duration, normalizedAge, "b", "value");
            var clamp = update.Add("Clamp", "Saturate lifetime"); update.Wire(normalizedAge, clamp, "a");
            var fade = update.Add("Subtract", "Fade out", ("a", "float", 1d)); update.Wire(clamp, fade, "b", "value");
            var rgba = update.Add("ComposeVector4", "Tint and opacity", ("r", "float", color[0]),
                ("g", "float", color[1]), ("b", "float", color[2]));
            update.Wire(fade, rgba, "a");
            var asColor = update.Add("Vector4ToColor", "Particle color"); update.Wire(rgba, asColor, "a", "value");
            var setColor = update.Add("CpuParticleSetColor", "Fade particle"); update.Wire(asColor, setColor, "value");
            update.Wire(move, setColor, "signal", "then");

            var particle = new Builder();
            particle.AddChild("CpuParticleSpawn", "Spawn", spawn.Graph);
            particle.AddChild("CpuParticleUpdate", "Update", update.Graph);
            particle.Add("CpuParticleSpriteDraw", "Procedural glow", ("assetPath", "string", ""),
                ("textureSource", "enum:ab42721e-d2c3-43e9-a96d-c80000000001", 0),
                ("blendMode", "string", "Additive"));
            var cpuNode = AddChild("CpuParticle", fountain ? "Fountain" : "Radial burst", particle.Graph,
                ("capacity", "int", 4096), ("burst", "int", burst), ("rate", "float", rate));
            var procTex = Add("ProceduralTexture", "Glow texture");
            var procMat = Add("ProceduralMaterial", "Glow material");
            Wire(procTex, procMat, "texture0", "value");
            Wire(procMat, cpuNode, "proceduralMaterial", "value");
            return (cpuNode, positionInput);
        }
    }
}
