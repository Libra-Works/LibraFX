using System.Buffers.Binary;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;

namespace LiberaFx.Services;

public sealed record LiveGameGraphSource(GraphSnapshot Graph, string Name, string? GroupDirectory = null);

public static class LiveGameGraphCompiler
{
    public static byte[] Compile(LiveGameGraphSource source)
    {
        var libraries = string.IsNullOrWhiteSpace(source.GroupDirectory)
            ? Array.Empty<GroupAssetLibrary>() : [new GroupAssetLibrary(source.GroupDirectory)];
        var graph = new GroupCompilationResolver(libraries).Resolve(source.Graph);
        // Use the SAME package compiler/converter as saved .lfxp, not a bespoke
        // parameter patch or preview payload. Never mutate the source/history.
        var package = SpawnableGroupPackage.Compile(graph);
        if (package.AutoDestroyWhenIdle || package.HasDeferredSignals || package.HasChildDeferredSignals ||
            package.OnStartNodeId is not null || package.OnDestroyNodeId is not null ||
            package.Templates.Count != 0 || package.DestroyRoutes.Count != 0 ||
            package.SignalRoutes.Count != 0 || package.GameIoPorts.Count != 0 || package.RootGpuContainers.Count != 0 ||
            package.ParentBytecode.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(package.ParentBytecode.AsSpan(4)) > 2)
            throw new NotSupportedException("UnsupportedBinary: live Restart supports only manually ended root-render numeric graphs. State, lifecycle signals, children, game I/O and GPU migration are unsupported.");
        var bytes = SpawnableGroupPackageBinary.Serialize(package);
        if (bytes.Length > 16 * 1024 * 1024) throw new NotSupportedException("UnsupportedBinary: live package exceeds 16 MiB.");
        return bytes;
    }
}
