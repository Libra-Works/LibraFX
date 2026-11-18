using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;

namespace LiberaFx.Services;

/// <summary>
/// Root owns native execution. UE consumes evaluated render defaults, not the
/// package or generator events. One serialized owner per preview connection.
/// </summary>
public sealed class EditorNativePreviewSession : IDisposable
{
    public const string ExecutionOwner = "editor-native";
    public const int StepsPerSecond = 60;
    public const int MaximumReplaySeconds = 120;
    private SpawnableGroupPackage _package;
    private readonly GraphSnapshot _graph;
    private readonly bool _hasSignalKeys;
    private readonly byte[] _rootRender;
    private readonly string _library;
    private EditorNativeHostSession? _native;
    private long _step = -1;
    private bool _disposed;
    private PreviewPlaybackState? _signalPlayback;
    private double _signalSampleTime = -1;
    private bool _exactTimeline;
    private int _collectedGameEvents;
    private int _collectedEvents;
    private bool _manualSignals;
    private readonly List<Guid> _pendingManualSignals = [];
    private readonly List<SpawnableSessionEvent> _events = [];
    private readonly List<EditorNativeGameIoEvent> _gameEvents = [];
    public double EvaluatedTimeSeconds => _exactTimeline ? Math.Max(0, _signalSampleTime) :
        _step < 0 ? 0 : (double)_step / StepsPerSecond;
    public int ActiveCount => _native?.ActiveCount ?? 0;
    public IReadOnlyList<SpawnableSessionEvent> Events => _exactTimeline ? _events : _native?.Events ?? [];
    public IReadOnlyList<EditorNativeGameIoEvent> GameIoEvents => _gameEvents;
    public static readonly byte[] TransportShell = CreateTransportShell();

    public static bool RequiresNative(GraphSnapshot graph) => graph.Nodes.Any(node =>
        node.SignalKeys is { Count: > 0 } ||
        node.Kind == GraphNodeKind.SpawnableGroup ||
        node.Kind.Value == "OnStart" && graph.Connections.Any(edge => edge.SourceNodeId == node.Id) ||
        node.Kind.Value is "OnDestroy" or "LifeTime" or
            "InstanceAge" or "InstanceRemaining" or "InstanceNormalizedAge" or "EffectAge" or "DeltaTime" ||
        SpawnableGroupSchema.IsOutput(node.Kind) ||
        node.ChildGraph is { } child && !GpuParticleEditorService.IsGpu(node.Kind) && RequiresNative(child));

    public static string ResolveLibraryPath(string? explicitPath = null)
    {
        var path = explicitPath ?? Environment.GetEnvironmentVariable("LIBERAFX_PREVIEW_NATIVE_LIBRARY");
        if (string.IsNullOrWhiteSpace(path)) path = Path.Combine(AppContext.BaseDirectory, "liberafx_native.dll");
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new InvalidDataException("Editor-native lifecycle preview requires a built native runtime. Set " +
                "LIBERAFX_PREVIEW_NATIVE_LIBRARY to an existing absolute liberafx_native.dll path, or place it beside the editor; then resync preview.");
        return path;
    }

    public EditorNativePreviewSession(GraphSnapshot graph, string? nativeLibraryPath = null,
        VfxCompileOptions? options = null)
    {
        VfxProgram.ValidateForCompilation(graph);
        if (SkeletalPoseSchema.ContainsSource(graph))
            throw new NotSupportedException(
                "Editor-native live preview cannot capture engine skeletal poses. SOURCE is currently supported only during Unity or Unreal Spawnable package playback.");
        _graph = graph;
        _hasSignalKeys = SignalTimeline.ContainsKeys(graph);
        _library = ResolveLibraryPath(nativeLibraryPath);
        if (ContainsGpu(graph))
            throw new NotSupportedException("Editor-native lifecycle preview cannot yet combine GPU particle containers with Spawnable lifecycles. Preview GPU graphs separately; no GPU payload will be silently discarded.");
        _package = SpawnableGroupPackage.Compile(graph, includePreviewSignalKeys: true, options: options);
        _rootRender = RenderProgramBindings.CreateScene(WithoutTemplates(graph), 0, options: options);
        try { _native = new EditorNativeHostSession(_package, _library); }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            throw new InvalidDataException("Editor-native lifecycle preview could not load the configured native library/ABI. " +
                "Build the matching x64 runtime and set LIBERAFX_PREVIEW_NATIVE_LIBRARY; then resync preview.", ex);
        }
    }

    private static GraphSnapshot WithoutTemplates(GraphSnapshot graph)
    {
        var ids = graph.Nodes.Where(node => node.Kind != GraphNodeKind.SpawnableGroup).Select(node => node.Id).ToHashSet();
        return graph with
        {
            Nodes = graph.Nodes.Where(node => ids.Contains(node.Id)).Select(node => node.ChildGraph is { } child &&
                !GpuParticleEditorService.IsGpu(node.Kind) ? node with { ChildGraph = WithoutTemplates(child) } : node).ToArray(),
            Connections = graph.Connections.Where(edge => ids.Contains(edge.SourceNodeId) && ids.Contains(edge.TargetNodeId)).ToArray()
        };
    }

    private static bool ContainsGpu(GraphSnapshot graph) => graph.Nodes.Any(node =>
        GpuParticleEditorService.IsGpu(node.Kind) || node.ChildGraph is { } child && ContainsGpu(child));

    public byte[] EvaluateScene(double requestedTime, long programSequence, PreviewPlaybackState? playback = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(requestedTime) || requestedTime < 0 || requestedTime > MaximumReplaySeconds)
            throw new InvalidDataException($"Editor-native preview supports a bounded 0..{MaximumReplaySeconds} second timeline. Shorten the timeline or seek within that range.");
        if (programSequence < 1) throw new ArgumentOutOfRangeException(nameof(programSequence));
        if ((_hasSignalKeys || _manualSignals) && playback is not null)
        {
            _exactTimeline = true;
            EvaluateSignalPlayback(playback, requestedTime);
        }
        else
        {
        var step = checked((long)Math.Floor(requestedTime * StepsPerSecond + 1e-9));
        if (step < _step)
        {
            // Stop/seek/loop never route teardown as natural Destroy. Recreate
            // programs/pools/IDs/seeds and replay the same fixed native steps.
            _native!.Dispose();
            _native = null;
            _native = new EditorNativeHostSession(_package, _library);
            _collectedGameEvents = 0;
            _collectedEvents = 0;
            _step = -1;
        }
        if (_step < 0)
        {
            AdvanceNative(0, 0);
            _step = 0;
        }
        while (_step < step)
        {
            AdvanceNative((double)(_step + 1) / StepsPerSecond, 1d / StepsPerSecond);
            ++_step;
        }
        }
        // Equal sampled time (pause/resume/resync) does not evaluate again.
        var nodes = new JsonArray();
        if (_native!.RootVisible) Append(_rootRender, Guid.Empty, 0, 0, nodes);
        foreach (var instance in _native!.ActiveInstances)
        {
            var template = _package.Templates.Single(item => item.ContainerId == instance.Container);
            Append(template.RenderConfiguration, instance.Container, instance.Instance,
                _native.InstanceStartTime(instance.Container, instance.Instance), nodes);
        }
        if (playback?.DebugDrawStopped == true)
            for (var index = nodes.Count - 1; index >= 0; --index)
                if (nodes[index]?["previewOnly"]?.GetValue<bool>() == true) nodes.RemoveAt(index);
        if (nodes.Count > 128) throw new InvalidDataException("Editor-native preview exceeds the 128 evaluated-renderer limit.");
        var result = new JsonObject
        {
            ["type"] = "render_config", ["programSequence"] = programSequence,
            ["executionOwner"] = ExecutionOwner,
            ["standaloneEngineLifecycle"] = false,
            ["requestedTimeSeconds"] = requestedTime,
            ["evaluatedTimeSeconds"] = EvaluatedTimeSeconds,
            ["sampleRate"] = StepsPerSecond,
            ["activeInstances"] = ActiveCount,
            ["nodes"] = nodes
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result);
        if (bytes.Length > CompiledPreviewProtocol.MaxSceneBytes)
            throw new InvalidDataException("Editor-native evaluated render snapshot exceeds the preview payload limit.");
        return bytes;
    }

    private void AdvanceNative(double time, double delta, bool publish = true)
    {
        _native!.Advance(time, delta);
        CollectEvents(publish);
    }

    private void CollectEvents(bool publish)
    {
        while (_collectedEvents < _native!.Events.Count)
        {
            var item = _native.Events[_collectedEvents++];
            if (!publish) continue;
            if (_events.Count >= 100_000)
                throw new InvalidDataException("Preview event history exceeded its bounded event limit; resync preview.");
            _events.Add(item);
        }
        while (_collectedGameEvents < _native.GameIoEvents.Count)
        {
            var item = _native.GameIoEvents[_collectedGameEvents++];
            if (!publish) continue;
            if (_gameEvents.Count >= 100_000)
                throw new InvalidDataException("Preview Game I/O history exceeded its bounded event limit; resync preview.");
            _gameEvents.Add(item);
        }
    }

    public void EnableManualSignals()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_manualSignals) return;
        _manualSignals = true;
        // Keep the preview owner available for exploratory inputs. Authored
        // LifeTime and exported package completion behavior are unchanged.
        _package = _package with { AutoDestroyWhenIdle = false };
        ResetSignalNative();
        _signalPlayback = null;
        _step = -1;
    }

    public void SendSignal(Guid node)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        static bool Contains(GraphSnapshot graph, Guid id) => graph.Nodes.Any(item =>
            item.Id == id && SignalTimeline.Supports(item.Kind) && item.Kind.Value != "SpawnInputSignal" ||
            item.ChildGraph is { } child && item.Kind != GraphNodeKind.SpawnableGroup &&
            !GpuParticleEditorService.IsGpu(item.Kind) && Contains(child, id));
        if (!_manualSignals || _signalSampleTime < 0 || !Contains(_graph, node))
            throw new InvalidOperationException("Preview Send requires an evaluated root-scope Signal input; child instances need explicit targeting.");
        if (_signalPlayback?.SignalStartSeconds is null && _pendingManualSignals.Count >= 256)
            throw new InvalidDataException("Too many paused preview inputs; restart preview or author a Signal track.");
        AdvanceNative(_signalSampleTime, 0);
        _native!.SendSignal(node);
        CollectEvents(true);
        if (_signalPlayback?.SignalStartSeconds is null)
        {
            _pendingManualSignals.Add(node);
        }
    }

    private void ResetSignalNative()
    {
        _native!.Dispose();
        _native = null;
        _native = new EditorNativeHostSession(_package, _library);
        _collectedGameEvents = 0;
        _collectedEvents = 0;
        _signalSampleTime = -1;
    }

    private void AdvanceSignalNative(double time, bool publish = true)
    {
        if (time < _signalSampleTime)
            throw new InvalidDataException("Signal playback cannot advance backwards without a seek epoch.");
        if (_signalSampleTime < 0) { AdvanceNative(0, 0, publish); _signalSampleTime = 0; }
        while (_signalSampleTime < time)
        {
            var next = (Math.Floor(_signalSampleTime * StepsPerSecond + 1e-9) + 1) / StepsPerSecond;
            if (next <= _signalSampleTime) next = _signalSampleTime + 1d / StepsPerSecond;
            next = Math.Min(next, time);
            AdvanceNative(next, next - _signalSampleTime, publish);
            _signalSampleTime = next;
        }
    }

    private void ReconstructSignalNative(double time, bool publishBoundary = false)
    {
        ResetSignalNative();
        if (publishBoundary && time > 0) AdvanceSignalNative(Math.BitDecrement(time), false);
        AdvanceSignalNative(time, publishBoundary);
    }

    private void EvaluateSignalPlayback(PreviewPlaybackState state, double requestedTime)
    {
        var start = state.RangeStart / state.FramesPerSecond;
        var end = state.RangeEnd / state.FramesPerSecond;
        if (!double.IsFinite(state.ForwardSeconds) || state.ForwardSeconds < 0 ||
            !double.IsFinite(state.SeekTimeSeconds) || state.SeekTimeSeconds is < 0 or > MaximumReplaySeconds ||
            state.SignalStartSeconds is { } signalStart &&
                (!double.IsFinite(signalStart) || signalStart < 0 || signalStart > MaximumReplaySeconds) ||
            !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start ||
            end > MaximumReplaySeconds || state.SignalEpoch < 0 || state.LoopCount < 0)
            throw new InvalidDataException("Invalid Signal timeline playback state.");
        var previous = _signalPlayback;
        if (previous is null)
        {
            ReconstructSignalNative(requestedTime);
        }
        else
        {
            var reset = previous.SignalEpoch != state.SignalEpoch ||
                previous.SignalStartSeconds != state.SignalStartSeconds;
            var priorLoops = reset ? 0 : previous.LoopCount;
            var priorProgress = reset ? 0 : previous.ForwardSeconds;
            var loops = state.LoopCount - priorLoops;
            var progress = state.ForwardSeconds - priorProgress;
            if (loops is < 0 or > 128 || progress < 0 || progress > MaximumReplaySeconds ||
                loops > 0 && !state.Loop)
                throw new InvalidDataException("Signal timeline advance exceeds its replay budget; resync preview.");
            if (reset)
            {
                var seeking = previous.SignalEpoch != state.SignalEpoch;
                if (seeking) _pendingManualSignals.Clear();
                ReconstructSignalNative(state.SeekTimeSeconds,
                    state.SignalStartSeconds is not null && state.IncludeSignalStart);
                foreach (var node in _pendingManualSignals)
                {
                    _native!.SendSignal(node);
                    CollectEvents(false);
                }
                _pendingManualSignals.Clear();
            }
            for (var index = 0L; index < loops; ++index)
            {
                AdvanceSignalNative(end);
                _pendingManualSignals.Clear();
                ReconstructSignalNative(start, true);
            }
            AdvanceSignalNative(requestedTime);
        }
        _signalPlayback = state;
    }

    private void Append(byte[] configuration, Guid container, ulong instance, double start, JsonArray nodes)
    {
        var config = JsonNode.Parse(configuration)!.AsObject();
        foreach (var value in config["nodes"]!.AsArray())
        {
            var draw = value!.DeepClone().AsObject();
            var templateNode = Guid.Parse(draw["nodeId"]!.GetValue<string>());
            draw["nodeId"] = RendererId(container, instance, templateNode).ToString("D");
            draw["templateNodeId"] = templateNode.ToString("D");
            draw["containerId"] = container.ToString("D");
            draw["instanceId"] = instance;
            draw["localTimeSeconds"] = EvaluatedTimeSeconds - start;
            foreach (var inputValue in draw["inputs"]!.AsArray())
            {
                var input = inputValue!.AsObject();
                var width = input["width"]!.GetValue<int>();
                var component = input["sourceComponent"]!.GetValue<int>();
                if (input["sourceNodeId"] is { } sourceNode)
                {
                    var sourceId = Guid.Parse(sourceNode.GetValue<string>());
                    var evaluated = container == Guid.Empty ? _native!.ReadRoot(sourceId) : _native!.Read(container, instance, sourceId);
                    if (width is < 1 or > 4 || component < 0 || component + width > evaluated.Width)
                        throw new InvalidDataException($"Invalid native renderer input width: {templateNode}/{input["id"]}.");
                    var defaults = new JsonArray();
                    for (var lane = 0; lane < width; ++lane)
                    {
                        var number = evaluated.Lanes[component + lane];
                        if (!double.IsFinite(number)) throw new InvalidDataException($"Nonfinite native renderer value: {templateNode}.");
                        defaults.Add(number);
                    }
                    input["defaults"] = defaults;
                }
                // Every transported lane is an authoritative native result
                // or an authored unconnected default. UE has no source binding
                // to the shell and cannot accidentally animate these values.
                input["sourceNodeId"] = null;
                input["sourceComponent"] = 0;
            }
            nodes.Add(draw);
        }
    }

    public static Guid RendererId(Guid container, ulong instance, Guid node)
    {
        Span<byte> source = stackalloc byte[40];
        container.TryWriteBytes(source[..16], bigEndian: true, out _);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(source.Slice(16, 8), instance);
        node.TryWriteBytes(source[24..], bigEndian: true, out _);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(source, digest);
        return new Guid(digest[..16], bigEndian: true);
    }

    private static byte[] CreateTransportShell()
    {
        // A valid, explicitly inert program establishes UE's program sequence.
        // It is NOT a Time graph and never computes any effect value.
        var definition = GraphNodeDefinition.Core.Single(item => item.Kind == GraphNodeKind.Float);
        var marker = new GraphNodeSnapshot(Guid.Parse("33c2f551-38f7-4f51-81e1-ef1100000001"),
            definition.Kind, "Editor-native transport shell (no effect execution)", 0, 0,
            definition.PortType, definition.PortCategory);
        return VfxProgram.Compile(new GraphSnapshot([marker], [])).ToBinary();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var native = _native;
        _native = null;
        native?.Dispose();
    }
}
