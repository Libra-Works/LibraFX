using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace LiberaFx.Unity
{
    // One instance per authored GPU container per effect scope. No GameObject
    // or engine component is allocated for a fluid.
    internal sealed class LibraFluid2DInstance : IDisposable
    {
        private static readonly string[] Names =
        {
            "LibraFluidClear", "LibraFluidAdvectVelocity", "LibraFluidApplyForces",
            "LibraFluidDivergence", "LibraFluidJacobi", "LibraFluidProjectVelocity",
            "LibraFluidAdvectDye", "LibraFluidPublish", "LibraFluidRenderDye"
        };
        private readonly LibraFluid2DSettings settings;
        private readonly float sizeScale;
        private readonly Transform owner;
        private readonly byte[][] drawRows;
        private readonly int[] kernels = new int[9];
        private ComputeShader compute;
        private ComputeBuffer[] buffers;
        private RenderTexture output;
        private Mesh mesh;
        private Material material;
        private LiberaFxRenderSubmission submission;
        private bool disposed;
        internal string NodeId { get; }
        internal RenderTexture Output => output;
        internal double DiscardedDelta { get; private set; }
        internal int LastSubsteps { get; private set; }

        internal static ComputeShader ValidateAssets()
        {
            if (!SystemInfo.supportsComputeShaders ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R16G16B16A16_SFloat, FormatUsage.LoadStore) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R16G16B16A16_SFloat, FormatUsage.Sample))
                throw new NotSupportedException("Fluid2D requires compute and RGBA16Float load/store.");
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
            NodeId = prepared.nodeId;
            settings = prepared.settings;
            sizeScale = prepared.sizeScale;
            drawRows = prepared.drawMatrixRowNodeBytes;
            settings.Validate();
            try
            {
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
                mesh = new Mesh { name = "Libera FX Fluid2D quad", hideFlags = HideFlags.DontSave };
                mesh.vertices = new[] { new Vector3(-.5f, -.5f, 0), new Vector3(.5f, -.5f, 0),
                    new Vector3(.5f, .5f, 0), new Vector3(-.5f, .5f, 0) };
                // Raw compute texel row 0 is logical bottom. Sample it at local
                // Y=-0.5; this is not a camera/blit texture, so do not apply
                // a graphicsUVStartsAtTop camera-render-target flip here.
                mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
                mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
                mesh.RecalculateBounds();
                material = new Material(Resources.Load<Shader>("LibraFluid2DDraw")) { hideFlags = HideFlags.DontSave };
                var state = new LiberaFxDrawPropertyState(1);
                state.SetTexture(Shader.PropertyToID("_MainTex"), output);
                submission = new LiberaFxRenderSubmission(state, statistics) { owner = owner };
                submission.Configure(mesh, material, 0, owner.gameObject.layer, statistics);
                Reset();
            }
            catch { Dispose(); throw; }
        }

        internal void Reset()
        {
            if (disposed) throw new ObjectDisposedException(nameof(LibraFluid2DInstance));
            Uniforms(0);
            for (var i = 0; i < 7; i++) Dispatch(0, -1, -1, i, -1);
            Dispatch(8, 2, -1, -1, -1);
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
            var duration = Math.Min(delta, (double)settings.maximumStep * settings.maximumSubsteps);
            LastSubsteps = Math.Min(settings.maximumSubsteps, Math.Max(1, (int)Math.Ceiling(duration / settings.maximumStep)));
            DiscardedDelta = delta - duration;
            Uniforms((float)(duration / LastSubsteps));
            for (var step = 0; step < LastSubsteps; step++)
            {
                Dispatch(1, 0, -1, 1, -1);
                Dispatch(2, 1, 2, 0, -1);
                Dispatch(3, 0, -1, 4, -1);
                Dispatch(0, -1, -1, 5, -1);
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
            }
        }

        private void Uniforms(float delta)
        {
            compute.SetVector("LibraFluidGrid", new Vector4(settings.width, settings.height, delta, 0));
            compute.SetVector("LibraFluidDomain", new Vector4(settings.domainWidth, settings.domainHeight,
                settings.velocityDissipation, settings.densityDissipation));
            compute.SetVector("LibraFluidSource", new Vector4(settings.sourceX, settings.sourceY,
                settings.sourceRadius, settings.sourceDensity));
            compute.SetVector("LibraFluidForce", new Vector4(settings.sourceVelocityX, settings.sourceVelocityY,
                settings.buoyancy, settings.sourceEnabled ? 1 : 0));
            compute.SetVector("LibraFluidColor", new Vector4(settings.colorR, settings.colorG, settings.colorB, settings.colorA));
        }

        private void Dispatch(int pass, int read0, int read1, int write0, int write1)
        {
            var kernel = kernels[pass];
            if (read0 >= 0) compute.SetBuffer(kernel, "LibraFluidRead0", buffers[read0]);
            if (read1 >= 0) compute.SetBuffer(kernel, "LibraFluidRead1", buffers[read1]);
            if (write0 >= 0) compute.SetBuffer(kernel, "LibraFluidWrite0", buffers[write0]);
            if (write1 >= 0) compute.SetBuffer(kernel, "LibraFluidWrite1", buffers[write1]);
            if (pass == 8) compute.SetTexture(kernel, "LibraFluidOutput", output);
            compute.Dispatch(kernel, (settings.width + 7) / 8, (settings.height + 7) / 8, 1);
        }

        internal void UpdateTransform(Matrix4x4 connected)
        {
            submission.layer = owner.gameObject.layer;
            submission.SetMatrix((drawRows != null ? connected : owner.localToWorldMatrix) *
                Matrix4x4.Scale(new Vector3(settings.domainWidth * sizeScale, settings.domainHeight * sizeScale, 1)));
            submission.enabled = true;
        }

        internal byte[][] DrawRows => drawRows;
        internal ComputeBuffer FieldForDiagnostics(int index) => buffers[index];

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            submission?.Dispose();
            submission = null;
            var oldBuffers = buffers; var oldOutput = output; var oldMesh = mesh;
            var oldMaterial = material; var oldCompute = compute;
            buffers = null; output = null; mesh = null; material = null; compute = null;
            if (oldBuffers == null && !oldOutput && !oldMesh && !oldMaterial && !oldCompute) return;
            LibraGpuRetirement.Retire(() =>
            {
                if (oldBuffers != null) foreach (var buffer in oldBuffers) buffer?.Release();
                if (oldOutput) { oldOutput.Release(); DestroyOwned(oldOutput); }
                if (oldMesh) DestroyOwned(oldMesh);
                if (oldMaterial) DestroyOwned(oldMaterial);
                if (oldCompute) DestroyOwned(oldCompute);
            });
        }

        private static void DestroyOwned(UnityEngine.Object value)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }
    }
}
