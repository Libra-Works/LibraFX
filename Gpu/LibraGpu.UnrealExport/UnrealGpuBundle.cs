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
}

public sealed record UnrealGpuKernelPacket(string Id, KernelStage Stage, byte[] Packet);
public sealed record UnrealGpuTextureBinding(string Id, string ObjectPath);

public static class UnrealGpuExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Compile validated IR and associate every declared texture with its Unreal object path.
    /// This operation never resolves, loads, or checks the type of the referenced Unreal assets.
    /// </summary>
    public static UnrealGpuBundle Compile(GpuProgram program, IReadOnlyDictionary<string, string> texturePaths)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(texturePaths);
        var compiled = GpuCompiler.Compile(program);
        var resources = compiled.Resources;
        if (texturePaths.Count != resources.Count)
            throw new ArgumentException("Supply exactly one object path for each declared texture.", nameof(texturePaths));

        var textures = new List<UnrealGpuTextureBinding>(resources.Count);
        foreach (var resource in resources)
        {
            if (resource.Kind != ResourceKind.Texture2D)
                throw new NotSupportedException($"Resource '{resource.Id}' is not a Texture2D.");
            if (!texturePaths.TryGetValue(resource.Id, out var path) || !IsObjectPath(path))
                throw new ArgumentException($"Texture '{resource.Id}' requires an absolute Unreal object path.", nameof(texturePaths));
            textures.Add(new(resource.Id, path));
        }

        var kernels = compiled.RegisterKernels
            .Select(kernel => new UnrealGpuKernelPacket(kernel.KernelId, kernel.Stage, kernel.SerializeLittleEndian()))
            .ToArray();
        return new()
        {
            CanonicalJson = compiled.CanonicalJson,
            Usf = compiled.EmitUsf().Text,
            Metadata = compiled.GetExportMetadata(ShaderFormat.Usf),
            Kernels = Array.AsReadOnly(kernels),
            Textures = textures.AsReadOnly()
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
