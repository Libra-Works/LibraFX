using System.Runtime.CompilerServices;
using System.Text.Json;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services;

public static class GpuPreviewScene
{
    public const int MaxPrograms = 16;
    private static readonly ConditionalWeakTable<GraphSnapshot, Lazy<IReadOnlyList<GpuProgramDelivery>>> Cache = new();

    public static void Validate(GraphSnapshot graph) => _ = Programs(graph);

    public static byte[] CreateScene(GraphSnapshot graph, long programSequence)
    {
        if (programSequence < 0) throw new ArgumentOutOfRangeException(nameof(programSequence));
        return Encode(Programs(graph), programSequence);
    }

    private static IReadOnlyList<GpuProgramDelivery> Programs(GraphSnapshot graph) =>
        Cache.GetValue(graph, snapshot => new(() =>
        {
            if (GpuProgramDeliveryService.Containers(snapshot).Take(MaxPrograms + 1).Count() > MaxPrograms)
                throw new InvalidDataException($"Live preview supports at most {MaxPrograms} GPU Particle containers.");
            var programs = GpuProgramDeliveryService.Create(snapshot, 0);
            if (programs.Select(program => program.NodeId).Distinct().Count() != programs.Count)
                throw new InvalidDataException("GPU Particle containers must have unique node IDs.");
            _ = Encode(programs, long.MaxValue);
            return programs;
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private static byte[] Encode(IReadOnlyList<GpuProgramDelivery> programs, long sequence)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "gpu_config", version = 1, programSequence = sequence,
            programs = programs.Select(program => program with { ProgramSequence = sequence }).ToArray()
        }, GpuParticleEditorService.Json);
        if (bytes.Length > CompiledPreviewProtocol.MaxSceneBytes)
            throw new InvalidDataException("GPU preview configuration exceeds the 1 MiB scene payload limit.");
        return bytes;
    }
}
