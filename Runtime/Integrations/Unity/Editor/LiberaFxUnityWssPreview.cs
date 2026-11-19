#if UNITY_EDITOR
using System;
using System.Buffers.Binary;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace LiberaFx.Unity
{
    internal sealed class LiberaFxUnityWssPreview : IDisposable
    {
        private readonly PreviewRenderUtility viewport = new PreviewRenderUtility();
        private LiberaFxSpawnableAsset package;
        private LiberaFxUnityBindingAsset bindings;
        private LiberaFxSpawnableComponent effect;
        private ulong sequence;
        private double time;

        internal GameObject PreviewObject => effect ? effect.gameObject : null;

        internal LiberaFxUnityWssPreview()
        {
            var camera = viewport.camera;
            camera.transform.position = new Vector3(3, 2, -6);
            camera.transform.LookAt(Vector3.zero);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.13f, .13f, .13f);
        }

        internal bool Apply(byte[] frame, out ulong acknowledgement, out string diagnostic)
        {
            acknowledgement = 0;
            diagnostic = null;
            var header = frame.AsSpan();
            var kind = header[6];
            var next = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(8));
            if (next == 0 || next < sequence)
            {
                return false;
            }
            if (kind == 3)
            {
                Clear();
                diagnostic = Encoding.UTF8.GetString(frame, 48, frame.Length - 48);
                return false;
            }
            if (kind == 2 && (!effect || BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(16)) != sequence))
            {
                return false;
            }
            var target = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(header.Slice(24)));
            if (double.IsNaN(target) || double.IsInfinity(target) || target < 0 || target > 120)
            {
                throw new InvalidOperationException("Unity preview supports playback times from 0 to 120 seconds.");
            }
            if (kind == 1)
            {
                var size = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(52));
                if (size <= 0 || size > 1024 * 1024 || size >= frame.Length - 56)
                {
                    throw new InvalidOperationException("Invalid Unity preview binding metadata.");
                }
                Clear();
                bindings = LiberaFxUnityResourceIdentityAdapter.CreatePreviewBindings(
                    Encoding.UTF8.GetString(frame, 56, size));
                package = ScriptableObject.CreateInstance<LiberaFxSpawnableAsset>();
                package.hideFlags = HideFlags.HideAndDontSave;
                package.SetPackageBytes(header.Slice(56 + size).ToArray());
                Restart();
            }
            else if (target < time)
            {
                Restart();
            }

            var steps = 0;
            while (effect.ElapsedTime < target && !effect.IsRootFinished)
            {
                if (++steps > 7201 || effect.AdvancePreview(Math.Min(target, effect.ElapsedTime + 1.0 / 60)) != NativeResult.Ok)
                {
                    throw new InvalidOperationException("Could not advance Unity preview: " + effect.LastError);
                }
            }
            if (!string.IsNullOrEmpty(effect.LastError))
            {
                throw new InvalidOperationException(effect.LastError);
            }
            time = target;
            sequence = next;
            acknowledgement = kind == 1 ? next : 0;
            return true;
        }

        internal void Draw(Rect rect)
        {
            if (Event.current.type != EventType.Repaint || !effect)
            {
                return;
            }
            viewport.BeginPreview(rect, GUIStyle.none);
            viewport.Render(true);
            GUI.DrawTexture(rect, viewport.EndPreview(), ScaleMode.StretchToFill, false);
        }

        private void Restart()
        {
            DestroyEffect();
            var owner = new GameObject("LIBERA FX WSS preview") { hideFlags = HideFlags.HideAndDontSave };
            viewport.AddSingleGO(owner);
            owner.SetActive(false);
            effect = owner.AddComponent<LiberaFxSpawnableComponent>();
            effect.EffectCameraOverride = viewport.camera;
            if (effect.ConfigureForSession(package, bindings, true) != NativeResult.Ok)
            {
                throw new InvalidOperationException("Could not configure Unity preview.");
            }
            owner.SetActive(true);
            if (effect.Play() != NativeResult.Ok)
            {
                throw new InvalidOperationException("Could not play Unity preview: " + effect.LastError);
            }
            time = 0;
        }

        private void DestroyEffect()
        {
            if (effect)
            {
                UnityEngine.Object.DestroyImmediate(effect.gameObject);
            }
            effect = null;
        }

        internal void Clear()
        {
            DestroyEffect();
            if (package)
            {
                UnityEngine.Object.DestroyImmediate(package);
            }
            if (bindings)
            {
                UnityEngine.Object.DestroyImmediate(bindings);
            }
            package = null;
            bindings = null;
            time = 0;
        }

        public void Dispose()
        {
            Clear();
            viewport.Cleanup();
        }
    }
}
#endif
