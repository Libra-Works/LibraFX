using System.Buffers.Binary;
using System.Text.Json;
using Avalonia.Threading;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;
using LibraGpu.Core;

internal static class SpawnablePackageBinaryTests
{
    private static readonly byte[] EmptyGpuPrograms = "{\"version\":1,\"programs\":[]}"u8.ToArray();

    public static void Run(string outputPath)
    {
        var package = SpawnableGroupPackage.Compile(FireworksSample.Create());
        var binary = Verify(package);
        Require(binary.AsSpan(0, 4).SequenceEqual("LFXP"u8), "Portable package signature missing.");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, binary);
        File.WriteAllBytes(Path.ChangeExtension(outputPath, ".metadata.lfxp"), Verify(CreateMetadataFixture()));
        File.WriteAllBytes(Path.ChangeExtension(outputPath, ".legacy.lfxp"), VerifyLegacyV1(package));
        File.WriteAllBytes(Path.ChangeExtension(outputPath, ".legacy-v2.lfxp"), VerifyLegacyV2(package));
        File.WriteAllBytes(Path.ChangeExtension(outputPath, ".root.lfxp"), VerifyRootPayloads());
        File.WriteAllBytes(Path.ChangeExtension(outputPath, ".gpu-programs.lfxp"), VerifyGpuPrograms());
        var godotGpu = new GraphWorkspaceViewModel(new UndoRedoManager()) { AutoDestroyWhenIdle = false };
        godotGpu.CreateNode(new("GpuParticle"));
        File.WriteAllBytes(Path.ChangeExtension(outputPath, ".godot-gpu.lfxp"),
            Verify(SpawnableGroupPackage.Compile(godotGpu.CreateSnapshot())));
        File.WriteAllBytes(Path.ChangeExtension(outputPath, ".godot-flow-map.lfxp"), Verify(CreateGodotFlowMapFixture()));
        var adapter = CreateAdapterFixture(includeGpu: false);
        File.WriteAllBytes(Path.ChangeExtension(outputPath, ".adapter.lfxp"), Verify(adapter.Package));
        File.WriteAllBytes(Path.ChangeExtension(outputPath, ".adapter.ids.json"), adapter.Identities);
        var godotMaterial = CreateAdapterFixture(includeGpu: false, includeMaterials: true);
        File.WriteAllBytes(Path.ChangeExtension(outputPath, ".godot-material.lfxp"), Verify(godotMaterial.Package));
        File.WriteAllBytes(Path.ChangeExtension(outputPath, ".godot-material.ids.json"), godotMaterial.Identities);
        var adapterGpu = CreateAdapterFixture(includeGpu: true);
        File.WriteAllBytes(Path.ChangeExtension(outputPath, ".adapter-gpu.lfxp"), Verify(adapterGpu.Package));
        File.WriteAllBytes(Path.ChangeExtension(outputPath, ".adapter-gpu.ids.json"), adapterGpu.Identities);
        var gpuSpawnable = CreateGpuSpawnableFixture();
        File.WriteAllBytes(Path.ChangeExtension(outputPath, ".gpu.lfxp"), Verify(gpuSpawnable.Package));
        File.WriteAllBytes(Path.ChangeExtension(outputPath, ".gpu.ids.json"), gpuSpawnable.Identities);
        var godotGpuMatrix = CreateGpuSpawnableFixture(includeDrawMatrix: true);
        File.WriteAllBytes(Path.ChangeExtension(outputPath, ".godot-gpu-matrix.lfxp"), Verify(godotGpuMatrix.Package));
        File.WriteAllBytes(Path.ChangeExtension(outputPath, ".template-gameio.lfxp"), CreateTemplateGameIoFixture());
        Console.WriteLine($"Portable Spawnable package v3 executable GPU payloads and v1/v2 compatibility passed ({binary.Length} bytes).");
    }

    public static byte[] Verify(SpawnableGroupPackage package)
    {
        var binary = SpawnableGroupPackageBinary.Serialize(package);
        Require(binary.AsSpan(0, 4).SequenceEqual("LFXP"u8) &&
            BinaryPrimitives.ReadUInt16LittleEndian(binary.AsSpan(4)) == 3 &&
            BinaryPrimitives.ReadUInt32LittleEndian(binary.AsSpan(16)) == 11,
            "Current writer must emit LFXP container/package version 3/11.");
        using (var emptyRootPrograms = JsonDocument.Parse(package.RootGpuPrograms.Length == 0
                   ? EmptyGpuPrograms : package.RootGpuPrograms))
            Require(emptyRootPrograms.RootElement.GetProperty("version").GetInt32() == 1 &&
                emptyRootPrograms.RootElement.GetProperty("programs").ValueKind == JsonValueKind.Array,
                "Current package root executable GPU payload must use the versioned schema.");
        var decoded = SpawnableGroupPackageBinary.Deserialize(binary);
        Require(decoded.ParentBytecode.SequenceEqual(package.ParentBytecode) &&
            decoded.RootRenderConfiguration.SequenceEqual(package.RootRenderConfiguration) &&
            decoded.RootGpuPrograms.SequenceEqual(EffectiveGpuPrograms(package.RootGpuPrograms)) &&
            JsonSerializer.SerializeToUtf8Bytes(decoded.RootGpuContainers)
                .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(package.RootGpuContainers)) &&
            decoded.AutoDestroyWhenIdle == package.AutoDestroyWhenIdle &&
            decoded.HasDeferredSignals == package.HasDeferredSignals &&
            decoded.HasChildDeferredSignals == package.HasChildDeferredSignals &&
            decoded.OnStartNodeId == package.OnStartNodeId &&
            decoded.OnDestroyNodeId == package.OnDestroyNodeId &&
            decoded.Templates.Count == package.Templates.Count &&
            decoded.Templates.Zip(package.Templates).All(pair =>
                pair.First.ContainerId == pair.Second.ContainerId &&
                pair.First.SignalTypeId == pair.Second.SignalTypeId &&
                pair.First.Bytecode.SequenceEqual(pair.Second.Bytecode) &&
                pair.First.RenderConfiguration.SequenceEqual(pair.Second.RenderConfiguration) &&
                pair.First.GpuPrograms.SequenceEqual(EffectiveGpuPrograms(pair.Second.GpuPrograms)) &&
                pair.First.Inputs.SequenceEqual(pair.Second.Inputs) &&
                SameOptional(pair.First.Outputs, pair.Second.Outputs) &&
                SameOptional(pair.First.LiveInputs, pair.Second.LiveInputs) &&
                SameOptional(pair.First.CapturedInputs, pair.Second.CapturedInputs) &&
                SameOptional(pair.First.SignalInputs, pair.Second.SignalInputs) &&
                SameOptional(pair.First.SignalOutputs, pair.Second.SignalOutputs) &&
                pair.First.Lifecycle == pair.Second.Lifecycle &&
                JsonSerializer.SerializeToUtf8Bytes(pair.First.GpuContainers)
                    .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(pair.Second.GpuContainers))) &&
            SameGameIoPorts(decoded.GameIoPorts, package.GameIoPorts) &&
            decoded.DestroyRoutes.Count == package.DestroyRoutes.Count &&
            decoded.DestroyRoutes.Zip(package.DestroyRoutes).All(pair =>
                pair.First.SourceContainerId == pair.Second.SourceContainerId &&
                pair.First.TargetContainerId == pair.Second.TargetContainerId &&
                pair.First.DelayNodeId == pair.Second.DelayNodeId &&
                pair.First.Captures.SequenceEqual(pair.Second.Captures)) &&
            decoded.SignalRoutes.SequenceEqual(package.SignalRoutes),
            "Portable package round-trip changed compiled lifecycle data.");
        Reject(() => SpawnableGroupPackageBinary.Deserialize(binary.AsSpan(0, binary.Length - 1)),
            "truncated portable package");
        var truncatedRoot = binary.AsSpan(0, binary.Length - 1).ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(truncatedRoot.AsSpan(8), (ulong)truncatedRoot.Length);
        Reject(() => SpawnableGroupPackageBinary.Deserialize(truncatedRoot), "truncated root payload");
        var unsupportedVersion = binary.ToArray();
        unsupportedVersion[4] = 255;
        Reject(() => SpawnableGroupPackageBinary.Deserialize(unsupportedVersion), "unknown portable package format");
        var invalidBoolean = binary.ToArray();
        invalidBoolean[24] = 2;
        Reject(() => SpawnableGroupPackageBinary.Deserialize(invalidBoolean), "invalid portable package boolean");
        var invalidRootPrograms = binary.ToArray();
        invalidRootPrograms[RootGpuProgramsOffset(package) + sizeof(int)] = 0xff;
        Reject(() => SpawnableGroupPackageBinary.Deserialize(invalidRootPrograms), "invalid root GPU program UTF-8");
        var invalidTemplatePrograms = binary.ToArray();
        invalidTemplatePrograms[^1] = 0xff;
        Reject(() => SpawnableGroupPackageBinary.Deserialize(invalidTemplatePrograms), "invalid template GPU program UTF-8");
        return binary;
    }

    private static byte[] VerifyLegacyV1(SpawnableGroupPackage package)
    {
        var current = SpawnableGroupPackageBinary.Serialize(package);
        var rootGpuContainers = JsonSerializer.SerializeToUtf8Bytes(package.RootGpuContainers);
        var rootTailBytes = sizeof(int) + package.RootRenderConfiguration.Length +
            sizeof(int) + rootGpuContainers.Length + TailProgramBytes(package);
        var legacy = current.AsSpan(0, current.Length - rootTailBytes).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(legacy.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(legacy.AsSpan(8), (ulong)legacy.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(legacy.AsSpan(16), 9);
        var decoded = SpawnableGroupPackageBinary.Deserialize(legacy);
        Require(decoded.ParentBytecode.SequenceEqual(package.ParentBytecode) &&
            decoded.Templates.Count == package.Templates.Count &&
            decoded.RootRenderConfiguration.Length == 0 && decoded.RootGpuContainers.Count == 0 &&
            decoded.RootGpuPrograms.Length == 0 && decoded.Templates.All(template => template.GpuPrograms.Length == 0),
            "Legacy v1 LFXP data changed or invented root/program payloads.");
        var mixed = legacy.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(mixed.AsSpan(16), SpawnableGroupPackage.FormatVersion);
        Reject(() => SpawnableGroupPackageBinary.Deserialize(mixed), "mismatched container/package versions");
        return legacy;
    }

    private static byte[] VerifyLegacyV2(SpawnableGroupPackage package)
    {
        var current = SpawnableGroupPackageBinary.Serialize(package);
        var tailBytes = TailProgramBytes(package);
        var legacy = current.AsSpan(0, current.Length - tailBytes).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(legacy.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(legacy.AsSpan(8), (ulong)legacy.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(legacy.AsSpan(16), 10);
        var decoded = SpawnableGroupPackageBinary.Deserialize(legacy);
        Require(decoded.ParentBytecode.SequenceEqual(package.ParentBytecode) &&
            decoded.Templates.Count == package.Templates.Count &&
            decoded.RootRenderConfiguration.SequenceEqual(package.RootRenderConfiguration) &&
            decoded.RootGpuContainers.Count == package.RootGpuContainers.Count &&
            decoded.RootGpuPrograms.Length == 0 && decoded.Templates.All(template => template.GpuPrograms.Length == 0),
            "Legacy v2 LFXP data changed or invented executable GPU payloads.");
        return legacy;
    }

    private static int TailProgramBytes(SpawnableGroupPackage package) =>
        sizeof(int) + EffectiveGpuPrograms(package.RootGpuPrograms).Length +
        package.Templates.Sum(template => sizeof(int) + EffectiveGpuPrograms(template.GpuPrograms).Length);

    private static byte[] EffectiveGpuPrograms(byte[] bytes) => bytes.Length == 0 ? EmptyGpuPrograms : bytes;

    private static int RootGpuProgramsOffset(SpawnableGroupPackage package) =>
        SpawnableGroupPackageBinary.Serialize(package).Length - TailProgramBytes(package);

    private static byte[] VerifyRootPayloads()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        void Draw(string asset)
        {
            var draw = graph.CreateNode(new("ModelDraw"));
            graph.SetParameter(draw, "assetPath", JsonSerializer.SerializeToElement(asset));
            var position = graph.CreateNode(GraphNodeKind.FloatX3);
            Require(graph.TryConnectNodes(position, draw, out _, "position"),
                "Could not connect the render fixture's runtime position.");
        }
        Draw("/Engine/BasicShapes/Cube.Cube");
        var ordinary = graph.CreateNode(GraphNodeKind.GraphGroup);
        graph.EnterGraph(ordinary);
        Draw("/Engine/BasicShapes/Cube.Cube");
        var rootGpu = DrawGpu([1, 2, 3]);
        graph.NavigateToGraph(0);
        var template = graph.CreateNode(GraphNodeKind.SpawnableGroup);
        graph.EnterGraph(template);
        Draw("/Engine/BasicShapes/Sphere.Sphere");
        var childGpu = DrawGpu([4, 5, 6]);
        graph.NavigateToGraph(0);
        var package = SpawnableGroupPackage.Compile(graph.CreateSnapshot());
        using var rootRender = JsonDocument.Parse(package.RootRenderConfiguration);
        Require(rootRender.RootElement.GetProperty("nodes").GetArrayLength() == 2 &&
            !rootRender.RootElement.GetRawText().Contains("Sphere", StringComparison.Ordinal) &&
            package.RootGpuContainers.Select(node => node.Id).SequenceEqual([rootGpu]) &&
            package.Templates.Single().GpuContainers.Select(node => node.Id).SequenceEqual([childGpu]),
            "Root payloads lost ordinary Group contents or included Spawnable template contents.");
        Guid DrawGpu(double[] translation)
        {
            var transform = graph.CreateNode(new("Transform"));
            graph.SetParameter(transform, "position", JsonSerializer.SerializeToElement(translation));
            var gpu = graph.CreateNode(new("GpuParticle"));
            Require(graph.TryConnectNodes(transform, gpu, out _, "drawMatrix", "value"),
                "The GPU Draw Matrix pin did not accept a Matrix.");
            return gpu.Id;
        }
        void VerifySourceIds(byte[] render, byte[] bytecode, Guid expectedGpu, byte[] otherBytecode)
        {
            using var scene = JsonDocument.Parse(render);
            foreach (var draw in scene.RootElement.GetProperty("nodes").EnumerateArray())
            {
                var input = draw.GetProperty("inputs").EnumerateArray()
                    .Single(item => item.GetProperty("id").GetString() == "position");
                var source = input.GetProperty("sourceNodeId").GetGuid();
                Require(bytecode.AsSpan().IndexOf(source.ToByteArray(bigEndian: true)) >= 0,
                    "Render binding references a source missing from its own runtime program.");
            }
            var transform = scene.RootElement.GetProperty("gpuDrawTransforms").EnumerateArray().Single();
            var rows = transform.GetProperty("rowNodeIds").EnumerateArray()
                .Select(row => row.GetGuid().ToByteArray(bigEndian: true)).ToArray();
            Require(transform.GetProperty("nodeId").GetGuid() == expectedGpu && rows.Length == 4 &&
                rows.All(row => bytecode.AsSpan().IndexOf(row) >= 0 && otherBytecode.AsSpan().IndexOf(row) < 0),
                "GPU draw transform rows must only reference the owning program.");
        }
        var templatePackage = package.Templates.Single();
        VerifySourceIds(package.RootRenderConfiguration, package.ParentBytecode, rootGpu, templatePackage.Bytecode);
        VerifySourceIds(templatePackage.RenderConfiguration, templatePackage.Bytecode, childGpu, package.ParentBytecode);
        var pending = LiberaFxMcpTools.CompileSpawnable(new ActiveGraphProvider(graph));
        Dispatcher.UIThread.RunJobs();
        using var mcp = JsonDocument.Parse(pending.GetAwaiter().GetResult());
        Require(Convert.FromBase64String(mcp.RootElement.GetProperty("rootRenderConfigurationBase64").GetString()!)
                .SequenceEqual(package.RootRenderConfiguration) &&
            Convert.FromBase64String(mcp.RootElement.GetProperty("rootGpuProgramsBase64").GetString()!)
                .SequenceEqual(package.RootGpuPrograms) &&
            Convert.FromBase64String(mcp.RootElement.GetProperty("templates")[0]
                .GetProperty("gpuProgramsBase64").GetString()!)
                .SequenceEqual(package.Templates.Single().GpuPrograms) &&
            mcp.RootElement.GetProperty("rootGpuContainers").GetArrayLength() == 1,
            "MCP compilation dropped root payloads.");
        return Verify(package);
    }

    private static byte[] VerifyGpuPrograms()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var rootGpu = graph.CreateNode(new("GpuParticle")).Id;
        var ordinary = graph.CreateNode(GraphNodeKind.GraphGroup);
        graph.EnterGraph(ordinary);
        var nestedRootGpu = graph.CreateNode(new("GpuParticle")).Id;
        graph.NavigateToGraph(0);
        var spawnable = graph.CreateNode(GraphNodeKind.SpawnableGroup);
        graph.EnterGraph(spawnable);
        var templateGpu = graph.CreateNode(new("GpuParticle")).Id;
        _ = graph.CreateNode(new("LifeTime"));
        graph.NavigateToGraph(0);

        var package = SpawnableGroupPackage.Compile(graph.CreateSnapshot());
        using var rootPayload = JsonDocument.Parse(package.RootGpuPrograms);
        using var templatePayload = JsonDocument.Parse(package.Templates.Single().GpuPrograms);
        var rootPrograms = rootPayload.RootElement.GetProperty("programs").EnumerateArray().ToArray();
        var templatePrograms = templatePayload.RootElement.GetProperty("programs").EnumerateArray().ToArray();
        Require(rootPayload.RootElement.GetProperty("version").GetInt32() == 1 &&
            rootPrograms.Length == 2 && templatePrograms.Length == 1 &&
            rootPrograms.Select(program => program.GetProperty("nodeId").GetGuid()).ToHashSet()
                .SetEquals([rootGpu, nestedRootGpu]) &&
            templatePrograms[0].GetProperty("nodeId").GetGuid() == templateGpu &&
            package.RootGpuContainers.Select(node => node.Id).ToHashSet().SetEquals([rootGpu, nestedRootGpu]) &&
            package.Templates.Single().GpuContainers.Select(node => node.Id).SequenceEqual([templateGpu]),
            "Executable GPU payloads mixed root and Spawnable ownership or lost nested root GPU nodes.");
        var overLimit = new GraphSnapshot(Enumerable.Range(0, 17).Select(_ =>
            new GraphNodeSnapshot(Guid.NewGuid(), new("GpuParticle"), "GPU", 0, 0,
                "GpuParticle", GraphPortCategory.Parameter)).ToArray(), []);
        Reject(() => _ = GpuProgramDeliveryService.CreateExecutablePayload(overLimit),
            "more than 16 executable GPU programs in one scope");
        var unboundGraph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var unboundNode = unboundGraph.CreateNode(new("GpuParticle"));
        var unboundState = GpuParticleEditorService.ReadState(unboundNode) with
        {
            ResourceAssetPaths = []
        };
        unboundGraph.SetParameter(unboundNode, GpuParticleEditorService.ParameterId,
            JsonSerializer.SerializeToElement(unboundState, GpuParticleEditorService.Json));
        Reject(() => SpawnableGroupPackage.Compile(unboundGraph.CreateSnapshot()),
            "GPU programs with missing engine resource references");

        foreach (var item in rootPrograms.Concat(templatePrograms))
        {
            var unreal = item.GetProperty("unreal");
            var canonical = JsonDocument.Parse(unreal.GetProperty("canonicalJson").GetString()!);
            using (canonical)
            using (var unity = JsonDocument.Parse(item.GetProperty("unityMetadataJson").GetString()!))
            {
                var unrealMetadata = unreal.GetProperty("metadata");
                Require(unrealMetadata.GetProperty("format").GetString() == "Usf" &&
                    unity.RootElement.GetProperty("format").GetString() == "UnityCompute" &&
                    unrealMetadata.GetProperty("programId").GetString() ==
                        unity.RootElement.GetProperty("programId").GetString() &&
                    unrealMetadata.GetProperty("contentHash").GetString() ==
                        unity.RootElement.GetProperty("contentHash").GetString() &&
                    canonical.RootElement.GetProperty("id").GetString() ==
                        unrealMetadata.GetProperty("programId").GetString() &&
                    item.GetProperty("unityShaderSource").GetString()!.Contains("Kernel", StringComparison.Ordinal),
                    "Unreal and Unity GPU metadata did not originate from one canonical compiled program.");
            }
        }
        return Verify(package);
    }

    private static (SpawnableGroupPackage Package, byte[] Identities) CreateAdapterFixture(bool includeGpu,
        bool includeMaterials = false)
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager()) { AutoDestroyWhenIdle = false };
        var signal = graph.CreateNode(GraphNodeKind.Signal);
        var rootPosition = graph.CreateNode(GraphNodeKind.FloatX3);
        var rootDraw = graph.CreateNode(new("ModelDraw"));
        graph.SetParameter(rootDraw, "assetPath", JsonSerializer.SerializeToElement("/Engine/BasicShapes/Cube.Cube"));
        var material = new EngineAsset("/Game/FX/ModelMaterial", "Godot material", "material", "Godot", Materials:
            [new(0, "/Game/FX/ModelMaterial", "Godot material",
                [new("strength", "scalar", JsonSerializer.SerializeToElement(0.75)),
                 new("effect_color", "vector", JsonSerializer.SerializeToElement(new[] { 0.25, 0.5, 0.75, 1 })),
                 new("effect_texture", "texture", JsonSerializer.SerializeToElement("/Game/FX/MaterialTexture"))])]);
        if (includeMaterials)
        {
            graph.AssignRenderMaterial(rootDraw, material, 0);
        }
        Require(graph.TryConnectNodes(rootPosition, rootDraw, out _, "position"),
            "Could not connect the adapter fixture's root constant position.");

        var spawnable = graph.CreateNode(GraphNodeKind.SpawnableGroup);
        Require(graph.TryConnectNodes(signal, spawnable, out _, "spawn"),
            "Could not connect the adapter fixture's root Signal to its Spawnable.");
        Require(graph.EnterGraph(spawnable), "Could not enter the adapter fixture's Spawnable.");
        var lifeTime = graph.CreateNode(new("LifeTime"));
        graph.SetParameter(lifeTime, "seconds", JsonSerializer.SerializeToElement(2d));
        var time = graph.CreateNode(GraphNodeKind.Time);
        var vector = graph.CreateNode(new("ScalarToVector3"));
        var templateDraw = graph.CreateNode(new("ModelDraw"));
        graph.SetParameter(templateDraw, "assetPath", JsonSerializer.SerializeToElement("/Engine/BasicShapes/Cube.Cube"));
        if (includeMaterials)
        {
            graph.AssignRenderMaterial(templateDraw, material, 0);
            Require(graph.TryConnectNodes(time, templateDraw, out _,
                templateDraw.InputPorts.Single(port => port.Name == "strength").Id),
                "Could not connect the material fixture's child-local strength.");
        }
        Require(graph.TryConnectNodes(time, vector, out _, "a") &&
            graph.TryConnectNodes(vector, templateDraw, out _, "position"),
            "Could not connect the adapter fixture's template time-to-position chain.");
        Guid? gpuNodeId = null;
        if (includeGpu) gpuNodeId = graph.CreateNode(new("GpuParticle")).Id;
        graph.NavigateToGraph(0);

        var package = SpawnableGroupPackage.Compile(graph.CreateSnapshot());
        var identities = JsonSerializer.SerializeToUtf8Bytes(new
        {
            rootSignalNodeId = signal.Id,
            rootDrawNodeId = rootDraw.Id,
            spawnableContainerId = spawnable.Id,
            templateDrawNodeId = templateDraw.Id,
            lifeTimeNodeId = lifeTime.Id,
            timeNodeId = time.Id,
            vectorNodeId = vector.Id,
            gpuNodeId
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return (package, identities);
    }

    private static byte[] CreateTemplateGameIoFixture()
    {
        var inputDefinition = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.InputProperty);
        var input = new GraphNodeSnapshot(Guid.NewGuid(), inputDefinition.Kind, inputDefinition.Name, 0, 0,
            inputDefinition.PortType, inputDefinition.PortCategory, Parameters:
            [
                new("name", "string", JsonSerializer.SerializeToElement("template-input")),
                new("valueType", "int", JsonSerializer.SerializeToElement(0))
            ]);
        var child = new GraphSnapshot([input], []);
        var program = VfxProgram.Compile(child);
        var rootProgram = VfxProgram.Compile(new GraphSnapshot([], []));
        var template = new SpawnableTemplate(Guid.NewGuid(), Guid.NewGuid(), program.ToBinary(),
            [], [], []);
        return SpawnableGroupPackageBinary.Serialize(new SpawnableGroupPackage(rootProgram.ToBinary(), [template]));
    }

    private static SpawnableGroupPackage CreateGodotFlowMapFixture()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager()) { AutoDestroyWhenIdle = false };
        var gpu = graph.CreateNode(new("GpuParticle"));
        var state = GpuParticleEditorService.ReadState(gpu);
        const string resourceId = "godotFlowMap";
        var resources = state.Program.Resources.Append(new ResourceDeclaration
        {
            Id = resourceId, Kind = ResourceKind.FlowMap2D, Width = 2, Height = 2, FlowMap = new()
        }).ToList();
        var spawn = state.Program.Kernels.Single(kernel => kernel.Stage == KernelStage.Spawn);
        var replacement = GpuDsl.Compile(
            "@Position = sampleFlowMap(\"godotFlowMap\", float3(0, 0, 1));" +
            "@Size = sampleFlowMap(\"godotFlowMap\", float2(2, -1));",
            spawn.Id, spawn.Stage, resources: resources);
        var paths = new Dictionary<string, string>(state.ResourceAssetPaths)
        {
            [resourceId] = "/Game/FX/T_GodotFlow.T_GodotFlow"
        };
        graph.SetParameter(gpu, GpuParticleEditorService.ParameterId, JsonSerializer.SerializeToElement(state with
        {
            ResourceAssetPaths = paths,
            Program = state.Program with
            {
                Resources = resources,
                Kernels = state.Program.Kernels.Select(kernel => kernel == spawn ? replacement : kernel).ToList()
            }
        }, GpuParticleEditorService.Json));
        return SpawnableGroupPackage.Compile(graph.CreateSnapshot());
    }

    private static (SpawnableGroupPackage Package, byte[] Identities) CreateGpuSpawnableFixture(
        bool includeDrawMatrix = false)
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var onStart = graph.CreateNode(new("OnStart"));
        var spawnable = graph.CreateNode(GraphNodeKind.SpawnableGroup);
        Require(graph.TryConnectNodes(onStart, spawnable, out _, "spawn", "then") &&
            graph.EnterGraph(spawnable),
            "Could not connect On Start to the finite GPU Spawnable.");
        var lifeTime = graph.CreateNode(new("LifeTime"));
        graph.SetParameter(lifeTime, "seconds", JsonSerializer.SerializeToElement(2d));
        var gpu = graph.CreateNode(new("GpuParticle"));
        if (includeDrawMatrix)
        {
            var transform = graph.CreateNode(new("Transform"));
            graph.SetParameter(transform, "position", JsonSerializer.SerializeToElement(new[] { 250d, 0, 0 }));
            Require(graph.TryConnectNodes(transform, gpu, out _, "drawMatrix", "value"),
                "The Godot GPU fixture could not connect its child Draw Matrix.");
        }
        graph.NavigateToGraph(0);
        var package = SpawnableGroupPackage.Compile(graph.CreateSnapshot());
        Require(package.OnStartNodeId == onStart.Id && package.RootGpuContainers.Count == 0 &&
            package.Templates.Single().GpuContainers.Select(node => node.Id).SequenceEqual([gpu.Id]),
            "GPU Spawnable fixture must start one finite GPU template and keep root GPU empty.");
        using var configuration = JsonDocument.Parse(package.Templates.Single().RenderConfiguration);
        Require(configuration.RootElement.TryGetProperty("gpuDrawTransforms", out _) == includeDrawMatrix,
            "The GPU Draw Matrix metadata must match whether the pin is connected.");
        var identities = JsonSerializer.SerializeToUtf8Bytes(new
        {
            onStartNodeId = onStart.Id,
            spawnableContainerId = spawnable.Id,
            lifeTimeNodeId = lifeTime.Id,
            gpuNodeId = gpu.Id,
            lifeTimeSeconds = 2
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return (package, identities);
    }

    private static SpawnableGroupPackage CreateMetadataFixture()
    {
        var containerId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var targetContainerId = Guid.Parse("10000000-0000-0000-0000-000000000002");
        var typeId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var nodeId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        var sourceNodeId = Guid.Parse("30000000-0000-0000-0000-000000000002");
        var routeCapture = new SpawnableRouteCapture(nodeId, 1, containerId, sourceNodeId, 3);
        var template = new SpawnableTemplate(containerId, typeId, [1, 2, 3],
            [new("color", nodeId, "Color", 4, true, true)], [4, 5], [],
            new(nodeId, sourceNodeId, nodeId), [new("out", sourceNodeId, "Color")],
            [routeCapture], [new(nodeId, 1, null, sourceNodeId, 0)],
            [new("signal-in", nodeId, sourceNodeId)], [new("signal-out", sourceNodeId)]);
        return new([6, 7, 8], [template])
        {
            AutoDestroyWhenIdle = false,
            HasDeferredSignals = true,
            HasChildDeferredSignals = true,
            OnStartNodeId = nodeId,
            OnDestroyNodeId = sourceNodeId,
            RootRenderConfiguration = [11, 12],
            GameIoPorts =
            [
                new(nodeId, "input-signal", GameIoPortKind.InputSignal, 0, false, [sourceNodeId]),
                new(sourceNodeId, "output-color", GameIoPortKind.OutputProperty, 4, true)
            ],
            DestroyRoutes = [new(containerId, targetContainerId, [routeCapture], sourceNodeId)],
            SignalRoutes = [new(containerId, nodeId, sourceNodeId)]
        };
    }

    private static void Reject(Action action, string name)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException($"Unsupported {name} was accepted.");
    }

    private static bool SameOptional<T>(IReadOnlyList<T>? first, IReadOnlyList<T>? second) =>
        first is null ? second is null : second is not null && first.SequenceEqual(second);

    private static bool SameGameIoPorts(IReadOnlyList<GameIoPort> first, IReadOnlyList<GameIoPort> second) =>
        first.Count == second.Count && first.Zip(second).All(pair =>
            pair.First.NodeId == pair.Second.NodeId &&
            pair.First.Name == pair.Second.Name &&
            pair.First.Kind == pair.Second.Kind &&
            pair.First.Width == pair.Second.Width &&
            pair.First.IsColor == pair.Second.IsColor &&
            SameOptional(pair.First.RuntimeNodeIds, pair.Second.RuntimeNodeIds));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
