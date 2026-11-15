using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiberaFx.Unity
{
    [Serializable] public sealed class LibraGpuKernelMetadata
    {
        public string id, stage, entryPoint;
        public int threadsX;
    }

    [Serializable] public sealed class LibraGpuFlowMetadata
    {
        public string id, kind, kernelId, resourceId;
    }

    [Serializable] public sealed class LibraGpuResourceMetadata
    {
        public string id, kind, shaderName;
    }

    [Serializable] public sealed class LibraGpuUniformMetadata
    {
        public string name, shaderName;
        public int byteOffset;
    }

    [Serializable] public sealed class LibraGpuMetadata
    {
        public int version, capacity, particleStride, uniformBufferBytes;
        public string purpose, format, programId, contentHash, sourceHash, particleBuffer, uniformBuffer;
        public bool runtimeExecuted;
        public LibraGpuKernelMetadata[] kernels;
        public LibraGpuFlowMetadata[] flow;
        public LibraGpuResourceMetadata[] resources;
        public LibraGpuUniformMetadata[] uniforms;

        public static LibraGpuMetadata Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) throw new ArgumentException("GPU metadata is empty.");
            LibraGpuMetadata m;
            try { m = JsonUtility.FromJson<LibraGpuMetadata>(json); }
            catch (Exception e) { throw new ArgumentException("Invalid GPU metadata JSON.", e); }
            if (m == null || m.version != 1 || m.purpose != "SourceExport" || m.format != "UnityCompute" || m.runtimeExecuted)
                throw new ArgumentException("Expected version 1 UnityCompute SourceExport GPU metadata.");
            if (m.capacity <= 0 || m.capacity > 3355443 || m.particleStride != 80 || m.uniformBufferBytes != 48)
                throw new ArgumentException("GPU capacity or particle/uniform ABI is invalid.");
            if (string.IsNullOrEmpty(m.programId) || string.IsNullOrEmpty(m.contentHash) || string.IsNullOrEmpty(m.sourceHash) ||
                string.IsNullOrEmpty(m.particleBuffer) || string.IsNullOrEmpty(m.uniformBuffer) || m.kernels == null || m.flow == null || m.resources == null || m.uniforms == null)
                throw new ArgumentException("GPU metadata is missing required fields.");
            string[] names = { "capacity", "count", "spawnStart", "spawnCount", "deltaTime", "time", "emitterSeed", "frameIndex", "emitterPosition", "padding" };
            string[] shaderNames = { "LibraGpuCapacity", "LibraGpuCount", "LibraGpuSpawnStart", "LibraGpuSpawnCount", "LibraGpuDeltaTime", "LibraGpuTime", "LibraGpuEmitterSeed", "LibraGpuFrameIndex", "LibraGpuEmitterPosition", "LibraGpuPadding" };
            int[] offsets = { 0, 4, 8, 12, 16, 20, 24, 28, 32, 44 };
            if (m.uniforms.Length != names.Length) throw new ArgumentException("GPU uniform ABI is incomplete.");
            for (int i = 0; i < names.Length; i++)
                if (m.uniforms[i] == null || m.uniforms[i].name != names[i] || m.uniforms[i].shaderName != shaderNames[i] || m.uniforms[i].byteOffset != offsets[i])
                    throw new ArgumentException("GPU uniform ABI mismatch at " + names[i] + ".");
            var kernels = new Dictionary<string, LibraGpuKernelMetadata>();
            foreach (var k in m.kernels)
            {
                if (k == null || string.IsNullOrEmpty(k.id) || string.IsNullOrEmpty(k.entryPoint) ||
                    (k.stage != "Spawn" && k.stage != "Update") || k.threadsX != 64 || kernels.ContainsKey(k.id))
                    throw new ArgumentException("Invalid or duplicate GPU kernel metadata.");
                kernels.Add(k.id, k);
            }
            var resources = new HashSet<string>();
            foreach (var r in m.resources)
                if (r == null || string.IsNullOrEmpty(r.id) || r.kind != "Texture2D" || string.IsNullOrEmpty(r.shaderName) || !resources.Add(r.id))
                    throw new ArgumentException("Only uniquely identified Texture2D GPU resources are supported.");
            var steps = new HashSet<string>();
            bool rendered = false;
            foreach (var f in m.flow)
            {
                if (f == null || string.IsNullOrEmpty(f.id) || !steps.Add(f.id))
                    throw new ArgumentException("Invalid or duplicate GPU flow step.");
                if (f.kind == "Spawn" || f.kind == "Update")
                {
                    if (rendered) throw new ArgumentException("Unity billboard rendering must follow all compute flow steps.");
                    if (string.IsNullOrEmpty(f.kernelId) || !kernels.TryGetValue(f.kernelId, out var k) || k.stage != f.kind || !string.IsNullOrEmpty(f.resourceId))
                        throw new ArgumentException("GPU flow references a missing or incompatible kernel: " + f.id);
                }
                else if (f.kind == "RenderBillboard")
                {
                    rendered = true;
                    if (!string.IsNullOrEmpty(f.kernelId) || string.IsNullOrEmpty(f.resourceId) || !resources.Contains(f.resourceId))
                        throw new ArgumentException("GPU billboard flow references a missing texture: " + f.id);
                }
                else throw new ArgumentException("Unsupported GPU flow kind: " + f.kind);
            }
            return m;
        }
    }
}
