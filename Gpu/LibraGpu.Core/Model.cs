using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace LibraGpu.Core;

public enum GpuType { Float = 0, Int = 1, UInt = 2, Bool = 3, Float2 = 4, Float3 = 5, Float4 = 6, Quaternion = 7 }
public enum GpuOp
{
    Constant = 0, ReadAttribute = 1, ReadUniform = 2, Add = 3, Sub = 4, Mul = 5, Div = 6,
    Sin = 7, Cos = 8, Clamp = 9, Min = 10, Max = 11, Normalize = 12, Length = 13,
    Compose = 14, Select = 15, Random = 16, Component = 17,
    // 18 remains the version-1 packet WriteAttribute opcode.
    Abs = 19, Floor = 20, Ceil = 21, Round = 22, Frac = 23, Mod = 24, Sign = 25,
    Pow = 26, Sqrt = 27, Exp = 28, Log = 29, Lerp = 30, Smoothstep = 31, Remap = 32,
    Dot = 33, Cross = 34, Distance = 35, Saturate = 36,
    GradientNoise3D = 37, Fbm = 38, Turbulence = 39, CurlNoise = 40, SampleFlowMap = 41,
    GreaterThan = 42, LessThan = 43
}
public enum KernelStage { Spawn, Update }
public enum FlowKind { Spawn, Update, RenderBillboard, RenderStartTexture, ComputePass }
public enum ResourceKind { Texture2D, StorageTexture2D, Buffer, FlowMap2D }
public enum GpuAxis { X, Y, Z }
public enum FlowMapEncoding { SignedNormalized, FloatVelocity, FloatDensity }
public enum FlowMapCoordinates { WorldBounds, GeneratedPlane }
public enum GpuSpawnShapeKind { Point, Box, Sphere, Circle }

public sealed record GpuProgram
{
    public int Version { get; init; } = 1;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool TransformEmitterShape { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? SpawnRate { get; init; }
    public string Id { get; init; } = "emitter";
    public uint Capacity { get; init; } = 4096;
    public List<AttributeDeclaration> Attributes { get; init; } = ParticleAbi.Attributes.ToList();
    public List<ResourceDeclaration> Resources { get; init; } = [];
    public List<KernelGraph> Kernels { get; init; } = [];
    public List<FlowStep> Flow { get; init; } = [];
}
public sealed record AttributeDeclaration(string Id, GpuType Type);
public sealed record ResourceDeclaration
{
    public string Id { get; init; } = "";
    public ResourceKind Kind { get; init; }
    public uint Width { get; init; }
    public uint Height { get; init; }
    public FlowMapSettings? FlowMap { get; init; }
}
public sealed record FlowMapSettings
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public FlowMapEncoding Encoding { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public FlowMapCoordinates Coordinates { get; init; }
    public GpuAxis AxisU { get; init; } = GpuAxis.X;
    public GpuAxis AxisV { get; init; } = GpuAxis.Z;
    public double WorldMinimumU { get; init; } = -1;
    public double WorldMaximumU { get; init; } = 1;
    public double WorldMinimumV { get; init; } = -1;
    public double WorldMaximumV { get; init; } = 1;
}
public sealed record KernelGraph
{
    public string Id { get; init; } = "";
    public KernelStage Stage { get; init; }
    public List<GpuNode> Nodes { get; init; } = [];
    public List<AttributeWrite> Writes { get; init; } = [];
}
public sealed record GpuNode
{
    public string Id { get; init; } = "";
    public GpuOp Op { get; init; }
    public GpuType Type { get; init; }
    public List<string> Inputs { get; init; } = [];
    public List<double> Value { get; init; } = [];
    public string? Attribute { get; init; }
    public string? Uniform { get; init; }
    public int? ComponentIndex { get; init; }
    public string? Resource { get; init; }
    public TextSpan? SourceSpan { get; init; }
}
public sealed record AttributeWrite(string Id, string Attribute, string Value, TextSpan? SourceSpan = null);
public sealed record FlowStep
{
    public string Id { get; init; } = "";
    public FlowKind Kind { get; init; }
    public string? Kernel { get; init; }
    public string? Resource { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GpuSpawnShapeSettings? SpawnShape { get; init; }
}
public sealed record GpuSpawnShapeSettings
{
    public GpuSpawnShapeKind Kind { get; init; } = GpuSpawnShapeKind.Point;
    public List<double> HalfExtents { get; init; } = [1, 1, 1];
    public double Radius { get; init; } = 1;
}
public sealed record TextSpan(int Start, int Length);
public sealed record GpuDiagnostic(string Code, string Message, string? KernelId = null, string? NodeId = null, string? PinId = null, TextSpan? Span = null);
public sealed class GpuValidationException : Exception
{
    public IReadOnlyList<GpuDiagnostic> Diagnostics { get; }
    public GpuValidationException(IEnumerable<GpuDiagnostic> diagnostics) : base("GPU program validation failed.")
        => Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
}
public sealed record GpuLimits
{
    public int MaxJsonBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxJsonDepth { get; init; } = 32;
    public int MaxPropertiesPerObject { get; init; } = 32;
    public int MaxKernels { get; init; } = 64;
    public int MaxNodesPerKernel { get; init; } = 4096;
    public int MaxResources { get; init; } = 64;
    public int MaxFlowSteps { get; init; } = 128;
    public ulong MaxParticleBytes { get; init; } = 256UL * 1024 * 1024;
    public ulong MaxResourceTexels { get; init; } = 64UL * 1024 * 1024;
}

/// <summary>StructuredBuffer ABI: five 16-byte slots, stride 80, independent of engine.</summary>
[StructLayout(LayoutKind.Explicit, Size = 80)]
public struct LibraGpuParticle
{
    [FieldOffset(0)] public System.Numerics.Vector4 PositionAge;
    [FieldOffset(16)] public System.Numerics.Vector4 VelocityLifetime;
    [FieldOffset(32)] public System.Numerics.Vector4 Color;
    [FieldOffset(48)] public System.Numerics.Vector4 SizeRotation;
    [FieldOffset(64)] public uint Seed;
    [FieldOffset(68)] public uint Alive;
    [FieldOffset(72)] public uint Spare0;
    [FieldOffset(76)] public uint Spare1;
}
/// <summary>Constant-buffer ABI, 48 bytes. Count bounds Update; Spawn uses start/count within capacity.
/// The host must initially clear particle storage, supply finite uniforms, and dispatch flow order with UAV barriers.</summary>
[StructLayout(LayoutKind.Explicit, Size = 48)]
public struct LibraGpuUniforms
{
    [FieldOffset(0)] public uint Capacity;
    [FieldOffset(4)] public uint Count;
    [FieldOffset(8)] public uint SpawnStart;
    [FieldOffset(12)] public uint SpawnCount;
    [FieldOffset(16)] public float DeltaTime;
    [FieldOffset(20)] public float Time;
    [FieldOffset(24)] public uint EmitterSeed;
    [FieldOffset(28)] public uint FrameIndex;
    [FieldOffset(32)] public System.Numerics.Vector3 EmitterPosition;
    [FieldOffset(44)] public uint Padding;
}
public static class ParticleAbi
{
    public const int Stride = 80;
    public static IReadOnlyList<AttributeDeclaration> Attributes { get; } = Array.AsReadOnly(new[]
    {
        new AttributeDeclaration("Position", GpuType.Float3), new("Velocity", GpuType.Float3),
        new("Color", GpuType.Float4), new("Size", GpuType.Float2), new("Rotation", GpuType.Float),
        new("Age", GpuType.Float), new("Lifetime", GpuType.Float), new("Alive", GpuType.Bool), new("Seed", GpuType.UInt)
    });
    public static IReadOnlyDictionary<string, GpuType> Uniforms { get; } =
        new ReadOnlyDictionary<string, GpuType>(new Dictionary<string, GpuType>
        { ["deltaTime"] = GpuType.Float, ["time"] = GpuType.Float, ["emitterPosition"] = GpuType.Float3,
            ["emitterRow0"] = GpuType.Float4, ["emitterRow1"] = GpuType.Float4,
            ["emitterRow2"] = GpuType.Float4, ["emitterRow3"] = GpuType.Float4 });
}
