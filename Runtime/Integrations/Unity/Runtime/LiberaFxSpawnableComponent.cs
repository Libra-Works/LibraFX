using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace LiberaFx.Unity
{
    [DisallowMultipleComponent]
    public sealed partial class LiberaFxSpawnableComponent : MonoBehaviour
    {
        private const uint SpawnEvent = 1;
        private const uint StopEvent = 2;
        private const uint DestroyEvent = 3;
        private const uint SignalEvent = 4;
        private const uint ForceTerminatedEvent = 5;
        private const int MaximumTemplates = 512;

        [SerializeField] private LiberaFxSpawnableAsset effect;
        [SerializeField] private LiberaFxUnityBindingAsset engineBindings;
        [SerializeField] private bool playOnEnable = true;
        [SerializeField, Min(0)] private float playRate = 1;
        [SerializeField, Min(1)] private int maxActivePerTemplate = 32;
        [SerializeField, Min(1024)] private int memoryBudgetBytesPerTemplate = 4 * 1024 * 1024;
        [SerializeField, Min(1)] private int eventCapacity = 128;
        [SerializeField] private uint instructionBudget = 10000;
        [SerializeField, Tooltip("Explicit mapping from this project's Unity quality indices to LIBERA FX tiers. Unmapped levels remain Unknown.")]
        private HostQualityTier[] qualityTierMapping = Array.Empty<HostQualityTier>();

        private NativeSpawnableHost host;
        private LiberaFxRenderScope rootRenderScope;
        private readonly Dictionary<string, LiberaFxPreparedScope> templateDefinitions =
            new Dictionary<string, LiberaFxPreparedScope>(StringComparer.Ordinal);
        private readonly Dictionary<string, LiberaFxRenderScope> instanceScopes =
            new Dictionary<string, LiberaFxRenderScope>(StringComparer.Ordinal);
        private double elapsed;
        private bool rootFinished;
        private bool effectForced;
        private bool sourceInitialAdvancePending;
        private readonly List<LiberaFxSkeletalPoseSourcePorts> skeletalPoseSources =
            new List<LiberaFxSkeletalPoseSourcePorts>();
        private int ownerThreadId;
        private bool debugging, debugPending, debugAdvance;
        private double debugTime, debugDelta;
        private int debugRetries;
        public event Action<NativeResult> DebugCommandCompleted;

        public NativeResult GetDebuggerGeneration(out ulong generation)
        {
            generation = 0;
            if (host == null) return NativeResult.NotReady;
            try { return host.DebugGeneration(out generation); }
            catch (EntryPointNotFoundException) { return NativeResult.UnsupportedBinary; }
        }

        public NativeResult AttachExecutionDebugger(ulong generation, byte[] container, ulong instance, string invitation)
        {
            if (host == null) return NativeResult.NotReady;
            if (ownerThreadId != System.Threading.Thread.CurrentThread.ManagedThreadId || eventDrainDepth != 0 ||
                debugging || sourceInitialAdvancePending)
                return NativeResult.Busy;
            if (Application.platform != RuntimePlatform.WindowsEditor && Application.platform != RuntimePlatform.WindowsPlayer)
                return NativeResult.UnsupportedBinary;
            try
            {
                var result = host.AttachDebugger(generation, container, instance, invitation);
                debugging = result == NativeResult.Ok;
                return result;
            }
            catch (EntryPointNotFoundException) { return NativeResult.UnsupportedBinary; }
        }

        public NativeResult DetachExecutionDebugger()
        {
            if (!debugging || host == null) return NativeResult.Ok;
            if (ownerThreadId != System.Threading.Thread.CurrentThread.ManagedThreadId)
                return NativeResult.Busy;
            var current = host;
            var result = current.DetachDebugger();
            if (result != NativeResult.Ok) return result;
            if (debugPending && debugAdvance) elapsed = debugTime;
            debugging = debugPending = false;
            if (eventDrainDepth == 0) DrainEvents(current);
            return NativeResult.Ok;
        }

        private NativeResult SubmitDebug(byte operation, string node = null, string name = null,
            byte[] container = null, ulong instance = 0)
        {
            if (debugPending || eventDrainDepth != 0) return NativeResult.Busy;
            var result = host.SubmitDebugger(operation, container: container, instance: instance, node: node, name: name);
            if (result == NativeResult.Ok) { debugPending = true; debugAdvance = false; }
            return result;
        }
        // Only retain cancellations while nested event batches can still hold
        // already-popped notices. Never accumulate every retired ID forever.
        private int eventDrainDepth;
        private readonly HashSet<string> cancelledBatchScopes = new HashSet<string>(StringComparer.Ordinal);

        public event Action<SpawnableHostEvent> HostEventReceived;
        public event Action<GameIoEvent> GameIoEventReceived;
        public event Action<NativeResult> RuntimeError;

        public bool IsRootFinished => rootFinished;
        public bool IsForceTerminated => effectForced;
        public double ElapsedTime => elapsed;
        public string LastError { get; private set; }
        public int ActiveRenderScopeCount => (rootRenderScope == null ? 0 : 1) + instanceScopes.Count;

        // Session factory hook: configure a NEW inactive owner before Play.
        // Never mutate an asset shared by another playing component.
        public NativeResult ConfigureForSession(LiberaFxSpawnableAsset package, LiberaFxUnityBindingAsset bindings)
        {
            UnityInstanceSession.RequireMainThread();
            if (host != null || gameObject.activeInHierarchy) return NativeResult.Busy;
            effect = package;
            engineBindings = bindings;
            playOnEnable = false;
            return NativeResult.Ok;
        }

        private void OnEnable()
        {
            if (playOnEnable) Play();
        }

        // The default Unity Animator has completed its pose evaluation before
        // LateUpdate. Copy Source matrices here before queueing worker evaluation.
        private void LateUpdate()
        {
            var current = host;
            if (current == null || rootFinished) return;
            if (sourceInitialAdvancePending)
            {
                if (!PublishSkeletalPoseSources(current)) return;
                var quality = current.SetHostQuality(
                    UnityHostQuality.Read(qualityTierMapping, (ulong)Time.frameCount));
                if (quality != NativeResult.Ok)
                {
                    FailPlayback(quality, "Publishing host quality failed: " + quality + ".", current);
                    enabled = false;
                    return;
                }
                var startResult = NativeResult.QueueRejected;
                for (var attempt = 0; attempt < 16; ++attempt)
                {
                    startResult = current.Advance(0, 0);
                    DrainEvents(current);
                    if (host != current || effectForced) return;
                    if (startResult != NativeResult.QueueRejected) break;
                }
                if (startResult != NativeResult.Ok)
                {
                    FailPlayback(startResult,
                        "Native Spawnable initialization failed: " + startResult + ".", current);
                    enabled = false;
                    return;
                }
                sourceInitialAdvancePending = false;
                rootRenderScope.Initialize(current, 0);
                FinishFrame(current);
                return;
            }
            if (debugging) { UpdateDebugger(current); return; }
            var frameDelta = (double)Time.deltaTime;
            var rate = (double)playRate;
            if (double.IsNaN(rate) || double.IsInfinity(rate) || rate < 0)
            {
                FailPlayback(NativeResult.InvalidArgument, "Play Rate must be finite and nonnegative.", current);
                enabled = false;
                return;
            }

            if (!PublishSkeletalPoseSources(current)) return;
            elapsed += frameDelta * rate;
            var qualityResult = current.SetHostQuality(UnityHostQuality.Read(qualityTierMapping, (ulong)Time.frameCount));
            if (qualityResult != NativeResult.Ok)
            {
                FailPlayback(qualityResult, "Publishing host quality failed: " + qualityResult + ".", current);
                enabled = false;
                return;
            }
            var result = NativeResult.QueueRejected;
            for (var attempt = 0; attempt < 16; ++attempt)
            {
                result = current.Advance(elapsed, frameDelta);
                DrainEvents(current);
                if (host != current) return;
                if (effectForced) return;
                if (result != NativeResult.QueueRejected) break;
            }
            if (result != NativeResult.Ok)
            {
                FailPlayback(result, "Native Spawnable advance failed: " + result + ".", current);
                enabled = false;
                return;
            }

            FinishFrame(current);
        }

        private void UpdateDebugger(NativeSpawnableHost current)
        {
            if (debugPending)
            {
                var poll = current.PollDebugger(out var result);
                if (poll == NativeResult.Busy) return;
                debugPending = false;
                if (poll != NativeResult.Ok) result = poll;
                DrainEvents(current);
                if (host != current || !debugging || effectForced) return;
                if (!debugAdvance)
                {
                    DebugCommandCompleted?.Invoke(result);
                    if (host == current && !debugPending) FinishFrame(current);
                    return;
                }
                if (result == NativeResult.QueueRejected && ++debugRetries < 16)
                {
                    result = current.SubmitDebugger(0, debugTime, debugDelta);
                    if (result == NativeResult.Ok) { debugPending = true; return; }
                }
                if (result != NativeResult.Ok)
                {
                    FailPlayback(result, "Debug Advance failed: " + result, current);
                    return;
                }
                elapsed = debugTime;
                FinishFrame(current);
                return;
            }
            debugDelta = Time.deltaTime;
            debugTime = elapsed + debugDelta * playRate;
            if (playRate < 0 || double.IsNaN(debugTime) || double.IsInfinity(debugTime))
            {
                FailPlayback(NativeResult.InvalidArgument, "Invalid debugger playback time.", current);
                return;
            }
            if (!PublishSkeletalPoseSources(current)) return;
            var status = current.SetHostQuality(UnityHostQuality.Read(qualityTierMapping, (ulong)Time.frameCount));
            if (status == NativeResult.Ok) status = current.SubmitDebugger(0, debugTime, debugDelta);
            if (status == NativeResult.Ok) { debugPending = debugAdvance = true; debugRetries = 0; }
            else FailPlayback(status, "Could not queue debug Advance: " + status, current);
        }

        private void FinishFrame(NativeSpawnableHost current)
        {
            try
            {
                if (current.IsRootFinished(out var finished) != NativeResult.Ok)
                    throw new InvalidOperationException("Native root completion query failed.");
                if (host != current) return;
                if (finished)
                {
                    rootFinished = true;
                    ReleaseAllScopes();
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

        private bool PublishSkeletalPoseSources(NativeSpawnableHost current)
        {
            foreach (var source in skeletalPoseSources)
            {
                var result = source.Publish(this, current);
                if (result == NativeResult.Ok) continue;
                FailPlayback(result, "Publishing SOURCE pose inputs failed: " + result + ".", current);
                enabled = false;
                return false;
            }
            return true;
        }

        public NativeResult Play(bool restart = true)
        {
            if (!effect || effect.PackageBytes == null || effect.PackageBytes.Length == 0)
                return Report(NativeResult.InvalidArgument, "Assign a nonempty LiberaFx Spawnable package.");
            if (host != null && !restart) return effectForced ? NativeResult.NotReady : NativeResult.Ok;
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
                ownerThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                elapsed = 0;
                rootFinished = false;
                effectForced = false;
                sourceInitialAdvancePending = false;
                skeletalPoseSources.Clear();
                cancelledBatchScopes.Clear();
                LastError = null;
                templateDefinitions.Clear();
                foreach (var pair in plan.templates) templateDefinitions.Add(pair.Key, pair.Value);
                rootRenderScope = LiberaFxRenderScope.CreatePending(transform, plan.root,
                    true, null, 0, 0, plan.bindings.RootGpuSeed, suppressPreviewDebugDraw);

                var current = host;
                if (!LiberaFxSkeletalPoseSourcePorts.TryRead(current,
                    out var sourceInputs, out var sourceError))
                    throw new InvalidOperationException("LiberaFx: " + sourceError);
                skeletalPoseSources.AddRange(sourceInputs);
                sourceInitialAdvancePending = skeletalPoseSources.Count != 0;
                if (sourceInitialAdvancePending)
                    return NativeResult.Ok;

                NativeInstance.Check(current.SetHostQuality(
                    UnityHostQuality.Read(qualityTierMapping, (ulong)Time.frameCount)));
                var result = NativeResult.QueueRejected;
                for (var attempt = 0; attempt < 16; ++attempt)
                {
                    result = current.Advance(0, 0);
                    if (result == NativeResult.Ok)
                    {
                        // A reentrant batch may already hold a retired target's
                        // notice, so an idempotent native force need not emit a new
                        // kind 5. Retire that adapter snapshot immediately as well.
                        rootRenderScope.Initialize(current, 0);
                        DrainEvents(current);
                        break;
                    }
                    DrainEvents(current);
                    if (host != current) return NativeResult.Busy;
                    if (effectForced) return NativeResult.Ok;
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
            if (debugging) return SubmitDebug(3);
            var result = current.RequestNaturalEnd();
            DrainEvents(current);
            return host != current ? NativeResult.Ok :
                result == NativeResult.Ok ? result : Report(result, "RequestNaturalEnd failed: " + result + ".");
        }

        // Whole effect, including root GPU and all generated children. This
        // component is the public owner; no extra author-added component needed.
        public NativeResult ForceTerminate() => ForceTerminateScope(null, 0);

        public NativeResult ForceTerminateChild(byte[] containerId, ulong instanceId)
        {
            if (containerId == null || containerId.Length != 16 || instanceId == 0)
                return NativeResult.InvalidArgument;
            return ForceTerminateScope(containerId, instanceId);
        }

        private NativeResult ForceTerminateScope(byte[] containerId, ulong instanceId)
        {
            var current = host;
            if (current == null) return NativeResult.NotReady;
            if (debugPending) return NativeResult.Busy;
            if (ownerThreadId != System.Threading.Thread.CurrentThread.ManagedThreadId) return NativeResult.Busy;
            var wholeEffect = containerId == null;
            var result = NativeResult.QueueRejected;
            for (var attempt = 0; attempt < 16; ++attempt)
            {
                result = wholeEffect ? current.ForceTerminate() :
                    current.ForceTerminateChild(containerId, instanceId);
                if (result == NativeResult.Ok)
                {
                    if (wholeEffect)
                    {
                        effectForced = rootFinished = true;
                        ReleaseAllScopes();
                    }
                    else
                    {
                        cancelledBatchScopes.Add(ScopeKey(containerId, instanceId));
                        ReleaseChildScope(ScopeKey(containerId, instanceId));
                    }
                }
                if (host != current) return NativeResult.Ok;
                DrainEvents(current); // handles cleanup before forwarding kind 5
                if (host != current) return NativeResult.Ok;
                if (result != NativeResult.QueueRejected) break;
            }
            return result == NativeResult.Ok ? result : Report(result, "Force termination failed: " + result + ".");
        }

        // nodeGuid names user logic, not a fixed End mode or natural Destroy.
        public NativeResult NotifyEndRequest(string nodeGuid)
        {
            var current = host;
            if (current == null) return NativeResult.NotReady;
            if (debugging) return SubmitDebug(4, node: nodeGuid);
            if (ownerThreadId != System.Threading.Thread.CurrentThread.ManagedThreadId) return NativeResult.Busy;
            var result = current.NotifyEndRequest(nodeGuid);
            DrainEvents(current);
            return host != current || result == NativeResult.Ok ? NativeResult.Ok :
                Report(result, "End request notification failed: " + result + ".");
        }

        public NativeResult NotifyChildEndRequest(byte[] containerId, ulong instanceId, string nodeGuid)
        {
            var current = host;
            if (current == null) return NativeResult.NotReady;
            if (debugging) return SubmitDebug(4, node: nodeGuid, container: containerId, instance: instanceId);
            if (ownerThreadId != System.Threading.Thread.CurrentThread.ManagedThreadId) return NativeResult.Busy;
            var result = current.NotifyChildEndRequest(containerId, instanceId, nodeGuid);
            DrainEvents(current);
            return host != current || result == NativeResult.Ok ? NativeResult.Ok :
                Report(result, "Child End request notification failed: " + result + ".");
        }

        public NativeResult DispatchSignal(string nodeGuid)
        {
            var current = host;
            if (current == null) return NativeResult.NotReady;
            if (debugging) return SubmitDebug(1, node: nodeGuid);
            var result = current.DispatchSignal(nodeGuid);
            DrainEvents(current);
            return host != current ? NativeResult.Ok :
                result == NativeResult.Ok ? result : Report(result, "Signal dispatch failed: " + result + ".");
        }

        public NativeResult FireGameSignal(string name)
        {
            var current = host;
            if (current == null) return NativeResult.NotReady;
            if (debugging) return SubmitDebug(2, name: name);
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

        public NativeResult GetInstanceTarget(byte[] container, ulong instance, out ulong generation)
        {
            generation = 0;
            return host == null ? NativeResult.NotReady : host.GetInstanceTarget(container, instance, out generation);
        }

        public NativeResult SetInstanceProperty(ulong generation, byte[] container, ulong instance,
            string name, double[] values) => host == null ? NativeResult.NotReady :
            host.SetInstanceProperty(generation, container, instance, name, values);

        public NativeResult GetInstanceProperty(ulong generation, byte[] container, ulong instance,
            string name, out double[] values)
        {
            values = null;
            return host == null ? NativeResult.NotReady :
                host.GetInstanceProperty(generation, container, instance, name, out values);
        }

        public NativeResult FireInstanceSignal(ulong generation, byte[] container, ulong instance, string name)
        {
            var current = host;
            if (current == null) return NativeResult.NotReady;
            if (debugging) return NativeResult.Busy;
            var result = current.FireInstanceSignal(generation, container, instance, name);
            DrainEvents(current);
            return host != current ? NativeResult.Ok : result;
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
            if (debugging)
            {
                var detached = DetachExecutionDebugger();
                if (detached != NativeResult.Ok || host != current) return detached;
            }
            if (effectForced)
            {
                ReleasePlayback(current);
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
            ++eventDrainDepth;
            try { DrainEventBatch(current); }
            finally
            {
                if (--eventDrainDepth == 0) cancelledBatchScopes.Clear();
            }
        }

        private void DrainEventBatch(NativeSpawnableHost current)
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
            while (host == current && !effectForced)
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
            var wholeEffectTerminated = false;
            try
            {
                foreach (var item in events)
                    if (item.Kind == ForceTerminatedEvent && IsWholeEffect(item))
                        wholeEffectTerminated = true;
                    else if (item.Kind == StopEvent || item.Kind == DestroyEvent || item.Kind == ForceTerminatedEvent)
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
                if (host != current) return;
                if (item.Kind != ForceTerminatedEvent &&
                    (effectForced || cancelledBatchScopes.Contains(ScopeKey(item.ContainerId, item.InstanceId))))
                    continue;
                try
                {
                    var skipSpawn = item.Kind == SpawnEvent &&
                        (wholeEffectTerminated || terminalScopes.Contains(ScopeKey(item.ContainerId, item.InstanceId)));
                    ProcessHostEvent(current, item, skipSpawn);
                }
                catch (Exception error)
                {
                    FailPlayback(NativeResult.InternalError,
                        "Could not create or retire render scope: " + error.Message, current);
                    return;
                }
                if (host != current) return;
                InvokeHostEvent(item);
                if (host != current) return;
            }
        }

        private void ProcessHostEvent(NativeSpawnableHost current, SpawnableHostEvent item, bool skipSpawn)
        {
            if (item.Kind == ForceTerminatedEvent && IsWholeEffect(item))
            {
                effectForced = rootFinished = true;
                ReleaseAllScopes(); // includes every GPU owner
                return;
            }
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
                    item.ContainerId, item.InstanceId, item.Time, item.Seed, suppressPreviewDebugDraw);
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
            else if (item.Kind == StopEvent || item.Kind == DestroyEvent || item.Kind == ForceTerminatedEvent)
            {
                // Stop values may already be finalized; release without reading them.
                // Destroy is a natural completion notification, not a synthetic Stop.
                if (item.Kind == ForceTerminatedEvent) cancelledBatchScopes.Add(scopeKey);
                ReleaseChildScope(scopeKey);
            }
            else throw new InvalidOperationException("Unknown native lifecycle event kind " + item.Kind + ".");
        }

        private static bool IsWholeEffect(SpawnableHostEvent item)
        {
            if (item.InstanceId != 0 || item.ContainerId == null || item.ContainerId.Length != 16) return false;
            foreach (var value in item.ContainerId) if (value != 0) return false;
            return true;
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
                if (candidate == host && debugging)
                {
                    candidate.DetachDebugger();
                    debugging = debugPending = false;
                }
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
            skeletalPoseSources.Clear();
            sourceInitialAdvancePending = false;
            rootFinished = false;
            effectForced = false;
            elapsed = 0;
            Debug.LogError("LiberaFx playback failed: " + message, this);
            InvokeRuntimeError(result);
            return result;
        }

        private void ReleasePlayback(NativeInstance.NativeSpawnableHostBase current)
        {
            if (host == current)
            {
                host = null;
                debugging = debugPending = false;
            }
            ReleaseAllScopes();
            templateDefinitions.Clear();
            skeletalPoseSources.Clear();
            sourceInitialAdvancePending = false;
            current?.Dispose();
            elapsed = 0;
            rootFinished = false;
            effectForced = false;
        }

        private void ReleaseAllScopes()
        {
            var root = rootRenderScope;
            var children = new List<LiberaFxRenderScope>(instanceScopes.Values);
            rootRenderScope = null;
            instanceScopes.Clear();
            root?.Dispose();
            foreach (var child in children) child.Dispose();
        }

        private void ReleaseChildScope(string key)
        {
            if (!instanceScopes.TryGetValue(key, out var scope)) return;
            instanceScopes.Remove(key);
            scope.Dispose();
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
            debugging = debugPending = false;
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
