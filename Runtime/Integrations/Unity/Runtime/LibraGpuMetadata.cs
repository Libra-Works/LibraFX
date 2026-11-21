using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiberaFx.Unity
{
    [Serializable] public sealed class LibraGpuKernelMetadata
    {
        public string id, stage, entryPoint;
        public int threadsX;
        public string[] flowMapResourceIds;
    }

    [Serializable] public sealed class LibraGpuFlowMetadata
    {
        public string id, kind, kernelId, resourceId;
    }

    [Serializable] public sealed class LibraGpuFlowMapMetadata
    {
        public string axisU, axisV;
        public string encoding;
        public string coordinates;
        public float worldMinimumU, worldMaximumU, worldMinimumV, worldMaximumV;
    }

    [Serializable] public sealed class LibraGpuResourceMetadata
    {
        public string id, kind, shaderName;
        public LibraGpuFlowMapMetadata flowMap;
    }

    [Serializable] public sealed class LibraGpuUniformMetadata
    {
        public string name, shaderName;
        public int byteOffset;
    }

    [Serializable] public sealed class LibraGpuGeneratedFieldMappingMetadata
    {
        public string shaderName;
        public int register, elements, stride;
    }
    [Serializable] public sealed class LibraGpuMetadata
    {
        public LibraGpuGeneratedFieldMappingMetadata generatedFieldMappings;
        public int version, capacity, particleStride, uniformBufferBytes;
        public string purpose, format, programId, contentHash, sourceHash, particleBuffer, uniformBuffer;
        public bool runtimeExecuted;
        public bool emitterMatrix;
        public float spawnRate;
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
            if (m == null || m.version < 1 || m.version > 3 || m.purpose != "SourceExport" || m.format != "UnityCompute" || m.runtimeExecuted ||
                m.version == 3 && (float.IsNaN(m.spawnRate) || float.IsInfinity(m.spawnRate) || m.spawnRate < 0 || m.spawnRate > 1000000))
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
            if (m.resources.Length > 64) throw new ArgumentException("GPU resource limit exceeded.");
            var resources = new Dictionary<string, LibraGpuResourceMetadata>();
            var resourceShaderNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in m.resources)
            {
                if (r == null || string.IsNullOrEmpty(r.id) ||
                    (r.kind != "Texture2D" && r.kind != "FlowMap2D") ||
                    string.IsNullOrEmpty(r.shaderName) || !resourceShaderNames.Add(r.shaderName) || resources.ContainsKey(r.id))
                    throw new ArgumentException("GPU resources must be uniquely identified Texture2D or FlowMap2D declarations.");
                if (r.kind == "FlowMap2D")
                {
                    if (r.flowMap == null || r.flowMap.coordinates != null && r.flowMap.coordinates != "" && r.flowMap.coordinates != "WorldBounds" && r.flowMap.coordinates != "GeneratedPlane" ||
                        r.flowMap.encoding != null && r.flowMap.encoding != "" && r.flowMap.encoding != "SignedNormalized" &&
                        r.flowMap.encoding != "FloatVelocity" && r.flowMap.encoding != "FloatDensity" ||
                        !ValidAxis(r.flowMap.axisU) || !ValidAxis(r.flowMap.axisV) ||
                        r.flowMap.axisU == r.flowMap.axisV ||
                        !Finite(r.flowMap.worldMinimumU) || !Finite(r.flowMap.worldMaximumU) ||
                        !Finite(r.flowMap.worldMinimumV) || !Finite(r.flowMap.worldMaximumV) ||
                        r.flowMap.worldMinimumU >= r.flowMap.worldMaximumU ||
                        r.flowMap.worldMinimumV >= r.flowMap.worldMaximumV)
                        throw new ArgumentException("FlowMap2D sampling metadata is invalid.");
                }
                else if (r.flowMap != null)
                {
                    // JsonUtility can materialize an omitted nested object.
                    if (!string.IsNullOrEmpty(r.flowMap.axisU) || !string.IsNullOrEmpty(r.flowMap.axisV) ||
                        r.flowMap.worldMinimumU != 0 || r.flowMap.worldMaximumU != 0 ||
                        r.flowMap.worldMinimumV != 0 || r.flowMap.worldMaximumV != 0)
                    {
                        throw new ArgumentException("Texture2D resources cannot carry FlowMap2D settings.");
                    }
                    r.flowMap = null;
                }
                resources.Add(r.id, r);
            }
            var generatedCoordinates = false;
            foreach (var resource in m.resources)
                generatedCoordinates |= resource.flowMap?.coordinates == "GeneratedPlane";
            if (generatedCoordinates && (m.generatedFieldMappings == null || m.generatedFieldMappings.shaderName != "LibraGpuFieldMappings" ||
                m.generatedFieldMappings.elements != 256 || m.generatedFieldMappings.stride != 16 ||
                m.generatedFieldMappings.register < 0 || m.generatedFieldMappings.register > 127))
                throw new ArgumentException("Generated-plane sampling requires the instance-owned mapping buffer metadata.");
            if (!generatedCoordinates && m.generatedFieldMappings != null)
            {
                // Some Unity versions materialize an empty serializable object for an
                // omitted JSON field. It has no mapping; retain rejection of real extras.
                var mapping = m.generatedFieldMappings;
                if (!string.IsNullOrEmpty(mapping.shaderName) || mapping.register != 0 || mapping.elements != 0 || mapping.stride != 0)
                    throw new ArgumentException("Unexpected generated field mapping buffer.");
                m.generatedFieldMappings = null;
            }
            foreach (var kernel in m.kernels)
            {
                var ids = kernel.flowMapResourceIds ?? Array.Empty<string>();
                var uniqueIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in ids)
                    if (string.IsNullOrEmpty(id) || !uniqueIds.Add(id) ||
                        !resources.TryGetValue(id, out var resource) || resource.kind != "FlowMap2D")
                        throw new ArgumentException("GPU kernel references a missing or invalid FlowMap2D resource.");
            }
            var steps = new HashSet<string>();
            var spawned = false;
            var updated = false;
            var rendered = false;
            foreach (var f in m.flow)
            {
                if (f == null || string.IsNullOrEmpty(f.id) || !steps.Add(f.id))
                    throw new ArgumentException("Invalid or duplicate GPU flow step.");
                if (f.kind == "Spawn" || f.kind == "Update")
                {
                    if (rendered || (f.kind == "Spawn" ? spawned || updated : updated))
                    {
                        throw new ArgumentException("GPU dispatch flow allows one Spawn before one Update, both before rendering.");
                    }
                    if (string.IsNullOrEmpty(f.kernelId) || !kernels.TryGetValue(f.kernelId, out var k) || k.stage != f.kind || !string.IsNullOrEmpty(f.resourceId))
                        throw new ArgumentException("GPU flow references a missing or incompatible kernel: " + f.id);
                    spawned |= f.kind == "Spawn";
                    updated |= f.kind == "Update";
                }
                else if (f.kind == "RenderBillboard")
                {
                    rendered = true;
                    if (!string.IsNullOrEmpty(f.kernelId) || string.IsNullOrEmpty(f.resourceId) || !resources.ContainsKey(f.resourceId) ||
                        resources[f.resourceId].kind != "Texture2D")
                        throw new ArgumentException("GPU billboard flow references a missing texture: " + f.id);
                }
                else throw new ArgumentException("Unsupported GPU flow kind: " + f.kind);
            }
            return m;
        }

        static bool ValidAxis(string axis) => axis == "X" || axis == "Y" || axis == "Z";
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
