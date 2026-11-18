using System;
using UnityEngine;

namespace LiberaFx.Unity
{
    // Owned by one existing render scope (root or exact child instance), never a
    // global OnGUI/Debug.DrawLine singleton. Destroyed by the same scope teardown
    // used for Stop, disconnect, replacement, force termination and destruction.
    internal sealed class LiberaFxDebugDrawInstance : IDisposable
    {
        private readonly LiberaFxPreparedRenderNode definition;
        private GameObject owned;

        private LiberaFxDebugDrawInstance(LiberaFxPreparedRenderNode definition, GameObject owned)
        { this.definition = definition; this.owned = owned; }

        public static LiberaFxDebugDrawInstance Create(LiberaFxPreparedRenderNode definition, ulong instance,
            UnityEngine.SceneManagement.Scene scene)
        {
            var owned = new GameObject("LIBERA FX Preview Debug " + definition.nodeId + " #" + instance);
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(owned, scene);
            owned.hideFlags = HideFlags.DontSave;
            owned.SetActive(false);
            try
            {
                // Deliberately unparented: the authored Matrix is WORLD space.
                // The scope keeps explicit ownership even if its owner moves.
                if (definition.debugDraw.kind == "debugText")
                {
                    var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                    if (!font) throw new InvalidOperationException("Unity's world TextMesh font is unavailable.");
                    var label = owned.AddComponent<TextMesh>();
                    label.font = font;
                    label.text = definition.debugDraw.text;
                    label.richText = false;
                    label.fontSize = 48;
                    label.characterSize = 1f / 48;
                    label.anchor = TextAnchor.MiddleCenter;
                    owned.GetComponent<MeshRenderer>().sharedMaterial = font.material;
                }
                else
                {
                    owned.AddComponent<MeshFilter>().sharedMesh = definition.mesh;
                    owned.AddComponent<MeshRenderer>().sharedMaterials = definition.materials;
                }
                return new LiberaFxDebugDrawInstance(definition, owned);
            }
            catch { DestroyOwned(owned); throw; }
        }

        public void Update(NativeInstance.NativeSpawnableHostBase host, bool root, byte[] container, ulong instance)
        {
            if (!owned) return;
            var matrix = Matrix4x4.identity;
            var visible = false;
            for (var index = 0; index < definition.inputs.Length; ++index)
            {
                var input = definition.inputs[index];
                var value = new Vector4();
                if (string.IsNullOrEmpty(input.sourceNodeId))
                    for (var lane = 0; lane < input.width; ++lane) value[lane] = Finite(input.defaults[lane]);
                else
                {
                    NativeValue native;
                    var result = root ? host.ReadRootValue(input.sourceNodeBytes, out native) :
                        host.ReadChildValue(container, instance, input.sourceNodeBytes, out native);
                    if (result != NativeResult.Ok || input.sourceComponent + input.width > native.ComponentCount)
                        throw new InvalidOperationException("Debug draw source is unavailable: " + input.sourceNodeId);
                    for (var lane = 0; lane < input.width; ++lane)
                    {
                        var component = input.sourceComponent + lane;
                        value[lane] = Finite(component == 0 ? native.X : component == 1 ? native.Y :
                            component == 2 ? native.Z : native.W);
                    }
                }
                if (input.id == "visible") visible = value.x > 0;
                else matrix.SetRow(input.id[3] - '0', value);
            }
            // A SOURCE.Valid -> Visible connection can explicitly suppress a
            // missing pose's zero Matrix; do not invent a visible identity pose.
            if (!visible) { owned.SetActive(false); return; }
            if (matrix.m30 != 0 || matrix.m31 != 0 || matrix.m32 != 0 || matrix.m33 != 1)
                throw new InvalidOperationException("Debug draw world Matrix must be affine.");
            var x = (Vector3)matrix.GetColumn(0);
            var y = (Vector3)matrix.GetColumn(1);
            var z = (Vector3)matrix.GetColumn(2);
            var scale = new Vector3(x.magnitude, y.magnitude, z.magnitude);
            if (Mathf.Min(scale.x, Mathf.Min(scale.y, scale.z)) <= 1e-6f)
                throw new InvalidOperationException("Debug draw world Matrix must be nonsingular.");
            x /= scale.x; y /= scale.y; z /= scale.z;
            if (Mathf.Abs(Vector3.Dot(x, y)) > 0.0001f || Mathf.Abs(Vector3.Dot(x, z)) > 0.0001f ||
                Mathf.Abs(Vector3.Dot(y, z)) > 0.0001f)
                throw new InvalidOperationException("Unity world Text/Model debug rendering does not support a sheared Matrix.");
            if (Vector3.Dot(Vector3.Cross(x, y), z) < 0) { x = -x; scale.x = -scale.x; }
            owned.transform.SetPositionAndRotation(matrix.GetColumn(3), Quaternion.LookRotation(z, y));
            owned.transform.localScale = scale * Finite(definition.debugDraw.size);
            owned.SetActive(visible);
        }

        private static float Finite(double value)
        {
            if (!LiberaFxUnityBindingLookup.IsFiniteFloat(value))
                throw new InvalidOperationException("Debug draw value exceeds finite Unity float range.");
            return (float)value;
        }
        public void Dispose()
        {
            if (!owned) return;
            owned.SetActive(false); // Destroy may be deferred until the end of frame.
            DestroyOwned(owned);
            owned = null;
        }
        private static void DestroyOwned(UnityEngine.Object value)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }
    }
}
