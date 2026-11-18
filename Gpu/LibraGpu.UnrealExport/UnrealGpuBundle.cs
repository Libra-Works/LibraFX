using System.Text.Json;
using System.Text.Json.Serialization;
using LibraGpu.Core;

namespace LibraGpu.UnrealExport;

/// <summary>
/// Portable, source-only interchange. This is not a cooked Unreal asset or an executable shader.
/// A consuming engine must independently validate packets and resource paths before use.
/// </summary>
public sealed record UnrealGpuBundle
{
    public int Version { get; init; } = 1;
    public string Purpose { get; init; } = "SourceExport";
    public bool RuntimeExecuted { get; init; }
    public required string CanonicalJson { get; init; }
    public required string Usf { get; init; }
    public required GpuExportMetadata Metadata { get; init; }
    public required IReadOnlyList<UnrealGpuKernelPacket> Kernels { get; init; }
    public required IReadOnlyList<UnrealGpuTextureBinding> Textures { get; init; }
    public int RendererBindingsVersion { get; init; } = 1;
    public IReadOnlyList<UnrealGpuRendererBinding> RendererBindings { get; init; } = [];
}

public sealed record UnrealGpuKernelPacket(string Id, KernelStage Stage, byte[] Packet);
public sealed record UnrealGpuTextureBinding(string Id, string ObjectPath);
public sealed record UnrealGpuRendererBinding(
    string FlowId, string Renderer = "builtin:billboard",
    string Material = "builtin:unlit-alpha", float SizeScale = 1);

public static class UnrealGpuExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Compile validated IR and explicitly bind each texture to an Unreal object path or builtin:white.
    /// This operation never resolves, loads, or checks the type of the referenced Unreal assets.
    /// </summary>
    public static UnrealGpuBundle Compile(GpuProgram program, IReadOnlyDictionary<string, string> texturePaths,
        IReadOnlyList<UnrealGpuRendererBinding>? rendererBindings = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(texturePaths);
        var compiled = GpuCompiler.Compile(program);
        var diagnostics = GpuBackendRegistry.BuiltIn.Validate(GpuBackendCapabilities.Unreal.Id, compiled);
        if (diagnostics.Count != 0)
        {
            throw new NotSupportedException(string.Join(" ", diagnostics.Select(diagnostic => diagnostic.Message)));
        }
        var resources = compiled.Resources;
        if (texturePaths.Count != resources.Count)
            throw new ArgumentException("Supply exactly one explicit binding for each declared texture.", nameof(texturePaths));

        var textures = new List<UnrealGpuTextureBinding>(resources.Count);
        foreach (var resource in resources)
        {
            if (!texturePaths.TryGetValue(resource.Id, out var path) || (path != "builtin:white" && !IsObjectPath(path)))
                throw new ArgumentException($"Texture '{resource.Id}' requires an absolute Unreal object path or explicit builtin:white.", nameof(texturePaths));
            textures.Add(new(resource.Id, path));
        }

        var kernels = compiled.RegisterKernels
            .Select(kernel => new UnrealGpuKernelPacket(kernel.KernelId, kernel.Stage, kernel.SerializeLittleEndian()))
            .ToArray();
        var renderFlowIds = compiled.Flow.Where(flow => flow.Kind == FlowKind.RenderBillboard)
            .Select(flow => flow.Id).ToHashSet(StringComparer.Ordinal);
        var renderers = rendererBindings?.ToArray() ??
            compiled.Flow.Where(flow => flow.Kind == FlowKind.RenderBillboard)
                .Select(flow => new UnrealGpuRendererBinding(flow.Id)).ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in renderers)
        {
            if (binding is null || !renderFlowIds.Contains(binding.FlowId) || !seen.Add(binding.FlowId) ||
                binding.Renderer != "builtin:billboard" ||
                binding.Material is not ("builtin:unlit-alpha" or "builtin:unlit-additive") ||
                !float.IsFinite(binding.SizeScale) || binding.SizeScale <= 0 || binding.SizeScale > 10000)
                throw new ArgumentException("Exactly one renderer binding per RenderBillboard flow is required: " +
                    "builtin:billboard, builtin:unlit-alpha/additive, finite 0<sizeScale<=10000. Native UE materials are not evaluated.",
                    nameof(rendererBindings));
        }
        if (seen.Count != renderFlowIds.Count)
            throw new ArgumentException("Missing RenderBillboard renderer binding.", nameof(rendererBindings));
        return new()
        {
            CanonicalJson = compiled.CanonicalJson,
            Usf = compiled.EmitUsf().Text,
            Metadata = compiled.GetExportMetadata(ShaderFormat.Usf),
            Kernels = Array.AsReadOnly(kernels),
            Textures = textures.AsReadOnly(),
            RendererBindings = Array.AsReadOnly(renderers)
        };
    }

    public static string Serialize(UnrealGpuBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return JsonSerializer.Serialize(bundle, JsonOptions);
    }

    private static bool IsObjectPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 1024 || path[0] != '/')
            return false;
        var dot = path.LastIndexOf('.');
        var slash = path.LastIndexOf('/');
        if (slash < 1 || dot <= slash + 1 || dot == path.Length - 1)
            return false;
        return !path.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\' || c == ':');
    }
}
