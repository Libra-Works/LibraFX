using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiberaFx.Unity
{
    [Serializable] public sealed class LibraGpuTextureBinding
    {
        public string resourceId;
        public Texture2D texture;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LibraGpuParticle
    {
        public Vector4 positionAge, velocityLifetime, color, sizeRotation;
        public uint seed, alive, spare0, spare1;
    }

    [DisallowMultipleComponent]
    public sealed class LibraGpuParticleRuntime : MonoBehaviour, IDisposable
    {
        [SerializeField] ComputeShader compute;
        [SerializeField] TextAsset metadata;
        [SerializeField] Material billboardMaterial;
        [SerializeField] LibraGpuTextureBinding[] textures = Array.Empty<LibraGpuTextureBinding>();
        [SerializeField] bool playOnEnable = true;
        [SerializeField] bool automaticUpdate = true;
        [SerializeField] float spawnRate = 64;
        [SerializeField] float billboardSizeScale = 1;
        [SerializeField] uint emitterSeed = 1;
        [SerializeField] Vector3 boundsSize = new Vector3(1000, 1000, 1000);
        ComputeBuffer particles, args;
        Mesh quad;
        MaterialPropertyBlock properties;
        Matrix4x4 drawTransform = Matrix4x4.identity;
        bool drawTransformConnected;
        LibraGpuMetadata program;
        int[] flowKernels;
        Texture[] flowTextures;
        Texture[] resourceTextures;
        int[][] flowMapResourceIndices;
        int particleId;
        uint cursor, frame;
        float elapsed, spawnFraction;
        bool running, submittedWork, spawnableStepMode;
        public bool IsConfigured => particles != null;
        public bool IsRunning => running;
        public bool RuntimeExecuted { get; private set; }
        public string SourceContentHash => program == null ? null : program.contentHash;
        public string SourceHash => program == null ? null : program.sourceHash;
        public int DispatchCount { get; private set; }
        public int DrawCount { get; private set; }
        public int Capacity => program == null ? 0 : program.capacity;
        public static int PendingRetirements => LibraGpuRetirement.PendingCount;
        static readonly int CapacityId = Shader.PropertyToID("LibraGpuCapacity"), CountId = Shader.PropertyToID("LibraGpuCount"),
            SpawnStartId = Shader.PropertyToID("LibraGpuSpawnStart"), SpawnCountId = Shader.PropertyToID("LibraGpuSpawnCount"),
            DeltaId = Shader.PropertyToID("LibraGpuDeltaTime"), TimeId = Shader.PropertyToID("LibraGpuTime"),
            SeedId = Shader.PropertyToID("LibraGpuEmitterSeed"), FrameId = Shader.PropertyToID("LibraGpuFrameIndex"),
            PositionId = Shader.PropertyToID("LibraGpuEmitterPosition"), PaddingId = Shader.PropertyToID("LibraGpuPadding"),
            DrawParticlesId = Shader.PropertyToID("_LibraParticles"), TextureId = Shader.PropertyToID("_MainTex"),
            SizeScaleId = Shader.PropertyToID("_LibraGpuSizeScale"),
            DrawRow0Id = Shader.PropertyToID("_LibraGpuDrawMatrixRow0"),
            DrawRow1Id = Shader.PropertyToID("_LibraGpuDrawMatrixRow1"),
            DrawRow2Id = Shader.PropertyToID("_LibraGpuDrawMatrixRow2"),
            DrawRow3Id = Shader.PropertyToID("_LibraGpuDrawMatrixRow3");

        public void Configure(ComputeShader shader, TextAsset sourceMetadata, Material material, LibraGpuTextureBinding[] bindings)
        {
            if (shader == null || sourceMetadata == null || material == null) throw new ArgumentException("Compute shader, metadata and billboard material are required.");
            if (!material.enableInstancing) throw new ArgumentException("The billboard material must enable GPU instancing.");
            if (!material.HasProperty("_Color"))
                throw new ArgumentException("The billboard Material must expose _Color.");
            if (material.shader == null || !material.shader.isSupported) throw new ArgumentException("The billboard material shader is unsupported.");
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsInstancing) throw new NotSupportedException("GPU compute and instancing are required.");
            var next = LibraGpuMetadata.Parse(sourceMetadata.text);
#if UNITY_EDITOR
            string shaderPath = UnityEditor.AssetDatabase.GetAssetPath(shader);
            if (!string.IsNullOrEmpty(shaderPath) && System.IO.File.Exists(shaderPath))
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(System.IO.File.ReadAllText(shaderPath));
                    var actual = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                    if (actual != next.sourceHash) throw new ArgumentException("Compute shader source hash does not match the GPU metadata.");
                }
            }
#endif
            if (Marshal.SizeOf(typeof(LibraGpuParticle)) != 80) throw new InvalidOperationException("Managed particle ABI is not 80 bytes.");
            var lookup = new Dictionary<string, int>();
            foreach (var k in next.kernels)
            {
                int index;
                try { index = shader.FindKernel(k.entryPoint); }
                catch (Exception e) { throw new ArgumentException("Compute shader is missing kernel " + k.entryPoint, e); }
                shader.GetKernelThreadGroupSizes(index, out uint x, out uint y, out uint z);
                if (x != k.threadsX || y != 1 || z != 1) throw new ArgumentException("Compute shader thread group mismatch: " + k.entryPoint);
                lookup.Add(k.id, index);
            }
            var textureMap = new Dictionary<string, Texture>();
            if (bindings != null)
                foreach (var b in bindings)
                {
                    if (b == null || string.IsNullOrEmpty(b.resourceId) || textureMap.ContainsKey(b.resourceId))
                        throw new ArgumentException("Duplicate or invalid GPU texture binding.");
                    if (b.texture == null)
                        throw new ArgumentException("GPU texture binding is unresolved: " + b.resourceId);
                    textureMap.Add(b.resourceId, b.texture);
                }
            foreach (var r in next.resources)
                if (!textureMap.ContainsKey(r.id)) throw new ArgumentException("Missing texture binding for resource " + r.id);
            if (textureMap.Count != next.resources.Length)
                throw new ArgumentException("One or more GPU texture bindings do not belong to this program.");
            var resourceIndices = new Dictionary<string, int>(StringComparer.Ordinal);
            var nextResourceTextures = new Texture[next.resources.Length];
            for (int i = 0; i < next.resources.Length; i++)
            {
                resourceIndices.Add(next.resources[i].id, i);
                nextResourceTextures[i] = textureMap[next.resources[i].id];
            }
            var nextKernels = new int[next.flow.Length];
            var nextTextures = new Texture[next.flow.Length];
            var nextFlowMapResourceIndices = new int[next.flow.Length][];
            for (int i = 0; i < next.flow.Length; i++)
            {
                if (next.flow[i].kind == "RenderBillboard") nextTextures[i] = textureMap[next.flow[i].resourceId];
                else
                {
                    nextKernels[i] = lookup[next.flow[i].kernelId];
                    var kernel = Array.Find(next.kernels, candidate => candidate.id == next.flow[i].kernelId);
                    nextFlowMapResourceIndices[i] = (kernel.flowMapResourceIds ?? Array.Empty<string>())
                        .Select(id => resourceIndices[id]).ToArray();
                }
            }
            ComputeBuffer nextParticles = null, nextArgs = null;
            Mesh nextQuad = null;
            try
            {
                nextParticles = new ComputeBuffer(next.capacity, 80, ComputeBufferType.Structured);
                nextParticles.SetData(new LibraGpuParticle[next.capacity]);
                nextQuad = CreateQuad();
                nextArgs = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
                nextArgs.SetData(new uint[] { nextQuad.GetIndexCount(0), (uint)next.capacity, nextQuad.GetIndexStart(0), nextQuad.GetBaseVertex(0), 0 });
            }
            catch
            {
                nextParticles?.Release(); nextArgs?.Release();
                if (nextQuad != null) DestroyOwnedObject(nextQuad);
                throw;
            }
            Release();
            compute = shader; metadata = sourceMetadata; billboardMaterial = material; textures = bindings;
            program = next; particles = nextParticles; args = nextArgs; quad = nextQuad;
            flowKernels = nextKernels; flowTextures = nextTextures; resourceTextures = nextResourceTextures;
            flowMapResourceIndices = nextFlowMapResourceIndices; particleId = Shader.PropertyToID(next.particleBuffer);
            properties = new MaterialPropertyBlock();
            properties.SetBuffer(DrawParticlesId, particles);
            drawTransform = Matrix4x4.identity;
            drawTransformConnected = false;
            billboardSizeScale = 1;
            cursor = frame = 0; elapsed = spawnFraction = 0;
            RuntimeExecuted = submittedWork = false; DispatchCount = DrawCount = 0;
        }

        // Performs the same imported-asset, shader-kernel and resource checks as
        // Configure, without allocating per-instance GPU buffers.
        public static LibraGpuMetadata ValidateSpawnableConfiguration(ComputeShader shader,
            TextAsset sourceMetadata, Material material, LibraGpuTextureBinding[] bindings)
        {
            if (shader == null || sourceMetadata == null || material == null)
                throw new ArgumentException("Compute shader, metadata and billboard material are required.");
            if (!material.enableInstancing || !material.HasProperty("_Color") ||
                material.shader == null || !material.shader.isSupported)
                throw new ArgumentException("Billboard Material needs a supported instanced shader with _Color.");
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsInstancing)
                throw new NotSupportedException("GPU compute and instancing are required.");
            var next = LibraGpuMetadata.Parse(sourceMetadata.text);
#if UNITY_EDITOR
            var shaderPath = UnityEditor.AssetDatabase.GetAssetPath(shader);
            if (!string.IsNullOrEmpty(shaderPath) && System.IO.File.Exists(shaderPath))
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    var sourceBytes = System.Text.Encoding.UTF8.GetBytes(System.IO.File.ReadAllText(shaderPath));
                    var actual = BitConverter.ToString(sha.ComputeHash(sourceBytes)).Replace("-", "").ToLowerInvariant();
                    if (actual != next.sourceHash)
                        throw new ArgumentException("Compute shader source hash does not match the GPU metadata.");
                }
            }
#endif
            if (Marshal.SizeOf(typeof(LibraGpuParticle)) != 80)
                throw new InvalidOperationException("Managed particle ABI is not 80 bytes.");
            var kernelIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kernel in next.kernels)
            {
                if (!kernelIds.Add(kernel.id))
                    throw new ArgumentException("Duplicate GPU kernel metadata: " + kernel.id);
                int index;
                try { index = shader.FindKernel(kernel.entryPoint); }
                catch (Exception error) { throw new ArgumentException("Compute shader is missing kernel " + kernel.entryPoint, error); }
                shader.GetKernelThreadGroupSizes(index, out uint x, out uint y, out uint z);
                if (x != kernel.threadsX || y != 1 || z != 1)
                    throw new ArgumentException("Compute shader thread group mismatch: " + kernel.entryPoint);
            }
            var resourceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in bindings ?? Array.Empty<LibraGpuTextureBinding>())
                if (binding == null || string.IsNullOrWhiteSpace(binding.resourceId) ||
                    !binding.texture || !resourceIds.Add(binding.resourceId))
                    throw new ArgumentException("GPU texture bindings contain an unresolved or duplicate resource.");
            if (resourceIds.Count != next.resources.Length ||
                next.resources.Any(resource => !resourceIds.Contains(resource.id)))
                throw new ArgumentException("GPU texture bindings do not exactly match metadata resources.");
            return next;
        }

        // Spawnable scopes own a separate buffer set and are stepped only by their
        // LiberaFxSpawnableComponent. They never use Unity's global frame delta.
        public void ConfigureForSpawnable(ComputeShader shader, TextAsset sourceMetadata,
            Material material, LibraGpuTextureBinding[] bindings, uint seed, float rate, float sizeScale = 1)
        {
            if (float.IsNaN(rate) || float.IsInfinity(rate) || rate < 0)
                throw new ArgumentOutOfRangeException(nameof(rate), "Spawn rate must be finite and nonnegative.");
            if (float.IsNaN(sizeScale) || float.IsInfinity(sizeScale) || sizeScale <= 0 || sizeScale > 10000)
                throw new ArgumentOutOfRangeException(nameof(sizeScale), "Billboard size scale must be finite and positive.");
            Configure(shader, sourceMetadata, material, bindings);
            automaticUpdate = false;
            playOnEnable = false;
            spawnableStepMode = true;
            spawnRate = rate;
            emitterSeed = seed;
            billboardSizeScale = sizeScale;
        }

        // The draw transform is deliberately not uploaded to the compute shader:
        // simulation and spawn continue to operate on the authored particle positions.
        public void SetDrawTransform(Matrix4x4 matrix, bool connected)
        {
            if (connected)
            {
                if (!IsFiniteAffine(matrix))
                    throw new ArgumentException("GPU draw Matrix must be finite and affine so its bounds remain conservative.", nameof(matrix));
                if (!HasDrawTransformProperties(billboardMaterial))
                    throw new InvalidOperationException("GPU billboard Material must expose all four _LibraGpuDrawMatrixRow0..Row3 properties for a connected Matrix input.");
            }
            drawTransform = connected ? matrix : Matrix4x4.identity;
            drawTransformConnected = connected;
        }

        static bool IsFiniteAffine(Matrix4x4 matrix)
        {
            for (var row = 0; row < 4; ++row)
                for (var column = 0; column < 4; ++column)
                    if (float.IsNaN(matrix[row, column]) || float.IsInfinity(matrix[row, column]))
                        return false;
            return Mathf.Abs(matrix.m30) <= 1e-5f && Mathf.Abs(matrix.m31) <= 1e-5f &&
                Mathf.Abs(matrix.m32) <= 1e-5f && Mathf.Abs(matrix.m33 - 1) <= 1e-5f;
        }

        internal static bool HasDrawTransformProperties(Material material) =>
            material && material.HasProperty(DrawRow0Id) && material.HasProperty(DrawRow1Id) &&
            material.HasProperty(DrawRow2Id) && material.HasProperty(DrawRow3Id);

        static Bounds TransformBounds(Bounds bounds, Matrix4x4 matrix)
        {
            var minimum = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var maximum = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            var center = bounds.center;
            var extents = bounds.extents;
            for (var corner = 0; corner < 8; ++corner)
            {
                var point = center + new Vector3(
                    (corner & 1) == 0 ? -extents.x : extents.x,
                    (corner & 2) == 0 ? -extents.y : extents.y,
                    (corner & 4) == 0 ? -extents.z : extents.z);
                point = matrix.MultiplyPoint3x4(point);
                minimum = Vector3.Min(minimum, point);
                maximum = Vector3.Max(maximum, point);
            }
            var result = new Bounds();
            result.SetMinMax(minimum, maximum);
            return result;
        }

        static Matrix4x4 RelativeToEmitter(Matrix4x4 matrix, Vector3 emitterPosition)
        {
            for (var row = 0; row < 3; ++row)
                matrix[row, 3] -= matrix[row, 0] * emitterPosition.x +
                    matrix[row, 1] * emitterPosition.y + matrix[row, 2] * emitterPosition.z;
            return matrix;
        }

        static Mesh CreateQuad()
        {
            var m = new Mesh { name = "Libra GPU billboard quad" };
            m.vertices = new[] { new Vector3(-.5f,-.5f), new Vector3(.5f,-.5f), new Vector3(.5f,.5f), new Vector3(-.5f,.5f) };
            m.uv = new[] { new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1) };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            m.UploadMeshData(true);
            return m;
        }

        public void Step(float deltaTime, uint spawnStart, uint spawnCount, Vector3 emitterPosition, Camera camera = null)
        {
            StepInternal(deltaTime, spawnStart, spawnCount, emitterPosition, camera, false);
        }

        public void StepCircular(float deltaTime, uint spawnStart, uint spawnCount, Vector3 emitterPosition, Camera camera = null)
        {
            StepInternal(deltaTime, spawnStart, spawnCount, emitterPosition, camera, true);
        }

        // Advances one isolated Spawnable GPU scope. Fractional births and circular
        // buffer position belong to this component instance and reset on configure.
        public void StepSpawnable(float effectLocalDelta, Vector3 emitterPosition, Camera camera = null)
        {
            if (!spawnableStepMode)
                throw new InvalidOperationException("Configure this GPU runtime for a Spawnable scope before stepping it.");
            if (!running)
                throw new InvalidOperationException("Start the Spawnable GPU simulation before stepping it.");
            if (float.IsNaN(effectLocalDelta) || float.IsInfinity(effectLocalDelta) || effectLocalDelta < 0)
                throw new ArgumentOutOfRangeException(nameof(effectLocalDelta), "Effect-local delta must be finite and nonnegative.");
            var amount = (double)spawnRate * effectLocalDelta + spawnFraction;
            if (double.IsNaN(amount) || double.IsInfinity(amount))
                throw new ArgumentOutOfRangeException(nameof(effectLocalDelta), "Spawn count overflowed.");
            var whole = Math.Floor(amount);
            var count = (uint)Math.Min(whole, program.capacity);
            spawnFraction = (float)(amount - whole);
            StepCircular(effectLocalDelta, cursor, count, emitterPosition, camera);
            cursor = (cursor + count) % (uint)program.capacity;
        }

        void StepInternal(float deltaTime, uint spawnStart, uint spawnCount, Vector3 emitterPosition, Camera camera, bool circular)
        {
            if (!IsConfigured) throw new InvalidOperationException("Configure the GPU particle runtime before stepping.");
            if (float.IsNaN(deltaTime) || float.IsInfinity(deltaTime) || deltaTime < 0 ||
                float.IsNaN(emitterPosition.x) || float.IsInfinity(emitterPosition.x) ||
                float.IsNaN(emitterPosition.y) || float.IsInfinity(emitterPosition.y) ||
                float.IsNaN(emitterPosition.z) || float.IsInfinity(emitterPosition.z))
                throw new ArgumentException("GPU uniforms must be finite, and deltaTime nonnegative.");
            if (spawnStart > (uint)program.capacity || (spawnStart == (uint)program.capacity && spawnCount != 0) || spawnCount > (uint)program.capacity ||
                (!circular && spawnCount > (uint)program.capacity - spawnStart))
                throw new ArgumentOutOfRangeException(nameof(spawnCount), "Spawn range must fit capacity.");
            if (float.IsInfinity(elapsed + deltaTime)) throw new ArgumentOutOfRangeException(nameof(deltaTime), "GPU time overflow.");
            elapsed += deltaTime;
            compute.SetInt(CapacityId, program.capacity); compute.SetInt(CountId, program.capacity);
            compute.SetFloat(DeltaId, deltaTime); compute.SetFloat(TimeId, elapsed);
            compute.SetInt(SeedId, unchecked((int)emitterSeed)); compute.SetInt(FrameId, unchecked((int)frame++));
            compute.SetVector(PositionId, emitterPosition); compute.SetInt(PaddingId, 0);
            submittedWork = true;
            for (int i = 0; i < program.flow.Length; i++)
            {
                if (program.flow[i].kind == "RenderBillboard")
                {
                    properties.SetTexture(TextureId, flowTextures[i]);
                    properties.SetFloat(SizeScaleId, billboardSizeScale);
                    var appliedDrawTransform = drawTransformConnected
                        ? RelativeToEmitter(drawTransform, emitterPosition)
                        : Matrix4x4.identity;
                    if (HasDrawTransformProperties(billboardMaterial))
                    {
                        properties.SetVector(DrawRow0Id, appliedDrawTransform.GetRow(0));
                        properties.SetVector(DrawRow1Id, appliedDrawTransform.GetRow(1));
                        properties.SetVector(DrawRow2Id, appliedDrawTransform.GetRow(2));
                        properties.SetVector(DrawRow3Id, appliedDrawTransform.GetRow(3));
                    }
                    var bounds = drawTransformConnected
                        ? TransformBounds(new Bounds(Vector3.zero, boundsSize), drawTransform)
                        : new Bounds(emitterPosition, boundsSize);
                    Graphics.DrawMeshInstancedIndirect(quad, 0, billboardMaterial, bounds, args, 0, properties,
                        ShadowCastingMode.Off, false, gameObject.layer, camera);
                    DrawCount++;
                }
                else
                {
                    int kernel = flowKernels[i];
                    compute.SetBuffer(kernel, particleId, particles);
                    foreach (var resourceIndex in flowMapResourceIndices[i])
                    {
                        var resource = program.resources[resourceIndex];
                        compute.SetTexture(kernel, Shader.PropertyToID(resource.shaderName), resourceTextures[resourceIndex]);
                    }
                    uint first = Math.Min(spawnCount, (uint)program.capacity - spawnStart);
                    compute.SetInt(SpawnStartId, (int)spawnStart);
                    compute.SetInt(SpawnCountId, (int)first);
                    compute.Dispatch(kernel, (program.capacity + 63) / 64, 1, 1);
                    DispatchCount++;
                    if (program.flow[i].kind == "Spawn" && first < spawnCount)
                    {
                        compute.SetInt(SpawnStartId, 0);
                        compute.SetInt(SpawnCountId, (int)(spawnCount - first));
                        compute.Dispatch(kernel, (program.capacity + 63) / 64, 1, 1);
                        DispatchCount++;
                    }
                }
            }
            RuntimeExecuted = true;
        }

        public void StartSimulation() { if (!IsConfigured) throw new InvalidOperationException("GPU runtime is not configured."); running = true; }
        public void StopSimulation() { running = false; }
        public void ResetSimulation()
        {
            if (!IsConfigured) throw new InvalidOperationException("GPU runtime is not configured.");
            particles.SetData(new LibraGpuParticle[program.capacity]);
            cursor = frame = 0; elapsed = spawnFraction = 0; RuntimeExecuted = false; DispatchCount = DrawCount = 0;
        }
        // Explicit diagnostics only; never called by the simulation or renderer.
        public void ReadbackForDiagnostics(LibraGpuParticle[] destination)
        {
            if (!IsConfigured || destination == null || destination.Length < program.capacity) throw new ArgumentException("Diagnostic destination must fit capacity.");
            particles.GetData(destination, 0, 0, program.capacity);
        }
        void OnEnable()
        {
            if (playOnEnable && compute != null && metadata != null && billboardMaterial != null)
            {
                Configure(compute, metadata, billboardMaterial, textures);
                StartSimulation();
            }
        }
        void Update()
        {
            if (!running || !automaticUpdate) return;
            float delta = Time.deltaTime;
            float amount = Mathf.Max(0, spawnRate) * delta + spawnFraction;
            uint count = (uint)Mathf.Min(program.capacity, Mathf.Floor(amount));
            spawnFraction = amount - Mathf.Floor(amount);
            StepCircular(delta, cursor, count, transform.position);
            cursor = (cursor + count) % (uint)program.capacity;
        }
        void OnDisable() => Release();
        void OnDestroy() => Release();
        public void Dispose() => Release();
        void Release()
        {
            running = false;
            LibraGpuRetirement.Retire(particles, args, quad, submittedWork);
            particles = null; args = null; quad = null;
            program = null; flowKernels = null; flowTextures = null; properties = null;
            drawTransform = Matrix4x4.identity;
            drawTransformConnected = false;
            spawnableStepMode = false;
            DrawCount = 0; submittedWork = false;
        }
        static void DestroyOwnedObject(UnityEngine.Object o) { if (Application.isPlaying) Destroy(o); else DestroyImmediate(o); }
    }
}
