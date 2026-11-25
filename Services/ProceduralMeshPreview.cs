using System.Runtime.InteropServices;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;

namespace LiberaFx.Services;

internal sealed record ProceduralMeshPreviewFrame(float[] Positions, float[] Uvs, int[] Indices);
internal static class ProceduralMeshPreview
{
    public static ProceduralMeshPreviewFrame Render(GraphNodeSnapshot node, double time, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var definition = GraphNodeDefinition.Find(node.Kind) ?? throw new ArgumentException("Unknown procedural model.");
        var recipe = ProceduralResourceSchema.ReadEffective(definition, node.Parameters);
        var bytes = Convert.FromBase64String(ProceduralDefinitionWriter.Encode("ProceduralModel", recipe, node,
            Array.Empty<GraphConnectionSnapshot>().ToLookup(edge => edge.TargetNodeId)));
        var library = NativeLibrary.Load(PreviewNativeLibrary.ResolveLibraryPath());
        var memory = Marshal.AllocHGlobal(bytes.Length);
        var view = Marshal.AllocHGlobal(88);
        nint instance = 0;
        Release? release = null;
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            var descriptor = memory + 32;
            var modifiers = memory + 116;
            var count = (uint)recipe.Modifiers.Length;
            var predict = Marshal.GetDelegateForFunctionPointer<Predict>(NativeLibrary.GetExport(library, "libra_procedural_model_predict"));
            var create = Marshal.GetDelegateForFunctionPointer<Create>(NativeLibrary.GetExport(library, "libra_procedural_model_create"));
            var evaluate = Marshal.GetDelegateForFunctionPointer<Evaluate>(NativeLibrary.GetExport(library, "libra_procedural_model_evaluate"));
            var getView = Marshal.GetDelegateForFunctionPointer<GetView>(NativeLibrary.GetExport(library, "libra_procedural_model_view"));
            release = Marshal.GetDelegateForFunctionPointer<Release>(NativeLibrary.GetExport(library, "libra_procedural_model_release"));
            Check(predict(descriptor, modifiers, count, out var counts));
            if (counts.Vertices > 262144 || counts.Indices > 1572864)
                throw new InvalidDataException("The mesh exceeds the local preview budget.");
            Check(create(descriptor, modifiers, count, out instance));
            cancellation.ThrowIfCancellationRequested();
            Check(evaluate(instance, descriptor, modifiers, count, time, out _));
            Check(getView(instance, view));
            var vertexCount = Marshal.ReadInt32(view, 16);
            var indexCount = Marshal.ReadInt32(view, 20);
            if (vertexCount < 0 || vertexCount > counts.Vertices || indexCount < 0 || indexCount > counts.Indices)
                throw new InvalidDataException("Invalid native mesh preview counts.");
            var vertices = new float[checked(vertexCount * 18)];
            var indices = new int[indexCount];
            Marshal.Copy(Marshal.ReadIntPtr(view, 0), vertices, 0, vertices.Length);
            Marshal.Copy(Marshal.ReadIntPtr(view, 8), indices, 0, indices.Length);
            var positions = new float[vertexCount * 3];
            var uvs = new float[vertexCount * 2];
            for (var index = 0; index < vertexCount; index++)
            {
                Array.Copy(vertices, index * 18, positions, index * 3, 3);
                Array.Copy(vertices, index * 18 + 10, uvs, index * 2, 2);
            }
            cancellation.ThrowIfCancellationRequested();
            return new(positions, uvs, indices);
        }
        finally
        {
            if (instance != 0) release?.Invoke(instance);
            Marshal.FreeHGlobal(view); Marshal.FreeHGlobal(memory); NativeLibrary.Free(library);
        }
    }
    private static void Check(int result) { if (result != 0) throw new InvalidDataException("Native mesh preview failed (" + result + ")."); }
    [StructLayout(LayoutKind.Sequential)] private struct Counts { public uint Vertices, Indices; public ulong Bytes; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Predict(nint descriptor, nint modifiers, uint count, out Counts counts);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Create(nint descriptor, nint modifiers, uint count, out nint instance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Evaluate(nint instance, nint descriptor, nint modifiers, uint count, double time, out uint changed);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetView(nint instance, nint view);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Release(nint instance);
}
