using System.Runtime.InteropServices;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;

namespace LiberaFx.Services;

internal sealed record ProceduralTexturePreviewFrame(int Width, int Height, byte[] Pixels, bool UsesTime);

internal static class ProceduralTexturePreview
{
    // Use the portable runtime's pixel evaluator, rather than a second implementation
    // of noise, masks and gradients which could disagree with the exported effect.
    public static ProceduralTexturePreviewFrame Render(GraphNodeSnapshot node, double time,
        CancellationToken cancellation, int size = 128)
    {
        if (size is < 16 or > 256 || !double.IsFinite(time) || time < 0) throw new ArgumentOutOfRangeException(nameof(size));
        var definition = GraphNodeDefinition.Find(node.Kind) ?? throw new ArgumentException("Unknown procedural node.");
        var kind = ProceduralResourceSchema.Name(node.Kind);
        if (kind is not ("ProceduralTexture" or "ProceduralMaterial")) throw new ArgumentException("Select a procedural texture or material.");
        var recipe = ProceduralPixelCompilation.Prune(ProceduralResourceSchema.ReadEffective(definition, node.Parameters));
        var bytes = Convert.FromBase64String(ProceduralDefinitionWriter.Encode(kind, recipe, node,
            Array.Empty<GraphConnectionSnapshot>().ToLookup(edge => edge.TargetNodeId)));
        var library = NativeLibrary.Load(EditorNativePreviewSession.ResolveLibraryPath());
        var memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            var validate = Marshal.GetDelegateForFunctionPointer<Validate>(NativeLibrary.GetExport(library, "libra_procedural_pixel_validate"));
            var sample = Marshal.GetDelegateForFunctionPointer<Sample>(NativeLibrary.GetExport(library, "libra_procedural_pixel_sample"));
            var program = memory + 32;
            var operations = program + 16;
            var count = (uint)recipe.Operations.Length;
            if (validate(program, operations, count, out var usesTime) != 0)
                throw new InvalidDataException("The procedural pixel program is incomplete.");
            var pixels = new byte[size * size * 4];
            var rgba = new float[4];
            for (var y = 0; y < size; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (var x = 0; x < size; x++)
                {
                    var result = sample(program, operations, count, (x + .5f) / size, (y + .5f) / size,
                        time, 0, 0, rgba);
                    if (result != 0) throw new InvalidDataException("The procedural preview could not evaluate the pixel program (" + result + ").");
                    var offset = (y * size + x) * 4;
                    var alpha = Math.Clamp(rgba[3], 0, 1);
                    // Avalonia consumes premultiplied BGRA pixels.
                    pixels[offset] = Channel(rgba[2] * alpha);
                    pixels[offset + 1] = Channel(rgba[1] * alpha);
                    pixels[offset + 2] = Channel(rgba[0] * alpha);
                    pixels[offset + 3] = Channel(alpha);
                }
            }
            return new(size, size, pixels, usesTime != 0);
        }
        finally { Marshal.FreeHGlobal(memory); NativeLibrary.Free(library); }
    }
    private static byte Channel(float value) => (byte)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0, 1) * 255), 0, 255);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Validate(nint program, nint operations, uint count, out uint usesTime);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Sample(nint program, nint operations, uint count, float u, float v, double time,
        nint sampler, nint context, [Out] float[] rgba);
}
