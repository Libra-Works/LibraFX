using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace LibraGpu.Core;

public sealed record ShaderBindings
{
    public string ParticleBuffer { get; init; } = "ParticleBuffer";
    public string UniformBuffer { get; init; } = "LibraGpuParameters";
    public int ParticleRegister { get; init; }
    public int UniformRegister { get; init; }
    public int TextureRegisterBase { get; init; }
    public int SamplerRegisterBase { get; init; }
}
public enum ShaderFormat { HlslInclude, Usf, UnityCompute }
public enum CompilationPurpose { SourceExport }
public sealed record SourceLineMapping(int Line, string KernelId, string NodeId, string? PinId, TextSpan? Span = null);
public sealed record UniformMetadata(string Name, GpuType Type, int ByteOffset, string ShaderName);
public sealed record ResourceMetadata(string Id, ResourceKind Kind, string ShaderName, int Register, uint Width, uint Height,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FlowMapSettings? FlowMap = null);
public sealed record KernelMetadata(string Id, KernelStage Stage, string EntryPoint, uint ThreadsX, uint DispatchGroupsX)
{
    public IReadOnlyList<string> FlowMapResourceIds { get; init; } = [];
}
public sealed record FlowMetadata(string Id, FlowKind Kind, string? KernelId, string? ResourceId);
public sealed record GpuGeneratedFieldMappingMetadata(string ShaderName, int Register, int Elements, int Stride);
public sealed record GpuExportMetadata
{
    public int Version { get; init; } = 1;
    public CompilationPurpose Purpose { get; init; } = CompilationPurpose.SourceExport;
    public bool RuntimeExecuted { get; init; }
    public string ProgramId { get; init; } = "";
    public string ContentHash { get; init; } = "";
    public string SourceHash { get; init; } = "";
    public ShaderFormat Format { get; init; }
    public uint Capacity { get; init; }
    public int ParticleStride { get; init; } = 80;
    public int UniformBufferBytes { get; init; } = 48;
    public string ParticleBuffer { get; init; } = "ParticleBuffer";
    public string UniformBuffer { get; init; } = "LibraGpuParameters";
    public IReadOnlyList<KernelMetadata> Kernels { get; init; } = [];
    public IReadOnlyList<FlowMetadata> Flow { get; init; } = [];
    public IReadOnlyList<ResourceMetadata> Resources { get; init; } = [];
    public IReadOnlyList<UniformMetadata> Uniforms { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GpuGeneratedFieldMappingMetadata? GeneratedFieldMappings { get; init; }
}
public sealed class ShaderSource
{
    public ShaderFormat Format { get; }
    public string Text { get; }
    public string ContentHash { get; }
    public IReadOnlyList<SourceLineMapping> LineMappings { get; }
    public ShaderBindings Bindings { get; }
    public IReadOnlyList<ResourceMetadata> Resources { get; }
    internal ShaderSource(ShaderFormat format, string text, IEnumerable<SourceLineMapping> mapping, ShaderBindings bindings, IEnumerable<ResourceMetadata> resources)
    {
        Format = format; Text = text; ContentHash = ShaderEmitter.Hash(text);
        LineMappings = Array.AsReadOnly(mapping.ToArray());
        Bindings = bindings with { };
        Resources = Array.AsReadOnly(resources.ToArray());
    }
}
public sealed class CompiledGpuProgram
{
    public int Version => 1;
    public CompilationPurpose Purpose => CompilationPurpose.SourceExport;
    public bool RuntimeExecuted => false;
    public string ProgramId { get; }
    public string CanonicalJson { get; }
    public string ContentHash { get; }
    public uint Capacity { get; }
    public ulong ParticleBufferBytes => (ulong)Capacity * ParticleAbi.Stride;
    public int ParticleStride => ParticleAbi.Stride;
    public int UniformBufferBytes => 48;
    public IReadOnlyList<KernelMetadata> Kernels { get; }
    public IReadOnlyList<UniformMetadata> Uniforms { get; }
    public IReadOnlyList<ResourceMetadata> Resources { get; }
    public IReadOnlyList<FlowMetadata> Flow { get; }
    public IReadOnlyList<CompiledRegisterKernel> RegisterKernels { get; }
    public IReadOnlyList<GpuDiagnostic> Diagnostics { get; } = Array.AsReadOnly(Array.Empty<GpuDiagnostic>());
    private readonly GpuProgram snapshot;
    internal CompiledGpuProgram(GpuProgram p, string json)
    {
        snapshot = p; ProgramId = p.Id; Capacity = p.Capacity; CanonicalJson = json; ContentHash = ShaderEmitter.Hash(json);
        Kernels = Array.AsReadOnly(p.Kernels.Select((k, i) => new KernelMetadata(
            k.Id, k.Stage, $"LibraGpuKernel_{i:D4}", 64, (p.Capacity + 63) / 64)
        {
            FlowMapResourceIds = Array.AsReadOnly(k.Nodes.Where(node => node.Op == GpuOp.SampleFlowMap)
                .Select(node => node.Resource!).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray())
        }).ToArray());
        Uniforms = Array.AsReadOnly(new[]
        {
            new UniformMetadata("capacity",GpuType.UInt,0,"LibraGpuCapacity"),new("count",GpuType.UInt,4,"LibraGpuCount"),
            new("spawnStart",GpuType.UInt,8,"LibraGpuSpawnStart"),new("spawnCount",GpuType.UInt,12,"LibraGpuSpawnCount"),
            new("deltaTime",GpuType.Float,16,"LibraGpuDeltaTime"),new("time",GpuType.Float,20,"LibraGpuTime"),
            new("emitterSeed",GpuType.UInt,24,"LibraGpuEmitterSeed"),new("frameIndex",GpuType.UInt,28,"LibraGpuFrameIndex"),
            new("emitterPosition",GpuType.Float3,32,"LibraGpuEmitterPosition"),new("padding",GpuType.UInt,44,"LibraGpuPadding")
        });
        Resources = Array.AsReadOnly(p.Resources.Select((r, i) =>
            new ResourceMetadata(r.Id, r.Kind, $"LibraGpuTexture_{i:D4}", i, r.Width, r.Height, r.FlowMap)).ToArray());
        Flow = Array.AsReadOnly(p.Flow.Select(f => new FlowMetadata(f.Id, f.Kind, f.Kernel, f.Resource)).ToArray());
        RegisterKernels = Array.AsReadOnly(p.Kernels.Select(k => new CompiledRegisterKernel(k, p.Resources)).ToArray());
    }
    public ShaderSource EmitUsf(ShaderBindings? bindings = null) => ShaderEmitter.Emit(snapshot, Kernels, ShaderFormat.Usf, bindings ?? new());
    public ShaderSource EmitUnityCompute(ShaderBindings? bindings = null) => ShaderEmitter.Emit(snapshot, Kernels, ShaderFormat.UnityCompute, bindings ?? new());
    public ShaderSource EmitHlslInclude(ShaderBindings? bindings = null) => ShaderEmitter.Emit(snapshot, Kernels, ShaderFormat.HlslInclude, bindings ?? new());
    public GpuExportMetadata GetExportMetadata(ShaderFormat format = ShaderFormat.UnityCompute, ShaderBindings? bindings = null)
    {
        var source = format switch
        {
            ShaderFormat.HlslInclude => EmitHlslInclude(bindings),
            ShaderFormat.Usf => EmitUsf(bindings),
            ShaderFormat.UnityCompute => EmitUnityCompute(bindings),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        return new()
        {
            ProgramId = ProgramId,
            ContentHash = ContentHash,
            SourceHash = source.ContentHash,
            Format = format,
            Capacity = Capacity,
            ParticleBuffer = source.Bindings.ParticleBuffer,
            UniformBuffer = source.Bindings.UniformBuffer,
            Kernels = Kernels,
            Flow = Flow,
            Resources = source.Resources,
            Uniforms = Uniforms,
            GeneratedFieldMappings = format == ShaderFormat.UnityCompute && snapshot.Resources.Any(resource => resource.FlowMap?.Coordinates == FlowMapCoordinates.GeneratedPlane)
                ? new("LibraGpuFieldMappings", source.Bindings.TextureRegisterBase + snapshot.Resources.Count, 256, 16) : null
        };
    }
    public string ExportMetadataJson(ShaderFormat format = ShaderFormat.UnityCompute, ShaderBindings? bindings = null)
        => GpuCompiler.SerializeMetadata(GetExportMetadata(format, bindings));
}
