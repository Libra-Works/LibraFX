using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiberaFx.Unity
{
    internal sealed class LiberaFxCpuParticleRenderInstance : IDisposable
    {
        private readonly LiberaFxPreparedCpuParticleRenderer definition;
        private readonly List<Vector3> centers = new List<Vector3>();
        private readonly List<Vector2> uvs = new List<Vector2>();
        private readonly List<Vector2> offsets = new List<Vector2>();
        private readonly List<Color> colors = new List<Color>();
        private readonly List<int> indices = new List<int>();
        private GameObject batch;
        private MeshRenderer renderer;
        private Mesh mesh;
        private Material material;
        private bool disposed;

        private LiberaFxCpuParticleRenderInstance(LiberaFxPreparedCpuParticleRenderer definition)
            => this.definition = definition;

        public static LiberaFxCpuParticleRenderInstance Create(
            Transform parent, LiberaFxPreparedCpuParticleRenderer definition)
        {
            var instance = new LiberaFxCpuParticleRenderInstance(definition);
            try
            {
                var shader = Resources.Load<Shader>("LiberaFxCpuParticle");
                if (!shader || !shader.isSupported || !SystemInfo.supports32bitsIndexBuffer)
                    throw new NotSupportedException(
                        "CPU particles require the Libera FX billboard shader and 32-bit mesh indices.");
                instance.mesh = new Mesh
                {
                    name = "Libera FX CPU particle batch",
                    hideFlags = HideFlags.DontSave,
                    indexFormat = IndexFormat.UInt32
                };
                instance.mesh.MarkDynamic();
                instance.material = new Material(shader)
                {
                    hideFlags = HideFlags.DontSave,
                    mainTexture = definition.texture
                };
                instance.material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                instance.material.SetInt("_DstBlend", (int)(definition.additive
                    ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
                instance.batch = new GameObject("Libera FX CPU Particles " + definition.nodeId);
                instance.batch.layer = parent.gameObject.layer;
                instance.batch.transform.SetParent(parent, false);
                instance.batch.AddComponent<MeshFilter>().sharedMesh = instance.mesh;
                instance.renderer = instance.batch.AddComponent<MeshRenderer>();
                instance.renderer.sharedMaterial = instance.material;
                instance.renderer.shadowCastingMode = ShadowCastingMode.Off;
                instance.renderer.receiveShadows = false;
                instance.renderer.enabled = false;
                return instance;
            }
            catch
            {
                instance.Dispose();
                throw;
            }
        }

        public void Update(NativeInstance.NativeSpawnableHostBase host)
        {
            if (disposed || !renderer) throw new ObjectDisposedException(nameof(LiberaFxCpuParticleRenderInstance));
            var result = host.CpuParticleCount(definition.nodeIdBytes, out var count);
            if (result != NativeResult.Ok)
                throw new InvalidOperationException("Could not read CPU particle count for " + definition.nodeId + ": " + result);
            if (count > 1000000)
                throw new InvalidOperationException("CPU particle count exceeds the native system capacity limit.");
            if (count == 0)
            {
                renderer.enabled = false;
                return;
            }

            centers.Clear();
            uvs.Clear();
            offsets.Clear();
            colors.Clear();
            indices.Clear();
            var localToWorld = batch.transform.localToWorldMatrix;
            var worldToLocal = batch.transform.worldToLocalMatrix;
            if (localToWorld.determinant == 0)
                throw new InvalidOperationException("CPU particle batch cannot use a singular owner transform.");
            var ownerWidth = localToWorld.MultiplyVector(Vector3.right).magnitude;
            var ownerHeight = localToWorld.MultiplyVector(Vector3.up).magnitude;
            var localRadiusFactor = worldToLocal.MultiplyVector(Vector3.right).magnitude +
                worldToLocal.MultiplyVector(Vector3.up).magnitude +
                worldToLocal.MultiplyVector(Vector3.forward).magnitude;
            var radius = 0.0;
            for (var index = 0u; index < count; ++index)
            {
                result = host.CpuParticleAt(definition.nodeIdBytes, index, out var snapshot);
                if (result != NativeResult.Ok || snapshot.Id == 0)
                    throw new InvalidOperationException("Could not read a live CPU particle snapshot: " + result);
                var drawScale = definition.drawScale;
                var center = new Vector3(
                    ToFloat(snapshot.PositionX * drawScale),
                    ToFloat(snapshot.PositionY * drawScale),
                    ToFloat(snapshot.PositionZ * drawScale));
                var x = ToFloat(0.5 * snapshot.SizeX * drawScale * ownerWidth);
                var y = ToFloat(0.5 * snapshot.SizeY * drawScale * ownerHeight);
                var color = new Color(ToFloat(snapshot.ColorR), ToFloat(snapshot.ColorG),
                    ToFloat(snapshot.ColorB), ToFloat(snapshot.ColorA));
                radius = Math.Max(radius, Math.Sqrt((double)x * x + (double)y * y) * localRadiusFactor);
                var first = centers.Count;
                AddVertex(center, color, 0, 0, -x, -y);
                AddVertex(center, color, 1, 0, x, -y);
                AddVertex(center, color, 1, 1, x, y);
                AddVertex(center, color, 0, 1, -x, y);
                indices.Add(first);
                indices.Add(first + 2);
                indices.Add(first + 1);
                indices.Add(first);
                indices.Add(first + 3);
                indices.Add(first + 2);
            }
            mesh.SetVertices(centers);
            mesh.SetUVs(0, uvs);
            mesh.SetUVs(1, offsets);
            mesh.SetColors(colors);
            mesh.SetTriangles(indices, 0, true);
            var bounds = mesh.bounds;
            bounds.Expand(ToFloat(2 * radius));
            mesh.bounds = bounds;
            renderer.enabled = true;
        }

        private void AddVertex(Vector3 center, Color color, float u, float v, float x, float y)
        {
            centers.Add(center);
            colors.Add(color);
            uvs.Add(new Vector2(u, v));
            offsets.Add(new Vector2(x, y));
        }

        private static float ToFloat(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) ||
                value < -float.MaxValue || value > float.MaxValue)
                throw new InvalidOperationException(
                    "CPU particle snapshot cannot be represented as finite Unity floats.");
            return (float)value;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (renderer) renderer.enabled = false;
            if (batch) DestroyOwned(batch);
            if (mesh) DestroyOwned(mesh);
            if (material) DestroyOwned(material);
        }

        private static void DestroyOwned(UnityEngine.Object value)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }
    }
}
