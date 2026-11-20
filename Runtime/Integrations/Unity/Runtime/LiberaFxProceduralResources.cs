using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiberaFx.Unity
{
    internal static class LiberaFxProceduralValues
    {
        internal static float Checked(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > 1000000)
                throw new InvalidOperationException("Procedural value is non-finite or exceeds its supported range.");
            return (float)value;
        }
        internal static Vector4 Block(float[] values, int block, int length)
        {
            if (values == null || values.Length != length) throw new InvalidOperationException("Invalid procedural value block.");
            return new Vector4(Checked(values[block * 4]), Checked(values[block * 4 + 1]),
                Checked(values[block * 4 + 2]), Checked(values[block * 4 + 3]));
        }
        internal static Vector3 Vector(float[] values)
        {
            if (values == null || values.Length != 3) throw new InvalidOperationException("Invalid procedural vector.");
            return new Vector3(Checked(values[0]), Checked(values[1]), Checked(values[2]));
        }
        internal static Vector4 Read(LiberaFxRenderInputPayload input, NativeInstance.NativeSpawnableHostBase host,
            bool root, byte[] container, ulong instance)
        {
            if (string.IsNullOrEmpty(input.sourceNodeId))
            {
                var value = Vector4.zero;
                for (var i = 0; i < input.width; ++i) value[i] = Checked(input.defaults[i]);
                return value;
            }
            var result = root ? host.ReadRootValue(input.sourceNodeBytes, out var read)
                : host.ReadChildValue(container, instance, input.sourceNodeBytes, out read);
            if (result != NativeResult.Ok || read.ComponentCount > 4 || input.width + input.sourceComponent > read.ComponentCount)
                throw new InvalidOperationException("Procedural source read failed: " + result);
            var packed = new Vector4(Checked(read.X), Checked(read.Y), Checked(read.Z), Checked(read.W));
            var output = Vector4.zero;
            for (var i = 0; i < input.width; ++i) output[i] = packed[i + input.sourceComponent];
            return output;
        }
        internal static void Check(NativeResult result)
        {
            if (result != NativeResult.Ok) throw new InvalidOperationException("Procedural evaluation failed: " + result);
        }
    }

    internal sealed class LiberaFxProceduralBinding
    {
        internal LiberaFxRenderInputPayload input;
        internal int stage, block;
    }

    /// <summary>Per-scope retained geometry and GPU outputs; all consumers share each producer.</summary>
    internal sealed class LiberaFxProceduralResource : IDisposable
    {
        private static readonly Dictionary<string, WeakReference<LiberaFxProceduralResource>> cache =
            new Dictionary<string, WeakReference<LiberaFxProceduralResource>>(StringComparer.Ordinal);
        private int leases = 1;
        private bool disposed;
        internal string sharedKey;
        internal readonly LiberaFxProceduralResourcePayload definition;

        internal static LiberaFxProceduralResource Acquire(LiberaFxProceduralResourcePayload payload,
            Dictionary<string, LiberaFxProceduralResource> resources, LiberaFxRenderStatisticsCounter statistics)
        {
            var key = StaticKey(payload, resources);
            if (key != null && cache.TryGetValue(key, out var weak) && weak.TryGetTarget(out var shared) && !shared.disposed)
            {
                ++shared.leases;
                return shared;
            }
            var resource = new LiberaFxProceduralResource(payload, statistics);
            try { resource.Connect(resources); }
            catch { resource.Dispose(); throw; }
            resource.sharedKey = key;
            if (key != null)
            {
                var expired = cache.Where(p => !p.Value.TryGetTarget(out var target) || target.disposed).Select(p => p.Key).ToArray();
                foreach (var previous in expired) cache.Remove(previous);
                if (cache.Count < 256) cache[key] = new WeakReference<LiberaFxProceduralResource>(resource);
            }
            return resource;
        }
        private static string StaticKey(LiberaFxProceduralResourcePayload payload, Dictionary<string, LiberaFxProceduralResource> resources)
        {
            if (payload.inputs.Any(input => !string.IsNullOrEmpty(input.sourceNodeId))) return null;
            var recipe = JsonUtility.FromJson<LiberaFxProceduralRecipePayload>(JsonUtility.ToJson(payload.recipe));
            void Merge(string id, float[] values)
            {
                for (var block = 0; block < values.Length / 4; ++block)
                {
                    var input = payload.inputs.Single(p => p.id == "stage_" + Guid.Parse(id).ToString("N") + "_" + block);
                    for (var c = 0; c < 4; ++c) values[block * 4 + c] = LiberaFxProceduralValues.Checked(input.defaults[c]);
                }
            }
            if (payload.kind == "ProceduralModel")
            {
                if (recipe.animatedBase) return null;
                foreach (var stage in recipe.modifiers)
                {
                    Merge(stage.id, stage.values); stage.id = "";
                    if ((stage.flags & 16) != 0 || (stage.kind == 3 || stage.kind == 5) && stage.values[3] != 0) return null;
                }
            }
            else
            {
                var operations = new LibraPixelOperation[recipe.operations.Length];
                for (var i = 0; i < operations.Length; ++i)
                {
                    var stage = recipe.operations[i]; Merge(stage.id, stage.values); stage.id = "";
                    operations[i] = new LibraPixelOperation { kind = stage.kind, inputA = stage.inputA, inputB = stage.inputB, seed = stage.seed,
                        a = LiberaFxProceduralValues.Block(stage.values, 0, 12), b = LiberaFxProceduralValues.Block(stage.values, 1, 12), c = LiberaFxProceduralValues.Block(stage.values, 2, 12) };
                }
                var program = new LibraPixelProgram { version = 1, domain = payload.kind == "ProceduralMaterial" ? 1u : 0u,
                    operationCount = (uint)operations.Length, outputRegister = recipe.outputRegister };
                LiberaFxProceduralValues.Check(LibraProceduralNative.libra_procedural_pixel_validate(ref program, operations, (uint)operations.Length, out var time));
                if (time != 0) return null;
            }
            // Dictionary hashes accelerate lookup; complete canonical settings remain the equality key.
            var key = payload.kind + ":" + JsonUtility.ToJson(recipe);
            foreach (var reference in (payload.textures ?? Array.Empty<LiberaFxProceduralTextureReference>()).OrderBy(t => t.slot))
            {
                if (!resources.TryGetValue(reference.producerNodeId, out var source) || source.sharedKey == null) return null;
                key += ":" + reference.slot + ":" + source.sharedKey.Length + ":" + source.sharedKey;
            }
            return key;
        }
        internal Mesh mesh;
        internal RenderTexture texture;
        internal Vector3 pivot;
        internal ulong revision;
        private LibraProceduralDescriptor descriptor;
        private LibraProceduralModifier[] modifiers;
        private LibraPixelOperation[] operations;
        private readonly List<LiberaFxProceduralBinding> bindings = new List<LiberaFxProceduralBinding>();
        private readonly List<LiberaFxProceduralResource> dependencies = new List<LiberaFxProceduralResource>();
        private readonly ulong[] dependencyRevisions = new ulong[4];
        private IntPtr native;
        private LibraProceduralVertex[] vertices;
        private UploadVertex[] uploadVertices;
        private struct UploadVertex
        {
            public Vector3 position, normal;
            public Vector4 tangent;
            public Color color;
            public Vector2 uv, uv2;
        }
        private uint[] indices;
        private ulong vertexRevision, indexRevision;
        private ComputeShader compute;
        private ComputeBuffer operationBuffer;
        private uint usesTime;
        private double previousTime = double.NaN;
        private bool dirty = true;
        private readonly int kernel;
        private readonly bool model;
        private readonly LiberaFxRenderStatisticsCounter statistics;
        private long reservedBytes;
        private uint textureMask;
        private bool connected;

        internal LiberaFxProceduralResource(LiberaFxProceduralResourcePayload payload, LiberaFxRenderStatisticsCounter statistics = null)
        {
            this.statistics = statistics;
            definition = payload;
            model = payload.kind == "ProceduralModel";
            var recipe = payload.recipe;
            if (model)
            {
                descriptor = new LibraProceduralDescriptor { version = 1, shape = (uint)recipe.shape,
                    segmentsU = recipe.segmentsU, segmentsV = recipe.segmentsV,
                    size = LiberaFxProceduralValues.Vector(recipe.size), innerRadius = recipe.innerRadius,
                    sweepDegrees = recipe.sweepDegrees, turns = recipe.turns, tubeRadius = recipe.tubeRadius,
                    pivotMode = recipe.pivotMode, pivot = LiberaFxProceduralValues.Vector(recipe.pivot),
                    pivotVertex = recipe.pivotVertex, flags = (recipe.capped ? 1u : 0u) | 2u | (recipe.animatedBase ? 12u : 0u) };
                modifiers = new LibraProceduralModifier[recipe.modifiers.Length];
                for (var i = 0; i < modifiers.Length; ++i)
                {
                    var m = recipe.modifiers[i];
                    modifiers[i] = new LibraProceduralModifier { kind = (uint)m.kind, axis = (uint)m.axis, flags = m.flags,
                        count = m.count, seed = m.seed, a = LiberaFxProceduralValues.Block(m.values, 0, 16),
                        b = LiberaFxProceduralValues.Block(m.values, 1, 16), c = LiberaFxProceduralValues.Block(m.values, 2, 16),
                        d = LiberaFxProceduralValues.Block(m.values, 3, 16) };
                    Bind(m.id, i, 4);
                    if ((m.flags & 16) != 0 || (m.kind == 3 || m.kind == 5) && m.values[3] != 0) descriptor.flags |= 4;
                }
                foreach (var binding in bindings)
                {
                    modifiers[binding.stage].Set(binding.block, Default(binding.input));
                    if (!string.IsNullOrEmpty(binding.input.sourceNodeId))
                    {
                        modifiers[binding.stage].flags |= 16;
                        descriptor.flags |= 4;
                    }
                }
                // Connected inputs are initialized from their saved defaults before the first VM frame.
                LiberaFxProceduralValues.Check(LibraProceduralNative.libra_procedural_model_predict(ref descriptor,
                    modifiers, (uint)modifiers.Length, out var counts));
                statistics?.ReserveProcedural((long)counts.retainedBytes);
                reservedBytes = (long)counts.retainedBytes;
                try
                {
                    LiberaFxProceduralValues.Check(LibraProceduralNative.libra_procedural_model_create(ref descriptor,
                        modifiers, (uint)modifiers.Length, out native));
                    LiberaFxProceduralValues.Check(LibraProceduralNative.libra_procedural_model_view(native, out var view));
                    vertices = new LibraProceduralVertex[view.vertexCount]; indices = new uint[view.indexCount];
                    uploadVertices = new UploadVertex[view.vertexCount];
                    mesh = new Mesh { name = "Libera FX Procedural Model" };
                    if ((descriptor.flags & 12) != 0) mesh.MarkDynamic();
                    mesh.SetVertexBufferParams(vertices.Length,
                        new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                        new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
                        new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4),
                        new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4),
                        new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
                        new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2));
                    mesh.SetIndexBufferParams(indices.Length, IndexFormat.UInt32);
                    Upload(view);
                }
                catch { Dispose(); throw; }
            }
            else
            {
                operations = new LibraPixelOperation[recipe.operations.Length];
                for (var i = 0; i < operations.Length; ++i)
                {
                    var op = recipe.operations[i];
                    operations[i] = new LibraPixelOperation { kind = op.kind, inputA = op.inputA, inputB = op.inputB, seed = op.seed,
                        a = LiberaFxProceduralValues.Block(op.values, 0, 12), b = LiberaFxProceduralValues.Block(op.values, 1, 12), c = LiberaFxProceduralValues.Block(op.values, 2, 12) };
                    Bind(op.id, i, 3);
                }
                foreach (var binding in bindings)
                    operations[binding.stage].Set(binding.block, Default(binding.input));
                ValidatePixel();
                if (!SystemInfo.supportsComputeShaders || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf))
                    throw new NotSupportedException("Procedural textures require compute shaders and RGBA16F output.");
                var source = Resources.Load<ComputeShader>("LibraProcedural");
                if (!source) throw new InvalidOperationException("Shipped procedural compute shader is missing.");
                var bytes = (long)recipe.resolution * recipe.resolution * 8 + operations.Length * 64 * 4;
                statistics?.ReserveProcedural(bytes); reservedBytes = bytes;
                try
                {
                    compute = UnityEngine.Object.Instantiate(source); kernel = compute.FindKernel("Generate");
                    operationBuffer = new ComputeBuffer(operations.Length, 64, ComputeBufferType.Structured);
                    texture = new RenderTexture(recipe.resolution, recipe.resolution, 0, RenderTextureFormat.ARGBHalf,
                        RenderTextureReadWrite.Linear) { name = "Libera FX " + payload.kind, enableRandomWrite = true,
                        wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, useMipMap = false };
                    if (!texture.Create()) throw new InvalidOperationException("Cannot allocate procedural output texture.");
                    compute.SetBuffer(kernel, "_Operations", operationBuffer); compute.SetTexture(kernel, "_Output", texture);
                    compute.SetInt("_OperationCount", operations.Length); compute.SetInt("_OutputRegister", (int)recipe.outputRegister);
                    compute.SetInt("_Resolution", recipe.resolution);
                    for (var i = 0; i < 4; ++i) compute.SetTexture(kernel, "_Texture" + i, Texture2D.whiteTexture);
                }
                catch { Dispose(); throw; }
            }
        }
        private static Vector4 Default(LiberaFxRenderInputPayload input)
        {
            var value = Vector4.zero;
            for (var i = 0; i < input.width; ++i) value[i] = LiberaFxProceduralValues.Checked(input.defaults[i]);
            return value;
        }
        private void Bind(string stageId, int stage, int blocks)
        {
            if (!Guid.TryParse(stageId, out var id)) throw new InvalidOperationException("Invalid procedural stage identity.");
            for (var block = 0; block < blocks; ++block)
            {
                var key = "stage_" + id.ToString("N") + "_" + block;
                var input = definition.inputs.SingleOrDefault(p => p.id == key);
                if (input == null || input.width != 4) throw new InvalidOperationException("Missing procedural stage input.");
                bindings.Add(new LiberaFxProceduralBinding { input = input, stage = stage, block = block });
            }
        }
        internal void Connect(Dictionary<string, LiberaFxProceduralResource> resources)
        {
            if (connected) return;
            foreach (var reference in definition.textures ?? Array.Empty<LiberaFxProceduralTextureReference>())
            {
                if (model || reference.slot < 0 || reference.slot >= 4 || !resources.TryGetValue(reference.producerNodeId, out var source) ||
                    source.definition.kind != "ProceduralTexture") throw new InvalidOperationException("Invalid procedural texture reference.");
                compute.SetTexture(kernel, "_Texture" + reference.slot, source.texture);
                textureMask |= 1u << reference.slot;
                dependencies.Add(source); ++source.leases;
            }
            connected = true;
            if (!model) ValidatePixel();
        }
        internal void Update(NativeInstance.NativeSpawnableHostBase host, bool root, byte[] container, ulong instance, double time)
        {
            if (sharedKey != null && (model || !dirty)) return;
            foreach (var binding in bindings)
            {
                var value = LiberaFxProceduralValues.Read(binding.input, host, root, container, instance);
                if (model) modifiers[binding.stage].Set(binding.block, value);
                else
                {
                    var previous = binding.block == 0 ? operations[binding.stage].a : binding.block == 1 ? operations[binding.stage].b : operations[binding.stage].c;
                    if (!previous.Equals(value)) { operations[binding.stage].Set(binding.block, value); dirty = true; }
                }
            }
            if (model)
            {
                LiberaFxProceduralValues.Check(LibraProceduralNative.libra_procedural_model_evaluate(native, ref descriptor,
                    modifiers, (uint)modifiers.Length, time, out var changed));
                if (changed != 0)
                {
                    LiberaFxProceduralValues.Check(LibraProceduralNative.libra_procedural_model_view(native, out var view)); Upload(view);
                }
            }
            else
            {
                for (var i = 0; i < dependencies.Count; ++i)
                {
                    if (dependencyRevisions[i] != dependencies[i].revision) dirty = true;
                    dependencyRevisions[i] = dependencies[i].revision;
                }
                if (dirty) { ValidatePixel(); operationBuffer.SetData(operations); }
                if (dirty || usesTime != 0 && time != previousTime)
                {
                    compute.SetFloat("_LocalTime", (float)time);
                    compute.Dispatch(kernel, (texture.width + 7) / 8, (texture.height + 7) / 8, 1);
                    ++revision;
                }
                dirty = false; previousTime = time;
            }
        }
        private void ValidatePixel()
        {
            var program = new LibraPixelProgram { version = 1, domain = definition.kind == "ProceduralMaterial" ? 1u : 0u,
                operationCount = (uint)operations.Length, outputRegister = definition.recipe.outputRegister };
            LiberaFxProceduralValues.Check(LibraProceduralNative.libra_procedural_pixel_validate(ref program, operations,
                (uint)operations.Length, out usesTime));
            if (connected)
            {
                foreach (var operation in operations)
                    if (operation.kind == 30 && (textureMask & (1u << (int)operation.a.x)) == 0)
                        throw new InvalidOperationException("Procedural material samples an unconnected texture slot.");
            }
        }
        private void Upload(LibraProceduralView view)
        {
            if (view.vertexCount != vertices.Length || view.indexCount != indices.Length)
                throw new InvalidOperationException("Procedural topology changed during playback.");
            var flags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices;
            if (vertexRevision != view.vertexRevision)
            {
                LiberaFxProceduralValues.Check(LibraProceduralNative.libra_procedural_model_copy_vertices(native, vertices, view.vertexCount));
                for (var i = 0; i < vertices.Length; ++i)
                {
                    var v = vertices[i];
                    uploadVertices[i] = new UploadVertex { position = v.position, normal = v.normal, tangent = v.tangent,
                        color = v.color, uv = v.uv, uv2 = v.uv2 };
                }
                mesh.SetVertexBufferData(uploadVertices, 0, 0, uploadVertices.Length, 0, flags); vertexRevision = view.vertexRevision;
                statistics?.GeometryUpload(uploadVertices.Length);
            }
            if (indexRevision != view.indexRevision)
            {
                LiberaFxProceduralValues.Check(LibraProceduralNative.libra_procedural_model_copy_indices(native, indices, view.indexCount));
                mesh.SetIndexBufferData(indices, 0, 0, indices.Length, flags); indexRevision = view.indexRevision;
                mesh.subMeshCount = 1;
                mesh.SetSubMesh(0, new SubMeshDescriptor(0, indices.Length, MeshTopology.Triangles), flags);
            }
            mesh.bounds = new Bounds((view.boundsMin + view.boundsMax) * .5f, view.boundsMax - view.boundsMin);
            pivot = view.pivot; ++revision;
        }
        public void Dispose()
        {
            if (disposed || --leases > 0) return;
            disposed = true;
            if (reservedBytes != 0) { statistics?.ReleaseProcedural(reservedBytes); reservedBytes = 0; }
            if (native != IntPtr.Zero) { LibraProceduralNative.libra_procedural_model_release(native); native = IntPtr.Zero; }
            operationBuffer?.Dispose(); operationBuffer = null;
            if (texture) { texture.Release(); Destroy(texture); texture = null; }
            if (compute) { Destroy(compute); compute = null; }
            if (mesh) { Destroy(mesh); mesh = null; }
            foreach (var dependency in dependencies) dependency.Dispose();
            dependencies.Clear();
        }
        internal static void Destroy(UnityEngine.Object value)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(value); else UnityEngine.Object.DestroyImmediate(value);
        }
    }
}
