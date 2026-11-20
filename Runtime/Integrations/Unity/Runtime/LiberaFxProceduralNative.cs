using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace LiberaFx.Unity
{
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct LibraProceduralDescriptor
    {
        public uint version, shape, segmentsU, segmentsV;
        public Vector3 size;
        public float innerRadius, sweepDegrees, turns, tubeRadius;
        public uint pivotMode, flags;
        public Vector3 pivot;
        public uint maxVertices, maxTriangles, pivotVertex, reserved;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct LibraProceduralModifier
    {
        public uint kind, axis, flags, count, seed, reserved0, reserved1, reserved2;
        public Vector4 a, b, c, d;
        internal void Set(int block, Vector4 value)
        {
            switch (block) { case 0: a = value; break; case 1: b = value; break; case 2: c = value; break; default: d = value; break; }
        }
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct LibraProceduralVertex
    {
        public Vector3 position, normal;
        public Vector4 tangent;
        public Vector2 uv, uv2;
        public Color color;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct LibraProceduralView
    {
        public IntPtr vertices, indices;
        public uint vertexCount, indexCount;
        public ulong definitionId, vertexRevision, indexRevision;
        public Vector3 boundsMin, boundsMax, pivot;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct LibraPixelOperation
    {
        public uint kind, inputA, inputB, seed;
        public Vector4 a, b, c;
        internal void Set(int block, Vector4 value)
        {
            switch (block) { case 0: a = value; break; case 1: b = value; break; default: c = value; break; }
        }
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct LibraPixelProgram { public uint version, operationCount, outputRegister, domain; }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct LibraProceduralCounts { public uint vertices, indices; public ulong retainedBytes; }
    internal static class LibraProceduralNative
    {
#if UNITY_IOS && !UNITY_EDITOR
        private const string Library = "__Internal";
#else
        private const string Library = "liberafx_native";
#endif
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_procedural_model_predict(ref LibraProceduralDescriptor descriptor,
            [In] LibraProceduralModifier[] modifiers, uint count, out LibraProceduralCounts counts);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_procedural_model_create(ref LibraProceduralDescriptor descriptor,
            [In] LibraProceduralModifier[] modifiers, uint count, out IntPtr instance);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_procedural_model_evaluate(IntPtr instance, ref LibraProceduralDescriptor descriptor,
            [In] LibraProceduralModifier[] modifiers, uint count, double time, out uint changed);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_procedural_model_view(IntPtr instance, out LibraProceduralView view);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_procedural_model_copy_vertices(IntPtr instance,
            [Out] LibraProceduralVertex[] vertices, uint capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_procedural_model_copy_indices(IntPtr instance,
            [Out] uint[] indices, uint capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void libra_procedural_model_release(IntPtr instance);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_procedural_pixel_validate(ref LibraPixelProgram program,
            [In] LibraPixelOperation[] operations, uint count, out uint usesTime);
    }
    [Serializable] internal sealed class LiberaFxProceduralModifierPayload
    {
        public string id;
        public int kind, axis;
        public uint flags, count, seed;
        public float[] values;
    }
    [Serializable] internal sealed class LiberaFxProceduralPixelPayload
    {
        public string id;
        public uint kind, inputA, inputB, seed;
        public float[] values;
    }
    [Serializable] internal sealed class LiberaFxProceduralRecipePayload
    {
        public int version, shape;
        public uint segmentsU, segmentsV, pivotMode, pivotVertex;
        public float[] size, pivot;
        public float innerRadius, sweepDegrees, turns, tubeRadius;
        public bool capped, animatedBase, doubleSided;
        public LiberaFxProceduralModifierPayload[] modifiers;
        public LiberaFxProceduralPixelPayload[] operations;
        public int resolution;
        public uint outputRegister;
        public string blend;
        public float emission;
    }
    [Serializable] internal sealed class LiberaFxProceduralTextureReference
    {
        public int slot;
        public string producerNodeId;
    }
    [Serializable] internal sealed class LiberaFxProceduralResourcePayload
    {
        public string nodeId, kind;
        public LiberaFxProceduralRecipePayload recipe;
        public LiberaFxRenderInputPayload[] inputs;
        public LiberaFxProceduralTextureReference[] textures;
    }
}
