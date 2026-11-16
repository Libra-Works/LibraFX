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
    private readonly SpawnableGroupPackage _package;
    private readonly byte[] _rootRender;
    private readonly string _library;
    private NativeSpawnableSession? _native;
    private long _step = -1;
    private bool _disposed;
    public double EvaluatedTimeSeconds => _step < 0 ? 0 : (double)_step / StepsPerSecond;
    public int ActiveCount => _native?.ActiveCount ?? 0;
    public IReadOnlyList<SpawnableSessionEvent> Events => _native?.Events ?? [];
    public static readonly byte[] TransportShell = CreateTransportShell();

    public static bool RequiresNative(GraphSnapshot graph) => graph.Nodes.Any(node =>
        node.Kind == GraphNodeKind.SpawnableGroup || node.Kind.Value is "OnStart" or "OnDestroy" or "LifeTime" or
            "InstanceAge" or "InstanceRemaining" or "InstanceNormalizedAge" ||
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

    public EditorNativePreviewSession(GraphSnapshot graph, string? nativeLibraryPath = null)
    {
        _library = ResolveLibraryPath(nativeLibraryPath);
        if (ContainsGpu(graph))
            throw new NotSupportedException("Editor-native lifecycle preview cannot yet combine GPU particle containers with Spawnable lifecycles. Preview GPU graphs separately; no GPU payload will be silently discarded.");
        _package = SpawnableGroupPackage.Compile(graph);
        _rootRender = RenderProgramBindings.CreateScene(WithoutTemplates(graph), 0);
        try { _native = new NativeSpawnableSession(_package, _library); }
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

    public byte[] EvaluateScene(double requestedTime, long programSequence)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(requestedTime) || requestedTime < 0 || requestedTime > MaximumReplaySeconds)
            throw new InvalidDataException($"Editor-native preview supports a bounded 0..{MaximumReplaySeconds} second timeline. Shorten the timeline or seek within that range.");
        if (programSequence < 1) throw new ArgumentOutOfRangeException(nameof(programSequence));
        var step = checked((long)Math.Floor(requestedTime * StepsPerSecond + 1e-9));
        if (step < _step)
        {
            // Stop/seek/loop never route teardown as natural Destroy. Recreate
            // programs/pools/IDs/seeds and replay the same fixed native steps.
            _native!.Dispose();
            _native = null;
            _native = new NativeSpawnableSession(_package, _library);
            _step = -1;
        }
        if (_step < 0)
        {
            _native!.Advance(0);
            _step = 0;
        }
        while (_step < step)
        {
            _native!.Advance((double)(_step + 1) / StepsPerSecond);
            ++_step;
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
