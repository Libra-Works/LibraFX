using System;
using UnityEngine;

namespace LiberaFx.Unity
{
    [DisallowMultipleComponent]
    public sealed class LiberaFxSpawnableComponent : MonoBehaviour
    {
        [SerializeField] private LiberaFxSpawnableAsset effect;
        [SerializeField] private bool playOnEnable = true;
        [SerializeField, Min(0)] private float playRate = 1;
        [SerializeField, Min(1)] private int maxActivePerTemplate = 32;
        [SerializeField, Min(1024)] private int memoryBudgetBytesPerTemplate = 4 * 1024 * 1024;
        [SerializeField, Min(1)] private int eventCapacity = 128;
        [SerializeField] private uint instructionBudget = 10000;

        private NativeSpawnableHost host;
        private double elapsed;
        private bool rootFinished;

        public event Action<SpawnableHostEvent> HostEventReceived;
        public event Action<GameIoEvent> GameIoEventReceived;
        public event Action<NativeResult> RuntimeError;

        public bool IsRootFinished => rootFinished;
        public double ElapsedTime => elapsed;

        private void OnEnable()
        {
            if (playOnEnable) Play();
        }

        private void Update()
        {
            var current = host;
            if (current == null || rootFinished) return;
            var frameDelta = (double)Time.deltaTime;
            var rate = (double)playRate;
            if (double.IsNaN(rate) || double.IsInfinity(rate) || rate < 0)
            {
                RuntimeError?.Invoke(NativeResult.InvalidArgument);
                enabled = false;
                return;
            }
            elapsed += frameDelta * rate;
            var result = current.Advance(elapsed, frameDelta);
            DrainEvents();
            if (host != current) return;
            for (var attempt = 0; result == NativeResult.QueueRejected && attempt < 16; ++attempt)
            {
                result = current.Advance(elapsed, frameDelta);
                DrainEvents();
                if (host != current) return;
            }
            if (result != NativeResult.Ok)
            {
                RuntimeError?.Invoke(result);
                if (result != NativeResult.QueueRejected) enabled = false;
                return;
            }
            if (current.IsRootFinished(out rootFinished) != NativeResult.Ok)
            {
                RuntimeError?.Invoke(NativeResult.InternalError);
                enabled = false;
            }
        }

        public NativeResult Play(bool restart = true)
        {
            if (!effect || effect.PackageBytes == null || effect.PackageBytes.Length == 0)
                return Report(NativeResult.InvalidArgument);
            if (host != null && !restart) return NativeResult.Ok;
            if (host != null)
            {
                var stopResult = Stop();
                if (stopResult != NativeResult.Ok) return stopResult;
            }
            host = new NativeSpawnableHost(effect.PackageBytes,
                (uint)Math.Max(1, maxActivePerTemplate),
                (ulong)Math.Max(1024, memoryBudgetBytesPerTemplate),
                (uint)Math.Max(1, eventCapacity), instructionBudget);
            elapsed = 0;
            rootFinished = false;
            var result = host.Advance(0, 0);
            DrainEvents();
            return result == NativeResult.Ok ? result : Report(result);
        }

        public NativeResult RequestNaturalEnd()
        {
            if (host == null) return NativeResult.NotReady;
            var result = host.RequestNaturalEnd();
            DrainEvents();
            return result == NativeResult.Ok ? result : Report(result);
        }

        public NativeResult DispatchSignal(string nodeGuid)
        {
            if (host == null) return NativeResult.NotReady;
            var result = host.DispatchSignal(nodeGuid);
            DrainEvents();
            return result == NativeResult.Ok ? result : Report(result);
        }

        public NativeResult FireGameSignal(string name)
        {
            if (host == null) return NativeResult.NotReady;
            var result = host.FireGameSignal(name);
            DrainEvents();
            return result == NativeResult.Ok ? result : Report(result);
        }

        public NativeResult SetGameProperty(string name, double[] values) =>
            host == null ? NativeResult.NotReady : host.SetGameProperty(name, values);

        public NativeResult SetOwnerMatrix(double[] values) =>
            host == null ? NativeResult.NotReady : host.SetOwnerMatrix(values);

        public NativeResult ReadChildValue(byte[] containerId, ulong instanceId,
            string nodeGuid, out NativeValue value)
        {
            if (host == null) { value = default; return NativeResult.NotReady; }
            return host.ReadChildValue(containerId, instanceId, nodeGuid, out value);
        }

        public NativeResult GetTemplateCount(out ulong count)
        {
            if (host == null) { count = 0; return NativeResult.NotReady; }
            return host.GetTemplateCount(out count);
        }

        public NativeResult GetTemplateId(ulong index, out byte[] containerId)
        {
            if (host == null) { containerId = null; return NativeResult.NotReady; }
            return host.GetTemplateId(index, out containerId);
        }

        public NativeResult GetTemplatePayload(byte[] containerId,
            SpawnableTemplatePayloadKind kind, out byte[] payload)
        {
            if (host == null) { payload = null; return NativeResult.NotReady; }
            return host.GetTemplatePayload(containerId, kind, out payload);
        }

        public NativeResult GetRootPayload(SpawnableTemplatePayloadKind kind, out byte[] payload)
        {
            if (host == null) { payload = null; return NativeResult.NotReady; }
            return host.GetRootPayload(kind, out payload);
        }

        public NativeResult Stop()
        {
            if (host == null) return NativeResult.Ok;
            var result = NativeResult.QueueRejected;
            for (var attempt = 0; result == NativeResult.QueueRejected && attempt < 16; ++attempt)
            {
                result = host.Stop();
                DrainEvents();
            }
            if (result != NativeResult.Ok)
            {
                return Report(result);
            }
            ReleaseHost();
            return NativeResult.Ok;
        }

        private NativeResult Report(NativeResult result)
        {
            RuntimeError?.Invoke(result);
            return result;
        }

        private void DrainEvents()
        {
            var current = host;
            if (current == null) return;
            while (host == current)
            {
                var result = current.PopEvent(out var item);
                if (result != NativeResult.Ok) { Report(result); return; }
                if (item == null) break;
                HostEventReceived?.Invoke(item);
            }
            while (host == current)
            {
                var result = current.PopGameIoEvent(out var ioEvent);
                if (result != NativeResult.Ok) { Report(result); return; }
                if (ioEvent == null) break;
                GameIoEventReceived?.Invoke(ioEvent);
            }
        }

        private void ReleaseHost()
        {
            host?.Dispose();
            host = null;
        }

        private void OnDisable()
        {
            Stop();
        }

        private void OnDestroy()
        {
            ReleaseHost();
        }
    }
}
