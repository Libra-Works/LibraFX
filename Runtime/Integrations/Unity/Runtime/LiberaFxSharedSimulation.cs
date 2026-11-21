using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiberaFx.Unity
{
    // Runs after ordinary LateUpdate has copied camera/pose inputs. One executor
    // for all components, never Task.Run or a native worker pool per component.
    [DefaultExecutionOrder(32000)]
    internal sealed unsafe class LiberaFxSharedSimulation : MonoBehaviour
    {
        private const int MaximumEffects = 1024;
        private struct Pending
        {
            internal LiberaFxSpawnableComponent component;
            internal NativeSpawnableHost host;
            internal double time, delta;
        }
        private sealed class Prepared
        {
            internal byte[] bytes;
            internal PreparedHostOptions options;
            internal NativePreparedHostPool pool;
            internal NativeResult result;
        }
        private static LiberaFxSharedSimulation instance;
        private readonly List<Prepared> prepared = new List<Prepared>();
        private readonly Pending[] pending = new Pending[MaximumEffects], ready = new Pending[MaximumEffects];
        private readonly SharedEffectFrame[] frames = new SharedEffectFrame[MaximumEffects];
        private int count;
        private ulong world;
        private bool running;
        private void Awake()
        {
            NativeInstance.Check(PreparedNative.libra_effect_world_create(MaximumEffects, 4, out world));
        }
        private static bool SameBytes(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (var i = 0; i < left.Length; ++i) if (left[i] != right[i]) return false;
            return true;
        }
        private static LiberaFxSharedSimulation Ensure()
        {
            if (instance) return instance;
            var owner = new GameObject("LIBERA FX Shared Simulation") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(owner);
            instance = owner.AddComponent<LiberaFxSharedSimulation>();
            return instance;
        }
        internal static NativeResult Prewarm(byte[] bytes, PreparedHostOptions options)
        {
            var owner = Ensure();
            foreach (var entry in owner.prepared)
                if (SameBytes(entry.bytes, bytes) && entry.options.capacity == options.capacity &&
                    entry.options.preparationWorkers == options.preparationWorkers &&
                    entry.options.eventCapacity == options.eventCapacity && entry.options.instructionBudget == options.instructionBudget)
                    return entry.result;
            if (owner.prepared.Count >= 64) return NativeResult.InsufficientWork;
            var next = new Prepared { bytes = (byte[])bytes.Clone(), options = options };
            next.result = NativePreparedHostPool.Create(bytes, options, out next.pool);
            if (next.result != NativeResult.Ok && next.result != NativeResult.UnsupportedBinary) return next.result;
            owner.prepared.Add(next);
            return next.result;
        }
        internal static NativeResult Acquire(byte[] bytes, PreparedHostOptions options, out NativeSpawnableHost host)
        {
            host = null;
            if (!instance) return NativeResult.NotReady;
            foreach (var entry in instance.prepared)
                if (SameBytes(entry.bytes, bytes) && entry.options.capacity == options.capacity &&
                    entry.options.preparationWorkers == options.preparationWorkers &&
                    entry.options.eventCapacity == options.eventCapacity && entry.options.instructionBudget == options.instructionBudget)
                    return entry.pool == null ? entry.result : entry.pool.Acquire(out host);
            return NativeResult.NotReady;
        }
        internal static bool Queue(LiberaFxSpawnableComponent component, NativeSpawnableHost host, double time, double delta)
        {
            var owner = Ensure();
            if (owner.running || owner.count == MaximumEffects) return false;
            if (owner.world == 0)
                NativeInstance.Check(PreparedNative.libra_effect_world_create(MaximumEffects, 4, out owner.world));
            for (var i = 0; i < owner.count; ++i)
                if (ReferenceEquals(owner.pending[i].host, host)) return false;
            owner.pending[owner.count++] = new Pending { component = component, host = host, time = time, delta = delta };
            return true;
        }
        private void LateUpdate()
        {
            if (count == 0) return;
            var valid = 0;
            for (var i = 0; i < count; ++i)
            {
                var next = pending[i]; pending[i] = default;
                if (!next.component || !next.component.SharedFrameIsCurrent(next.host)) continue;
                ready[valid] = next;
                frames[valid++] = new SharedEffectFrame { host = next.host.RuntimeHandle, time = next.time, delta = next.delta };
            }
            count = 0;
            running = true;
            try
            {
                NativeResult status;
                fixed (SharedEffectFrame* data = frames)
                    status = PreparedNative.libra_effect_world_advance(world, data, (uint)valid);
                // Preflight errors execute nothing; fall back per instance on the
                // owner thread. A failed instance never suppresses other effects.
                for (var i = 0; i < valid; ++i)
                {
                    var next = ready[i]; ready[i] = default;
                    if (!next.component || !next.component.SharedFrameIsCurrent(next.host)) continue;
                    next.component.CompleteSharedFrame(next.host, next.time, next.delta,
                        status == NativeResult.Ok ? frames[i].result : next.host.Advance(next.time, next.delta), valid);
                }
            }
            finally { running = false; }
        }
        private void OnDestroy()
        {
            if (world != 0) { PreparedNative.libra_effect_world_release(world); world = 0; }
            foreach (var entry in prepared) entry.pool?.CloseWhenIdle();
            prepared.Clear();
            if (instance == this) instance = null;
        }
    }
}
