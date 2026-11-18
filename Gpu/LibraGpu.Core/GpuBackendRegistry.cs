using System.Collections.Frozen;

namespace LibraGpu.Core;

public sealed class GpuBackendRegistry
{
    public static GpuBackendRegistry BuiltIn { get; } = new([]);

    private readonly FrozenDictionary<string, GpuBackendCapabilities> backends;

    public GpuBackendRegistry(IEnumerable<GpuBackendCapabilities> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var registered = new Dictionary<string, GpuBackendCapabilities>(StringComparer.Ordinal)
        {
            [GpuBackendCapabilities.Unreal.Id] = GpuBackendCapabilities.Unreal,
            [GpuBackendCapabilities.Unity.Id] = GpuBackendCapabilities.Unity
        };
        foreach (var profile in profiles)
        {
            ArgumentNullException.ThrowIfNull(profile);
            if (!registered.TryAdd(profile.Id, profile))
            {
                throw new ArgumentException($"GPU backend '{profile.Id}' is already registered.", nameof(profiles));
            }
        }

        backends = registered.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public IReadOnlyList<GpuDiagnostic> Validate(string backendId, CompiledGpuProgram program)
    {
        ArgumentNullException.ThrowIfNull(backendId);
        ArgumentNullException.ThrowIfNull(program);
        return backends.TryGetValue(backendId, out var backend)
            ? backend.Validate(program)
            : [new("BackendUnknown", $"GPU backend '{backendId}' is not registered.")];
    }
}
