using System.Collections.Frozen;

namespace LibraGpu.Core;

public enum GpuBackendInput { RegisterPackets, GeneratedShader }

public sealed class GpuBackendCapabilities
{
    private const ulong Budget = 256UL * 1024 * 1024;
    private static readonly GpuInstructionOpcode[] V1Operations =
        Enum.GetValues<GpuInstructionOpcode>().Where(opcode => (uint)opcode <= 43).ToArray();

    public static GpuBackendCapabilities Unreal { get; } = new("unreal-lgpk", GpuBackendInput.RegisterPackets, 1,
        V1Operations, [ResourceKind.Texture2D, ResourceKind.FlowMap2D], 256, Budget);
    public static GpuBackendCapabilities Unity { get; } = new("unity-compute", GpuBackendInput.GeneratedShader, 1,
        V1Operations, [ResourceKind.Texture2D, ResourceKind.FlowMap2D], null, Budget);

    public string Id { get; }
    public GpuBackendInput Input { get; }
    public int AbiVersion { get; }
    public IReadOnlySet<GpuInstructionOpcode> Opcodes { get; }
    public IReadOnlySet<ResourceKind> Resources { get; }
    public uint? MaxRegisters { get; }
    public ulong MaxWorkBytes { get; }

    public GpuBackendCapabilities(string id, GpuBackendInput input, int abiVersion,
        IEnumerable<GpuInstructionOpcode> opcodes, IEnumerable<ResourceKind> resources,
        uint? maxRegisters, ulong maxWorkBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!Enum.IsDefined(input) || abiVersion < 1 || maxWorkBytes == 0 ||
            (input == GpuBackendInput.RegisterPackets) != maxRegisters.HasValue)
        {
            throw new ArgumentException("Invalid GPU backend capability profile.");
        }

        Id = id;
        Input = input;
        AbiVersion = abiVersion;
        Opcodes = opcodes.ToFrozenSet();
        Resources = resources.ToFrozenSet();
        MaxRegisters = maxRegisters;
        MaxWorkBytes = maxWorkBytes;
    }

    public IReadOnlyList<GpuDiagnostic> Validate(CompiledGpuProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        var diagnostics = new List<GpuDiagnostic>();
        if (program.Version != AbiVersion)
        {
            diagnostics.Add(new("BackendAbi", $"{Id} requires GPU ABI {AbiVersion}; got {program.Version}."));
            return diagnostics.AsReadOnly();
        }

        foreach (var resource in program.Resources)
        {
            if (!Resources.Contains(resource.Kind))
            {
                diagnostics.Add(new("BackendResource", $"{Id} does not support {resource.Kind} resource '{resource.Id}'."));
            }
        }

        ulong packetBytes = 0;
        uint registerCount = 0;
        foreach (var kernel in program.RegisterKernels)
        {
            registerCount = Math.Max(registerCount, kernel.RegisterCount);
            packetBytes += CompiledRegisterKernel.HeaderBytes +
                (ulong)CompiledRegisterKernel.InstructionStride * (uint)kernel.Instructions.Count;
            if (MaxRegisters is { } limit && kernel.RegisterCount > limit)
            {
                diagnostics.Add(new("BackendRegisters", $"{Id} supports at most {limit} registers.", kernel.KernelId));
            }

            var reported = new HashSet<GpuInstructionOpcode>();
            for (var index = 0; index < kernel.Instructions.Count; index++)
            {
                var opcode = kernel.Instructions[index].Opcode;
                if (!Opcodes.Contains(opcode) && reported.Add(opcode))
                {
                    var mapping = kernel.Mappings[index];
                    diagnostics.Add(new("BackendOpcode", $"{Id} does not support {opcode}.",
                        kernel.KernelId, mapping.NodeId, mapping.PinId, mapping.Span));
                }
            }
        }

        var bytes = program.ParticleBufferBytes + (Input == GpuBackendInput.RegisterPackets
            ? packetBytes + (ulong)program.Capacity * registerCount * 16 : 0);
        if (bytes > MaxWorkBytes)
        {
            diagnostics.Add(new("BackendBudget", $"{Id} needs {bytes} work bytes; limit is {MaxWorkBytes}."));
        }

        return diagnostics.AsReadOnly();
    }
}
