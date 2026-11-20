using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiberaFx.Unity
{
    /// <summary>Cumulative, effect-local observations of the direct render adapter.</summary>
    [Serializable]
    public struct LiberaFxRenderStatistics
    {
        public int activeSubmissions { get; internal set; }
        public int drawCalls { get; internal set; }
        public int instancedDrawCalls { get; internal set; }
        public int instancedInstances { get; internal set; }
        public int geometryUploads { get; internal set; }
        public long uploadedVertices { get; internal set; }
    }

    internal sealed class LiberaFxRenderStatisticsCounter
    {
        internal LiberaFxRenderStatistics value;
        private long reservedFluidBytes;
        internal void ReserveFluid(long bytes)
        {
            if (bytes < 0 || bytes > 256L * 1024 * 1024 - reservedFluidBytes)
                throw new InvalidOperationException("Active fluid storage exceeds the 256 MiB effect budget.");
            reservedFluidBytes += bytes;
        }
        internal void ReleaseFluid(long bytes) { reservedFluidBytes -= bytes; }

        internal void Reset()
        {
            value = default;
        }

        internal void Register()
        {
            value.activeSubmissions++;
        }

        internal void Unregister()
        {
            value.activeSubmissions = Math.Max(0, value.activeSubmissions - 1);
        }

        internal void DirectDraw()
        {
            value.drawCalls++;
        }

        internal void InstancedDraw()
        {
            value.drawCalls++;
            value.instancedDrawCalls++;
        }

        internal void InstancedInstance()
        {
            value.instancedInstances++;
        }

        internal void GeometryUpload(int vertexCount)
        {
            value.geometryUploads++;
            value.uploadedVertices += vertexCount;
        }
    }

    internal sealed class LiberaFxDrawPropertyState
    {
        private struct Entry
        {
            internal int id;
            internal byte kind;
            internal Vector4 vector;
            internal Texture texture;
        }

        private readonly Entry[] entries;
        private int count;

        internal MaterialPropertyBlock block { get; }

        internal LiberaFxDrawPropertyState(int capacity)
        {
            entries = new Entry[Math.Max(1, capacity)];
            block = new MaterialPropertyBlock();
        }

        internal void SetFloat(int id, float value)
        {
            var entry = GetOrCreate(id);
            entry.kind = 1;
            entry.vector = new Vector4(value, 0, 0, 0);
            entry.texture = null;
            entries[Find(id)] = entry;
            block.SetFloat(id, value);
        }

        internal void SetVector(int id, Vector4 value)
        {
            var entry = GetOrCreate(id);
            entry.kind = 2;
            entry.vector = value;
            entry.texture = null;
            entries[Find(id)] = entry;
            block.SetVector(id, value);
        }

        internal void SetTexture(int id, Texture value)
        {
            var entry = GetOrCreate(id);
            entry.kind = 3;
            entry.vector = default;
            entry.texture = value;
            entries[Find(id)] = entry;
            block.SetTexture(id, value);
        }

        internal bool Matches(LiberaFxDrawPropertyState other)
        {
            if (other == null || count != other.count)
            {
                return false;
            }
            for (var index = 0; index < count; index++)
            {
                var entry = entries[index];
                var otherIndex = other.Find(entry.id);
                if (otherIndex < 0)
                {
                    return false;
                }
                var candidate = other.entries[otherIndex];
                if (entry.kind != candidate.kind || !entry.vector.Equals(candidate.vector) ||
                    !ReferenceEquals(entry.texture, candidate.texture))
                {
                    return false;
                }
            }
            return true;
        }

        private Entry GetOrCreate(int id)
        {
            var index = Find(id);
            if (index >= 0)
            {
                return entries[index];
            }
            if (count == entries.Length)
            {
                throw new InvalidOperationException("Render property state exceeded its prepared capacity.");
            }
            entries[count++] = new Entry { id = id };
            return entries[count - 1];
        }

        private int Find(int id)
        {
            for (var index = 0; index < count; index++)
            {
                if (entries[index].id == id)
                {
                    return index;
                }
            }
            return -1;
        }
    }

    /// <summary>
    /// A camera-filtered direct draw. Compatible opaque submissions may share
    /// RenderMeshInstanced; transparent submissions remain individual draws.
    /// </summary>
#if UNITY_6000_5_OR_NEWER
    [global::Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    internal sealed class LiberaFxRenderSubmission : IDisposable
    {
        // RenderMeshInstanced can need both object-to-world and world-to-object.
        private const int MaxInstances = 511;
        private static readonly List<LiberaFxRenderSubmission> submissions =
            new List<LiberaFxRenderSubmission>();
        private static readonly List<LiberaFxRenderOrdering.Entry> ordered =
            new List<LiberaFxRenderOrdering.Entry>();
        private static ulong nextStableId;
        private static readonly Matrix4x4[] instanceMatrices = new Matrix4x4[MaxInstances];
        private static readonly int[] instanceIndices = new int[MaxInstances];
        private static bool[] visited = Array.Empty<bool>();
        private static bool callbacksRegistered;
        private static bool scriptableRenderPipeline;
        private static RenderPipelineAsset activePipeline;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            RemoveCallbacks();
            ordered.Clear();
            Array.Clear(visited, 0, visited.Length);
            EnsureCallbacks();
        }

        private readonly LiberaFxRenderStatisticsCounter statistics;
        private readonly LiberaFxDrawPropertyState propertyState;
        private bool shaderAllowsInstancing;
        private bool matrixAllowsInstancing;
        private bool disposed;
        private bool registered;

        internal Mesh mesh;
        internal Material material;
        internal Matrix4x4 matrix { get; private set; }
        internal int layer;
        internal int submesh;
        internal int? drawOrder;
        internal ulong stableId { get; private set; }
        internal bool enabled;
        internal Bounds worldBounds;
        internal Transform owner;
        internal bool useMeshRendererLighting;

        internal LiberaFxRenderSubmission(LiberaFxDrawPropertyState state,
            LiberaFxRenderStatisticsCounter statistics = null)
        {
            propertyState = state;
            this.statistics = statistics;
        }

        internal MaterialPropertyBlock properties => propertyState?.block;

        internal void Configure(Mesh sourceMesh, Material sourceMaterial, int sourceSubmesh,
            int sourceLayer, LiberaFxRenderStatisticsCounter sourceStatistics = null)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(LiberaFxRenderSubmission));
            }
            mesh = sourceMesh;
            material = sourceMaterial;
            submesh = sourceSubmesh;
            layer = sourceLayer;
            var disableBatching = sourceMaterial
                ? sourceMaterial.GetTag("DisableBatching", false, "False")
                : "True";
            shaderAllowsInstancing = disableBatching != "True" && disableBatching != "LODFading";
            if (sourceStatistics != null && !ReferenceEquals(statistics, sourceStatistics))
            {
                throw new InvalidOperationException("Render statistics owner cannot change after submission creation.");
            }
            if (!registered)
            {
                registered = true;
                stableId = ++nextStableId;
                submissions.Add(this);
                statistics?.Register();
                EnsureCallbacks();
            }
        }

        internal void SetMatrix(Matrix4x4 value)
        {
            matrix = value;
            // A mixed-parity draw cannot reliably preserve per-renderer face culling.
            var determinant = value.determinant;
            matrixAllowsInstancing = determinant > 0f && determinant < float.PositiveInfinity;
            if (mesh)
            {
                worldBounds = TransformBounds(mesh.bounds, value);
            }
        }

        internal bool CanInstanceWith(LiberaFxRenderSubmission other)
        {
            return other != null && enabled && other.enabled && mesh && material &&
                matrixAllowsInstancing && other.matrixAllowsInstancing &&
                !drawOrder.HasValue && !other.drawOrder.HasValue &&
                material.renderQueue <= (int)RenderQueue.AlphaTest &&
                other.mesh == mesh && other.material == material && other.submesh == submesh &&
                other.layer == layer && other.useMeshRendererLighting == useMeshRendererLighting &&
                material.enableInstancing && propertyState != null &&
                propertyState.Matches(other.propertyState);
        }

        internal bool SupportsInstancing()
        {
            return enabled && mesh && material && material.enableInstancing &&
                shaderAllowsInstancing && matrixAllowsInstancing && !drawOrder.HasValue &&
                material.renderQueue <= (int)RenderQueue.AlphaTest &&
                SystemInfo.supportsInstancing;
        }

        private static void EnsureCallbacks()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline;
            var usesScriptablePipeline = pipeline != null;
            if (callbacksRegistered &&
                (scriptableRenderPipeline != usesScriptablePipeline ||
                    !ReferenceEquals(activePipeline, pipeline)))
            {
                RemoveCallbacks();
            }
            if (callbacksRegistered || submissions.Count == 0)
            {
                return;
            }
            scriptableRenderPipeline = usesScriptablePipeline;
            activePipeline = pipeline;
            if (scriptableRenderPipeline)
            {
                RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            }
            else
            {
                Camera.onPreCull += OnCameraPreCull;
            }
            callbacksRegistered = true;
            RenderPipelineManager.activeRenderPipelineTypeChanged += EnsureCallbacks;
        }

        private static void RemoveCallbacks()
        {
            if (!callbacksRegistered)
            {
                return;
            }
            if (scriptableRenderPipeline)
            {
                RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            }
            else
            {
                Camera.onPreCull -= OnCameraPreCull;
            }
            callbacksRegistered = false;
            activePipeline = null;
            RenderPipelineManager.activeRenderPipelineTypeChanged -= EnsureCallbacks;
        }

        private static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                DrawForCamera(camera);
            }
        }

        private static void OnCameraPreCull(Camera camera)
        {
            if (GraphicsSettings.currentRenderPipeline == null)
            {
                DrawForCamera(camera);
            }
        }

        private static void DrawForCamera(Camera camera)
        {
            if (!camera || submissions.Count == 0)
            {
                return;
            }
            EnsureCallbacks();
            foreach (var submission in submissions)
            {
                if (submission.enabled && submission.mesh && submission.material && IsVisibleTo(submission, camera))
                {
                    ordered.Add(new LiberaFxRenderOrdering.Entry(submission, camera));
                }
            }
            try
            {
                LiberaFxRenderOrdering.Sort(ordered);
                if (visited.Length < ordered.Count)
                {
                    Array.Resize(ref visited, ordered.Count);
                }
                Array.Clear(visited, 0, ordered.Count);
                for (var index = 0; index < ordered.Count; index++)
                {
                    if (visited[index])
                    {
                        continue;
                    }
                    var first = ordered[index].submission;
                    if (first.SupportsInstancing())
                    {
                        DrawInstancedGroup(camera, first, index);
                    }
                    else
                    {
                        DrawDirect(camera, first);
                    }
                }
            }
            finally
            {
                ordered.Clear();
            }
        }

        private static bool IsVisibleTo(LiberaFxRenderSubmission submission, Camera camera)
        {
            if ((camera.cullingMask & (1 << submission.layer)) == 0)
            {
                return false;
            }
            if (!ReferenceEquals(submission.owner, null) &&
                (!submission.owner || !submission.owner.gameObject.activeInHierarchy))
            {
                return false;
            }
#if UNITY_EDITOR
            if (submission.owner)
            {
                var scene = submission.owner.gameObject.scene;
                if (scene.IsValid())
                {
                    if (camera.overrideSceneCullingMask != 0)
                    {
                        return (camera.overrideSceneCullingMask &
                            UnityEditor.SceneManagement.EditorSceneManager.GetSceneCullingMask(scene)) != 0;
                    }
                    var cameraScene = camera.scene;
                    if (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(scene) ||
                        (cameraScene.IsValid() && UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(cameraScene)))
                    {
                        return cameraScene == scene;
                    }
                }
            }
#endif
            return true;
        }

        private static void DrawInstancedGroup(Camera camera,
            LiberaFxRenderSubmission first, int firstIndex)
        {
            var count = 0;
            for (var index = firstIndex; index < ordered.Count && count < MaxInstances; index++)
            {
                var entry = ordered[index];
                if (entry.hasAuthoredOrder || entry.renderQueue != ordered[firstIndex].renderQueue)
                {
                    break;
                }
                var candidate = entry.submission;
                if (visited[index] || !first.CanInstanceWith(candidate))
                {
                    continue;
                }
                instanceMatrices[count] = candidate.matrix;
                instanceIndices[count] = index;
                count++;
            }
            if (count < 2)
            {
                DrawDirect(camera, first);
                return;
            }

            for (var index = 0; index < count; index++)
            {
                visited[instanceIndices[index]] = true;
            }
            var combinedBounds = ordered[instanceIndices[0]].submission.worldBounds;
            for (var index = 1; index < count; index++)
            {
                combinedBounds.Encapsulate(ordered[instanceIndices[index]].submission.worldBounds);
            }
            if (scriptableRenderPipeline)
            {
                var parameters = CreateParameters(first, camera, combinedBounds);
                Graphics.RenderMeshInstanced(parameters, first.mesh, first.submesh,
                    instanceMatrices, count, 0);
            }
            else
            {
                Graphics.DrawMeshInstanced(first.mesh, first.submesh, first.material,
                    instanceMatrices, count, first.properties, ShadowMode(first),
                    first.useMeshRendererLighting, first.layer, camera, ProbeMode(first));
            }
            for (var index = 0; index < count; index++)
            {
                var candidate = ordered[instanceIndices[index]].submission;
                candidate.statistics?.InstancedInstance();
                if (!HasEarlierStatisticsOwner(index, instanceIndices, candidate.statistics))
                {
                    candidate.statistics?.InstancedDraw();
                }
            }
        }

        private static bool HasEarlierStatisticsOwner(int current,
            int[] indices, LiberaFxRenderStatisticsCounter statistics)
        {
            if (statistics == null)
            {
                return true;
            }
            for (var index = 0; index < current; index++)
            {
                if (ReferenceEquals(ordered[indices[index]].submission.statistics, statistics))
                {
                    return true;
                }
            }
            return false;
        }

        private static void DrawDirect(Camera camera, LiberaFxRenderSubmission submission)
        {
            if (scriptableRenderPipeline)
            {
                var parameters = CreateParameters(submission, camera, submission.worldBounds);
                Graphics.RenderMesh(parameters, submission.mesh, submission.submesh,
                    submission.matrix, null);
            }
            else
            {
                Graphics.DrawMesh(submission.mesh, submission.matrix, submission.material,
                    submission.layer, camera, submission.submesh, submission.properties,
                    ShadowMode(submission), submission.useMeshRendererLighting, null, ProbeMode(submission));
            }
            submission.statistics?.DirectDraw();
        }

        private static RenderParams CreateParameters(LiberaFxRenderSubmission submission,
            Camera camera, Bounds bounds)
        {
            return new RenderParams(submission.material)
            {
                matProps = submission.properties,
                layer = submission.layer,
                renderingLayerMask = 1,
                camera = camera,
                worldBounds = bounds,
                shadowCastingMode = ShadowMode(submission),
                receiveShadows = submission.useMeshRendererLighting,
                lightProbeUsage = ProbeMode(submission),
                reflectionProbeUsage = submission.useMeshRendererLighting
                    ? ReflectionProbeUsage.BlendProbes : ReflectionProbeUsage.Off
            };
        }

        private static ShadowCastingMode ShadowMode(LiberaFxRenderSubmission submission) =>
            submission.useMeshRendererLighting ? ShadowCastingMode.On : ShadowCastingMode.Off;

        private static LightProbeUsage ProbeMode(LiberaFxRenderSubmission submission) =>
            submission.useMeshRendererLighting ? LightProbeUsage.BlendProbes : LightProbeUsage.Off;

        private static Bounds TransformBounds(Bounds bounds, Matrix4x4 matrix)
        {
            var center = matrix.MultiplyPoint3x4(bounds.center);
            var extents = bounds.extents;
            var worldExtents = Abs(matrix.MultiplyVector(new Vector3(extents.x, 0, 0))) +
                Abs(matrix.MultiplyVector(new Vector3(0, extents.y, 0))) +
                Abs(matrix.MultiplyVector(new Vector3(0, 0, extents.z)));
            return new Bounds(center, worldExtents * 2);
        }

        private static Vector3 Abs(Vector3 value)
        {
            return new Vector3(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            enabled = false;
            if (registered)
            {
                registered = false;
                submissions.Remove(this);
                statistics?.Unregister();
                if (submissions.Count == 0)
                {
                    RemoveCallbacks();
                }
            }
            mesh = null;
            material = null;
            owner = null;
        }
    }
}
