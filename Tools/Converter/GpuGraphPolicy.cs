using System.Text.Json;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services;

// GPU authoring data is opaque here; conversion is rejected before CPU code generation.
internal static class GpuParticleEditorService
{
    public const string ParameterId = "gpuState";
    public static bool IsGpu(GraphNodeKind kind) => kind.Value == "GpuParticle";
    public static bool IsStateValue(JsonElement value) => value.ValueKind == JsonValueKind.Object;

    public static void RejectGpu(GraphSnapshot graph)
    {
        foreach (var node in graph.Nodes)
        {
            if (IsGpu(node.Kind))
                throw new InvalidDataException("GPU Particle graphs require a GPU export package; CPU-only conversion cannot discard them.");
            if (node.ChildGraph is not null) RejectGpu(node.ChildGraph);
        }
    }
}
