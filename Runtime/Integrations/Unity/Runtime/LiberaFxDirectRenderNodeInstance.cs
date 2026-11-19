using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiberaFx.Unity
{
    internal sealed class LiberaFxDirectRenderNodeInstance : IDisposable
    {
        private readonly LiberaFxPreparedRenderNode definition;
        private readonly Transform parent;
        private readonly LiberaFxDrawPropertyState[] propertyStates;
        private readonly LiberaFxRenderSubmission[] submissions;
        private readonly Vector4[] inputValues;
        private readonly Dictionary<string, int> inputIndexes;
        private readonly int[] tintPropertyIds;
        private readonly int[] materialParameterPropertyIds;
        private readonly int[] texturePropertyIds;
        private readonly int atlasPropertyId = Shader.PropertyToID("_AtlasRect");
        private Mesh ownedMesh;
        private Vector3 localPosition;
        private Vector3 localScale = Vector3.one;
        private Quaternion localRotation = Quaternion.identity;
        private bool disposed;

        private LiberaFxDirectRenderNodeInstance(LiberaFxPreparedRenderNode prepared,
            Transform parent, LiberaFxRenderStatisticsCounter statistics)
        {
            definition = prepared;
            this.parent = parent;
            propertyStates = new LiberaFxDrawPropertyState[prepared.materials.Length];
            submissions = new LiberaFxRenderSubmission[prepared.materials.Length];
            tintPropertyIds = new int[prepared.materials.Length];
            materialParameterPropertyIds = new int[prepared.inputs.Length];
            texturePropertyIds = new int[prepared.textures.Length];
            inputValues = new Vector4[prepared.inputs.Length];
            inputIndexes = new Dictionary<string, int>(StringComparer.Ordinal);

            var propertyCapacity = 1 + prepared.inputs.Length + prepared.textures.Length;
            for (var slot = 0; slot < propertyStates.Length; slot++)
            {
                propertyStates[slot] = new LiberaFxDrawPropertyState(propertyCapacity + 1);
                tintPropertyIds[slot] = Shader.PropertyToID(prepared.tintPropertyNames[slot]);
            }
            for (var index = 0; index < prepared.inputs.Length; index++)
            {
                inputIndexes.Add(prepared.inputs[index].id, index);
                var name = prepared.materialParameterPropertyNames[index];
                if (!string.IsNullOrEmpty(name))
                {
                    materialParameterPropertyIds[index] = Shader.PropertyToID(name);
                }
            }
            for (var index = 0; index < prepared.textures.Length; index++)
            {
                texturePropertyIds[index] = Shader.PropertyToID(prepared.textures[index].name);
            }
        }

        internal Matrix4x4 worldMatrix { get; private set; }
        internal Vector3 LocalPosition => localPosition;
        internal Vector3 LocalScale => localScale;
        internal Quaternion LocalRotation => localRotation;
        internal IReadOnlyList<LiberaFxRenderSubmission> Submissions => submissions;
        internal string nodeId => definition.nodeId;

        internal static LiberaFxDirectRenderNodeInstance Create(Transform parent,
            LiberaFxPreparedRenderNode prepared, LiberaFxRenderStatisticsCounter statistics)
        {
            var instance = new LiberaFxDirectRenderNodeInstance(prepared, parent, statistics);
            try
            {
                var mesh = prepared.mesh;
                if (prepared.isSprite)
                {
                    instance.ownedMesh = CreateQuad();
                    mesh = instance.ownedMesh;
                }
                for (var slot = 0; slot < instance.submissions.Length; slot++)
                {
                    var submission = new LiberaFxRenderSubmission(instance.propertyStates[slot], statistics)
                    {
                        owner = parent,
                        useMeshRendererLighting = true,
                        drawOrder = prepared.spriteAnimation?.version == 2
                            ? prepared.spriteAnimation.drawOrder : (int?)null
                    };
                    instance.submissions[slot] = submission;
                    submission.Configure(mesh, prepared.materials[slot], slot,
                        parent.gameObject.layer, statistics);
                }
                instance.SetStaticTextures();
                return instance;
            }
            catch
            {
                instance.Dispose();
                throw;
            }
        }

        internal void Update(NativeInstance.NativeSpawnableHostBase host, bool rootScope,
            byte[] containerId, ulong instanceId, double localTime)
        {
            if (disposed || !parent)
            {
                return;
            }
            for (var index = 0; index < definition.inputs.Length; index++)
            {
                inputValues[index] = ReadInput(host, rootScope, containerId, instanceId,
                    definition.inputs[index]);
            }

            var position = Value("position");
            var rotation = Value("rotation");
            var scale = Value("scale");
            if (definition.spriteAnimation?.version != 2)
            {
                localPosition = new Vector3(position.x, position.y, position.z);
                localScale = new Vector3(scale.x, scale.y, scale.z);
                localRotation = definition.worldRotation
                    ? Quaternion.AngleAxis(rotation.z, Vector3.forward) *
                      Quaternion.AngleAxis(-rotation.y, Vector3.up) *
                      Quaternion.AngleAxis(-rotation.x, Vector3.right)
                    : Quaternion.Euler(rotation.x, rotation.y, rotation.z);
            }

            var tint = Value("tint");
            for (var slot = 0; slot < propertyStates.Length; slot++)
            {
                propertyStates[slot].SetVector(tintPropertyIds[slot], tint);
            }
            for (var index = 0; index < definition.inputs.Length; index++)
            {
                var parameter = definition.inputs[index].materialParameter;
                if (parameter == null)
                {
                    continue;
                }
                var value = inputValues[index];
                var propertyId = materialParameterPropertyIds[index];
                if (parameter.kind == "scalar")
                {
                    propertyStates[parameter.slot].SetFloat(propertyId, value.x);
                }
                else
                {
                    propertyStates[parameter.slot].SetVector(propertyId, value);
                }
            }

            if (definition.isSprite)
            {
                UpdateSprite(localTime);
            }
            worldMatrix = BuildWorldMatrix();
            for (var index = 0; index < submissions.Length; index++)
            {
                submissions[index].layer = parent.gameObject.layer;
                submissions[index].SetMatrix(worldMatrix);
                submissions[index].enabled = true;
            }
        }

        private void UpdateSprite(double localTime)
        {
            var frame = Value("frame").x;
            var playing = StaticValue("playing") >= 0.5;
            AtlasFrame atlas;
            NativeResult result;
            if (definition.spriteAnimation is { } animation)
            {
                uint sequenceFrame;
                if (animation.version == 2)
                {
                    result = NativeInstance.SampleSpriteComposition(localTime, animation.fps,
                        animation.durationFrames, animation.loop, definition.spriteDurations,
                        definition.spriteKeys, out sequenceFrame, out var pose);
                    if (result != NativeResult.Ok || sequenceFrame >= animation.frames.Length)
                    {
                        throw new InvalidOperationException("Layered sprite sampling failed: " + result);
                    }
                    var selected = animation.cells[animation.frames[sequenceFrame].cellIndex];
                    localPosition = new Vector3((float)pose.x, (float)pose.y, 0);
                    localRotation = Quaternion.AngleAxis((float)pose.rotation, Vector3.forward);
                    localScale = new Vector3((float)(pose.sx * selected.width),
                        (float)(pose.sy * selected.height), 1);
                }
                else
                {
                    if (definition.frameConnected && frame < 0)
                    {
                        throw new InvalidOperationException(
                            "Connected Sprite Frame input must be a non-negative sequence index.");
                    }
                    result = NativeInstance.SampleSpriteSequence(frame, localTime, animation.fps,
                        definition.spriteDurations, (uint)animation.startFrame,
                        (uint)animation.endFrame, animation.loop, playing, out sequenceFrame);
                    if (result != NativeResult.Ok || sequenceFrame >= animation.frames.Length)
                    {
                        throw new InvalidOperationException("Sprite sequence sampling failed: " + result);
                    }
                }
                var cell = animation.cells[animation.frames[sequenceFrame].cellIndex];
                result = NativeInstance.SampleRect(sequenceFrame, (float)cell.u, (float)cell.v,
                    (float)cell.uvWidth, (float)cell.uvHeight, out atlas);
            }
            else
            {
                var columns = (uint)StaticValue("columns");
                var rows = (uint)StaticValue("rows");
                var framesPerSecond = StaticValue("fps");
                if (frame == -1 && !definition.frameConnected)
                {
                    frame = playing ? (float)Math.Floor(localTime * framesPerSecond) : 0;
                }
                result = NativeInstance.SampleAtlas(frame, columns, rows, out atlas);
            }
            if (result != NativeResult.Ok)
            {
                throw new InvalidOperationException("Sprite atlas sampling failed: " + result);
            }
            propertyStates[0].SetVector(atlasPropertyId,
                new Vector4(atlas.U, 1 - atlas.V - atlas.Height, atlas.Width, atlas.Height));
        }

        private Matrix4x4 BuildWorldMatrix()
        {
            if (!definition.billboard)
            {
                return parent.localToWorldMatrix *
                    Matrix4x4.TRS(localPosition, localRotation, localScale);
            }
            if (!definition.billboardCamera)
            {
                throw new InvalidOperationException("Billboard camera was destroyed while the effect was running.");
            }
            var rotation = Quaternion.Inverse(parent.rotation) *
                definition.billboardCamera.transform.rotation * localRotation;
            return parent.localToWorldMatrix * Matrix4x4.TRS(localPosition, rotation, localScale);
        }

        private void SetStaticTextures()
        {
            for (var index = 0; index < definition.textures.Length; index++)
            {
                var texture = definition.textures[index];
                propertyStates[texture.slot].SetTexture(texturePropertyIds[index], texture.texture);
            }
            if (definition.isSprite)
            {
                propertyStates[0].SetTexture(Shader.PropertyToID("_MainTex"), definition.texture);
            }
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
            if (rootScope)
            {
                result = host.ReadRootValue(input.sourceNodeBytes, out value);
            }
            else
            {
                result = host.ReadChildValue(containerId, instanceId, input.sourceNodeBytes, out value);
            }
            if (result != NativeResult.Ok)
            {
                throw new InvalidOperationException(
                    "Could not read render source " + input.sourceNodeId + ": " + result);
            }
            if (value.ComponentCount > 4 || input.sourceComponent + input.width > value.ComponentCount)
            {
                throw new InvalidOperationException(
                    "Render source component selection exceeds its native value width.");
            }
            var first = Component(value, input.sourceComponent);
            var second = input.width > 1 ? Component(value, input.sourceComponent + 1) : 0;
            var third = input.width > 2 ? Component(value, input.sourceComponent + 2) : 0;
            var fourth = input.width > 3 ? Component(value, input.sourceComponent + 3) : 0;
            return new Vector4(ToUnityFloat(first), ToUnityFloat(second),
                ToUnityFloat(third), ToUnityFloat(fourth));
        }

        private Vector4 Value(string id)
        {
            if (!inputIndexes.TryGetValue(id, out var index))
            {
                throw new InvalidOperationException("Render node is missing the " + id + " input.");
            }
            return inputValues[index];
        }

        private double StaticValue(string id)
        {
            if (!inputIndexes.TryGetValue(id, out var index))
            {
                throw new InvalidOperationException("Sprite node is missing the " + id + " setting.");
            }
            return inputValues[index].x;
        }

        private static double Component(NativeValue value, int component)
        {
            if (component == 0)
            {
                return value.X;
            }
            if (component == 1)
            {
                return value.Y;
            }
            if (component == 2)
            {
                return value.Z;
            }
            return value.W;
        }

        private static float ToUnityFloat(double value)
        {
            if (!LiberaFxUnityBindingLookup.IsFiniteFloat(value))
            {
                throw new InvalidOperationException(
                    "Native render value cannot be represented as a finite Unity float.");
            }
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
            mesh.uv = new[]
            {
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1)
            };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateBounds();
            return mesh;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            foreach (var submission in submissions)
            {
                if (submission != null)
                {
                    submission.Dispose();
                }
            }
            if (ownedMesh)
            {
                DestroyOwned(ownedMesh);
                ownedMesh = null;
            }
        }

        private static void DestroyOwned(UnityEngine.Object value)
        {
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(value);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(value);
            }
        }
    }
}
