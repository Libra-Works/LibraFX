using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace LiberaFx.Unity
{
    [DisallowMultipleComponent]
    public sealed class LiberaFxSpawnableComponent : MonoBehaviour
    {
        private const uint SpawnEvent = 1;
        private const uint StopEvent = 2;
        private const uint DestroyEvent = 3;
        private const uint SignalEvent = 4;
        private const int MaximumTemplates = 512;

        [SerializeField] private LiberaFxSpawnableAsset effect;
        [SerializeField] private LiberaFxUnityBindingAsset engineBindings;
        [SerializeField] private bool playOnEnable = true;
        [SerializeField, Min(0)] private float playRate = 1;
        [SerializeField, Min(1)] private int maxActivePerTemplate = 32;
        [SerializeField, Min(1024)] private int memoryBudgetBytesPerTemplate = 4 * 1024 * 1024;
        [SerializeField, Min(1)] private int eventCapacity = 128;
        [SerializeField] private uint instructionBudget = 10000;

        private NativeSpawnableHost host;
        private LiberaFxRenderScope rootRenderScope;
        private readonly Dictionary<string, LiberaFxPreparedScope> templateDefinitions =
            new Dictionary<string, LiberaFxPreparedScope>(StringComparer.Ordinal);
        private readonly Dictionary<string, LiberaFxRenderScope> instanceScopes =
            new Dictionary<string, LiberaFxRenderScope>(StringComparer.Ordinal);
        private double elapsed;
        private bool rootFinished;

        public event Action<SpawnableHostEvent> HostEventReceived;
        public event Action<GameIoEvent> GameIoEventReceived;
        public event Action<NativeResult> RuntimeError;

        public bool IsRootFinished => rootFinished;
        public double ElapsedTime => elapsed;
        public string LastError { get; private set; }
        public int ActiveRenderScopeCount => (rootRenderScope == null ? 0 : 1) + instanceScopes.Count;

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
                FailPlayback(NativeResult.InvalidArgument, "Play Rate must be finite and nonnegative.", current);
                enabled = false;
                return;
            }

            elapsed += frameDelta * rate;
            var result = NativeResult.QueueRejected;
            for (var attempt = 0; attempt < 16; ++attempt)
            {
                result = current.Advance(elapsed, frameDelta);
                DrainEvents(current);
                if (host != current) return;
                if (result != NativeResult.QueueRejected) break;
            }
            if (result != NativeResult.Ok)
            {
                FailPlayback(result, "Native Spawnable advance failed: " + result + ".", current);
                enabled = false;
                return;
            }

            try
            {
                if (current.IsRootFinished(out var finished) != NativeResult.Ok)
                    throw new InvalidOperationException("Native root completion query failed.");
                if (host != current) return;
                if (finished)
                {
                    rootFinished = true;
                    ReleaseRootScope();
                    ReleaseInstanceScopes();
                    return;
                }
                UpdateRenderScopes(current);
            }
            catch (Exception error)
            {
                FailPlayback(NativeResult.InternalError, "Unity render update failed: " + error.Message, current);
                enabled = false;
            }
        }

        public NativeResult Play(bool restart = true)
        {
            if (!effect || effect.PackageBytes == null || effect.PackageBytes.Length == 0)
                return Report(NativeResult.InvalidArgument, "Assign a nonempty LiberaFx Spawnable package.");
            if (host != null && !restart) return NativeResult.Ok;
            if (host != null)
            {
                var stopResult = Stop();
                if (stopResult != NativeResult.Ok) return stopResult;
                if (host != null)
                    return Report(NativeResult.Busy, "A reentrant callback started another playback; refusing to replace it.");
            }

            NativeSpawnableHost candidate = null;
            try
            {
                candidate = new NativeSpawnableHost(effect.PackageBytes,
                    (uint)Math.Max(1, maxActivePerTemplate),
                    (ulong)Math.Max(1024, memoryBudgetBytesPerTemplate),
                    (uint)Math.Max(1, eventCapacity), instructionBudget);
                var plan = Preflight(candidate, effect.PackageBytes);

                host = candidate;
                elapsed = 0;
                rootFinished = false;
                LastError = null;
                templateDefinitions.Clear();
                foreach (var pair in plan.templates) templateDefinitions.Add(pair.Key, pair.Value);
                rootRenderScope = LiberaFxRenderScope.CreatePending(transform, plan.root,
                    true, null, 0, 0, plan.bindings.RootGpuSeed);

                var current = host;
                var result = NativeResult.QueueRejected;
                for (var attempt = 0; attempt < 16; ++attempt)
                {
                    result = current.Advance(0, 0);
                    if (result == NativeResult.Ok)
                    {
                        rootRenderScope.Initialize(current, 0);
                        DrainEvents(current);
                        break;
                    }
                    DrainEvents(current);
                    if (host != current) return NativeResult.Busy;
                    if (result != NativeResult.QueueRejected) break;
                }
                if (host != current) return NativeResult.Busy;
                if (result != NativeResult.Ok)
                    return FailPlayback(result, "Native Spawnable initialization failed: " + result + ".", current);
                return NativeResult.Ok;
            }
            catch (Exception error)
            {
                var result = error is NotSupportedException ? NativeResult.UnsupportedBinary :
                    error is ArgumentException ? NativeResult.InvalidArgument :
                    error is InvalidOperationException && error.Message.StartsWith("LiberaFx:", StringComparison.Ordinal)
                        ? NativeResult.InvalidBinary : NativeResult.InvalidArgument;
                return FailPlayback(result, "Could not start LiberaFx effect: " + error.Message, candidate);
            }
        }

        public NativeResult RequestNaturalEnd()
        {
            var current = host;
            if (current == null) return NativeResult.NotReady;
            var result = current.RequestNaturalEnd();
            DrainEvents(current);
            return host != current ? NativeResult.Ok :
                result == NativeResult.Ok ? result : Report(result, "RequestNaturalEnd failed: " + result + ".");
        }

        public NativeResult DispatchSignal(string nodeGuid)
        {
            var current = host;
            if (current == null) return NativeResult.NotReady;
            var result = current.DispatchSignal(nodeGuid);
            DrainEvents(current);
            return host != current ? NativeResult.Ok :
                result == NativeResult.Ok ? result : Report(result, "Signal dispatch failed: " + result + ".");
        }

        public NativeResult FireGameSignal(string name)
        {
            var current = host;
            if (current == null) return NativeResult.NotReady;
            var result = current.FireGameSignal(name);
            DrainEvents(current);
            return host != current ? NativeResult.Ok :
                result == NativeResult.Ok ? result : Report(result, "Game signal failed: " + result + ".");
        }

        public NativeResult SetGameProperty(string name, double[] values)
        {
            var current = host;
            if (current == null) return NativeResult.NotReady;
            var result = current.SetGameProperty(name, values);
            return result == NativeResult.Ok ? result : Report(result, "Game property update failed: " + result + ".");
        }

        public NativeResult SetOwnerMatrix(double[] values)
        {
            var current = host;
            if (current == null) return NativeResult.NotReady;
            var result = current.SetOwnerMatrix(values);
            return result == NativeResult.Ok ? result : Report(result, "Owner matrix update failed: " + result + ".");
        }

        public NativeResult ReadRootValue(string nodeGuid, out NativeValue value)
        {
            if (host == null) { value = default; return NativeResult.NotReady; }
            return host.ReadRootValue(nodeGuid, out value);
        }

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
            var current = host;
            if (current == null)
            {
                ReleaseAllScopes();
                return NativeResult.Ok;
            }
            var result = NativeResult.QueueRejected;
            for (var attempt = 0; result == NativeResult.QueueRejected && attempt < 16; ++attempt)
            {
                result = current.Stop();
                DrainEvents(current);
                if (host != current) return NativeResult.Ok;
            }
            if (result != NativeResult.Ok)
            {
                var failure = Report(result, "Native Spawnable Stop failed: " + result + ".");
                ReleasePlayback(current);
                return failure;
            }
            ReleasePlayback(current);
            return NativeResult.Ok;
        }

        private PlaybackPlan Preflight(NativeSpawnableHost candidate, byte[] packageBytes)
        {
            var autoDestroyWhenIdle = ReadAutoDestroyWhenIdle(packageBytes);
            var bindings = new LiberaFxUnityBindingLookup(engineBindings);
            var renderResult = candidate.GetRootPayload(
                SpawnableTemplatePayloadKind.RenderConfiguration, out var render);
            var rootRender = GetPayload(renderResult, render);
            var containersResult = candidate.GetRootPayload(
                SpawnableTemplatePayloadKind.GpuContainers, out var containers);
            var rootContainers = GetPayload(containersResult, containers);
            if (autoDestroyWhenIdle && LiberaFxUnityBindingLookup.HasGpuContainers(rootContainers))
                throw new NotSupportedException(
                    "Root GPU programs cannot auto-drain with AutoDestroyWhenIdle. Set AutoDestroyWhenIdle=false " +
                    "and call RequestNaturalEnd or Stop explicitly.");
            var rootPrograms = GetGpuProgramsPayload(candidate, null, rootContainers);
            var root = bindings.Prepare("root", rootRender, rootContainers, rootPrograms);

            var countResult = candidate.GetTemplateCount(out var count);
            if (countResult != NativeResult.Ok || count > MaximumTemplates)
                throw new InvalidOperationException("Native template count is invalid or exceeds " + MaximumTemplates + ".");
            var templates = new Dictionary<string, LiberaFxPreparedScope>(StringComparer.Ordinal);
            for (ulong index = 0; index < count; ++index)
            {
                if (candidate.GetTemplateId(index, out var idBytes) != NativeResult.Ok ||
                    !TryGuidFromBigEndian(idBytes, out var templateId))
                    throw new InvalidOperationException("Could not read a valid native template identity.");
                var templateRenderResult = candidate.GetTemplatePayload(idBytes,
                    SpawnableTemplatePayloadKind.RenderConfiguration, out var templateRender);
                var templateContainersResult = candidate.GetTemplatePayload(idBytes,
                    SpawnableTemplatePayloadKind.GpuContainers, out var templateContainers);
                if (templateRenderResult != NativeResult.Ok || templateContainersResult != NativeResult.Ok)
                    throw new InvalidOperationException("Could not read template payload for " + templateId + ".");
                var gpuPrograms = GetGpuProgramsPayload(candidate, idBytes, templateContainers);
                var prepared = bindings.Prepare(templateId, templateRender, templateContainers, gpuPrograms);
                if (templates.ContainsKey(templateId))
                    throw new InvalidOperationException("Duplicate native template identity " + templateId + ".");
                templates.Add(templateId, prepared);
            }
            return new PlaybackPlan { bindings = bindings, root = root, templates = templates };
        }

        private byte[] GetGpuProgramsPayload(NativeSpawnableHost candidate, byte[] templateId, byte[] authoredContainers)
        {
            byte[] payload;
            var result = templateId == null
                ? candidate.GetRootPayload(SpawnableTemplatePayloadKind.GpuPrograms, out payload)
                : candidate.GetTemplatePayload(templateId, SpawnableTemplatePayloadKind.GpuPrograms, out payload);
            if (result == NativeResult.Ok) return payload ?? Array.Empty<byte>();
            if (result is NativeResult.InvalidArgument or NativeResult.NotReady &&
                !LiberaFxUnityBindingLookup.HasGpuContainers(authoredContainers))
                return Array.Empty<byte>();
            throw new InvalidOperationException("Could not read executable GPU payload: " + result + ".");
        }

        private static byte[] GetPayload(NativeResult result, byte[] payload)
        {
            if (result != NativeResult.Ok)
                throw new InvalidOperationException("Native render/GPU metadata access failed: " + result + ".");
            return payload ?? Array.Empty<byte>();
        }

        private void DrainEvents(NativeSpawnableHost current)
        {
            var events = new List<SpawnableHostEvent>();
            while (host == current)
            {
                var result = current.PopEvent(out var item);
                if (result != NativeResult.Ok)
                {
                    FailPlayback(result, "Native lifecycle event read failed: " + result + ".", current);
                    return;
                }
                if (item == null) break;
                events.Add(item);
            }
            ProcessHostEventBatch(current, events);
            if (host != current) return;
            while (host == current)
            {
                var result = current.PopGameIoEvent(out var ioEvent);
                if (result != NativeResult.Ok)
                {
                    FailPlayback(result, "Native Game I/O event read failed: " + result + ".", current);
                    return;
                }
                if (ioEvent == null) break;
                InvokeGameIoEvent(ioEvent);
                if (host != current) return;
            }
        }

        private void ProcessHostEventBatch(NativeSpawnableHost current, List<SpawnableHostEvent> events)
        {
            var terminalScopes = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var item in events)
                    if (item.Kind == StopEvent || item.Kind == DestroyEvent)
                        terminalScopes.Add(ScopeKey(item.ContainerId, item.InstanceId));
            }
            catch (Exception error)
            {
                FailPlayback(NativeResult.InternalError,
                    "Could not identify a native render scope: " + error.Message, current);
                return;
            }

            foreach (var item in events)
            {
                try
                {
                    var skipSpawn = item.Kind == SpawnEvent &&
                        terminalScopes.Contains(ScopeKey(item.ContainerId, item.InstanceId));
                    ProcessHostEvent(current, item, skipSpawn);
                }
                catch (Exception error)
                {
                    FailPlayback(NativeResult.InternalError,
                        "Could not create or retire render scope: " + error.Message, current);
                    return;
                }
                InvokeHostEvent(item);
                if (host != current) return;
            }
        }

        private void ProcessHostEvent(NativeSpawnableHost current, SpawnableHostEvent item, bool skipSpawn)
        {
            if (item.Kind == SignalEvent) return;
            var scopeKey = ScopeKey(item.ContainerId, item.InstanceId);
            if (item.Kind == SpawnEvent)
            {
                if (skipSpawn) return;
                var templateId = GuidFromBigEndian(item.ContainerId);
                if (!templateDefinitions.TryGetValue(templateId, out var definition))
                    throw new InvalidOperationException("Spawn event references unknown template " + templateId + ".");
                if (instanceScopes.ContainsKey(scopeKey))
                    throw new InvalidOperationException("Duplicate active Spawn event for instance " + item.InstanceId + ".");
                var scope = LiberaFxRenderScope.CreatePending(transform, definition, false,
                    item.ContainerId, item.InstanceId, item.Time, item.Seed);
                try
                {
                    scope.Initialize(current, elapsed);
                    instanceScopes.Add(scopeKey, scope);
                }
                catch
                {
                    scope.Dispose();
                    throw;
                }
            }
            else if (item.Kind == StopEvent || item.Kind == DestroyEvent)
            {
                // Stop values may already be finalized; release without reading them.
                // Destroy is a natural completion notification, not a synthetic Stop.
                if (instanceScopes.TryGetValue(scopeKey, out var scope))
                {
                    instanceScopes.Remove(scopeKey);
                    scope.Dispose();
                }
            }
            else throw new InvalidOperationException("Unknown native lifecycle event kind " + item.Kind + ".");
        }

        private void UpdateRenderScopes(NativeSpawnableHost current)
        {
            rootRenderScope?.Update(current, elapsed);
            if (host != current) return;
            if (instanceScopes.Count == 0) return;
            var scopes = new List<LiberaFxRenderScope>(instanceScopes.Values);
            foreach (var scope in scopes)
            {
                if (host != current) return;
                scope.Update(current, elapsed);
            }
        }

        private NativeResult FailPlayback(NativeResult result, string message,
            NativeInstance.NativeSpawnableHostBase candidate)
        {
            LastError = message;
            if (candidate != null)
            {
                for (var attempt = 0; attempt < 16; ++attempt)
                {
                    var stop = candidate.Stop();
                    if (stop != NativeResult.QueueRejected) break;
                }
            }
            if (host == candidate) host = null;
            ReleaseAllScopes();
            candidate?.Dispose();
            templateDefinitions.Clear();
            rootFinished = false;
            elapsed = 0;
            Debug.LogError("LiberaFx playback failed: " + message, this);
            InvokeRuntimeError(result);
            return result;
        }

        private void ReleasePlayback(NativeInstance.NativeSpawnableHostBase current)
        {
            if (host == current) host = null;
            ReleaseAllScopes();
            templateDefinitions.Clear();
            current?.Dispose();
            elapsed = 0;
            rootFinished = false;
        }

        private void ReleaseAllScopes()
        {
            ReleaseRootScope();
            ReleaseInstanceScopes();
        }

        private void ReleaseRootScope()
        {
            rootRenderScope?.Dispose();
            rootRenderScope = null;
        }

        private void ReleaseInstanceScopes()
        {
            foreach (var scope in instanceScopes.Values) scope.Dispose();
            instanceScopes.Clear();
        }

        private NativeResult Report(NativeResult result, string message)
        {
            LastError = message;
            Debug.LogError("LiberaFx playback error: " + message, this);
            InvokeRuntimeError(result);
            return result;
        }

        private void InvokeHostEvent(SpawnableHostEvent item)
        {
            var handlers = HostEventReceived;
            if (handlers == null) return;
            foreach (Action<SpawnableHostEvent> handler in handlers.GetInvocationList())
            {
                try { handler(item); }
                catch (Exception error) { Debug.LogException(error, this); }
            }
        }

        private void InvokeGameIoEvent(GameIoEvent item)
        {
            var handlers = GameIoEventReceived;
            if (handlers == null) return;
            foreach (Action<GameIoEvent> handler in handlers.GetInvocationList())
            {
                try { handler(item); }
                catch (Exception error) { Debug.LogException(error, this); }
            }
        }

        private void InvokeRuntimeError(NativeResult result)
        {
            var handlers = RuntimeError;
            if (handlers == null) return;
            foreach (Action<NativeResult> handler in handlers.GetInvocationList())
            {
                try { handler(result); }
                catch (Exception error) { Debug.LogException(error, this); }
            }
        }

        private static string ScopeKey(byte[] containerId, ulong instanceId) =>
            GuidFromBigEndian(containerId) + ":" + instanceId.ToString(CultureInfo.InvariantCulture);

        private static string GuidFromBigEndian(byte[] bytes)
        {
            if (!TryGuidFromBigEndian(bytes, out var value))
                throw new InvalidOperationException("Native container GUID must contain exactly 16 bytes.");
            return value;
        }

        private static bool TryGuidFromBigEndian(byte[] bytes, out string value)
        {
            value = null;
            if (bytes == null || bytes.Length != 16) return false;
            value = bytes[0].ToString("x2", CultureInfo.InvariantCulture) +
                bytes[1].ToString("x2", CultureInfo.InvariantCulture) +
                bytes[2].ToString("x2", CultureInfo.InvariantCulture) +
                bytes[3].ToString("x2", CultureInfo.InvariantCulture) + "-" +
                bytes[4].ToString("x2", CultureInfo.InvariantCulture) +
                bytes[5].ToString("x2", CultureInfo.InvariantCulture) + "-" +
                bytes[6].ToString("x2", CultureInfo.InvariantCulture) +
                bytes[7].ToString("x2", CultureInfo.InvariantCulture) + "-" +
                bytes[8].ToString("x2", CultureInfo.InvariantCulture) +
                bytes[9].ToString("x2", CultureInfo.InvariantCulture) + "-" +
                bytes[10].ToString("x2", CultureInfo.InvariantCulture) +
                bytes[11].ToString("x2", CultureInfo.InvariantCulture) +
                bytes[12].ToString("x2", CultureInfo.InvariantCulture) +
                bytes[13].ToString("x2", CultureInfo.InvariantCulture) +
                bytes[14].ToString("x2", CultureInfo.InvariantCulture) +
                bytes[15].ToString("x2", CultureInfo.InvariantCulture);
            return true;
        }

        private static bool ReadAutoDestroyWhenIdle(byte[] package)
        {
            if (package == null || package.Length < 25 || package[0] != (byte)'L' ||
                package[1] != (byte)'F' || package[2] != (byte)'X' || package[3] != (byte)'P')
                throw new InvalidOperationException("Spawnable package header is invalid.");
            var format = package[4] | package[5] << 8;
            var headerSize = package[6] | package[7] << 8;
            if (format < 1 || format > 3 || headerSize != 24 || package[24] > 1)
                throw new InvalidOperationException("Spawnable package header version or flags are unsupported.");
            return package[24] != 0;
        }

        private void OnDisable()
        {
            Stop();
        }

        private void OnDestroy()
        {
            var current = host;
            host = null;
            ReleaseAllScopes();
            templateDefinitions.Clear();
            current?.Dispose();
        }

        private sealed class PlaybackPlan
        {
            public LiberaFxUnityBindingLookup bindings;
            public LiberaFxPreparedScope root;
            public Dictionary<string, LiberaFxPreparedScope> templates;
        }
    }
}
