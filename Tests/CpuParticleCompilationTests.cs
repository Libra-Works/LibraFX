using System.Buffers.Binary;
using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Commands;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;

namespace LiberaFx.Tests;

public static class CpuParticleCompilationTests
{
    private static void VerifyAheadOfTimeKernels()
    {
        var graph = LiberaFx.Services.ParticleSamples.Create("CpuFountain");
        var saved = JsonSerializer.Deserialize<GraphSnapshot>(JsonSerializer.Serialize(graph))!;
        var original = SpawnableGroupPackage.Compile(graph,
            options: new VfxCompileOptions { OptimizeCpuParticleKernels = false });
        var optimized = SpawnableGroupPackage.Compile(graph);
        Require(original.CpuPrograms.Length - optimized.CpuPrograms.Length == 5 * 256,
            "Distribution compilation must remove five lifetime-color intermediates before writing the CPU binary.");
        Require(original.ParentBytecode.SequenceEqual(optimized.ParentBytecode),
            "CPU kernel optimization must preserve the outer effect program.");
        Require(GraphSnapshot.ContentEquals(graph, saved), "Offline optimization must preserve the editable source graph.");
        var debug = SpawnableGroupPackage.Compile(graph, options: VfxCompileOptions.LiveTransfer);
        Require(debug.CpuPrograms.SequenceEqual(original.CpuPrograms),
            "Live debug transfer must retain source-node instructions for stepping.");
        var restored = SpawnableGroupPackageBinary.Deserialize(SpawnableGroupPackageBinary.Serialize(optimized));
        Require(restored.CpuPrograms.SequenceEqual(optimized.CpuPrograms),
            "Preoptimized CPU code must survive ordinary package serialization.");
        var originalBytes = SpawnableGroupPackageBinary.Serialize(original);
        var savedBytes = originalBytes.ToArray();
        var rewritten = CpuParticleBinaryOptimizer.OptimizePackage(originalBytes);
        Require(rewritten.SequenceEqual(SpawnableGroupPackageBinary.Serialize(optimized)),
            "Optimization from the binary must match optimization from the source program.");
        Require(originalBytes.SequenceEqual(savedBytes), "Binary optimization must preserve its input buffer.");
        Require(CpuParticleBinaryOptimizer.OptimizePackage(rewritten).SequenceEqual(rewritten),
            "Optimizing an already optimized binary must be idempotent.");
        Reject(() => CpuParticleBinaryOptimizer.OptimizePackage(originalBytes[..^1]), "Truncated binary optimization");
        var fireworks = LiberaFx.Services.ParticleSamples.Create("Fireworks");
        var nestedOriginal = SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(fireworks,
            options: new VfxCompileOptions { OptimizeCpuParticleKernels = false }));
        var nestedOptimized = SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(fireworks));
        Require(CpuParticleBinaryOptimizer.OptimizePackage(nestedOriginal).SequenceEqual(nestedOptimized),
            "Binary optimization must relocate CPU kernels inside nested Spawnable templates too.");
    }

    public static void Run()
    {
        VerifyAheadOfTimeKernels();
        VerifyMatrixInputs();
        VerifyFlipbook();
        VerifyParticleMaterials();
        ParticleAtlasTests.Run();
        var graph = Fixture();
        GraphValidator.ValidateDocument(graph);
        var saved = JsonSerializer.Deserialize<GraphSnapshot>(JsonSerializer.Serialize(graph))!;
        Require(GraphSnapshot.ContentEquals(graph, saved), "CPU stage snapshots must roundtrip with stable IDs.");
        var package = SpawnableGroupPackage.Compile(saved);
        Require(package.Version == 12 && package.CpuPrograms.Length > 0, "CPU must produce executable numeric payload.");
        var bytes = SpawnableGroupPackageBinary.Serialize(package);
        Require(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)) == 4, "CPU requires explicit new envelope version.");
        var restored = SpawnableGroupPackageBinary.Deserialize(bytes);
        Require(restored.CpuPrograms.SequenceEqual(package.CpuPrograms), "CPU payload must survive package serialization.");
        using var scene = JsonDocument.Parse(restored.RootRenderConfiguration);
        Require(scene.RootElement.GetProperty("cpuParticleVersion").GetInt32() == 1 &&
            scene.RootElement.GetProperty("cpuParticles").GetArrayLength() == 1,
            "CPU batch scene metadata must be present.");
        var stageIds = graph.Nodes.Single(Cpu).ChildGraph!.Nodes
            .Where(node => CpuParticleSchema.IsStage(node.Kind)).SelectMany(node => node.ChildGraph!.Nodes)
            .Where(node => !CpuParticleSchema.IsBoundary(node.Kind)).Select(node => node.Id).ToArray();
        Span<byte> uuid = stackalloc byte[16];
        foreach (var id in stageIds)
        {
            id.TryWriteBytes(uuid, true, out _);
            Require(!ContainsInstructionId(package.ParentBytecode, uuid), "CPU stage nodes must never leak into the parent numeric VM.");
        }
        Reject(() => VfxProgram.Compile(new([Node("CpuParticlePosition")], [])), "Stage-only attributes at root");
        Reject(() => VfxProgram.Compile(graph), "CPU raw-bytecode export");
        VerifyOuterSignalTarget(graph);
        var invalid = graph with { Nodes = [graph.Nodes.Single(Cpu) with
        {
            ChildGraph = graph.Nodes.Single(Cpu).ChildGraph! with
            {
                Nodes = graph.Nodes.Single(Cpu).ChildGraph!.Nodes.Where(node => CpuParticleSchema.Name(node.Kind) != "CpuParticleUpdate").ToArray()
            }
        }] };
        Reject(() => SpawnableGroupPackage.Compile(invalid), "Missing Update stage");
        var legacy = SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(new([Node("Float")], [])));
        Require(BinaryPrimitives.ReadUInt16LittleEndian(legacy.AsSpan(4)) == 3, "Non-CPU package format must remain unchanged.");

        var burst = Node("EmitterBurst", Parameter("count", "float", 4d));
        var trigger = Node("Signal");
        var burstProgram = VfxProgram.Compile(new([trigger, burst], [new(trigger.Id, burst.Id, "trigger")]));
        Require(burstProgram.CustomInstructions.Any(item => item.TypeId == GraphLogicalSchema.SignalToNumberTypeId),
            "Burst must lower to real CPU Signal counting, not a catalog-only trigger.");

        var history = new UndoRedoManager();
        var workspace = new GraphWorkspaceViewModel(history);
        var cpuDefinition = Definition("CpuParticle");
        var vm = workspace.CreateNode(cpuDefinition.Kind, 80, 80);
        Require(vm.ChildGraph?.Nodes.Count == 3 && workspace.EnterGraph(vm), "CPU authoring must use ordinary child navigation.");
        var spawn = workspace.Nodes.Single(node => CpuParticleSchema.Name(node.Definition.Kind) == "CpuParticleSpawn");
        Require(workspace.EnterGraph(spawn), "Spawn stage must be navigable.");
        var before = workspace.CreateSnapshot();
        workspace.CreateNode(Definition("CpuParticleSetLife").Kind, 800, 100);
        var after = workspace.CreateSnapshot();
        Require(!GraphSnapshot.ContentEquals(before, after), "Nested stage edits must be document state.");
        history.Undo();
        Require(GraphSnapshot.ContentEquals(before, workspace.CreateSnapshot()), "Stage edit Undo must restore the whole document.");
        history.Redo();
        Require(GraphSnapshot.ContentEquals(after, workspace.CreateSnapshot()), "Stage edit Redo must restore IDs and nested edits.");
        // History entry points are the same ordinary workspace commands, not a CPU-specific mutable model.
        workspace.NavigateToGraph(0);
        Require(workspace.Nodes.Single().ChildGraph!.Nodes.Single(node =>
            CpuParticleSchema.Name(node.Kind) == "CpuParticleSpawn").ChildGraph!.Nodes.Count == 3,
            "Leaving a CPU stage must retain edits.");
        CpuParticleFlowTests.Run();
    }

    private static void VerifyOuterSignalTarget(GraphSnapshot graph)
    {
        var system = graph.Nodes.Single(Cpu);
        var output = system.ChildGraph!.Nodes
            .Single(node => CpuParticleSchema.Name(node.Kind) == "CpuParticleUpdate").ChildGraph!.Nodes
            .Single(node => CpuParticleSchema.Name(node.Kind) == "CpuParticleOutputSignal");
        var counter = Node("SignalToNumber");
        var withReset = graph with
        {
            Nodes = [.. graph.Nodes, counter],
            Connections = [.. graph.Connections, new(system.Id, counter.Id, "reset", CpuParticleSchema.PortId(output.Id))]
        };
        var lowered = CpuParticleCompilation.Lower(withReset, null, null);
        var relay = lowered.Graph.Connections.Single(edge => edge.TargetNodeId == counter.Id);
        Require(relay.TargetPortId == "reset" && relay.SourcePortId is null &&
            lowered.Graph.Nodes.Single(node => node.Id == relay.SourceNodeId).Kind == GraphNodeKind.Signal,
            "CPU output Signal must forward through an ordinary VM edge to Reset, not the first input.");
        var program = VfxProgram.Compile(lowered.Graph);
        var route = program.LogicalSignalRoutes.Single(item =>
            program.TypedInstructions[item.SourceIndex].NodeId == relay.SourceNodeId &&
            program.TypedInstructions[item.TargetIndex].NodeId == counter.Id);
        Require(route.TargetInputSlot == 1, "The VM route must encode Reset as slot 1, not Signal as slot 0.");
        var package = SpawnableGroupPackage.Compile(withReset);
        Span<byte> relayId = stackalloc byte[16];
        relay.SourceNodeId.TryWriteBytes(relayId, true, out _);
        Require(package.CpuPrograms.AsSpan().IndexOf(relayId) >= 0 &&
            ContainsInstructionId(package.ParentBytecode, relayId),
            "The CPU payload must dispatch to the compiled relay entry, not directly to the counter.");
    }

    public static GraphSnapshot Fixture()
    {
        var system = Node("CpuParticle", Parameter("rate", "float", 0d), Parameter("burst", "int", 3)) with
        {
            ChildGraph = CpuParticleSchema.CreateChild(Definition("CpuParticle").Kind)
        };
        var child = system.ChildGraph!;
        var update = child.Nodes.Single(node => CpuParticleSchema.Name(node.Kind) == "CpuParticleUpdate");
        var input = Node("CpuParticleInputVector3");
        var setVelocity = Node("CpuParticleSetVelocity");
        var outputId = Node("CpuParticleOutputFloat");
        var id = Node("CpuParticleId");
        var outputSignal = Node("CpuParticleOutputSignal");
        var inputSignal = Node("CpuParticleInputSignal");
        var entry = update.ChildGraph!.Nodes.Single(node => CpuParticleSchema.Name(node.Kind) == "CpuParticleStageEvent");
        var move = update.ChildGraph.Nodes.Single(node => CpuParticleSchema.Name(node.Kind) == "CpuParticleMove");
        update = update with { ChildGraph = new(
            [entry, move, input, setVelocity, outputId, id, outputSignal, inputSignal],
            [new(entry.Id, setVelocity.Id, "signal", "then"),
             new(input.Id, setVelocity.Id, "value"), new(setVelocity.Id, move.Id, "signal", "then"),
             new(id.Id, outputId.Id, null, "value"), new(inputSignal.Id, outputSignal.Id)]) };
        system = system with { ChildGraph = child with
        {
            Nodes = child.Nodes.Select(node =>
                node.Id == update.Id ? update :
                CpuParticleSchema.Name(node.Kind) == "CpuParticleSpriteDraw" ? node with { Parameters = [Parameter("assetPath", "string", "builtin:white")] } :
                node).ToArray()
        } };
        var velocity = Node("FloatX3", Parameter("value", "vector3", new[] { 10d, 0, 0 }));
        return new([system, velocity], [new(velocity.Id, system.Id, CpuParticleSchema.PortId(input.Id))]);
    }

    public static GraphSnapshot MatrixFixture()
    {
        var graph = Fixture();
        var system = graph.Nodes.Single(Cpu);
        var spawn = system.ChildGraph!.Nodes.Single(n => CpuParticleSchema.Name(n.Kind) == "CpuParticleSpawn");
        var shape = Node("CpuParticleEmitterBox", Parameter("extent", "vector3", new[] { 0d, 0, 0 }));
        var position = Node("CpuParticleSetPosition");
        var first = spawn.ChildGraph!.Connections.Single();
        spawn = spawn with { ChildGraph = spawn.ChildGraph with
        {
            Nodes = [.. spawn.ChildGraph.Nodes, shape, position],
            Connections = [first with { TargetNodeId = position.Id }, new(position.Id, first.TargetNodeId, "signal", "then"),
                new(shape.Id, position.Id, "value", "value")]
        } };
        system = system with { ChildGraph = system.ChildGraph with
            { Nodes = system.ChildGraph.Nodes.Select(n => n.Id == spawn.Id ? spawn : n).ToArray() } };
        var matrix = Node("Transform", Parameter("position", "vector3", new[] { 10d, 20, 30 }));
        return graph with { Nodes = graph.Nodes.Where(n => n.Id != system.Id).Append(system).Append(matrix).ToArray(),
            Connections = [.. graph.Connections, new(matrix.Id, system.Id, "emitterMatrix", "value"),
                new(matrix.Id, system.Id, "drawMatrix", "value")] };
    }

    private static void VerifyMatrixInputs()
    {
        var package = SpawnableGroupPackage.Compile(MatrixFixture());
        using var render = JsonDocument.Parse(package.RootRenderConfiguration);
        var scene = render.RootElement;
        Require(scene.GetProperty("cpuParticleVersion").GetInt32() == 2 &&
            scene.GetProperty("cpuParticles")[0].GetProperty("drawMatrixRowNodeIds").GetArrayLength() == 4,
            "CPU Draw Matrix must automatically select the matching scene format and retain all four row bindings.");
        Span<byte> id = stackalloc byte[16];
        foreach (var row in scene.GetProperty("cpuParticles")[0].GetProperty("drawMatrixRowNodeIds").EnumerateArray())
        {
            row.GetGuid().TryWriteBytes(id, true, out _);
            Require(ContainsInstructionId(package.ParentBytecode, id), "CPU draw rows must be actual root VM values.");
        }
        Require(package.CpuPrograms.Length > 0, "CPU emitter Matrix must retain executable particle stages.");
    }

    private static void VerifyFlipbook()
    {
        var graph = MatrixFixture();
        var system = graph.Nodes.Single(Cpu);
        var child = system.ChildGraph!;
        var draw = child.Nodes.Single(node => CpuParticleSchema.Name(node.Kind) == "CpuParticleSpriteDraw");
        GraphSnapshot WithAtlas(int columns) => graph with
        {
            Nodes = graph.Nodes.Select(node => node.Id != system.Id ? node : system with
            {
                ChildGraph = child with { Nodes = child.Nodes.Select(n => n.Id != draw.Id ? n : draw with
                {
                    Parameters = [Parameter("assetPath", "string", "builtin:white"),
                        Parameter("flipbookEnabled", "bool", true), Parameter("atlasColumns", "int", columns),
                        Parameter("atlasRows", "int", 2), Parameter("atlasFps", "float", 12d),
                        Parameter("atlasStartFrame", "int", 1), Parameter("atlasOverLife", "bool", true)]
                }).ToArray() }
            }).ToArray()
        };
        var source = WithAtlas(2);
        var roundtrip = JsonSerializer.Deserialize<GraphSnapshot>(JsonSerializer.Serialize(source))!;
        var package = SpawnableGroupPackageBinary.Deserialize(SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(roundtrip)));
        using var render = JsonDocument.Parse(package.RootRenderConfiguration);
        var scene = render.RootElement;
        var item = scene.GetProperty("cpuParticles")[0];
        var atlas = item.GetProperty("flipbook");
        Require(scene.GetProperty("cpuParticleVersion").GetInt32() == 3 &&
            item.GetProperty("drawMatrixRowNodeIds").GetArrayLength() == 4 &&
            atlas.GetProperty("columns").GetInt32() == 2 && atlas.GetProperty("rows").GetInt32() == 2 &&
            atlas.GetProperty("startFrame").GetInt32() == 1 && atlas.GetProperty("overLife").GetBoolean(),
            "Flipbook must survive document/package serialization alongside Draw Matrix, with an explicit runtime version.");
        Reject(() => SpawnableGroupPackage.Compile(WithAtlas(65)), "Out of range atlas");
    }

    private static void VerifyParticleMaterials()
    {
        var procMat = Node("ProceduralMaterial");
        var system = Node("CpuParticle");
        var child = CpuParticleSchema.CreateChild(Definition("CpuParticle").Kind);
        var draw = child.Nodes.Single(n => CpuParticleSchema.Name(n.Kind) == "CpuParticleSpriteDraw");
        var matSchema = RenderNodeSchema.EncodeMaterials([new(0, "/Game/Materials/M_CpuMat", "M_CpuMat", [])]);
        system = system with
        {
            ChildGraph = child with
            {
                Nodes = child.Nodes.Select(n => n.Id != draw.Id ? n : n with
                {
                    Parameters = [Parameter("materialSchema", "string", matSchema)]
                }).ToArray()
            }
        };
        var cpuGraph = new GraphSnapshot([procMat, system], [new(procMat.Id, system.Id, "proceduralMaterial", "value")]);
        var cpuPkg = SpawnableGroupPackage.Compile(cpuGraph);
        using (var render = JsonDocument.Parse(cpuPkg.RootRenderConfiguration))
        {
            var root = render.RootElement;
            Require(root.GetProperty("cpuParticleVersion").GetInt32() == 5, "CPU particle materials require version 5.");
            var item = root.GetProperty("cpuParticles")[0];
            Require(item.GetProperty("proceduralMaterial").GetGuid() == procMat.Id, "CPU particle must emit proceduralMaterial.");
            Require(item.GetProperty("materials")[0].GetProperty("path").GetString() == "/Game/Materials/M_CpuMat",
                "CPU particle must emit engine material slot 0.");
            Require(root.GetProperty("proceduralResources").EnumerateArray()
                .Any(r => r.GetProperty("nodeId").GetGuid() == procMat.Id),
                "CPU particle proceduralMaterial must be included in proceduralResources.");
        }

        var gpu = Node("GpuParticle", Parameter("materialSchema", "string", matSchema));
        var gpuGraph = new GraphSnapshot([procMat, gpu], [new(procMat.Id, gpu.Id, "proceduralMaterial", "value")]);
        var gpuPkg = SpawnableGroupPackage.Compile(gpuGraph);
        using (var gpuRender = JsonDocument.Parse(gpuPkg.RootRenderConfiguration))
        {
            Require(gpuRender.RootElement.GetProperty("proceduralResources").EnumerateArray()
                .Any(r => r.GetProperty("nodeId").GetGuid() == procMat.Id),
                "GPU particle proceduralMaterial must be included in proceduralResources.");
        }
        using var gpuPayload = JsonDocument.Parse(gpuPkg.RootGpuPrograms);
        var rb = gpuPayload.RootElement.GetProperty("programs")[0].GetProperty("unreal").GetProperty("rendererBindings")[0];
        Require(gpuPayload.RootElement.GetProperty("programs")[0].GetProperty("unreal").GetProperty("rendererBindingsVersion").GetInt32() == 3,
            "GPU particle materials require rendererBindingsVersion 3.");
        Require(rb.GetProperty("proceduralMaterial").GetGuid() == procMat.Id, "GPU particle must emit proceduralMaterial.");
        Require(rb.GetProperty("materials")[0].GetProperty("path").GetString() == "/Game/Materials/M_CpuMat",
            "GPU particle must emit engine material slot 0.");
    }

    // Called only by an explicitly invoked fixture command; never runs a build/editor.
    public static void WriteNativeFixture(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || Directory.Exists(directory))
        {
            throw new ArgumentException("Supply an absolute NEW isolated temporary directory.");
        }
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "cpu-emitter-matrix.lfxb"),
            SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(MatrixFixture())));
        File.WriteAllBytes(Path.Combine(directory, "cpu-particles.lfxb"),
            SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(Fixture())));
        var atlasSystem = Node("CpuParticle", Parameter("rate", "float", 0d), Parameter("burst", "int", 1)) with
        { ChildGraph = CpuParticleSchema.CreateChild(Definition("CpuParticle").Kind) };
        atlasSystem = atlasSystem with { ChildGraph = atlasSystem.ChildGraph! with
        {
            Nodes = atlasSystem.ChildGraph!.Nodes.Select(node => CpuParticleSchema.Name(node.Kind) != "CpuParticleSpriteDraw" ? node : node with
            {
                Parameters = [Parameter("assetPath", "string", "/Game/LibraTests/Atlas.Atlas"), Parameter("flipbookEnabled", "bool", true),
                    Parameter("atlasColumns", "int", 2), Parameter("atlasRows", "int", 2), Parameter("atlasFps", "float", 4d)]
            }).ToArray()
        } };
        File.WriteAllBytes(Path.Combine(directory, "cpu-flipbook.lfxb"),
            SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(new([atlasSystem], []))));
        var renderGraph = Fixture();
        var system = renderGraph.Nodes.Single(Cpu);
        var child = system.ChildGraph!;
        var spawn = child.Nodes.Single(node => CpuParticleSchema.Name(node.Kind) == "CpuParticleSpawn");
        var velocity = spawn.ChildGraph!.Nodes.Single(node =>
            CpuParticleSchema.Name(node.Kind) == "CpuParticleSetVelocity");
        var color = Node("CpuParticleSetColor",
            Parameter("value", "color", new[] { 1d, 0.1, 0.05, 0.5 }));
        spawn = spawn with { ChildGraph = spawn.ChildGraph with
        {
            Nodes = [.. spawn.ChildGraph.Nodes, color],
            Connections = [.. spawn.ChildGraph.Connections, new(velocity.Id, color.Id, "signal", "then")]
        } };
        system = system with { ChildGraph = child with
        {
            Nodes = child.Nodes.Select(node => node.Id == spawn.Id ? spawn : node).ToArray()
        } };
        renderGraph = renderGraph with
        {
            Nodes = renderGraph.Nodes.Select(node => node.Id == system.Id ? system : node).ToArray()
        };
        File.WriteAllBytes(Path.Combine(directory, "cpu-particles-render.lfxb"),
            SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(renderGraph)));
    }
    private static bool Cpu(GraphNodeSnapshot node) => CpuParticleSchema.IsContainer(node.Kind);
    private static GraphNodeDefinition Definition(string name) => GraphNodeDefinition.All.Single(item => item.TypeName == name);
    private static GraphNodeSnapshot Node(string name, params NodeParameterSnapshot[] parameters)
    {
        var definition = Definition(name);
        return new(Guid.NewGuid(), definition.Kind, definition.Name, 80, 80, definition.PortType,
            definition.PortCategory, Parameters: parameters.Length == 0 ? null : parameters);
    }
    private static NodeParameterSnapshot Parameter<T>(string id, string type, T value) => new(id, type, JsonSerializer.SerializeToElement(value));
    private static bool ContainsInstructionId(byte[] bytes, ReadOnlySpan<byte> id)
    {
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        var stride = bytes[4] >= 3 ? 256 : 96;
        for (var index = 0; index < count; index++)
        {
            if (bytes.AsSpan(16 + index * stride, 16).SequenceEqual(id))
            {
                return true;
            }
        }
        return false;
    }
    private static void Reject(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException(message + " must fail explicitly.");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
