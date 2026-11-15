using LibraGpu.Core;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services;

public sealed record GpuKernelPacket(string Id, KernelStage Stage, string Base64);
public sealed record GpuProgramDelivery(
    long ProgramSequence, Guid NodeId, string CanonicalJson, GpuExportMetadata Metadata,
    string ShaderSource, IReadOnlyList<GpuKernelPacket> RegisterKernels,
    IReadOnlyDictionary<string, string> ResourceAssetPaths);
public sealed record GpuContainerDiagnostic(Guid NodeId, GpuDiagnostic Diagnostic);

public static class GpuProgramDeliveryService
{
    public static IEnumerable<GraphNodeSnapshot> Containers(GraphSnapshot graph)
    {
        foreach (var node in graph.Nodes)
        {
            if (node.KindDomain == "GpuParticle") yield return node;
            else if (node.Kind != GraphNodeKind.SpawnableGroup && node.ChildGraph is { } child)
                foreach (var nested in Containers(child)) yield return nested;
        }
    }
    public static IReadOnlyList<GpuContainerDiagnostic> Validate(GraphSnapshot graph) =>
        Containers(graph).SelectMany(node =>
            GpuParticleEditorService.Diagnostics(GpuParticleEditorService.ReadState(node))
                .Select(d => new GpuContainerDiagnostic(node.Id, d))).ToArray();
    public static IReadOnlyList<GpuProgramDelivery> Create(GraphSnapshot graph, long programSequence,
        ShaderFormat format = ShaderFormat.Usf)
    {
        return Containers(graph).Select(node =>
        {
            var state = GpuParticleEditorService.ReadState(node);
            var compiled = GpuParticleEditorService.Compile(state);
            var shader = format switch
            {
                ShaderFormat.Usf => compiled.EmitUsf(), ShaderFormat.HlslInclude => compiled.EmitHlslInclude(),
                ShaderFormat.UnityCompute => compiled.EmitUnityCompute(), _ => throw new ArgumentException("Unsupported shader format.")
            };
            return new GpuProgramDelivery(programSequence, node.Id, compiled.CanonicalJson,
                compiled.GetExportMetadata(format), shader.Text,
                compiled.RegisterKernels.Select(k => new GpuKernelPacket(k.KernelId, k.Stage,
                    Convert.ToBase64String(k.SerializeLittleEndian()))).ToArray(),
                new Dictionary<string, string>(state.ResourceAssetPaths));
        }).ToArray();
    }
}
