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
        private readonly Transform parent;
        private readonly LiberaFxRenderStatisticsCounter statistics;
        private LiberaFxRenderSubmission submission;
        private Mesh mesh;
        private Material material;
        private bool ownsMaterial;
        private bool disposed;
        private readonly NativeCpuParticleSnapshot[] particleBatch = new NativeCpuParticleSnapshot[256];
        private readonly NativeCpuParticleTiming[] timingBatch = new NativeCpuParticleTiming[256];

        private LiberaFxCpuParticleRenderInstance(Transform parent,
            LiberaFxPreparedCpuParticleRenderer definition,
            LiberaFxRenderStatisticsCounter statistics)
        {
            this.parent = parent;
            this.definition = definition;
            this.statistics = statistics;
            centers.Capacity = 256;
            uvs.Capacity = 256;
            offsets.Capacity = 256;
            colors.Capacity = 256;
            indices.Capacity = 384;
        }

        public static LiberaFxCpuParticleRenderInstance Create(
            Transform parent, LiberaFxPreparedCpuParticleRenderer definition,
            LiberaFxRenderStatisticsCounter statistics,
            IReadOnlyDictionary<string, LiberaFxProceduralResource> proceduralResources = null)
        {
            var instance = new LiberaFxCpuParticleRenderInstance(parent, definition, statistics);
            try
            {
                if (!SystemInfo.supports32bitsIndexBuffer)
                    throw new NotSupportedException(
                        "CPU particles require 32-bit mesh indices.");
                instance.mesh = new Mesh
                {
                    name = "Libera FX CPU particle batch",
                    hideFlags = HideFlags.DontSave,
                    indexFormat = IndexFormat.UInt32
                };
                instance.mesh.MarkDynamic();
                LiberaFxDrawPropertyState propertyState = null;
                if (definition.customMaterial != null)
                {
                    instance.material = definition.customMaterial;
                    instance.ownsMaterial = false;
                    var propCount = (definition.texturePropertyIds?.Length ?? 0) +
                        (definition.materialParameterPropertyIds?.Length ?? 0);
                    if (propCount > 0)
                    {
                        propertyState = new LiberaFxDrawPropertyState(propCount);
                        for (var i = 0; i < (definition.texturePropertyIds?.Length ?? 0); ++i)
                            propertyState.SetTexture(definition.texturePropertyIds[i], definition.textureValues[i]);
                        for (var i = 0; i < (definition.materialParameterPropertyIds?.Length ?? 0); ++i)
                        {
                            var val = definition.materialParameterDefaults[i];
                            if (val.Length == 1)
                                propertyState.SetFloat(definition.materialParameterPropertyIds[i], val[0]);
                            else
                                propertyState.SetVector(definition.materialParameterPropertyIds[i],
                                    new Vector4(val.Length > 0 ? val[0] : 0f, val.Length > 1 ? val[1] : 0f,
                                        val.Length > 2 ? val[2] : 0f, val.Length > 3 ? val[3] : 0f));
                        }
                    }
                }
                else
                {
                    var shader = Resources.Load<Shader>("LiberaFxCpuParticle");
                    if (!shader || !shader.isSupported)
                        throw new NotSupportedException(
                            "CPU particles require the Libera FX billboard shader.");
                    var activeTexture = definition.texture;
                    var additive = definition.additive;
                    var proceduralId = !string.IsNullOrEmpty(definition.proceduralMaterial)
                        ? definition.proceduralMaterial : definition.proceduralTexture;
                    if (!string.IsNullOrEmpty(proceduralId))
                    {
                        if (proceduralResources == null ||
                            !proceduralResources.TryGetValue(proceduralId, out var producer) || !producer.texture)
                            throw new InvalidOperationException("CPU particle procedural material/texture is outside this resource scope.");
                        activeTexture = producer.texture;
                        if (!string.IsNullOrEmpty(definition.proceduralMaterial))
                            additive = producer.definition.recipe.blend == "additive";
                    }
                    instance.material = new Material(shader)
                    {
                        hideFlags = HideFlags.DontSave,
                        mainTexture = activeTexture
                    };
                    instance.ownsMaterial = true;
                    instance.material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                    instance.material.SetInt("_DstBlend", (int)(additive
                        ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
                }
                instance.submission = new LiberaFxRenderSubmission(propertyState, statistics);
                instance.submission.owner = parent;
                instance.submission.Configure(instance.mesh, instance.material, 0,
                    parent.gameObject.layer, statistics);
                return instance;
            }
            catch
            {
                instance.Dispose();
                throw;
            }
        }

        public void Update(NativeInstance.NativeSpawnableHostBase host,
            byte[] containerId = null, ulong instanceId = 0)
        {
            if (disposed || !parent)
            {
                throw new ObjectDisposedException(nameof(LiberaFxCpuParticleRenderInstance));
            }
            var result = host.CpuParticleCount(containerId, instanceId, definition.nodeIdBytes, out var count);
            if (result != NativeResult.Ok)
                throw new InvalidOperationException("Could not read CPU particle count for " + definition.nodeId + ": " + result);
            if (count > 1000000)
                throw new InvalidOperationException("CPU particle count exceeds the native system capacity limit.");
            if (count == 0 || definition.spriteAnimation?.partCount==0)
            {
                submission.enabled = false;
                return;
            }

            centers.Clear();
            uvs.Clear();
            offsets.Clear();
            colors.Clear();
            indices.Clear();
            var localToWorld = parent.localToWorldMatrix;
            if (definition.drawRows != null)
                localToWorld = host.CoordinateBasis.EffectToHostMatrix() *
                    LiberaFxRenderScope.ReadDrawTransform(host, containerId == null, containerId, instanceId, definition.drawRows);
            if (localToWorld.determinant == 0)
                throw new InvalidOperationException("CPU particle batch cannot use a singular owner transform.");
            var worldToLocal = localToWorld.inverse;
            var ownerWidth = localToWorld.MultiplyVector(Vector3.right).magnitude;
            var ownerHeight = localToWorld.MultiplyVector(Vector3.up).magnitude;
            var localRadiusFactor = worldToLocal.MultiplyVector(Vector3.right).magnitude +
                worldToLocal.MultiplyVector(Vector3.up).magnitude +
                worldToLocal.MultiplyVector(Vector3.forward).magnitude;
            var radius = 0.0;
            // Part-major submission preserves the Sprite editor's layer order.
            for (var part=0;part<(definition.spriteAnimation?.partCount ?? 1);++part)
            for (var index = 0u; index < count; ++index)
            {
                if (index % 256 == 0)
                {
                    result = host.CpuParticleRead(containerId, instanceId, definition.nodeIdBytes, index,
                        particleBatch, definition.flipbook != null || definition.spriteAnimation!=null ? timingBatch : null, out var written);
                    if (result != NativeResult.Ok || written != Math.Min(256u,count-index))
                        throw new InvalidOperationException("Could not read CPU particle batch: " + result);
                }
                var snapshot = particleBatch[index % 256];
                if (snapshot.Id == 0)
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
                float u0=0, v0=0, u1=1, v1=1;
                var sprite=default(ParticleSpriteSample);
                if (definition.spriteAnimation!=null)
                {
                    sprite=definition.spriteAnimation.Sample(part,timingBatch[index%256].Age);
                    u0=(float)sprite.u; v0=(float)(1-sprite.v-sprite.height);
                    u1=u0+(float)sprite.width; v1=v0+(float)sprite.height;
                    radius=Math.Max(radius,(2*Math.Abs(x*sprite.x)+2*Math.Abs(y*sprite.y)+
                        Math.Sqrt(x*x*sprite.scaleX*sprite.scaleX+y*y*sprite.scaleY*sprite.scaleY))*localRadiusFactor);
                }
                else if (definition.flipbook != null)
                {
                    var atlas = definition.flipbook;
                    var timing = timingBatch[index % 256];
                    var frame = atlas.Frame(timing.Age,timing.Life);
                    u0 = (float)(frame % atlas.columns)/atlas.columns;
                    v0 = 1f-(float)(frame/atlas.columns+1)/atlas.rows;
                    u1 = u0+1f/atlas.columns; v1=v0+1f/atlas.rows;
                }
                var cam = definition.billboardCamera ? definition.billboardCamera : Camera.main;
                var invWorld = localToWorld.inverse;
                var camRight = cam ? invWorld.MultiplyVector(cam.transform.right).normalized : Vector3.right;
                var camUp = cam ? invWorld.MultiplyVector(cam.transform.up).normalized : Vector3.up;
                void Vertex(float u,float v,float ox,float oy)
                {
                    if (definition.spriteAnimation!=null)
                    {
                        var px=ox*sprite.scaleX; var py=oy*sprite.scaleY;
                        var c=Math.Cos(sprite.rotation); var s=Math.Sin(sprite.rotation);
                        ox=ToFloat(px*c-py*s+2*x*sprite.x); oy=ToFloat(px*s+py*c+2*y*sprite.y);
                    }
                    var pos = definition.customMaterial != null
                        ? center + camRight * ox + camUp * oy
                        : center;
                    AddVertex(pos,color,u,v,ox,oy);
                }
                Vertex(u0,v0,-x,-y); Vertex(u1,v0,x,-y);
                Vertex(u1,v1,x,y); Vertex(u0,v1,-x,y);
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
            submission.layer = parent.gameObject.layer;
            submission.SetMatrix(localToWorld);
            submission.enabled = true;
            statistics?.GeometryUpload(centers.Count);
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
            if (submission != null) submission.Dispose();
            submission = null;
            if (mesh) DestroyOwned(mesh);
            if (ownsMaterial && material) DestroyOwned(material);
        }

        private static void DestroyOwned(UnityEngine.Object value)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }
    }
}
