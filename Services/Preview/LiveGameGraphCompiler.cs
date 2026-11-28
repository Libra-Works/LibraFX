using System.Buffers.Binary;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;
using LiberaFx.Services.Assets;

namespace LiberaFx.Services.Preview;

public sealed record LiveGameGraphSource(GraphSnapshot Graph, string Name, string? GroupDirectory = null);

public static class LiveGameGraphCompiler
{
    public static byte[] Compile(LiveGameGraphSource source, VfxCompileOptions? options = null)
    {
        var libraries = string.IsNullOrWhiteSpace(source.GroupDirectory)
            ? Array.Empty<GroupAssetLibrary>() : [new GroupAssetLibrary(source.GroupDirectory)];
        var graph = new GroupCompilationResolver(libraries).Resolve(source.Graph);
        if (SkeletalPoseSchema.ContainsSource(graph))
        {
            throw new NotSupportedException(
                "Live game transfer does not support SOURCE skeletal poses; SOURCE requires Unity or Unreal Spawnable package playback.");
        }
        // Use the SAME package compiler/converter as saved .lfxp, not a bespoke
        // parameter patch or preview payload. Never mutate the source/history.
        var package = SpawnableGroupPackage.Compile(graph, options: options);
        if (package.AutoDestroyWhenIdle || package.HasDeferredSignals || package.HasChildDeferredSignals ||
            package.OnStartNodeId is not null || package.OnDestroyNodeId is not null ||
            package.Templates.Count != 0 || package.DestroyRoutes.Count != 0 ||
            package.SignalRoutes.Count != 0 || package.GameIoPorts.Count != 0 || package.RootGpuContainers.Count != 0 ||
            package.CpuPrograms.Length != 0 ||
            !HasPureInstructions(package.ParentBytecode))
        {
            throw new NotSupportedException("UnsupportedBinary: live Restart supports only manually ended root-render numeric graphs. State, lifecycle signals, children, game I/O and GPU migration are unsupported.");
        }
        var bytes = SpawnableGroupPackageBinary.Serialize(package);
        if (bytes.Length > 16 * 1024 * 1024)
        {
            throw new NotSupportedException("UnsupportedBinary: live package exceeds 16 MiB.");
        }
        return bytes;
    }

    private static bool HasPureInstructions(byte[] bytecode)
    {
        const int headerSize = 16;
        const int instructionSize = 256;
        if (bytecode.Length < headerSize || !bytecode.AsSpan(0, 4).SequenceEqual("LFXB"u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytecode.AsSpan(4)) != 11 ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytecode.AsSpan(12)) != instructionSize)
        {
            return false;
        }
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytecode.AsSpan(8));
        if (count < 0 || count > (bytecode.Length - headerSize) / instructionSize)
        {
            return false;
        }
        for (var index = 0; index < count; index++)
        {
            if (bytecode[headerSize + index * instructionSize + 16] is < 1 or > 39)
            {
                return false;
            }
        }
        return true;
    }
}
