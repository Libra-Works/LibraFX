#if UNITY_EDITOR
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace LiberaFx.Unity
{
    internal sealed class LiberaFxUnityWssPreview : IDisposable
    {
        private const int MaxCatchupStepsPerFrame = 60;
        private const double MaxCatchupMilliseconds = 8.0;
        private readonly PreviewRenderUtility viewport = new PreviewRenderUtility();
        private LiberaFxSpawnableAsset package;
        private LiberaFxUnityBindingAsset bindings;
        private LiberaFxSpawnableComponent effect;
        private ulong sequence;
        private double time;
        private double targetTime;

        internal GameObject PreviewObject => effect ? effect.gameObject : null;
        internal bool IsSeeking => effect && !effect.IsRootFinished && effect.ElapsedTime + 1e-6 < targetTime;
        internal double TargetTime => targetTime;

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
            if (kind == 4)
            {
                var control = JsonUtility.FromJson<PreviewControl>(Encoding.UTF8.GetString(frame, 48, frame.Length - 48));
                if (control?.type == "preview_motion")
                {
                    if (control.offset == null || control.offset.Length != 3 ||
                        Array.Exists(control.offset, lane => double.IsNaN(lane) || double.IsInfinity(lane) || Math.Abs(lane) > 1000))
                        return false;
                    if (effect)
                    {
                        effect.transform.localPosition = new Vector3((float)control.offset[0], (float)control.offset[1], (float)control.offset[2]);
                        effect.SynchronizeOwnerTransform();
                    }
                    return true;
                }
                if (control?.type == "unity_preview_signals")
                {
                    if (effect && control.requests != null)
                    {
                        foreach (var req in control.requests)
                        {
                            if (req != null && !string.IsNullOrEmpty(req.nodeId))
                                effect.DispatchSignal(req.nodeId);
                        }
                    }
                    return true;
                }
                if (control?.type != "preview_reset") return false;
                Clear();
                sequence = 0;
                return true;
            }
            var next = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(8));
            if (next == 0 || next <= sequence)
            {
                return false;
            }
            if (kind == 3)
            {
                Clear();
                diagnostic = Encoding.UTF8.GetString(frame, 48, frame.Length - 48);
                return false;
            }
            if (kind == 5)
            {
                if (!effect || next == 0 || next <= sequence) return false;
                var jsonBytes = new byte[frame.Length - 48];
                Buffer.BlockCopy(frame, 48, jsonBytes, 0, jsonBytes.Length);
                var delta = JsonUtility.FromJson<ParameterDeltaHeader>(Encoding.UTF8.GetString(jsonBytes));
                if (delta == null || delta.type != "parameter_delta" || delta.sequence < 1 ||
                    (ulong)delta.sequence != next) return false;
                if (effect.ApplyParameterDelta(jsonBytes) != LiberaFxNative.NativeResult.Ok) return false;
                var refresh = effect.RefreshPreview();
                if (refresh != LiberaFxNative.NativeResult.Ok)
                    throw new InvalidOperationException("Could not refresh paused Unity preview after parameter delta: " + refresh);
                sequence = next;
                return true;
            }
            if (kind == 2 && (!effect || BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(16)) != sequence))
            {
                return false;
            }
            var target = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(header.Slice(24)));
            if (double.IsNaN(target) || double.IsInfinity(target) || target < 0)
            {
                throw new InvalidOperationException("Unity preview requires a finite non-negative playback time.");
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

            targetTime = target;
            StepTowardTarget();
            sequence = next;
            acknowledgement = kind == 1 ? next : 0;
            return true;
        }

        internal void StepTowardTarget()
        {
            if (!effect || effect.IsRootFinished || effect.ElapsedTime >= targetTime)
            {
                return;
            }
            var stopwatch = Stopwatch.StartNew();
            var steps = 0;
            while (effect.ElapsedTime < targetTime && !effect.IsRootFinished)
            {
                if (steps >= MaxCatchupStepsPerFrame || steps > 0 && stopwatch.Elapsed.TotalMilliseconds >= MaxCatchupMilliseconds)
                {
                    break;
                }
                if (effect.AdvancePreview(Math.Min(targetTime, effect.ElapsedTime + 1.0 / 60)) != NativeResult.Ok)
                {
                    throw new InvalidOperationException("Could not advance Unity preview: " + effect.LastError);
                }
                ++steps;
            }
            if (!string.IsNullOrEmpty(effect.LastError))
            {
                throw new InvalidOperationException(effect.LastError);
            }
            time = effect.ElapsedTime;
        }

        internal void Draw(Rect rect)
        {
            if (Event.current.type != EventType.Repaint || !effect)
            {
                return;
            }
            StepTowardTarget();
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
            targetTime = 0;
        }

        public void Dispose()
        {
            Clear();
            viewport.Cleanup();
        }
        [Serializable] private sealed class SignalRequest { public string nodeId; }
        [Serializable] private sealed class PreviewControl { public string type; public double[] offset; public SignalRequest[] requests; }
        [Serializable] private sealed class ParameterDeltaHeader { public string type; public long sequence; }
    }
}
#endif
