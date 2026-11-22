using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LiberaFx.Unity
{
    internal sealed class LiberaFxRenderScope : IDisposable
    {
        private readonly bool rootScope;
        private readonly byte[] containerId;
        private readonly ulong instanceId;
        private readonly double startTime;
        private readonly Transform owner;
        private readonly List<LiberaFxDirectRenderNodeInstance> renderNodes =
            new List<LiberaFxDirectRenderNodeInstance>();
        private readonly List<LiberaFxFlexiblePlaneRenderer> flexiblePlanes =
            new List<LiberaFxFlexiblePlaneRenderer>();
        private readonly LiberaFxFlexiblePlaneHistoryScope flexibleHistory =
            new LiberaFxFlexiblePlaneHistoryScope();
        private readonly List<LiberaFxCpuParticleRenderInstance> cpuParticleRenderers =
            new List<LiberaFxCpuParticleRenderInstance>();
        private readonly List<LiberaFxDebugDrawInstance> debugDraws = new List<LiberaFxDebugDrawInstance>();
        private readonly Dictionary<string, LiberaFxProceduralResource> proceduralResources = new Dictionary<string, LiberaFxProceduralResource>(StringComparer.Ordinal);
        private readonly List<LiberaFxProceduralResource> proceduralOrder = new List<LiberaFxProceduralResource>();
        private readonly List<LiberaFxProceduralDraw> proceduralDraws = new List<LiberaFxProceduralDraw>();
        private readonly List<LibraGpuParticleRuntime> gpuPrograms = new List<LibraGpuParticleRuntime>();
        private readonly List<LibraFluid2DInstance> fluidPrograms = new List<LibraFluid2DInstance>();
        private readonly Dictionary<LibraGpuParticleRuntime, LiberaFxPreparedGpuProgram> gpuDrawTransforms =
            new Dictionary<LibraGpuParticleRuntime, LiberaFxPreparedGpuProgram>();
        private readonly LiberaFxRenderStatisticsCounter statistics;
        private GameObject scopeObject;
        private double previousLocalTime;
        private bool disposed;
        internal IReadOnlyList<LiberaFxDirectRenderNodeInstance> DrawNodes => renderNodes;

        private LiberaFxRenderScope(Transform owner, bool isRoot, byte[] id, ulong instance, double time,
            LiberaFxRenderStatisticsCounter statistics)
        {
            this.owner = owner;
            rootScope = isRoot;
            containerId = id;
            instanceId = instance;
            startTime = time;
            this.statistics = statistics;
            previousLocalTime = 0;
        }

        public static LiberaFxRenderScope CreatePending(Transform owner,
            LiberaFxPreparedScope prepared, bool isRoot, byte[] containerId,
            ulong instanceId, double startTime, ulong seed, bool suppressDebugDraw = false,
            LiberaFxRenderStatisticsCounter statistics = null, CoordinateBasis? coordinateBasis = null)
        {
            var scope = new LiberaFxRenderScope(owner, isRoot, containerId,
                instanceId, startTime, statistics);
            try
            {
                if (prepared.renderNodes.Length == 0 && prepared.cpuParticles.Length == 0 &&
                    prepared.gpuPrograms.Length == 0 && (prepared.fluidPrograms?.Length ?? 0) == 0)
                    return scope;

                if (prepared.gpuPrograms.Length != 0 || coordinateBasis.HasValue)
                {
                    scope.scopeObject = new GameObject(isRoot
                        ? "Libera FX Root GPU Scope"
                        : "Libera FX Spawnable GPU " + prepared.templateId + " #" + instanceId);
                    scope.scopeObject.layer = owner.gameObject.layer;
                    scope.scopeObject.transform.SetParent(owner, false);
                    if (coordinateBasis.HasValue)
                    {
                        var basis = coordinateBasis.Value.EffectToHostMatrix();
                        var transform = scope.scopeObject.transform;
                        transform.localRotation = Quaternion.LookRotation(basis.GetColumn(2), basis.GetColumn(1));
                        var units = (float)coordinateBasis.Value.unitsPerMetre;
                        transform.localScale = new Vector3(basis.determinant < 0 ? -units : units, units, units);
                    }
                    scope.scopeObject.SetActive(false);
                }
                var renderParent = scope.scopeObject
                    ? scope.scopeObject.transform : owner;
                foreach (var resource in (prepared.proceduralResources ?? Array.Empty<LiberaFxProceduralResourcePayload>()).OrderBy(p => p.kind == "ProceduralModel" ? 0 : p.kind == "ProceduralTexture" ? 1 : 2))
                {
                    var producer = LiberaFxProceduralResource.Acquire(resource, scope.proceduralResources, statistics);
                    scope.proceduralResources.Add(resource.nodeId, producer);
                }
                foreach (var producer in scope.proceduralResources.Values)
                {
                    producer.Connect(scope.proceduralResources);
                    if (producer.definition.kind != "ProceduralMaterial") scope.proceduralOrder.Add(producer);
                }
                foreach (var producer in scope.proceduralResources.Values)
                    if (producer.definition.kind == "ProceduralMaterial") scope.proceduralOrder.Add(producer);
                foreach (var fluid in prepared.fluidPrograms ?? Array.Empty<LiberaFxPreparedFluidProgram>())
                    scope.fluidPrograms.Add(new LibraFluid2DInstance(renderParent, fluid, statistics));
                foreach (var node in prepared.renderNodes)
                {
                    if (node.proceduralDraw != null)
                    {
                        scope.proceduralDraws.Add(new LiberaFxProceduralDraw(renderParent, node.proceduralDraw, scope.proceduralResources, statistics));
                    }
                    else if (node.flexiblePlane != null)
                    {
                        scope.flexiblePlanes.Add(new LiberaFxFlexiblePlaneRenderer(
                            renderParent, node.flexiblePlane,
                            scope.flexibleHistory, statistics,
                            node.generatedTextures.Length == 0 ? null : Resolve(node.generatedTextures[0].producerNodeId, node.generatedTextures[0].outputId),
                            node.generatedTextures.Length != 0 && node.generatedTextures[0].outputId == "DyeOutput",
                            node.generatedTextures.Length != 0 && node.generatedTextures[0].outputId == "FieldOutput"));
                    }
                    else if (node.debugDraw != null)
                    {
                        if (!suppressDebugDraw)
                        {
                            scope.debugDraws.Add(LiberaFxDebugDrawInstance.Create(
                                node, instanceId, renderParent.gameObject.scene));
                        }
                    }
                    else
                    {
                        scope.renderNodes.Add(LiberaFxDirectRenderNodeInstance.Create(
                            renderParent, node, statistics, Resolve));
                    }
                }
                foreach (var cpuParticle in prepared.cpuParticles)
                {
                    scope.cpuParticleRenderers.Add(
                        LiberaFxCpuParticleRenderInstance.Create(
                            renderParent, cpuParticle, statistics));
                }
                foreach (var gpu in prepared.gpuPrograms)
                {
                    var textures = new List<LibraGpuTextureBinding>(gpu.binding.textures ?? Array.Empty<LibraGpuTextureBinding>());
                    foreach (var reference in gpu.generatedResources)
                    {
                        if (!scope.TryGetGeneratedOutput(reference.producerNodeId, reference.outputId, out var texture, out var source) || !texture)
                            throw new InvalidOperationException("Generated GPU texture producer is unavailable in this scope.");
                        textures.Add(new LibraGpuTextureBinding { resourceId = reference.resourceId, texture = texture,
                            premultipliedAlpha = reference.outputId == "DyeOutput", generatedSource = source });
                    }
                    var gpuObject = new GameObject("Libera FX GPU " + gpu.nodeId);
                    gpuObject.layer = scope.scopeObject.layer;
                    gpuObject.transform.SetParent(scope.scopeObject.transform, false);
                    var runtime = gpuObject.AddComponent<LibraGpuParticleRuntime>();
                    runtime.ConfigureForSpawnable(gpu.binding.computeShader, gpu.binding.metadata,
                        gpu.binding.billboardMaterial, textures.ToArray(),
                        SeedForProgram(seed, gpu.nodeId), gpu.spawnRate, gpu.sizeScale);
                    runtime.ConfigureSpriteAnimation(gpu.spriteAnimation);
                    scope.gpuPrograms.Add(runtime);
                    scope.gpuDrawTransforms.Add(runtime, gpu);
                }
                return scope;

                Texture Resolve(string producer, string output)
                {
                    if (!scope.TryGetGeneratedOutput(producer, output, out var texture, out _))
                        throw new InvalidOperationException("Generated draw producer is unavailable in this scope.");
                    return texture;
                }
            }
            catch
            {
                scope.Dispose();
                throw;
            }
        }

        public void Initialize(NativeInstance.NativeSpawnableHostBase host, double effectTime)
        {
            if (disposed) throw new ObjectDisposedException(nameof(LiberaFxRenderScope));
            Apply(host, effectTime);
            foreach (var gpu in gpuPrograms) gpu.StartSimulation();
            if (scopeObject) scopeObject.SetActive(true);
            var localTime = LocalTime(effectTime);
            if (localTime > 0)
            {
                if (localTime > float.MaxValue)
                    throw new InvalidOperationException("GPU scope initial age exceeds Unity float range.");
                var emitterPosition = Vector3.zero;
                foreach (var fluid in fluidPrograms) fluid.Step(localTime);
                foreach (var gpu in gpuPrograms)
                    if (gpu && gpu.IsRunning)
                        gpu.StepSpawnable((float)localTime, emitterPosition);
            }
            previousLocalTime = localTime;
        }

        public void Update(NativeInstance.NativeSpawnableHostBase host, double effectTime)
        {
            if (disposed || !owner) return;
            var oldLocalTime = previousLocalTime;
            Apply(host, effectTime);
            var localTime = LocalTime(effectTime);
            var delta = localTime - oldLocalTime;
            if (delta < 0 || double.IsNaN(delta) || double.IsInfinity(delta) || delta > float.MaxValue)
                throw new InvalidOperationException("GPU scope local time delta is invalid.");
            if (delta > 0)
            {
                var emitterPosition = Vector3.zero;
                foreach (var fluid in fluidPrograms) fluid.Step(delta);
                foreach (var gpu in gpuPrograms)
                    if (gpu && gpu.IsRunning)
                        gpu.StepSpawnable((float)delta, emitterPosition);
            }
            previousLocalTime = localTime;
        }

        private void Apply(NativeInstance.NativeSpawnableHostBase host, double effectTime)
        {
            var localTime = LocalTime(effectTime);
            if (scopeObject)
            {
                scopeObject.layer = owner.gameObject.layer;
            }
            flexibleHistory.BeginUpdate();
            foreach (var producer in proceduralOrder) producer.Update(host, rootScope, containerId, instanceId, localTime);
            foreach (var draw in proceduralDraws) draw.Update(host, rootScope, containerId, instanceId);
            foreach (var plane in flexiblePlanes)
            {
                plane.Update(host, rootScope, containerId, instanceId, localTime);
            }
            foreach (var node in renderNodes)
                node.Update(host, rootScope, containerId, instanceId, localTime);
            foreach (var cpuParticleRenderer in cpuParticleRenderers)
            {
                cpuParticleRenderer.Update(host, rootScope ? null : containerId, instanceId);
            }
            foreach (var draw in debugDraws)
                draw.Update(host, rootScope, containerId, instanceId);
            foreach (var gpu in gpuPrograms)
            {
                gpu.SetEmitterTransform(ReadDrawTransform(host, rootScope, containerId, instanceId,
                    gpuDrawTransforms[gpu].emitterMatrixRowNodeBytes));
                var rows = gpuDrawTransforms[gpu].drawMatrixRowNodeBytes;
                var draw = rows != null ? host.CoordinateBasis.EffectToHostMatrix() *
                    ReadDrawTransform(host, rootScope, containerId, instanceId, rows) :
                    (scopeObject ? scopeObject.transform.localToWorldMatrix : owner.localToWorldMatrix);
                gpu.SetDrawTransform(draw, true);
            }
            foreach (var fluid in fluidPrograms)
                fluid.UpdateTransform(host.CoordinateBasis.EffectToHostMatrix() *
                    ReadDrawTransform(host, rootScope, containerId, instanceId, fluid.DrawRows));
            previousLocalTime = localTime;
        }

        internal static Matrix4x4 ReadDrawTransform(NativeInstance.NativeSpawnableHostBase host,
            bool root, byte[] container, ulong instance, byte[][] rows)
        {
            if (rows == null) return Matrix4x4.identity;
            if (host == null || rows.Length != 4)
                throw new InvalidOperationException("GPU draw Matrix binding is incomplete.");
            var matrix = Matrix4x4.identity;
            for (var row = 0; row < 4; ++row)
            {
                NativeValue value;
                NativeResult result;
                if (root) result = host.ReadRootValue(rows[row], out value);
                else result = host.ReadChildValue(container, instance,
                    rows[row], out value);
                if (result != NativeResult.Ok || value.ComponentCount != 4)
                    throw new InvalidOperationException("GPU draw Matrix row " + row + " could not be read as FloatX4: " + result);
                if (!LiberaFxUnityBindingLookup.IsFiniteFloat(value.X) ||
                    !LiberaFxUnityBindingLookup.IsFiniteFloat(value.Y) ||
                    !LiberaFxUnityBindingLookup.IsFiniteFloat(value.Z) ||
                    !LiberaFxUnityBindingLookup.IsFiniteFloat(value.W))
                    throw new InvalidOperationException("GPU draw Matrix contains a non-finite or out-of-range FloatX4 lane.");
                matrix.SetRow(row, new Vector4((float)value.X, (float)value.Y,
                    (float)value.Z, (float)value.W));
            }
            if (Mathf.Abs(matrix.m30) > 1e-5f || Mathf.Abs(matrix.m31) > 1e-5f ||
                Mathf.Abs(matrix.m32) > 1e-5f || Mathf.Abs(matrix.m33 - 1) > 1e-5f)
                throw new InvalidOperationException("GPU draw Matrix must be affine.");
            return matrix;
        }

        private double LocalTime(double effectTime)
        {
            var value = rootScope ? effectTime : effectTime - startTime;
            if (double.IsNaN(value) || double.IsInfinity(value) || value < -1e-8)
                throw new InvalidOperationException("Render scope local time is invalid.");
            return Math.Max(0, value);
        }

        internal bool TryGetRenderMatrix(string nodeId, out Matrix4x4 matrix)
        {
            foreach (var node in renderNodes)
            {
                if (node.nodeId == nodeId)
                {
                    matrix = node.worldMatrix;
                    return true;
                }
            }
            matrix = default;
            return false;
        }

        internal ulong ScopeInstanceId => instanceId;

        internal bool TryGetFluidOutput(string nodeId, out RenderTexture texture, bool fields = false, bool straight = false)
        {
            foreach (var fluid in fluidPrograms)
                if (fluid.NodeId == nodeId) { texture = fields ? fluid.FieldOutput : straight ? fluid.ColorOutput : fluid.Output; return texture; }
            texture = null;
            return false;
        }

        private bool TryGetGeneratedOutput(string nodeId, string output, out RenderTexture texture, out ILibraGpuFieldSource source)
        {
            texture = null; source = null;
            foreach (var fluid in fluidPrograms)
                if (fluid.NodeId == nodeId)
                {
                    source = fluid;
                    if (output == "DyeOutput") texture = fluid.Output;
                    else if (output == "ColorOutput") texture = fluid.ColorOutput;
                    else if (output == "FieldOutput" || output == "VelocityOutput" || output == "DensityOutput") texture = fluid.FieldOutput;
                    return texture;
                }
            return false;
        }

        internal bool TryGetFluidStats(string nodeId, out LibraFluid2DFrameStats stats)
        {
            foreach (var fluid in fluidPrograms)
                if (fluid.NodeId == nodeId)
                {
                    stats = new LibraFluid2DFrameStats
                    {
                        Substeps = fluid.LastSubsteps,
                        DiscardedDelta = fluid.DiscardedDelta
                    };
                    return true;
                }
            stats = default;
            return false;
        }

        public void ClearPreviewDebugDraw()
        {
            foreach (var draw in debugDraws) draw.Dispose();
            debugDraws.Clear();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (scopeObject) scopeObject.SetActive(false);
            foreach (var gpu in gpuPrograms)
            {
                if (!gpu) continue;
                gpu.StopSimulation();
                gpu.Dispose();
            }
            foreach (var node in renderNodes) node.Dispose();
            foreach (var draw in proceduralDraws) draw.Dispose();
            proceduralDraws.Clear();
            foreach (var producer in proceduralResources.Values) producer.Dispose();
            proceduralResources.Clear();
            proceduralOrder.Clear();
            foreach (var plane in flexiblePlanes)
            {
                plane.Dispose();
            }
            flexiblePlanes.Clear();
            foreach (var fluid in fluidPrograms) fluid.Dispose();
            fluidPrograms.Clear();
            flexibleHistory.Reset();
            foreach (var cpuParticleRenderer in cpuParticleRenderers)
            {
                cpuParticleRenderer.Dispose();
            }
            foreach (var draw in debugDraws) draw.Dispose();
            debugDraws.Clear();
            renderNodes.Clear();
            cpuParticleRenderers.Clear();
            gpuPrograms.Clear();
            gpuDrawTransforms.Clear();
            if (scopeObject) DestroyOwned(scopeObject);
            scopeObject = null;
        }

        private static uint SeedForProgram(ulong seed, string nodeId)
        {
            var hash = 2166136261u;
            for (var index = 0; index < nodeId.Length; ++index)
                hash = (hash ^ char.ToLowerInvariant(nodeId[index])) * 16777619u;
            return (uint)seed ^ (uint)(seed >> 32) ^ hash;
        }

        private static void DestroyOwned(UnityEngine.Object value)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }
    }

}
