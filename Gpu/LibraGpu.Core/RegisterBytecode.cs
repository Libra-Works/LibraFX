using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace LibraGpu.Core;

public enum GpuInstructionOpcode : uint
{
    Constant = 0, ReadAttribute = 1, ReadUniform = 2, Add = 3, Sub = 4, Mul = 5, Div = 6,
    Sin = 7, Cos = 8, Clamp = 9, Min = 10, Max = 11, Normalize = 12, Length = 13,
    Compose = 14, Select = 15, Random = 16, Component = 17, WriteAttribute = 18,
    Abs = 19, Floor = 20, Ceil = 21, Round = 22, Frac = 23, Mod = 24, Sign = 25,
    Pow = 26, Sqrt = 27, Exp = 28, Log = 29, Lerp = 30, Smoothstep = 31, Remap = 32,
    Dot = 33, Cross = 34, Distance = 35, Saturate = 36,
    GradientNoise3D = 37, Fbm = 38, Turbulence = 39, CurlNoise = 40, SampleFlowMap = 41
}
public enum GpuAttributeSlot : uint { Position = 0, Velocity = 1, Color = 2, Size = 3, Rotation = 4, Age = 5, Lifetime = 6, Alive = 7, Seed = 8 }
public enum GpuUniformSlot : uint { DeltaTime = 0, Time = 1, EmitterPosition = 2 }

/// <summary>
/// 64-byte, 16-byte-slot GPU instruction contract. uint fields at 0/4/8/12/16/20/24/28 are
/// opcode/destination/a/b/c/type/aux/d. Float immediate is at 32; exact integer/bool uint4 at 48.
/// Unused registers are uint.MaxValue. Aux is attribute/uniform slot or component index.
/// The interpreter must implement the same typed scalar-broadcast and safe math policies as exported shaders.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public readonly struct GpuInstruction
{
    [FieldOffset(0)] public readonly GpuInstructionOpcode Opcode;
    [FieldOffset(4)] public readonly uint Destination;
    [FieldOffset(8)] public readonly uint A;
    [FieldOffset(12)] public readonly uint B;
    [FieldOffset(16)] public readonly uint C;
    [FieldOffset(20)] public readonly GpuType Type;
    [FieldOffset(24)] public readonly uint Aux;
    [FieldOffset(28)] public readonly uint D;
    [FieldOffset(32)] public readonly Vector4 FloatImmediate;
    [FieldOffset(48)] public readonly uint Integer0;
    [FieldOffset(52)] public readonly uint Integer1;
    [FieldOffset(56)] public readonly uint Integer2;
    [FieldOffset(60)] public readonly uint Integer3;
    internal GpuInstruction(GpuInstructionOpcode opcode, uint dst, uint a, uint b, uint c, uint d, GpuType type, uint aux, Vector4 immediate, uint integer0 = 0)
    {
        Opcode = opcode; Destination = dst; A = a; B = b; C = c; D = d; Type = type; Aux = aux;
        FloatImmediate = immediate; Integer0 = integer0; Integer1 = 0; Integer2 = 0; Integer3 = 0;
    }
}
public sealed record InstructionMapping(int Instruction, string NodeId, string? PinId, TextSpan? Span);
public sealed class CompiledRegisterKernel
{
    public const int InstructionStride = 64;
    public const int HeaderBytes = 64;
    public const uint MissingRegister = uint.MaxValue;
    public string KernelId { get; }
    public Guid KernelKey { get; }
    public KernelStage Stage { get; }
    public bool AdvanceDefaultAge { get; }
    public uint RegisterCount { get; }
    public IReadOnlyList<GpuInstruction> Instructions { get; }
    public IReadOnlyList<InstructionMapping> Mappings { get; }
    internal CompiledRegisterKernel(KernelGraph k, IReadOnlyList<ResourceDeclaration> resources)
    {
        KernelId = k.Id; Stage = k.Stage;
        AdvanceDefaultAge = k.Stage == KernelStage.Update && !k.Writes.Any(w => w.Attribute == "Age");
        KernelKey = Guid.TryParse(k.Id, out var guid) ? guid : new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(k.Id)).AsSpan(0, 16), bigEndian: true);
        var lookup = k.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var ordered = GpuCompiler.Topological(lookup);
        RegisterCount = (uint)ordered.Count;
        var registers = ordered.Select((n, i) => (n.Id, Register: (uint)i)).ToDictionary(x => x.Id, x => x.Register, StringComparer.Ordinal);
        var resourceIndices = resources.Select((resource, index) => (resource.Id, Index: (uint)index))
            .ToDictionary(x => x.Id, x => x.Index, StringComparer.Ordinal);
        var instructions = new List<GpuInstruction>();
        var mappings = new List<InstructionMapping>();
        foreach (var n in ordered)
        {
            uint Ref(int i) => i < n.Inputs.Count ? registers[n.Inputs[i]] : MissingRegister;
            uint aux = n.Op switch
            {
                GpuOp.ReadAttribute => (uint)Enum.Parse<GpuAttributeSlot>(n.Attribute!),
                GpuOp.ReadUniform => n.Uniform switch { "deltaTime" => (uint)GpuUniformSlot.DeltaTime, "time" => (uint)GpuUniformSlot.Time, "emitterPosition" => (uint)GpuUniformSlot.EmitterPosition, _ => throw new InvalidOperationException() },
                GpuOp.Component => (uint)n.ComponentIndex!.Value,
                GpuOp.SampleFlowMap => resourceIndices[n.Resource!],
                _ => 0
            };
            var floats = new float[4]; uint integer = 0;
            uint d = Ref(3);
            if (n.Op == GpuOp.Constant)
            {
                if (GpuOperationRegistry.Float(n.Type))
                    for (int i = 0; i < n.Value.Count; i++) floats[i] = (float)n.Value[i];
                else integer = n.Type == GpuType.Int ? unchecked((uint)(int)n.Value[0]) : (uint)n.Value[0];
            }
            else if (n.Op == GpuOp.SampleFlowMap && n.Type == GpuType.Float3)
            {
                var settings = resources[(int)aux].FlowMap!;
                floats[0] = (float)settings.WorldMinimumU;
                floats[1] = (float)settings.WorldMinimumV;
                floats[2] = (float)(settings.WorldMaximumU - settings.WorldMinimumU);
                floats[3] = (float)(settings.WorldMaximumV - settings.WorldMinimumV);
                d = (uint)settings.AxisU | (uint)settings.AxisV << 2;
            }
            mappings.Add(new(instructions.Count, n.Id, "out", n.SourceSpan));
            instructions.Add(new((GpuInstructionOpcode)(uint)n.Op, registers[n.Id], Ref(0), Ref(1), Ref(2), d, n.Type, aux, new(floats[0], floats[1], floats[2], floats[3]), integer));
        }
        foreach (var w in k.Writes)
        {
            mappings.Add(new(instructions.Count, w.Id, "value", w.SourceSpan));
            instructions.Add(new(GpuInstructionOpcode.WriteAttribute, MissingRegister, registers[w.Value], MissingRegister, MissingRegister, MissingRegister,
                lookup[w.Value].Type, (uint)Enum.Parse<GpuAttributeSlot>(w.Attribute), Vector4.Zero));
        }
        Instructions = Array.AsReadOnly(instructions.ToArray()); Mappings = Array.AsReadOnly(mappings.ToArray());
    }

    /// <summary>
    /// LE packet header: magic 'LGPK'@0, version1@4, stride64@8, instruction count@12,
    /// register count@16, stage@20, particle stride80@24, uniform bytes48@28,
    /// canonical big-endian GUID@32, default-age flag@48, reserved zeros@52..63.
    /// Instruction data starts at64; int/bool immediates never pass through float conversion.
    /// This is a bounded evaluate-only packet, not a shipped interpreter or runtime-execution claim.
    /// Host lifecycle initialization/age/death rules must match the exported compute entry points.
    /// </summary>
    public byte[] SerializeLittleEndian()
    {
        if (Instructions.Count > 65536 + ParticleAbi.Attributes.Count || RegisterCount > 65536)
            throw new InvalidOperationException("Register packet limit exceeded.");
        var bytes = new byte[checked(HeaderBytes + Instructions.Count * InstructionStride)];
        var s = bytes.AsSpan();
        s[0] = (byte)'L'; s[1] = (byte)'G'; s[2] = (byte)'P'; s[3] = (byte)'K';
        void UInt(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
        UInt(4, 1); UInt(8, 64); UInt(12, (uint)Instructions.Count); UInt(16, RegisterCount);
        UInt(20, (uint)Stage); UInt(24, 80); UInt(28, 48);
        KernelKey.TryWriteBytes(s.Slice(32, 16), bigEndian: true, out _);
        UInt(48, AdvanceDefaultAge ? 1u : 0u);
        for (int i = 0; i < Instructions.Count; i++)
        {
            var n = Instructions[i]; int o = HeaderBytes + i * InstructionStride;
            UInt(o, (uint)n.Opcode); UInt(o + 4, n.Destination); UInt(o + 8, n.A); UInt(o + 12, n.B); UInt(o + 16, n.C);
            UInt(o + 20, (uint)n.Type); UInt(o + 24, n.Aux); UInt(o + 28, n.D);
            BinaryPrimitives.WriteSingleLittleEndian(s.Slice(o + 32, 4), n.FloatImmediate.X);
            BinaryPrimitives.WriteSingleLittleEndian(s.Slice(o + 36, 4), n.FloatImmediate.Y);
            BinaryPrimitives.WriteSingleLittleEndian(s.Slice(o + 40, 4), n.FloatImmediate.Z);
            BinaryPrimitives.WriteSingleLittleEndian(s.Slice(o + 44, 4), n.FloatImmediate.W);
            UInt(o + 48, n.Integer0); UInt(o + 52, n.Integer1); UInt(o + 56, n.Integer2); UInt(o + 60, n.Integer3);
        }
        return bytes;
    }
}
