using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiberaFx.Unity
{
    internal sealed class LiberaFxRenderScope : IDisposable
    {
        private readonly bool rootScope;
        private readonly byte[] containerId;
        private readonly ulong instanceId;
        private readonly double startTime;
        private readonly List<LiberaFxRenderNodeInstance> renderNodes = new List<LiberaFxRenderNodeInstance>();
        private readonly List<LibraGpuParticleRuntime> gpuPrograms = new List<LibraGpuParticleRuntime>();
        private GameObject scopeObject;
        private double previousLocalTime;
        private bool disposed;

        private LiberaFxRenderScope(bool isRoot, byte[] id, ulong instance, double time)
        {
            rootScope = isRoot;
            containerId = id;
            instanceId = instance;
            startTime = time;
            previousLocalTime = 0;
        }

        public static LiberaFxRenderScope CreatePending(Transform owner,
            LiberaFxPreparedScope prepared, bool isRoot, byte[] containerId,
            ulong instanceId, double startTime, ulong seed)
        {
            var scope = new LiberaFxRenderScope(isRoot, containerId, instanceId, startTime);
            try
            {
                if (prepared.renderNodes.Length == 0 && prepared.gpuPrograms.Length == 0)
                    return scope;

                scope.scopeObject = new GameObject(isRoot
                    ? "Libera FX Root Render Scope"
                    : "Libera FX Spawnable " + prepared.templateId + " #" + instanceId);
                scope.scopeObject.transform.SetParent(owner, false);
                scope.scopeObject.SetActive(false);
                foreach (var node in prepared.renderNodes)
                    scope.renderNodes.Add(LiberaFxRenderNodeInstance.Create(scope.scopeObject.transform, node));
                foreach (var gpu in prepared.gpuPrograms)
                {
                    var gpuObject = new GameObject("Libera FX GPU " + gpu.nodeId);
                    gpuObject.transform.SetParent(scope.scopeObject.transform, false);
                    var runtime = gpuObject.AddComponent<LibraGpuParticleRuntime>();
                    runtime.ConfigureForSpawnable(gpu.binding.computeShader, gpu.binding.metadata,
                        gpu.binding.billboardMaterial, gpu.binding.textures,
                        SeedForProgram(seed, gpu.nodeId), gpu.binding.spawnRate, gpu.sizeScale);
                    scope.gpuPrograms.Add(runtime);
                }
                return scope;
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
                var emitterPosition = scopeObject ? scopeObject.transform.position : Vector3.zero;
                foreach (var gpu in gpuPrograms)
                    if (gpu && gpu.IsRunning)
                        gpu.StepSpawnable((float)localTime, emitterPosition);
            }
            previousLocalTime = localTime;
        }

        public void Update(NativeInstance.NativeSpawnableHostBase host, double effectTime)
        {
            if (disposed || !scopeObject) return;
            var oldLocalTime = previousLocalTime;
            Apply(host, effectTime);
            var localTime = LocalTime(effectTime);
            var delta = localTime - oldLocalTime;
            if (delta < 0 || double.IsNaN(delta) || double.IsInfinity(delta) || delta > float.MaxValue)
                throw new InvalidOperationException("GPU scope local time delta is invalid.");
            if (delta > 0)
            {
                var emitterPosition = scopeObject.transform.position;
                foreach (var gpu in gpuPrograms)
                    if (gpu && gpu.IsRunning)
                        gpu.StepSpawnable((float)delta, emitterPosition);
            }
            previousLocalTime = localTime;
        }

        private void Apply(NativeInstance.NativeSpawnableHostBase host, double effectTime)
        {
            var localTime = LocalTime(effectTime);
            foreach (var node in renderNodes)
                node.Update(host, rootScope, containerId, instanceId, localTime);
            previousLocalTime = localTime;
        }

        private double LocalTime(double effectTime)
        {
            var value = rootScope ? effectTime : effectTime - startTime;
            if (double.IsNaN(value) || double.IsInfinity(value) || value < -1e-8)
                throw new InvalidOperationException("Render scope local time is invalid.");
            return Math.Max(0, value);
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
            renderNodes.Clear();
            gpuPrograms.Clear();
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

    internal sealed class LiberaFxRenderNodeInstance : IDisposable
    {
        private readonly LiberaFxPreparedRenderNode definition;
        private readonly Transform nodeTransform;
        private readonly MeshRenderer renderer;
        private readonly MaterialPropertyBlock[] propertyBlocks;
        private readonly Vector4[] inputValues;
        private readonly Dictionary<string, int> inputIndexes;
        private Mesh ownedMesh;
        private bool disposed;

        private LiberaFxRenderNodeInstance(LiberaFxPreparedRenderNode prepared,
            Transform nodeTransform, MeshRenderer meshRenderer, Mesh ownedMesh)
        {
            definition = prepared;
            this.nodeTransform = nodeTransform;
            renderer = meshRenderer;
            this.ownedMesh = ownedMesh;
            propertyBlocks = new MaterialPropertyBlock[prepared.materials.Length];
            for (var slot = 0; slot < propertyBlocks.Length; ++slot)
                propertyBlocks[slot] = new MaterialPropertyBlock();
            inputValues = new Vector4[prepared.inputs.Length];
            inputIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < prepared.inputs.Length; ++index)
                inputIndexes.Add(prepared.inputs[index].id, index);
        }

        public static LiberaFxRenderNodeInstance Create(Transform parent, LiberaFxPreparedRenderNode prepared)
        {
            GameObject gameObject = null;
            Mesh ownedMesh = null;
            try
            {
                gameObject = new GameObject(prepared.isSprite ? "Libera FX Sprite " + prepared.nodeId :
                    "Libera FX Model " + prepared.nodeId);
                gameObject.transform.SetParent(parent, false);
                var filter = gameObject.AddComponent<MeshFilter>();
                var meshRenderer = gameObject.AddComponent<MeshRenderer>();
                if (prepared.isSprite)
                {
                    ownedMesh = CreateQuad();
                    filter.sharedMesh = ownedMesh;
                }
                else filter.sharedMesh = prepared.mesh;
                meshRenderer.sharedMaterials = prepared.materials;
                if (prepared.spriteAnimation?.version == 2)
                    meshRenderer.sortingOrder = prepared.spriteAnimation.drawOrder;
                var instance = new LiberaFxRenderNodeInstance(prepared, gameObject.transform,
                    meshRenderer, ownedMesh);
                instance.SetStaticTextures();
                return instance;
            }
            catch
            {
                if (ownedMesh) DestroyOwned(ownedMesh);
                if (gameObject) DestroyOwned(gameObject);
                throw;
            }
        }

        public void Update(NativeInstance.NativeSpawnableHostBase host, bool rootScope,
            byte[] containerId, ulong instanceId, double localTime)
        {
            if (disposed || !renderer) return;
            for (var index = 0; index < definition.inputs.Length; ++index)
                inputValues[index] = ReadInput(host, rootScope, containerId, instanceId,
                    definition.inputs[index]);

            var position = Value("position");
            var rotation = Value("rotation");
            var scale = Value("scale");
            if (definition.spriteAnimation?.version != 2)
            {
                nodeTransform.localPosition = new Vector3(position.x, position.y, position.z);
                nodeTransform.localScale = new Vector3(scale.x, scale.y, scale.z);
                var euler = definition.worldRotation
                    ? Quaternion.AngleAxis(rotation.z, Vector3.forward) *
                      Quaternion.AngleAxis(-rotation.y, Vector3.up) *
                      Quaternion.AngleAxis(-rotation.x, Vector3.right)
                    : Quaternion.Euler(rotation.x, rotation.y, rotation.z);
                nodeTransform.localRotation = euler;
                if (definition.billboard)
                {
                    if (!definition.billboardCamera)
                        throw new InvalidOperationException("Billboard camera was destroyed while the effect was running.");
                    nodeTransform.rotation = definition.billboardCamera.transform.rotation * euler;
                }
            }

            var tint = Value("tint");
            for (var slot = 0; slot < propertyBlocks.Length; ++slot)
            {
                propertyBlocks[slot].SetVector(definition.tintPropertyNames[slot], tint);
            }
            for (var index = 0; index < definition.inputs.Length; ++index)
            {
                var parameter = definition.inputs[index].materialParameter;
                if (parameter == null) continue;
                var value = inputValues[index];
                var block = propertyBlocks[parameter.slot];
                var unityPropertyName = definition.materialParameterPropertyNames[index];
                if (parameter.kind == "scalar") block.SetFloat(unityPropertyName, value.x);
                else block.SetVector(unityPropertyName, value);
            }

            if (definition.isSprite)
            {
                double frame = Value("frame").x;
                var playing = StaticValue("playing") >= 0.5;
                AtlasFrame atlas;
                NativeResult result;
                if (definition.spriteAnimation is { } animation)
                {
                    uint sequenceFrame;
                    if (animation.version == 2)
                    {
                        result = NativeInstance.SampleSpriteComposition(localTime, animation.fps, animation.durationFrames,
                            animation.loop, definition.spriteDurations, definition.spriteKeys, out sequenceFrame, out var pose);
                        if (result != NativeResult.Ok || sequenceFrame >= animation.frames.Length)
                            throw new InvalidOperationException("Layered sprite sampling failed: " + result);
                        var selected = animation.cells[animation.frames[sequenceFrame].cellIndex];
                        nodeTransform.localPosition = new Vector3((float)pose.x, (float)pose.y, 0);
                        nodeTransform.localRotation = Quaternion.AngleAxis((float)pose.rotation, Vector3.forward);
                        nodeTransform.localScale = new Vector3((float)(pose.sx * selected.width), (float)(pose.sy * selected.height), 1);
                    }
                    else
                    {
                        if (definition.frameConnected && frame < 0)
                            throw new InvalidOperationException("Connected Sprite Frame input must be a non-negative sequence index.");
                        result = NativeInstance.SampleSpriteSequence(frame, localTime, animation.fps,
                            definition.spriteDurations, (uint)animation.startFrame, (uint)animation.endFrame,
                            animation.loop, playing, out sequenceFrame);
                        if (result != NativeResult.Ok || sequenceFrame >= animation.frames.Length)
                            throw new InvalidOperationException("Sprite sequence sampling failed: " + result);
                    }
                    var cell = animation.cells[animation.frames[sequenceFrame].cellIndex];
                    result = NativeInstance.SampleRect(sequenceFrame, (float)cell.u, (float)cell.v,
                        (float)cell.uvWidth, (float)cell.uvHeight, out atlas);
                }
                else
                {
                    var columns = (uint)StaticValue("columns");
                    var rows = (uint)StaticValue("rows");
                    var fps = StaticValue("fps");
                    if (frame == -1 && !definition.frameConnected)
                        frame = playing ? Math.Floor(localTime * fps) : 0;
                    result = NativeInstance.SampleAtlas(frame, columns, rows, out atlas);
                }
                if (result != NativeResult.Ok)
                    throw new InvalidOperationException("Sprite atlas sampling failed: " + result);
                propertyBlocks[0].SetVector("_AtlasRect",
                    new Vector4(atlas.U, 1 - atlas.V - atlas.Height, atlas.Width, atlas.Height));
            }
            for (var slot = 0; slot < propertyBlocks.Length; ++slot)
                renderer.SetPropertyBlock(propertyBlocks[slot], slot);
        }

        private void SetStaticTextures()
        {
            foreach (var texture in definition.textures)
                propertyBlocks[texture.slot].SetTexture(texture.name, texture.texture);
            if (definition.isSprite)
                propertyBlocks[0].SetTexture("_MainTex", definition.texture);
        }

        private Vector4 ReadInput(NativeInstance.NativeSpawnableHostBase host, bool rootScope,
            byte[] containerId, ulong instanceId, LiberaFxRenderInputPayload input)
        {
            if (string.IsNullOrEmpty(input.sourceNodeId))
            {
                var defaults = input.defaults;
                return new Vector4((float)defaults[0],
                    input.width > 1 ? (float)defaults[1] : 0,
                    input.width > 2 ? (float)defaults[2] : 0,
                    input.width > 3 ? (float)defaults[3] : 0);
            }
            NativeResult result;
            NativeValue value;
            if (rootScope) result = host.ReadRootValue(input.sourceNodeBytes, out value);
            else result = host.ReadChildValue(containerId, instanceId, input.sourceNodeBytes, out value);
            if (result != NativeResult.Ok)
                throw new InvalidOperationException("Could not read render source " + input.sourceNodeId + ": " + result);
            if (value.ComponentCount > 4 || input.sourceComponent + input.width > value.ComponentCount)
                throw new InvalidOperationException("Render source component selection exceeds its native value width.");
            var first = Component(value, input.sourceComponent);
            var second = input.width > 1 ? Component(value, input.sourceComponent + 1) : 0;
            var third = input.width > 2 ? Component(value, input.sourceComponent + 2) : 0;
            var fourth = input.width > 3 ? Component(value, input.sourceComponent + 3) : 0;
            return new Vector4(ToUnityFloat(first), ToUnityFloat(second), ToUnityFloat(third), ToUnityFloat(fourth));
        }

        private Vector4 Value(string id)
        {
            if (!inputIndexes.TryGetValue(id, out var index))
                throw new InvalidOperationException("Render node is missing the " + id + " input.");
            return inputValues[index];
        }

        private double StaticValue(string id)
        {
            if (!inputIndexes.TryGetValue(id, out var index))
                throw new InvalidOperationException("Sprite node is missing the " + id + " setting.");
            return inputValues[index].x;
        }

        private static double Component(NativeValue value, int component) =>
            component == 0 ? value.X : component == 1 ? value.Y : component == 2 ? value.Z : value.W;

        private static float ToUnityFloat(double value)
        {
            if (!LiberaFxUnityBindingLookup.IsFiniteFloat(value))
                throw new InvalidOperationException("Native render value cannot be represented as a finite Unity float.");
            return (float)value;
        }

        private static Mesh CreateQuad()
        {
            var mesh = new Mesh { name = "Libera FX sprite quad" };
            mesh.vertices = new[]
            {
                new Vector3(-.5f, -.5f, 0), new Vector3(.5f, -.5f, 0),
                new Vector3(.5f, .5f, 0), new Vector3(-.5f, .5f, 0)
            };
            mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(1, 1), new Vector2(0, 1) };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateBounds();
            return mesh;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (renderer) renderer.SetPropertyBlock(null);
            if (ownedMesh)
            {
                var filter = renderer ? renderer.GetComponent<MeshFilter>() : null;
                if (filter && filter.sharedMesh == ownedMesh) filter.sharedMesh = null;
                DestroyOwned(ownedMesh);
                ownedMesh = null;
            }
        }

        private static void DestroyOwned(UnityEngine.Object value)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }
    }
}
