using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace LiberaFx.Unity
{
    // One instance per authored GPU container per effect scope. No GameObject
    // or engine component is allocated for a fluid.
    internal sealed class LibraFluid2DInstance : IDisposable, ILibraGpuFieldSource
    {
        private static readonly string[] Names =
        {
            "LibraFluidClear", "LibraFluidAdvectVelocity", "LibraFluidApplyForces",
            "LibraFluidDivergence", "LibraFluidJacobi", "LibraFluidProjectVelocity",
            "LibraFluidAdvectDye", "LibraFluidPublish", "LibraFluidRenderDye",
            "LibraFluidInitializeWater", "LibraFluidShallowWater", "LibraFluidRenderFields", "LibraFluidRenderColor"
        };
        private readonly LibraFluid2DSettings settings;
        private readonly float sizeScale;
        private readonly Transform owner;
        private readonly byte[][] drawRows;
        private readonly int[] kernels = new int[13];
        private readonly Vector4[] forceUniforms = new Vector4[25];
        private double simulationTime;
        private readonly LiberaFxRenderStatisticsCounter memoryCounter;
        private long reservedBytes;
        private ComputeShader compute;
        private ComputeBuffer[] buffers;
        private RenderTexture output;
        private RenderTexture fieldOutput;
        private RenderTexture colorOutput;
        private Matrix4x4 sourceToWorld;
        private Mesh mesh;
        private Material material;
        private LiberaFxRenderSubmission submission;
        private bool disposed;
        internal string NodeId { get; }
        internal RenderTexture Output => output;
        internal RenderTexture FieldOutput => fieldOutput;
        internal RenderTexture ColorOutput => colorOutput;
        internal double DiscardedDelta { get; private set; }
        internal int LastSubsteps { get; private set; }

        internal static ComputeShader ValidateAssets()
        {
            if (!SystemInfo.supportsComputeShaders ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R16G16B16A16_SFloat, FormatUsage.LoadStore) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R16G16B16A16_SFloat, FormatUsage.Sample))
                throw new NotSupportedException("Fluid2D requires compute and RGBA16Float load/store.");
            if (!SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat, FormatUsage.LoadStore) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat, FormatUsage.Sample))
                throw new NotSupportedException("Fluid2D requires sampled RGBA32Float field storage.");
            var assets = Resources.Load<LibraFluid2DResources>("LibraFluid2DResources");
            var draw = Resources.Load<Shader>("LibraFluid2DDraw");
            if (!assets || !assets.compute || !draw || !draw.isSupported)
                throw new InvalidOperationException("The trusted Fluid2D compute or draw asset is missing or unsupported.");
            foreach (var name in Names)
            {
                var kernel = assets.compute.FindKernel(name);
                assets.compute.GetKernelThreadGroupSizes(kernel, out var x, out var y, out var z);
                if (x != 8 || y != 8 || z != 1)
                    throw new InvalidOperationException("Fluid2D compute thread group mismatch: " + name);
            }
            return assets.compute;
        }

        internal LibraFluid2DInstance(Transform owner, LiberaFxPreparedFluidProgram prepared,
            LiberaFxRenderStatisticsCounter statistics)
        {
            this.owner = owner;
            sourceToWorld = owner.localToWorldMatrix;
            NodeId = prepared.nodeId;
            settings = prepared.settings;
            sizeScale = prepared.sizeScale;
            drawRows = prepared.drawMatrixRowNodeBytes;
            settings.Validate();
            forceUniforms[0].x = settings.forces?.Length ?? 0;
            for (var index = 0; index < (settings.forces?.Length ?? 0); index++)
            {
                var node = settings.forces[index];
                forceUniforms[1 + index * 3] = new Vector4(node.KindId, node.x, node.y, node.strength);
                forceUniforms[2 + index * 3] = new Vector4(node.frequency, node.seed, node.octaves, node.roughness);
                var inputA = node.inputA == null ? 0 : 1 + Array.FindIndex(settings.forces, value => Guid.Parse(value.id) == Guid.Parse(node.inputA));
                var inputB = node.inputB == null ? 0 : 1 + Array.FindIndex(settings.forces, value => Guid.Parse(value.id) == Guid.Parse(node.inputB));
                var outputNode = (node.KindId < 3 || node.KindId >= 7) && !Array.Exists(settings.forces,
                    value => value.inputA != null && Guid.Parse(value.inputA) == Guid.Parse(node.id) ||
                        value.inputB != null && Guid.Parse(value.inputB) == Guid.Parse(node.id));
                forceUniforms[3 + index * 3] = new Vector4(node.animationSpeed, inputA, inputB, outputNode ? 1 : 0);
            }
            memoryCounter = statistics;
            try
            {
                var bytes = (long)settings.width * settings.height * 144;
                memoryCounter?.ReserveFluid(bytes);
                reservedBytes = memoryCounter == null ? 0 : bytes;
                compute = UnityEngine.Object.Instantiate(ValidateAssets());
                compute.hideFlags = HideFlags.DontSave;
                for (var i = 0; i < Names.Length; i++) kernels[i] = compute.FindKernel(Names[i]);
                buffers = new ComputeBuffer[7];
                for (var i = 0; i < buffers.Length; i++)
                    buffers[i] = new ComputeBuffer(settings.width * settings.height, 16, ComputeBufferType.Structured);
                var descriptor = new RenderTextureDescriptor(settings.width, settings.height)
                {
                    graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat,
                    depthBufferBits = 0, enableRandomWrite = true, msaaSamples = 1
                };
                output = new RenderTexture(descriptor)
                {
                    name = "Libera FX Fluid2D " + NodeId,
                    hideFlags = HideFlags.DontSave, filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
                if (!output.Create()) throw new InvalidOperationException("Could not allocate Fluid2D output texture.");
                descriptor.graphicsFormat = GraphicsFormat.R32G32B32A32_SFloat;
                fieldOutput = new RenderTexture(descriptor)
                {
                    name = "Libera FX Fluid2D Fields " + NodeId, hideFlags = HideFlags.DontSave,
                    filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
                };
                if (!fieldOutput.Create()) throw new InvalidOperationException("Could not allocate Fluid2D field texture.");
                descriptor.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                colorOutput = new RenderTexture(descriptor)
                {
                    name = "Libera FX Fluid2D Color " + NodeId, hideFlags = HideFlags.DontSave,
                    filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
                };
                if (!colorOutput.Create()) throw new InvalidOperationException("Could not allocate straight-alpha fluid color.");
                if (!prepared.automaticPlane)
                {
                    Reset();
                    return;
                }
                mesh = new Mesh { name = "Libera FX Fluid2D quad", hideFlags = HideFlags.DontSave };
                mesh.vertices = new[] { new Vector3(-.5f, -.5f, 0), new Vector3(.5f, -.5f, 0),
                    new Vector3(.5f, .5f, 0), new Vector3(-.5f, .5f, 0) };
                // Raw compute texel row 0 is logical bottom. Sample it at local
                // Y=-0.5; this is not a camera/blit texture, so do not apply
                // a graphicsUVStartsAtTop camera-render-target flip here.
                mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
                mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
                mesh.RecalculateBounds();
                if (settings.ModelId == 2) BuildShallowSurface(mesh);
                material = new Material(Resources.Load<Shader>("LibraFluid2DDraw")) { hideFlags = HideFlags.DontSave };
                var state = new LiberaFxDrawPropertyState(2);
                state.SetTexture(Shader.PropertyToID("_MainTex"), output);
                state.SetTexture(Shader.PropertyToID("_LibraFluidFields"), fieldOutput);
                material.SetFloat("_LibraFluidModel", settings.ModelId);
                material.SetFloat("_LibraFluidInitialDepth", settings.initialWaterDepth);
                material.SetFloat("_LibraFluidIgnition", settings.ignitionTemperature);
                material.SetVector("_LibraFluidFields_TexelSize", new Vector4(1f / settings.width, 1f / settings.height, settings.width, settings.height));
                submission = new LiberaFxRenderSubmission(state, statistics) { owner = owner };
                submission.Configure(mesh, material, 0, owner.gameObject.layer, statistics);
                Reset();
            }
            catch { Dispose(); throw; }
        }

        internal void Reset()
        {
            if (disposed) throw new ObjectDisposedException(nameof(LibraFluid2DInstance));
            simulationTime = 0;
            Uniforms(0);
            for (var i = 0; i < 7; i++) Dispatch(0, -1, -1, i, -1);
            if (settings.ModelId == 1 || settings.ModelId == 2) Dispatch(9, -1, -1, 2, -1);
            Dispatch(8, 2, -1, -1, -1);
            Dispatch(11, 0, 2, -1, -1);
            Dispatch(12, 2, -1, -1, -1);
            LastSubsteps = 0;
            DiscardedDelta = 0;
        }

        internal void Step(double delta)
        {
            if (disposed) throw new ObjectDisposedException(nameof(LibraFluid2DInstance));
            if (double.IsNaN(delta) || double.IsInfinity(delta) || delta < 0)
                throw new ArgumentOutOfRangeException(nameof(delta));
            LastSubsteps = 0;
            DiscardedDelta = 0;
            if (delta == 0) return;
            var maximumStep = settings.MaximumStableStep();
            var duration = Math.Min(delta, maximumStep * settings.maximumSubsteps);
            LastSubsteps = Math.Min(settings.maximumSubsteps, Math.Max(1, (int)Math.Ceiling(duration / maximumStep)));
            DiscardedDelta = delta - duration;
            Uniforms((float)(duration / LastSubsteps));
            for (var step = 0; step < LastSubsteps; step++)
            {
                forceUniforms[0].y = (float)(simulationTime % 4096);
                compute.SetVectorArray("LibraFluidForceNodes", forceUniforms);
                simulationTime += duration / LastSubsteps;
                if (settings.ModelId == 2)
                {
                    Dispatch(10, 2, -1, 3, -1);
                    Dispatch(7, 3, 3, 0, 2);
                    Dispatch(8, 2, -1, -1, -1);
                    Dispatch(11, 0, 2, -1, -1);
                    Dispatch(12, 2, -1, -1, -1);
                    continue;
                }
                Dispatch(1, 0, -1, 1, -1);
                Dispatch(2, 1, 2, 0, -1);
                Dispatch(3, 0, 2, 4, -1);
                if (settings.ModelId != 1) Dispatch(0, -1, -1, 5, -1);
                var pressure = 5;
                var scratch = 6;
                for (var i = 0; i < settings.pressureIterations; i++)
                {
                    Dispatch(4, pressure, 4, scratch, -1);
                    var swap = pressure; pressure = scratch; scratch = swap;
                }
                Dispatch(5, 0, pressure, 1, -1);
                Dispatch(6, 2, 1, 3, -1);
                Dispatch(7, 1, 3, 0, 2);
                Dispatch(8, 2, -1, -1, -1);
                Dispatch(11, 0, 2, -1, -1);
                Dispatch(12, 2, -1, -1, -1);
            }
        }

        private void Uniforms(float delta)
        {
            compute.SetVector("LibraFluidGrid", new Vector4(settings.width, settings.height, delta, settings.vorticity));
            compute.SetVector("LibraFluidDomain", new Vector4(settings.domainWidth, settings.domainHeight,
                settings.velocityDissipation, settings.densityDissipation));
            compute.SetVector("LibraFluidSource", new Vector4(settings.sourceX, settings.sourceY,
                settings.sourceRadius, settings.sourceDensity));
            compute.SetVector("LibraFluidForce", new Vector4(settings.sourceVelocityX, settings.sourceVelocityY,
                settings.buoyancy, settings.sourceEnabled ? 1 : 0));
            compute.SetVector("LibraFluidColor", new Vector4(settings.colorR, settings.colorG, settings.colorB, settings.colorA));
            compute.SetVector("LibraFluidModel", new Vector4(settings.ModelId, settings.gravity, settings.initialWaterDepth,
                settings.ModelId == 3 ? settings.sourceTemperature : settings.maximumWaterSpeed));
            compute.SetVector("LibraFluidReaction", new Vector4(settings.fuelBurnRate, settings.ignitionTemperature,
                settings.heatYield, settings.coolingRate));
            forceUniforms[0].y = (float)(simulationTime % 4096);
            compute.SetVectorArray("LibraFluidForceNodes", forceUniforms);
        }

        private void Dispatch(int pass, int read0, int read1, int write0, int write1)
        {
            var kernel = kernels[pass];
            if (read0 >= 0) compute.SetBuffer(kernel, "LibraFluidRead0", buffers[read0]);
            if (read1 >= 0) compute.SetBuffer(kernel, "LibraFluidRead1", buffers[read1]);
            if (write0 >= 0) compute.SetBuffer(kernel, "LibraFluidWrite0", buffers[write0]);
            if (write1 >= 0) compute.SetBuffer(kernel, "LibraFluidWrite1", buffers[write1]);
            if (pass == 8) compute.SetTexture(kernel, "LibraFluidOutput", output);
            if (pass == 11) compute.SetTexture(kernel, "LibraFluidOutput", fieldOutput);
            if (pass == 12) compute.SetTexture(kernel, "LibraFluidOutput", colorOutput);
            compute.Dispatch(kernel, (settings.width + 7) / 8, (settings.height + 7) / 8, 1);
        }

        internal void UpdateTransform(Matrix4x4 connected)
        {
            sourceToWorld = drawRows != null ? connected : owner.localToWorldMatrix;
            if (submission == null) return;
            submission.layer = owner.gameObject.layer;
            var scale = settings.ModelId == 2
                ? new Vector3(settings.domainWidth * sizeScale, sizeScale, settings.domainHeight * sizeScale)
                : new Vector3(settings.domainWidth * sizeScale, settings.domainHeight * sizeScale, 1);
            submission.SetMatrix((drawRows != null ? connected : owner.localToWorldMatrix) * Matrix4x4.Scale(scale));
            submission.enabled = true;
        }

        internal byte[][] DrawRows => drawRows;
        internal ComputeBuffer FieldForDiagnostics(int index) => buffers[index];

        public void GetMapping(Matrix4x4 consumerToWorld, out Vector4 uvU, out Vector4 uvV, out Vector4 velocityU, out Vector4 velocityV)
        {
            var determinant = sourceToWorld.determinant;
            var consumerDeterminant = consumerToWorld.determinant;
            if (float.IsNaN(determinant) || float.IsInfinity(determinant) || Math.Abs(determinant) < .00000001f ||
                float.IsNaN(consumerDeterminant) || float.IsInfinity(consumerDeterminant) || Math.Abs(consumerDeterminant) < .00000001f)
                throw new InvalidOperationException("Generated field sampling needs an invertible producer transform.");
            var inverse = sourceToWorld.inverse * consumerToWorld;
            uvU = inverse.GetRow(0) / (settings.domainWidth * sizeScale); uvU.w += .5f;
            uvV = inverse.GetRow(settings.ModelId == 2 ? 2 : 1) / (settings.domainHeight * sizeScale); uvV.w += .5f;
            var worldToConsumer = consumerToWorld.inverse;
            velocityU = worldToConsumer.MultiplyVector(sourceToWorld.MultiplyVector(Vector3.right) * sizeScale);
            velocityV = worldToConsumer.MultiplyVector(sourceToWorld.MultiplyVector(settings.ModelId == 2 ? Vector3.forward : Vector3.up) * sizeScale);
        }

        private void BuildShallowSurface(Mesh target)
        {
            var width = Math.Min(settings.width - 1, 128);
            var height = Math.Min(settings.height - 1, 128);
            var vertices = new Vector3[(width + 1) * (height + 1)];
            var uv = new Vector2[vertices.Length];
            var triangles = new int[width * height * 6];
            for (var y = 0; y <= height; y++)
                for (var x = 0; x <= width; x++)
                {
                    var index = y * (width + 1) + x;
                    uv[index] = new Vector2(x / (float)width, y / (float)height);
                    vertices[index] = new Vector3(uv[index].x - .5f, 0, uv[index].y - .5f);
                }
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var index = y * (width + 1) + x;
                    var offset = (y * width + x) * 6;
                    triangles[offset] = index; triangles[offset + 1] = index + width + 1; triangles[offset + 2] = index + 1;
                    triangles[offset + 3] = index + 1; triangles[offset + 4] = index + width + 1; triangles[offset + 5] = index + width + 2;
                }
            target.Clear(); target.vertices = vertices; target.uv = uv; target.triangles = triangles;
            target.bounds = new Bounds(new Vector3(0, settings.initialWaterDepth * 7, 0), new Vector3(1, settings.initialWaterDepth * 16, 1));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            submission?.Dispose();
            submission = null;
            var oldBuffers = buffers; var oldOutput = output; var oldFieldOutput = fieldOutput; var oldColorOutput = colorOutput; var oldMesh = mesh;
            var oldMaterial = material; var oldCompute = compute;
            buffers = null; output = null; fieldOutput = null; colorOutput = null; mesh = null; material = null; compute = null;
            var retiredBytes = reservedBytes;
            reservedBytes = 0;
            if (oldBuffers == null && !oldOutput && !oldFieldOutput && !oldColorOutput && !oldMesh && !oldMaterial && !oldCompute)
            {
                memoryCounter?.ReleaseFluid(retiredBytes);
                return;
            }
            LibraGpuRetirement.Retire(() =>
            {
                if (oldBuffers != null) foreach (var buffer in oldBuffers) buffer?.Release();
                if (oldOutput) { oldOutput.Release(); DestroyOwned(oldOutput); }
                if (oldFieldOutput) { oldFieldOutput.Release(); DestroyOwned(oldFieldOutput); }
                if (oldColorOutput) { oldColorOutput.Release(); DestroyOwned(oldColorOutput); }
                if (oldMesh) DestroyOwned(oldMesh);
                if (oldMaterial) DestroyOwned(oldMaterial);
                if (oldCompute) DestroyOwned(oldCompute);
                memoryCounter?.ReleaseFluid(retiredBytes);
            });
        }

        private static void DestroyOwned(UnityEngine.Object value)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }
    }
}
