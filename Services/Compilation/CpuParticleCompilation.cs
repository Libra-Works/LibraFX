using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Resources;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Gpu;
using LiberaFx.Services.Sprite;

namespace LiberaFx.Services.Compilation;

/// <summary>Compiles normal CPU VM programs, not the GPU language. Stage UUIDs remain document state.</summary>
public static class CpuParticleCompilation
{
    public static GraphSnapshot LowerEmitters(GraphSnapshot graph)
    {
        var counters = new Dictionary<Guid, Guid>();
        var nodes = new List<GraphNodeSnapshot>();
        foreach (var original in graph.Nodes)
        {
            var node = original with { ChildGraph = original.ChildGraph is { } child ? LowerEmitters(child) : null };
            if (CpuParticleSchema.Name(node.Kind) != "EmitterBurst")
            {
                nodes.Add(node);
                continue;
            }
            var count = Parameter(node, "count").GetDouble();
            if (!double.IsFinite(count) || count < 0 || count > uint.MaxValue || Math.Truncate(count) != count)
            {
                throw new InvalidDataException("Emitter Burst count must be an exact nonnegative UInt32.");
            }
            var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"LiberaFx.Cpu.Burst.v1:{node.Id:D}")).AsSpan(0, 16), true);
            counters.Add(node.Id, id);
            nodes.Add(new(id, new(GraphLogicalSchema.SignalToNumberTypeId.ToString("D")), node.Name + " counter",
                node.X, node.Y, "Float", GraphPortCategory.Parameter, Parameters:
                [new("mode", $"enum:{GraphLogicalSchema.SignalToNumberModeTypeId:D}", JsonSerializer.SerializeToElement(2))]));
            nodes.Add(node with { Kind = GraphNodeKind.Multiply, Parameters =
                [new("a", "float", JsonSerializer.SerializeToElement(0d)), new("b", "float", JsonSerializer.SerializeToElement(count))] });
        }
        return graph with { Nodes = nodes, Connections = graph.Connections.Select(edge => edge with
        {
            TargetNodeId = counters.GetValueOrDefault(edge.TargetNodeId, edge.TargetNodeId),
            TargetPortId = counters.ContainsKey(edge.TargetNodeId) ? "signal" : edge.TargetPortId,
            SourcePortId = counters.ContainsKey(edge.SourceNodeId) ? null : edge.SourcePortId
        }).Concat(counters.Select(pair => new GraphConnectionSnapshot(pair.Value, pair.Key, "a", "value"))).ToArray() };
    }
    private sealed record Io(byte Kind, int Width, Guid NodeId, Guid RootId, int Component);
    public static bool Contains(GraphSnapshot graph) => graph.Nodes.Any(node => CpuParticleSchema.IsContainer(node.Kind) ||
        node.ChildGraph is { } child && !CpuParticleSchema.HasChild(node.Kind) && Contains(child));
    public static IEnumerable<GraphNodeSnapshot> Systems(GraphSnapshot graph) => graph.Nodes.SelectMany(node =>
        CpuParticleSchema.IsContainer(node.Kind) ? new[] { node } :
        node.Kind != GraphNodeKind.SpawnableGroup && node.ChildGraph is { } child &&
        !GpuParticleEditorService.IsGpu(node.Kind) ? Systems(child) : Array.Empty<GraphNodeSnapshot>());

    public static (GraphSnapshot Graph, byte[] Payload, IReadOnlyDictionary<Guid, Guid> SignalTypes) Lower(
        GraphSnapshot original, IRenderResourceBindingContext? resources, VfxCompileOptions? options)
    {
        CpuParticleSchema.ValidateContext(original);
        var graph = GroupBoundaryLowering.Lower(original);
        var systems = graph.Nodes.Where(node => CpuParticleSchema.IsContainer(node.Kind)).ToArray();
        if (systems.Length == 0)
        {
            return (original, [], new Dictionary<Guid, Guid>());
        }
        var emitterRows = new Dictionary<Guid, Guid[]>();
        foreach (var system in systems)
        {
            var binding = CpuEmitterMatrixLowering.Bind(graph, system);
            graph = binding.Graph;
            emitterRows[system.Id] = binding.Rows;
            graph = CpuEmitterMatrixLowering.Bind(graph, system, "draw").Graph;
        }
        var nodes = graph.Nodes.Where(node => !CpuParticleSchema.IsContainer(node.Kind)).ToList();
        var ids = systems.Select(node => node.Id).ToHashSet();
        var edges = graph.Connections.Where(edge => !ids.Contains(edge.SourceNodeId) && !ids.Contains(edge.TargetNodeId)).ToList();
        var types = new Dictionary<Guid, Guid>();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(2u);
        writer.Write(systems.Length);
        foreach (var system in systems)
        {
            if (Parameter(system, "outputReduction").GetString() is not ("None" or "LastById"))
            {
                throw new InvalidDataException("CPU output reduction must be None or LastById.");
            }
            GuidId(writer, system.Id);
            var capacity = Integer(system, "capacity", 1, 1_000_000);
            var queueCapacity = Integer(system, "queueCapacity", 1, 1_000_000);
            var seed = Number(system, "seed");
            if (seed < 0 || seed > uint.MaxValue || Math.Truncate(seed) != seed)
            {
                throw new InvalidDataException("CPU Particle Seed must be an exact UInt32.");
            }
            writer.Write(capacity);
            writer.Write((uint)seed);
            writer.Write(queueCapacity);
            var rate = Number(system, "rate");
            if (rate < 0)
            {
                throw new InvalidDataException("CPU Particle rate must be nonnegative.");
            }
            writer.Write(rate);
            writer.Write(Integer(system, "burst", 0, capacity));
            var emission = graph.Connections.SingleOrDefault(edge => edge.TargetNodeId == system.Id && edge.TargetPortId == "emitted");
            GuidId(writer, emission?.SourceNodeId ?? Guid.Empty);
            writer.Write(emission is null ? 0 : Component(graph, emission));
            foreach (var stageName in new[] { "CpuParticleSpawn", "CpuParticleUpdate" })
            {
                var stage = system.ChildGraph!.Nodes.Single(node => CpuParticleSchema.Name(node.Kind) == stageName);
                var stageGraph = stage.ChildGraph!;
                var entry = stageGraph.Nodes.Where(node => CpuParticleSchema.Name(node.Kind) == "CpuParticleStageEvent").ToArray();
                if (entry.Length > 1)
                {
                    throw new InvalidDataException("Each CPU stage supports one Stage Event entry.");
                }
                if (stageGraph.Nodes.Any(node => node.Kind.Value is "OnStart" or "OnDestroy" or "OwnerDestroy" or "LifeTime" or "Delay"))
                {
                    throw new InvalidDataException("Use CPU Stage Event and particle Life. Deferred lifecycle continuations are not supported inside CPU particle kernels.");
                }
                var io = new List<Io>();
                foreach (var boundary in stageGraph.Nodes.Where(node => CpuParticleSchema.IsBoundary(node.Kind)))
                {
                    var input = CpuParticleSchema.IsInput(boundary.Kind);
                    var signal = boundary.PortCategory == GraphPortCategory.Signal;
                    var edge = input
                        ? graph.Connections.SingleOrDefault(edge => edge.TargetNodeId == system.Id &&
                            edge.TargetPortId == CpuParticleSchema.PortId(boundary.Id))
                        : graph.Connections.SingleOrDefault(edge => edge.SourceNodeId == system.Id &&
                            edge.SourcePortId == CpuParticleSchema.PortId(boundary.Id));
                    var rootId = edge is null ? Guid.Empty : input ? edge.SourceNodeId : boundary.Id;
                    if (edge is not null && (ids.Contains(edge.SourceNodeId) && input || ids.Contains(edge.TargetNodeId) && !input))
                    {
                        throw new InvalidDataException("Connect CPU systems through ordinary outer Property/Signal nodes; direct cross-system boundary links are not supported.");
                    }
                    if (!input && signal && edge is not null)
                    {
                        // The CPU payload dispatches by entry UUID. Let a regular VM
                        // edge from that entry encode the destination's named input slot.
                        rootId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
                            $"LiberaFx.Cpu.OutputSignal.v1:{system.Id:D}:{boundary.Id:D}")).AsSpan(0, 16), true);
                        nodes.Add(ValueNode(boundary, GraphNodeKind.Signal) with { Id = rootId });
                        edges.Add(edge with { SourceNodeId = rootId, SourcePortId = null });
                    }
                    if (input && signal)
                    {
                        if (stageName == "CpuParticleSpawn")
                        {
                            throw new InvalidDataException("Place external Input Signal in Update; Spawn executes once and is not an ongoing event scope.");
                        }
                        nodes.Add(ValueNode(boundary, GraphNodeKind.Signal));
                        types.Add(boundary.Id, boundary.Id);
                        if (edge is not null)
                        {
                            edges.Add(edge with { TargetNodeId = boundary.Id, TargetPortId = null });
                        }
                    }
                    if (!input && !signal && edge is not null)
                    {
                        var reduction = Parameter(system, "outputReduction").GetString();
                        if (reduction != "LastById")
                        {
                            throw new InvalidDataException("Many particles cannot implicitly overwrite one outer Property. Select explicit Output reduction 'LastById', or consume identity-bearing CPU events in the host.");
                        }
                        nodes.Add(ValueNode(boundary, ValueKind(boundary.PortType)));
                        edges.Add(edge with { SourceNodeId = boundary.Id, SourcePortId = null });
                    }
                    io.Add(new((byte)(signal ? input ? 2 : 3 : input ? 0 : 1),
                        signal ? 0 : SpawnableGroupSchema.InputWidth(boundary.PortType), boundary.Id, rootId,
                        input && edge is not null && !signal ? Component(graph, edge) : 0));
                }
                var lowered = CpuEmitterMatrixLowering.TransformShapes(
                    LowerBoundaries(LowerAttributeStageNodes(stageGraph)), stage.Id,
                    emitterRows[system.Id], out var matrixBindings);
                io.AddRange(matrixBindings.Select(binding => new Io(0, 4, binding.Inner, binding.Outer, 0)));
                var program = VfxProgram.CompileForPackage(lowered, out var render, resourceContext: resources, options: options);
                if (render.Length != 0 && lowered.Nodes.Any(node => RenderProgramBindings.IsRenderNode(node.Kind)))
                {
                    throw new InvalidDataException("Particle stage graphs cannot contain Draw nodes; use CpuParticleSpriteDraw in the container.");
                }
                GuidId(writer, entry.SingleOrDefault()?.Id ?? Guid.Empty);
                if (io.Count == 0 && options?.OptimizeCpuParticleKernels != false && options?.IncludePreviewDebugDraw != true)
                    program = program.OptimizeCpuKernel();
                var bytes = program.ToPackageBinary();
                writer.Write(bytes.Length);
                writer.Write(bytes);
                writer.Write(io.Count);
                foreach (var port in io)
                {
                    writer.Write(port.Kind);
                    writer.Write(port.Width);
                    GuidId(writer, port.NodeId);
                    GuidId(writer, port.RootId);
                    writer.Write(port.Component);
                }
            }
            var meshEdge = graph.Connections.SingleOrDefault(edge => edge.TargetNodeId == system.Id && edge.TargetPortId == "emitterMesh");
            var needsMesh = system.ChildGraph!.Nodes.Where(stage => CpuParticleSchema.IsStage(stage.Kind))
                .SelectMany(stage => stage.ChildGraph!.Nodes).Any(node => CpuParticleSchema.Name(node.Kind) is
                    "CpuParticleMeshPosition" or "CpuParticleMeshNormal" or "CpuParticleMeshUV" or "CpuParticleMeshColor");
            if (needsMesh && meshEdge is null)
                throw new InvalidDataException("Connect a Procedural Model to CPU Particle's Emitter Mesh before using mesh sampling nodes.");
            byte[] definition = [];
            if (meshEdge is not null)
            {
                var mesh = graph.Nodes.Single(node => node.Id == meshEdge.SourceNodeId);
                var modelDefinition = GraphNodeDefinition.Find(mesh.Kind)!;
                if (ProceduralResourceSchema.Name(mesh.Kind) != "ProceduralModel")
                    throw new InvalidDataException("Emitter Mesh currently requires a Procedural Model.");
                if (graph.Connections.Any(edge => edge.TargetNodeId == mesh.Id))
                    throw new InvalidDataException("Mesh emission currently supports procedural Flow settings and time animation; connected Flow parameters are not yet supported.");
                definition = Convert.FromBase64String(ProceduralDefinitionWriter.Encode("ProceduralModel",
                    ProceduralResourceSchema.ReadEffective(modelDefinition, mesh.Parameters), mesh,
                    graph.Connections.ToLookup(edge => edge.TargetNodeId)));
            }
            writer.Write(definition.Length);
            writer.Write(definition);
        }
        writer.Flush();
        return (graph with { Nodes = nodes, Connections = edges }, stream.ToArray(), types);
    }

    private static GraphSnapshot LowerAttributeStageNodes(GraphSnapshot graph)
    {
        var inputStageNodes = graph.Nodes
            .Where(node => CpuParticleSchema.Name(node.Kind) == "CpuParticleAttributesInput")
            .ToDictionary(node => node.Id);
        var outputStageNodes = graph.Nodes
            .Where(node => CpuParticleSchema.Name(node.Kind) == "CpuParticleAttributesOutput")
            .ToDictionary(node => node.Id);
        if (inputStageNodes.Count == 0 && outputStageNodes.Count == 0)
        {
            return graph;
        }

        var nodes = graph.Nodes
            .Where(node => !inputStageNodes.ContainsKey(node.Id) && !outputStageNodes.ContainsKey(node.Id))
            .ToList();
        var connections = graph.Connections.ToList();

        foreach (var (nodeId, inputNode) in inputStageNodes)
        {
            var outgoing = connections.Where(edge => edge.SourceNodeId == nodeId).ToArray();
            var portMap = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var edge in outgoing)
            {
                var portId = edge.SourcePortId ?? "position";
                if (!portMap.TryGetValue(portId, out var readerId))
                {
                    readerId = Guid.NewGuid();
                    portMap[portId] = readerId;
                    var (kindName, customSlot) = portId switch
                    {
                        "position" => ("CpuParticlePosition", -1),
                        "velocity" => ("CpuParticleVelocity", -1),
                        "color" => ("CpuParticleColor", -1),
                        "size" => ("CpuParticleSize", -1),
                        "life" => ("CpuParticleLife", -1),
                        "age" => ("CpuParticleAge", -1),
                        "stepDelta" => ("CpuParticleStepDelta", -1),
                        "id" => ("CpuParticleId", -1),
                        "seed" => ("CpuParticleSeed", -1),
                        "custom0" => ("CpuParticleCustomRead", 0),
                        "custom1" => ("CpuParticleCustomRead", 1),
                        "custom2" => ("CpuParticleCustomRead", 2),
                        "custom3" => ("CpuParticleCustomRead", 3),
                        _ => throw new InvalidDataException($"Unknown CPU particle input attribute '{portId}'.")
                    };
                    var def = GraphNodeDefinition.Find(new(kindName))!;
                    var parameters = customSlot >= 0
                        ? new[] { new NodeParameterSnapshot("slot", "int", JsonSerializer.SerializeToElement(customSlot)) }
                        : null;
                    nodes.Add(new GraphNodeSnapshot(readerId, def.Kind, def.Name, inputNode.X, inputNode.Y,
                        def.PortType, def.PortCategory, Parameters: parameters));
                }
                connections[connections.IndexOf(edge)] = edge with { SourceNodeId = readerId, SourcePortId = "value" };
            }
        }

        var outputPins = new (string PortId, string KindName, int CustomSlot)[]
        {
            ("position", "CpuParticleSetPosition", -1),
            ("velocity", "CpuParticleSetVelocity", -1),
            ("color", "CpuParticleSetColor", -1),
            ("size", "CpuParticleSetSize", -1),
            ("life", "CpuParticleSetLife", -1),
            ("age", "CpuParticleSetAge", -1),
            ("custom0", "CpuParticleCustomWrite", 0),
            ("custom1", "CpuParticleCustomWrite", 1),
            ("custom2", "CpuParticleCustomWrite", 2),
            ("custom3", "CpuParticleCustomWrite", 3)
        };

        foreach (var (nodeId, outputNode) in outputStageNodes)
        {
            var incomingSignals = connections
                .Where(edge => edge.TargetNodeId == nodeId && edge.TargetPortId == "signal")
                .ToArray();
            var outgoingSignals = connections
                .Where(edge => edge.SourceNodeId == nodeId && edge.SourcePortId == "then")
                .ToArray();
            connections.RemoveAll(edge =>
                edge.TargetNodeId == nodeId && edge.TargetPortId == "signal" ||
                edge.SourceNodeId == nodeId && edge.SourcePortId == "then");

            var chainIds = new List<Guid>();
            foreach (var (portId, kindName, customSlot) in outputPins)
            {
                var paramEdge = connections.SingleOrDefault(edge =>
                    edge.TargetNodeId == nodeId && edge.TargetPortId == portId);
                if (paramEdge is null) continue;
                var def = GraphNodeDefinition.Find(new(kindName))!;
                var setterId = Guid.NewGuid();
                var parameters = customSlot >= 0
                    ? new[] { new NodeParameterSnapshot("slot", "int", JsonSerializer.SerializeToElement(customSlot)) }
                    : null;
                nodes.Add(new GraphNodeSnapshot(setterId, def.Kind, def.Name, outputNode.X, outputNode.Y,
                    def.PortType, def.PortCategory, Parameters: parameters));
                connections[connections.IndexOf(paramEdge)] = paramEdge with
                {
                    TargetNodeId = setterId,
                    TargetPortId = "value"
                };
                chainIds.Add(setterId);
            }

            if (chainIds.Count == 0)
            {
                foreach (var inSig in incomingSignals)
                {
                    foreach (var outSig in outgoingSignals)
                    {
                        connections.Add(new GraphConnectionSnapshot(
                            inSig.SourceNodeId, outSig.TargetNodeId, outSig.TargetPortId, inSig.SourcePortId));
                    }
                }
            }
            else
            {
                foreach (var inSig in incomingSignals)
                {
                    connections.Add(inSig with { TargetNodeId = chainIds[0], TargetPortId = "signal" });
                }
                for (var i = 0; i < chainIds.Count - 1; i++)
                {
                    connections.Add(new GraphConnectionSnapshot(chainIds[i], chainIds[i + 1], "signal", "then"));
                }
                foreach (var outSig in outgoingSignals)
                {
                    connections.Add(outSig with { SourceNodeId = chainIds[^1], SourcePortId = "then" });
                }
            }
        }

        return graph with { Nodes = nodes.ToArray(), Connections = connections.ToArray() };
    }

    private static GraphSnapshot LowerBoundaries(GraphSnapshot graph)
    {
        var boundaries = graph.Nodes.Where(node => CpuParticleSchema.IsBoundary(node.Kind)).Select(node => node.Id).ToHashSet();
        return graph with
        {
            Nodes = graph.Nodes.Select(node => boundaries.Contains(node.Id)
                ? ValueNode(node, node.PortCategory == GraphPortCategory.Signal ? GraphNodeKind.Signal : ValueKind(node.PortType))
                : node).ToArray(),
            Connections = graph.Connections.Select(edge => edge with
            {
                SourcePortId = boundaries.Contains(edge.SourceNodeId) ? null : edge.SourcePortId,
                TargetPortId = boundaries.Contains(edge.TargetNodeId) ? null : edge.TargetPortId
            }).ToArray()
        };
    }
    private static GraphNodeKind ValueKind(string type) => new(type);
    private static GraphNodeSnapshot ValueNode(GraphNodeSnapshot node, GraphNodeKind kind) =>
        node with { Kind = kind, ChildGraph = null };
    private static int Component(GraphSnapshot graph, GraphConnectionSnapshot edge)
    {
        var source = graph.Nodes.Single(node => node.Id == edge.SourceNodeId);
        var definition = GraphNodeDefinition.All.Single(item => item.Kind == source.Kind);
        if (!definition.TypeName.StartsWith("BreakVector", StringComparison.Ordinal))
        {
            return 0;
        }
        return definition.OutputPorts!.ToList().FindIndex(port => port.Id == edge.SourcePortId);
    }
    public static JsonElement Parameter(GraphNodeSnapshot node, string id) =>
        node.Parameters?.SingleOrDefault(parameter => parameter.Id == id)?.Value ??
        GraphNodeDefinition.All.Single(definition => definition.Kind == node.Kind).Parameters.Single(parameter => parameter.Id == id).DefaultValue;
    private static double Number(GraphNodeSnapshot node, string id)
    {
        var value = Parameter(node, id).GetDouble();
        if (!double.IsFinite(value))
        {
            throw new InvalidDataException($"CPU Particle '{id}' must be finite.");
        }
        return value;
    }
    private static int Integer(GraphNodeSnapshot node, string id, int minimum, int maximum)
    {
        var value = Parameter(node, id).GetInt32();
        if (value < minimum || value > maximum)
        {
            throw new InvalidDataException($"CPU Particle '{id}' must be {minimum}..{maximum}.");
        }
        return value;
    }
    private static void GuidId(BinaryWriter writer, Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        writer.Write(bytes);
    }

    public static object[] Draws(GraphSnapshot graph, IRenderResourceBindingContext? resources)
    {
        var flat = GroupBoundaryLowering.Lower(graph);
        var systems = new List<(GraphNodeSnapshot Node, IReadOnlyList<Guid> Scope)>();
        void Visit(GraphSnapshot current, IReadOnlyList<Guid> scope)
        {
            foreach (var node in current.Nodes)
            {
                if (node.Kind == GraphNodeKind.SpawnableGroup)
                {
                    continue;
                }
                if (CpuParticleSchema.IsContainer(node.Kind))
                {
                    systems.Add((node, ResourceIdentityScope.Child(scope, node.Id)));
                }
                else if (node.ChildGraph is { } child && !GpuParticleEditorService.IsGpu(node.Kind))
                {
                    Visit(child, ResourceIdentityScope.Child(scope, node.Id));
                }
            }
        }
        Visit(graph, []);
        var flatNodes = flat.Nodes.ToDictionary(n => n.Id);
        var flatIncoming = flat.Connections.ToLookup(e => e.TargetNodeId);
        var emptyScopes = new Dictionary<Guid, IReadOnlyList<Guid>>();
        return systems.Select(item =>
        {
            var system = item.Node;
            var draw = system.ChildGraph!.Nodes.Single(node => CpuParticleSchema.Name(node.Kind) == "CpuParticleSpriteDraw");
            var path = Parameter(draw, "assetPath").GetString() ?? "";
            var textureSource = Parameter(draw, "textureSource").GetInt32();
            path = textureSource switch { 0 => path, 1 => "builtin:white", 2 => "builtin:radial-glow",
                _ => throw new InvalidDataException("Unsupported CPU particle texture source.") };
            var scope = resources?.GetInstancePath(draw.Id, item.Scope) ?? item.Scope;
            if (textureSource == 0) path = resources?.ResolvePath(draw.Id, "assetPath", "texture", path, scope) ?? path;

            var proceduralTexture = ProceduralProgramBindings.Source(system, "proceduralTexture", "ProceduralTexture",
                flatNodes, flatIncoming, emptyScopes);
            var proceduralMaterial = ProceduralProgramBindings.Source(system, "proceduralMaterial", "ProceduralMaterial",
                flatNodes, flatIncoming, emptyScopes);

            var baseDrawDef = GraphNodeDefinition.All.Single(d => d.Kind == draw.Kind);
            var drawDef = RenderNodeSchema.Effective(baseDrawDef, draw.Parameters);
            var materialEntries = RenderNodeSchema.Read(drawDef, draw.Parameters);
            var materialSlots = RenderNodeSchema.ReadMaterials(drawDef, draw.Parameters);
            var materials = materialSlots
                .Where(slot => !string.IsNullOrEmpty(slot.Path) ||
                    resources?.HasResourceUse(draw.Id, $"material:{slot.Slot}", scope) == true)
                .Select(slot => new
                {
                    slot = slot.Slot,
                    path = resources?.ResolvePath(draw.Id, $"material:{slot.Slot}", "material",
                        slot.Path, scope) ?? slot.Path
                }).ToArray();
            var textures = materialEntries.Where(entry => entry.Kind == "texture")
                .Select(entry =>
                {
                    var rawTex = draw.Parameters?.FirstOrDefault(p => p.Id == entry.Id)?.Value.GetString()
                        ?? drawDef.Parameters.Single(p => p.Id == entry.Id).DefaultValue.GetString() ?? "";
                    return new
                    {
                        name = entry.Name,
                        slot = entry.Slot,
                        path = resources?.ResolvePath(draw.Id, "texture:" + entry.Id, "texture", rawTex, scope) ?? rawTex,
                        association = RenderNodeSchema.MaterialAssociation(entry),
                        index = entry.Index
                    };
                }).ToArray();
            var inputs = materialEntries.Where(entry => entry.Kind != "texture")
                .Select(entry =>
                {
                    var declared = drawDef.Parameters.Single(p => p.Id == entry.Id);
                    var val = draw.Parameters?.FirstOrDefault(p => p.Id == entry.Id)?.Value ?? declared.DefaultValue;
                    var defaults = entry.Kind == "scalar"
                        ? new[] { val.GetDouble() }
                        : val.EnumerateArray().Select(v => v.GetDouble()).ToArray();
                    return new
                    {
                        id = entry.Id,
                        width = defaults.Length,
                        sourceNodeId = (Guid?)null,
                        sourceComponent = 0,
                        defaults,
                        materialParameter = new
                        {
                            name = entry.Name,
                            slot = entry.Slot,
                            association = RenderNodeSchema.MaterialAssociation(entry),
                            index = entry.Index
                        }
                    };
                }).ToArray();

            if (string.IsNullOrWhiteSpace(path))
            {
                if (proceduralMaterial is not null) path = "builtin:procedural-material";
                else if (proceduralTexture is not null) path = "builtin:procedural-texture";
                else if (materials.Length > 0) path = "builtin:white";
            }

            if (path is not ("builtin:white" or "builtin:radial-glow" or "builtin:procedural-material" or "builtin:procedural-texture") &&
                (resources is null || !resources.HasResourceUse(draw.Id, "assetPath", scope)))
            {
                EngineAssetPath.Validate(path);
            }
            var blend = Parameter(draw, "blendMode").GetString();
            var scale = Number(draw, "drawScale");
            var spriteAnimation = Parameter(draw,"spriteAnimationEnabled").GetBoolean() ? ParticleSpriteAnimation.Compile(
                SpriteAnimationData.Deserialize(Parameter(draw,"spriteData")),
                SpriteCompositionData.Deserialize(Parameter(draw,"compositionData"))) : null;
            var flipbook = new ParticleAtlasSettings(Parameter(draw,"flipbookEnabled").GetBoolean(),
                Parameter(draw,"atlasColumns").GetInt32(),Parameter(draw,"atlasRows").GetInt32(),
                Number(draw,"atlasFps"),Parameter(draw,"atlasLoop").GetBoolean(),
                Parameter(draw,"atlasStartFrame").GetInt32(),Parameter(draw,"atlasOverLife").GetBoolean());
            if (!flipbook.IsValid) throw new InvalidDataException("Flipbook requires 1–64 columns/rows, at most 256 cells, a valid start cell and 0–1000 FPS.");
            if (path.Length == 0 || blend is not ("Alpha" or "Additive") || scale <= 0)
            {
                throw new InvalidDataException("CPU Sprite Draw needs an explicit texture, Alpha/Additive blend and positive draw scale.");
            }
            var rows = flat.Connections.Any(e => e.TargetNodeId == system.Id && e.TargetPortId == "drawMatrix")
                ? CpuEmitterMatrixLowering.RowIds(system.Id, "draw") : null;
            return (object)new CpuParticleDrawDescriptor(
                system.Id, path, blend, scale, true, rows,
                spriteAnimation is null && flipbook.Enabled ? flipbook : null,
                spriteAnimation,
                proceduralTexture,
                proceduralMaterial,
                materials.Length > 0 ? materials : null,
                textures.Length > 0 ? textures : null,
                inputs.Length > 0 ? inputs : null);
        }).ToArray();
    }

    private sealed record CpuParticleDrawDescriptor(
        [property: JsonPropertyName("nodeId")] Guid NodeId,
        [property: JsonPropertyName("assetPath")] string AssetPath,
        [property: JsonPropertyName("blendMode")] string BlendMode,
        [property: JsonPropertyName("drawScale")] double DrawScale,
        [property: JsonPropertyName("billboard")] bool Billboard,
        [property: JsonPropertyName("drawMatrixRowNodeIds")] Guid[]? DrawMatrixRowNodeIds,
        [property: JsonPropertyName("flipbook")] ParticleAtlasSettings? Flipbook,
        [property: JsonPropertyName("spriteAnimation")] ParticleSpriteAnimation? SpriteAnimation,
        [property: JsonPropertyName("proceduralTexture"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ProceduralTexture,
        [property: JsonPropertyName("proceduralMaterial"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ProceduralMaterial,
        [property: JsonPropertyName("materials"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object[]? Materials,
        [property: JsonPropertyName("textures"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object[]? Textures,
        [property: JsonPropertyName("inputs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object[]? Inputs);

    public static byte[] AddDraws(byte[] scene, GraphSnapshot graph, IRenderResourceBindingContext? resources)
    {
        if (!Contains(graph))
        {
            return scene;
        }
        var root = System.Text.Json.Nodes.JsonNode.Parse(scene)!.AsObject();
        var draws = JsonSerializer.SerializeToNode(Draws(graph, resources));
        if (draws!.AsArray().Any(d => d?["proceduralMaterial"] is not null || d?["proceduralTexture"] is not null))
        {
            var flat = GroupBoundaryLowering.Lower(graph);
            var flatNodes = flat.Nodes.Where(n => n.Kind != GraphNodeKind.SpawnableGroup).ToDictionary(n => n.Id);
            var flatIncoming = flat.Connections.ToLookup(e => e.TargetNodeId);
            var compiledProc = ProceduralProgramBindings.Resources(flatNodes, flatIncoming,
                new Dictionary<Guid, IReadOnlyList<Guid>>());
            if (compiledProc.Length > 0)
            {
                var procArray = root["proceduralResources"] as System.Text.Json.Nodes.JsonArray;
                if (procArray is null)
                {
                    procArray = new System.Text.Json.Nodes.JsonArray();
                    root["proceduralVersion"] = 1;
                    root["proceduralResources"] = procArray;
                }
                var existingIds = procArray
                    .Select(item => item?["nodeId"]?.GetValue<Guid>())
                    .Where(id => id.HasValue)
                    .Select(id => id!.Value)
                    .ToHashSet();
                var serializedProc = JsonSerializer.SerializeToNode(compiledProc, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsArray();
                foreach (var entry in serializedProc)
                {
                    if (entry?["nodeId"] is { } idNode && existingIds.Add(idNode.GetValue<Guid>()))
                    {
                        procArray.Add(entry.DeepClone());
                    }
                }
            }
        }
        root["cpuParticleVersion"] = draws!.AsArray().Any(d =>
            d?["proceduralMaterial"] is not null || d?["proceduralTexture"] is not null ||
            d?["materials"] is not null) ? 5 :
            draws.AsArray().Any(d => d?["spriteAnimation"] is not null) ? 4 :
            draws.AsArray().Any(d => d?["flipbook"] is not null) ? 3 :
            draws.AsArray().Any(d => d?["drawMatrixRowNodeIds"] is not null) ? 2 : 1;
        root["cpuParticles"] = draws;
        var result=JsonSerializer.SerializeToUtf8Bytes(root);
        if (result.Length>4*1024*1024) throw new InvalidDataException("CPU particle render configuration exceeds 4 MiB.");
        return result;
    }
}
