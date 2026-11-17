using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;

internal static class SpawnablePackageBinaryTests
{
    public static void Run(string outputPath)
    {
        var binary = Verify(SpawnableGroupPackage.Compile(FireworksSample.Create()));
        Require(binary.AsSpan(0, 4).SequenceEqual("LFXP"u8), "Portable package signature missing.");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, binary);
        File.WriteAllBytes(Path.ChangeExtension(outputPath, ".metadata.lfxp"), Verify(CreateMetadataFixture()));
        Console.WriteLine($"Portable Spawnable package v1 round-trip passed ({binary.Length} bytes).");
    }

    public static byte[] Verify(SpawnableGroupPackage package)
    {
        var binary = SpawnableGroupPackageBinary.Serialize(package);
        Require(binary.AsSpan(0, 4).SequenceEqual("LFXP"u8), "Portable package signature missing.");
        var decoded = SpawnableGroupPackageBinary.Deserialize(binary);
        Require(decoded.ParentBytecode.SequenceEqual(package.ParentBytecode) &&
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
        var unsupportedVersion = binary.ToArray();
        unsupportedVersion[4] = 2;
        Reject(() => SpawnableGroupPackageBinary.Deserialize(unsupportedVersion), "unknown portable package format");
        var invalidBoolean = binary.ToArray();
        invalidBoolean[24] = 2;
        Reject(() => SpawnableGroupPackageBinary.Deserialize(invalidBoolean), "invalid portable package boolean");
        return binary;
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
