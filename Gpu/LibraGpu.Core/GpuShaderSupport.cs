namespace LibraGpu.Core;

/// <summary>Original portable scalar math/noise support, also usable by a fixed GPU interpreter.
/// No engine bindings or particle structs. This include has a stable version-1 ABI.</summary>
public static class GpuShaderSupport
{
    private static readonly Lazy<string> Source = new(() =>
    {
        using var stream = typeof(GpuShaderSupport).Assembly.GetManifestResourceStream("LibraGpu.Core.Shaders.LibraGpuSupport.hlsl")
            ?? throw new InvalidOperationException("GPU shader support resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    });
    public static string EmitHlslInclude() => Source.Value;
}
