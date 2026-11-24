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
        [SerializeField] private CoordinateBasis coordinateBasis = CoordinateBasis.Unity;
        [SerializeField, Min(1)] private int maxActivePerTemplate = 32;
        [SerializeField, Min(1024)] private int memoryBudgetBytesPerTemplate = 4 * 1024 * 1024;
        [SerializeField, Min(1)] private int eventCapacity = 128;
        [SerializeField] private uint instructionBudget = 10000;
        [SerializeField] private bool usePreparedHosts = true;
        [SerializeField, Min(1)] private int preparedHostCapacity = 256;
        [SerializeField, Range(1, 16)] private int preparationWorkers = 4;
        [SerializeField] private bool useSharedSimulation = true;
        [SerializeField, Tooltip("Explicit mapping from this project's Unity quality indices to LIBERA FX tiers. Unmapped levels remain Unknown.")]
        private HostQualityTier[] qualityTierMapping = Array.Empty<HostQualityTier>();

        private NativeSpawnableHost host;
        public bool UsesPreparedStorage => host != null && host.UsesPreparedStorage;
        public int LastSimulationBatchSize { get; private set; }
        private LiberaFxRenderScope rootRenderScope;
        private readonly LiberaFxRenderStatisticsCounter renderStatistics =
            new LiberaFxRenderStatisticsCounter();
        private readonly Dictionary<string, LiberaFxPreparedScope> templateDefinitions =
            new Dictionary<string, LiberaFxPreparedScope>(StringComparer.Ordinal);
        private readonly Dictionary<string, LiberaFxRenderScope> instanceScopes =
            new Dictionary<string, LiberaFxRenderScope>(StringComparer.Ordinal);
        private double elapsed;
        private double realElapsed;
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
            if (debugPending && debugAdvance)
            {
                realElapsed = debugTime;
                if (current.GetTimeSnapshot(out var clock) == NativeResult.Ok) elapsed = clock.effectTime;
            }
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
        public bool IsPaused => paused && host != null && !rootFinished && !effectForced;
        private bool paused;
        public double ElapsedTime => elapsed;
        public double RealElapsedTime => realElapsed;
        public double TimeScale
        {
            get => playRate;
            set
            {
                UnityInstanceSession.RequireMainThread();
                if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 1000000)
                    throw new ArgumentOutOfRangeException(nameof(value));
                if (host != null && host.SetTimeScale(value) != NativeResult.Ok)
                    throw new InvalidOperationException("The effect time scale could not be changed.");
                playRate = (float)value;
            }
        }
        public string LastError { get; private set; }
        public int ActiveRenderScopeCount => (rootRenderScope == null ? 0 : 1) + instanceScopes.Count;

        /// <summary>Returns the effect-owned fluid texture; valid only until the scope stops.</summary>
        public bool TryGetFluidOutput(string nodeId, ulong instanceId, out RenderTexture texture)
        {
            texture = null;
            if (!LiberaFxUnityBindingLookup.TryCanonicalGuid(nodeId, out var canonical)) return false;
            if (instanceId == 0) return rootRenderScope != null && rootRenderScope.TryGetFluidOutput(canonical, out texture);
            foreach (var scope in instanceScopes.Values)
                if (scope.ScopeInstanceId == instanceId && scope.TryGetFluidOutput(canonical, out texture)) return true;
            return false;
        }

        /// <summary>Borrowed RGBA32F: logical velocity XY, density/depth Z, temperature W. No readback.</summary>
        public bool TryGetFluidFieldOutput(string nodeId, ulong instanceId, out RenderTexture texture)
        {
            texture = null;
            if (!LiberaFxUnityBindingLookup.TryCanonicalGuid(nodeId, out var canonical)) return false;
            if (instanceId == 0) return rootRenderScope != null && rootRenderScope.TryGetFluidOutput(canonical, out texture, true);
            foreach (var scope in instanceScopes.Values)
                if (scope.ScopeInstanceId == instanceId && scope.TryGetFluidOutput(canonical, out texture, true)) return true;
            return false;
        }

        /// <summary>Reports the most recent capped fluid step for a live scope.</summary>
        public bool TryGetFluidStats(string nodeId, ulong instanceId, out LibraFluid2DFrameStats stats)
        {
            stats = default;
            if (!LiberaFxUnityBindingLookup.TryCanonicalGuid(nodeId, out var canonical)) return false;
            if (instanceId == 0) return rootRenderScope != null && rootRenderScope.TryGetFluidStats(canonical, out stats);
            foreach (var scope in instanceScopes.Values)
                if (scope.ScopeInstanceId == instanceId && scope.TryGetFluidStats(canonical, out stats)) return true;
            return false;
        }
        public LiberaFxRenderStatistics RenderStatistics => renderStatistics.value;
        internal IEnumerable<LiberaFxDirectRenderNodeInstance> InspectDrawNodes()
        {
            if (rootRenderScope != null)
            {
                foreach (var node in rootRenderScope.DrawNodes)
                {
                    yield return node;
                }
            }
            foreach (var scope in instanceScopes.Values)
            {
                foreach (var node in scope.DrawNodes)
                {
                    yield return node;
                }
            }
        }

        internal NativeResult InspectCpuParticleCount(byte[] nodeId, out uint count)
        {
            count = 0;
            return host == null ? NativeResult.NotReady : host.CpuParticleCount(nodeId, out count);
        }

        internal NativeResult InspectCpuParticle(byte[] nodeId, uint index, out NativeCpuParticleSnapshot snapshot)
        {
            snapshot = default;
            return host == null ? NativeResult.NotReady : host.CpuParticleAt(nodeId, index, out snapshot);
        }

        internal NativeResult InspectCpuParticleCount(byte[] containerId, ulong instanceId,
            byte[] nodeId, out uint count)
        {
            count = 0;
            return host == null ? NativeResult.NotReady :
                host.CpuParticleCount(containerId, instanceId, nodeId, out count);
        }

        internal NativeResult InspectCpuParticle(byte[] containerId, ulong instanceId,
            byte[] nodeId, uint index, out NativeCpuParticleSnapshot snapshot)
        {
            snapshot = default;
            return host == null ? NativeResult.NotReady :
                host.CpuParticleAt(containerId, instanceId, nodeId, index, out snapshot);
        }

        internal bool TryGetRenderMatrix(string nodeId, ulong instanceId, out Matrix4x4 matrix)
        {
            if (instanceId == 0 && rootRenderScope != null)
            {
                return rootRenderScope.TryGetRenderMatrix(nodeId, out matrix);
            }
            foreach (var scope in instanceScopes.Values)
            {
                if (scope.ScopeInstanceId == instanceId && scope.TryGetRenderMatrix(nodeId, out matrix))
                {
                    return true;
                }
            }
            matrix = default;
            return false;
        }

        // Session factory hook: configure a NEW inactive owner before Play.
        // Never mutate an asset shared by another playing component.
        private bool manualPreviewTime;

        public NativeResult ConfigureForSession(LiberaFxSpawnableAsset package, LiberaFxUnityBindingAsset bindings)
            => ConfigureForSession(package, bindings, false);

        public NativeResult ConfigureForSession(LiberaFxSpawnableAsset package, LiberaFxUnityBindingAsset bindings,
            bool useManualPreviewTime)
        {
            UnityInstanceSession.RequireMainThread();
            if (host != null || gameObject.activeInHierarchy) return NativeResult.Busy;
            effect = package;
            engineBindings = bindings;
            playOnEnable = false;
            manualPreviewTime = useManualPreviewTime;
            return NativeResult.Ok;
        }

        private void OnEnable()
        {
            if (playOnEnable) Play();
        }

        private PreparedHostOptions PreparedOptions => new PreparedHostOptions {
            capacity = (uint)Math.Max(1, preparedHostCapacity), preparationWorkers = (uint)Math.Max(1, preparationWorkers),
            eventCapacity = (uint)Math.Max(1, eventCapacity), instructionBudget = instructionBudget };

        // Call while loading, before actors start casting. Awake also prepares
        // assigned scene effects, once per shared asset/options combination.
        public NativeResult PrewarmEffect()
        {
            UnityInstanceSession.RequireMainThread();
            if (!Application.isPlaying || manualPreviewTime || !effect || effect.PackageBytes == null) return NativeResult.NotReady;
            return LiberaFxSharedSimulation.Prewarm(effect.PackageBytes, PreparedOptions);
        }
        private void Awake()
        {
            if (usePreparedHosts && Application.isPlaying && effect) PrewarmEffect();
        }

        // The default Unity Animator has completed its pose evaluation before
        // LateUpdate. Copy Source matrices here before queueing worker evaluation.
        private void LateUpdate()
        {
            var current = host;
            if (current == null || rootFinished || manualPreviewTime || paused) return;
            if (sourceInitialAdvancePending)
            {
                if (!PublishFrameInputs(current)) return;
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

            var scaleResult = current.SetTimeScale(rate);
            if (scaleResult != NativeResult.Ok) { FailPlayback(scaleResult, "Updating Time Scale failed.", current); return; }
            if (useSharedSimulation && Application.isPlaying &&
                PreparedNative.libra_spawnable_host_execution_capabilities(current.RuntimeHandle, out var capabilities) == NativeResult.Ok && (capabilities & 1) != 0)
            {
                if (!PublishFrameInputs(current)) return;
                var quality = current.SetHostQuality(UnityHostQuality.Read(qualityTierMapping, (ulong)Time.frameCount));
                if (quality != NativeResult.Ok) { FailPlayback(quality, "Publishing host quality failed.", current); return; }
                if (LiberaFxSharedSimulation.Queue(this, current, realElapsed + frameDelta, frameDelta)) return;
            }
            AdvanceFrame(current, realElapsed + frameDelta, frameDelta);
        }

        internal bool SharedFrameIsCurrent(NativeSpawnableHost current) =>
            isActiveAndEnabled && ReferenceEquals(host, current) && !rootFinished && !effectForced && !debugging;

        internal void CompleteSharedFrame(NativeSpawnableHost current, double time, double delta, NativeResult result, int batchSize)
        {
            if (!SharedFrameIsCurrent(current)) return;
            LastSimulationBatchSize = batchSize;
            realElapsed = time;
            if (current.GetTimeSnapshot(out var clock) == NativeResult.Ok) elapsed = clock.effectTime;
            DrainEvents(current);
            if (!SharedFrameIsCurrent(current)) return;
            if (result == NativeResult.QueueRejected) { AdvanceFrame(current, time, delta); return; }
            if (result != NativeResult.Ok) { FailPlayback(result, "Shared simulation failed: " + result, current); return; }
            FinishFrame(current);
        }

        public NativeResult AdvancePreview(double time)
        {
            UnityInstanceSession.RequireMainThread();
            if (scriptActionDepth != 0)
            {
                return NativeResult.Busy;
            }
            if (!manualPreviewTime || host == null || debugging || sourceInitialAdvancePending)
            {
                return NativeResult.NotReady;
            }
            if (double.IsNaN(time) || double.IsInfinity(time) || time < elapsed)
            {
                return NativeResult.InvalidArgument;
            }
            if (rootFinished || time == elapsed)
            {
                return NativeResult.Ok;
            }
            return AdvanceFrame(host, time, time - elapsed);
        }

        private NativeResult AdvanceFrame(NativeSpawnableHost current, double time, double frameDelta)
        {
            if (!PublishFrameInputs(current))
            {
                return NativeResult.NotReady;
            }
            realElapsed = time;
            var qualityResult = current.SetHostQuality(UnityHostQuality.Read(qualityTierMapping, (ulong)Time.frameCount));
            if (qualityResult != NativeResult.Ok)
            {
                FailPlayback(qualityResult, "Publishing host quality failed: " + qualityResult + ".", current);
                enabled = false;
                return qualityResult;
            }
            var result = NativeResult.QueueRejected;
            for (var attempt = 0; attempt < 16; ++attempt)
            {
                result = current.Advance(realElapsed, frameDelta);
                var clockResult = current.GetTimeSnapshot(out var clock);
                if (clockResult == NativeResult.Ok) elapsed = clock.effectTime;
                DrainEvents(current);
                if (host != current || effectForced)
                {
                    return result;
                }
                if (result != NativeResult.QueueRejected)
                {
                    break;
                }
            }
            if (result != NativeResult.Ok)
            {
                FailPlayback(result, "Native Spawnable advance failed: " + result + ".", current);
                enabled = false;
                return result;
            }

            FinishFrame(current);
            return NativeResult.Ok;
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
                realElapsed = debugTime;
                if (current.GetTimeSnapshot(out var clock) == NativeResult.Ok) elapsed = clock.effectTime;
                FinishFrame(current);
                return;
            }
            debugDelta = Time.deltaTime;
            debugTime = realElapsed + debugDelta;
            if (playRate < 0 || double.IsNaN(debugTime) || double.IsInfinity(debugTime))
            {
                FailPlayback(NativeResult.InvalidArgument, "Invalid debugger playback time.", current);
                return;
            }
            if (!PublishFrameInputs(current)) return;
            var status = current.SetTimeScale(playRate);
            if (status == NativeResult.Ok) status = current.SetHostQuality(UnityHostQuality.Read(qualityTierMapping, (ulong)Time.frameCount));
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

        private bool PublishFrameInputs(NativeSpawnableHost current)
        {
            var cameraResult = PublishCurrentCamera(current);
            if (cameraResult != NativeResult.Ok)
            {
                FailPlayback(cameraResult, "Publishing Current Camera failed: " + cameraResult + ".", current);
                enabled = false;
                return false;
            }
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
            if (host != null && !restart)
            {
                if (effectForced) return NativeResult.NotReady;
                paused = false;
                return NativeResult.Ok;
            }
            paused = false;
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
                var acquired = usePreparedHosts && Application.isPlaying && !manualPreviewTime
                    ? LiberaFxSharedSimulation.Acquire(effect.PackageBytes, PreparedOptions, out candidate) : NativeResult.NotReady;
                if (acquired == NativeResult.InsufficientWork) return Report(acquired, "Prepared host pool is full; increase its loading-time capacity.");
                if (acquired != NativeResult.Ok && acquired != NativeResult.NotReady && acquired != NativeResult.UnsupportedBinary)
                    return Report(acquired, "Prepared host acquisition failed.");
                if (candidate == null) candidate = new NativeSpawnableHost(effect.PackageBytes,
                    (uint)Math.Max(1, maxActivePerTemplate),
                    (ulong)Math.Max(1024, memoryBudgetBytesPerTemplate),
                    (uint)Math.Max(1, eventCapacity), instructionBudget);
                var clockSetup = candidate.SetTimeScale(manualPreviewTime ? 1 : playRate);
                if (clockSetup != NativeResult.Ok) throw new InvalidOperationException("Invalid effect Time Scale: " + clockSetup);
                NativeInstance.Check(candidate.SetCoordinateBasis(coordinateBasis));
                var plan = Preflight(candidate, effect.PackageBytes);

                host = candidate;
                ownerThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                elapsed = 0;
                realElapsed = 0;
                rootFinished = false;
                effectForced = false;
                LastSimulationBatchSize = 0;
                renderStatistics.Reset();
                sourceInitialAdvancePending = false;
                skeletalPoseSources.Clear();
                cancelledBatchScopes.Clear();
                LastError = null;
                templateDefinitions.Clear();
                foreach (var pair in plan.templates) templateDefinitions.Add(pair.Key, pair.Value);
                rootRenderScope = LiberaFxRenderScope.CreatePending(transform, plan.root,
                    true, null, 0, 0, plan.bindings.RootGpuSeed, suppressPreviewDebugDraw,
                    renderStatistics, coordinateBasis);

                var current = host;
                BindCurrentCamera(current);
                if (!LiberaFxSkeletalPoseSourcePorts.TryRead(current,
                    out var sourceInputs, out var sourceError))
                    throw new InvalidOperationException("LiberaFx: " + sourceError);
                skeletalPoseSources.AddRange(sourceInputs);
                sourceInitialAdvancePending = skeletalPoseSources.Count != 0;
                if (sourceInitialAdvancePending)
                    return NativeResult.Ok;

                NativeInstance.Check(PublishCurrentCamera(current));
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
            if (scriptActionDepth != 0)
            {
                return NativeResult.Busy;
            }
            var current = host;
            if (current == null) return NativeResult.NotReady;
            if (debugging) return SubmitDebug(2, name: name);
            var result = current.FireGameSignal(name);
            DrainEvents(current);
            return host != current ? NativeResult.Ok :
                result == NativeResult.Ok ? result : Report(result, "Game signal failed: " + result + ".");
        }

        public NativeResult SetGameProperty(string name, bool value) =>
            SetGameProperty(name, new double[] { value ? 1 : 0 });

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

        public NativeResult SetOwnerMatrix(Matrix4x4 matrix)
        {
            var values = new double[16];
            for (var row = 0; row < 4; row++)
                for (var column = 0; column < 4; column++) values[row * 4 + column] = matrix[row, column];
            return SetOwnerMatrix(values);
        }

        // Opt-in automatic binding for moving previews and ordinary gameplay owners.
        public NativeResult SynchronizeOwnerTransform()
        {
            var current = host;
            if (current == null) return NativeResult.NotReady;
            for (var index = 0u; index < 256; index++)
            {
                var result = current.GetGameIoPortName(index, out var name);
                if (result == NativeResult.InvalidArgument) return NativeResult.Ok;
                if (result != NativeResult.Ok) return result;
                if (name == "OwnerMatrix") return SetOwnerMatrix(transform.localToWorldMatrix);
            }
            return NativeResult.Ok;
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

        public NativeResult PopCpuParticleEvent(out CpuParticleEvent particleEvent, out bool hasEvent)
        {
            return PopScopedCpuParticleEvent(null, 0, out particleEvent, out hasEvent);
        }

        public NativeResult PopScopedCpuParticleEvent(byte[] containerId, ulong instanceId,
            out CpuParticleEvent particleEvent, out bool hasEvent)
        {
            particleEvent = default;
            hasEvent = false;
            var current = host;
            if (current == null) return NativeResult.NotReady;
            if (debugPending || ownerThreadId != System.Threading.Thread.CurrentThread.ManagedThreadId)
                return NativeResult.Busy;
            return current.PopCpuParticleEvent(containerId, instanceId, out particleEvent, out hasEvent);
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

        public NativeResult Pause()
        {
            UnityInstanceSession.RequireMainThread();
            if (host == null || rootFinished || effectForced) return NativeResult.NotReady;
            paused = true;
            return NativeResult.Ok;
        }

        public NativeResult Resume()
        {
            UnityInstanceSession.RequireMainThread();
            if (host == null || rootFinished || effectForced) return NativeResult.NotReady;
            paused = false;
            return NativeResult.Ok;
        }

        public NativeResult Stop()
        {
            paused = false;
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
            var root = bindings.Prepare("root", rootRender, rootContainers, rootPrograms, true);

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
            if (format < 1 || format > 5 || headerSize != 24 || package[24] > 1)
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
