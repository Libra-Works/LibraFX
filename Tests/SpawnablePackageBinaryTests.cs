using System.Buffers.Binary;
using System.Text.Json;
using Avalonia.Threading;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;

internal static class SpawnablePackageBinaryTests
{
    public static void Run(string outputPath)
    {
        var package = SpawnableGroupPackage.Compile(FireworksSample.Create());
        var binary = Verify(package);
        Require(binary.AsSpan(0, 4).SequenceEqual("LFXP"u8), "Portable package signature missing.");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, binary);
        File.WriteAllBytes(Path.ChangeExtension(outputPath, ".metadata.lfxp"), Verify(CreateMetadataFixture()));
        File.WriteAllBytes(Path.ChangeExtension(outputPath, ".legacy.lfxp"), VerifyLegacy(package));
        File.WriteAllBytes(Path.ChangeExtension(outputPath, ".root.lfxp"), VerifyRootPayloads());
        Console.WriteLine($"Portable Spawnable package v2 root payloads and v1 compatibility passed ({binary.Length} bytes).");
    }

    public static byte[] Verify(SpawnableGroupPackage package)
    {
        var binary = SpawnableGroupPackageBinary.Serialize(package);
        Require(binary.AsSpan(0, 4).SequenceEqual("LFXP"u8), "Portable package signature missing.");
        var decoded = SpawnableGroupPackageBinary.Deserialize(binary);
        Require(decoded.ParentBytecode.SequenceEqual(package.ParentBytecode) &&
            decoded.RootRenderConfiguration.SequenceEqual(package.RootRenderConfiguration) &&
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
        var invalidRootGpu = binary.ToArray();
        invalidRootGpu[^1] = 0xff;
        Reject(() => SpawnableGroupPackageBinary.Deserialize(invalidRootGpu), "invalid root GPU JSON");
        return binary;
    }

    private static byte[] VerifyLegacy(SpawnableGroupPackage package)
    {
        package = package with { RootGpuContainers = [] };
        var current = SpawnableGroupPackageBinary.Serialize(package);
        var rootPayloadBytes = 2 * sizeof(int) + package.RootRenderConfiguration.Length + "[]"u8.Length;
        var legacy = current.AsSpan(0, current.Length - rootPayloadBytes).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(legacy.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(legacy.AsSpan(8), (ulong)legacy.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(legacy.AsSpan(16), 9);
        var decoded = SpawnableGroupPackageBinary.Deserialize(legacy);
        Require(decoded.ParentBytecode.SequenceEqual(package.ParentBytecode) &&
            decoded.Templates.Count == package.Templates.Count &&
            decoded.RootRenderConfiguration.Length == 0 && decoded.RootGpuContainers.Count == 0,
            "Legacy LFXP data changed or invented root payloads.");
        _ = Verify(decoded);
        var mixed = legacy.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(mixed.AsSpan(16), SpawnableGroupPackage.FormatVersion);
        Reject(() => SpawnableGroupPackageBinary.Deserialize(mixed), "mismatched container/package versions");
        return legacy;
    }

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
        var rootGpu = graph.CreateNode(new("GpuParticle")).Id;
        graph.NavigateToGraph(0);
        var template = graph.CreateNode(GraphNodeKind.SpawnableGroup);
        graph.EnterGraph(template);
        Draw("/Engine/BasicShapes/Sphere.Sphere");
        var childGpu = graph.CreateNode(new("GpuParticle")).Id;
        graph.NavigateToGraph(0);
        var package = SpawnableGroupPackage.Compile(graph.CreateSnapshot());
        using var rootRender = JsonDocument.Parse(package.RootRenderConfiguration);
        Require(rootRender.RootElement.GetProperty("nodes").GetArrayLength() == 2 &&
            !rootRender.RootElement.GetRawText().Contains("Sphere", StringComparison.Ordinal) &&
            package.RootGpuContainers.Select(node => node.Id).SequenceEqual([rootGpu]) &&
            package.Templates.Single().GpuContainers.Select(node => node.Id).SequenceEqual([childGpu]),
            "Root payloads lost ordinary Group contents or included Spawnable template contents.");
        void VerifySourceIds(byte[] render, byte[] bytecode)
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
        }
        VerifySourceIds(package.RootRenderConfiguration, package.ParentBytecode);
        VerifySourceIds(package.Templates.Single().RenderConfiguration, package.Templates.Single().Bytecode);
        var pending = LiberaFxMcpTools.CompileSpawnable(new ActiveGraphProvider(graph));
        Dispatcher.UIThread.RunJobs();
        using var mcp = JsonDocument.Parse(pending.GetAwaiter().GetResult());
        Require(Convert.FromBase64String(mcp.RootElement.GetProperty("rootRenderConfigurationBase64").GetString()!)
                .SequenceEqual(package.RootRenderConfiguration) &&
            mcp.RootElement.GetProperty("rootGpuContainers").GetArrayLength() == 1,
            "MCP compilation dropped root payloads.");
        return Verify(package);
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
