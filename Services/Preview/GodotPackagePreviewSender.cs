using System.Globalization;
using System.Text;
using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Gpu;

namespace LiberaFx.Services.Preview;

// Per-connection package preview sender.
internal sealed class GodotPackagePreviewSender
{
    private const int MaxPackageBytes = 16 * 1024 * 1024 - CompiledPreviewProtocol.HeaderSize;
    private const int MaxSignalRequests = 32;
    private PreviewPublication? _good;
    private PreviewPublication? _failed;
    private PreviewPublication? _lastTransport;
    private long _lastSent;
    private long _signalSequence;
    private string? _signalDocument;
    private object? _signalGraph;

    public CompiledPreviewMessage? Prepare(PreviewPublication publication, bool force,
        ResourceIdentityRenderContext? resources = null, bool supportsCurveDelta = false,
        bool supportsParameterDelta = true)
    {
        var sameSignalStream = _signalDocument == publication.DocumentId &&
            ReferenceEquals(_signalGraph, publication.Graph);
        if (sameSignalStream && _signalSequence != publication.SignalRequestSequence)
        {
            if (_good is null || !SameGraph(_good, publication))
                return Error(publication, "Godot package preview has no successfully loaded program for Editor Send.");
            var requests = publication.SignalRequests
                .Where(request => request.Sequence > _signalSequence).ToArray();
            if (publication.SignalRequestSequence < _signalSequence ||
                publication.SignalRequestSequence - _signalSequence != requests.Length ||
                requests.Length > MaxSignalRequests ||
                requests.Where((request, index) => request.Sequence != _signalSequence + index + 1).Any())
                return Error(publication,
                    "Preview Send history exceeded its bounded window; reconnect the Godot preview before sending again.");

            try
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type = "godot_preview_signals",
                    sequence = publication.Sequence.ToString(CultureInfo.InvariantCulture),
                    timeSeconds = PlaybackTime(publication),
                    baseSequence = _signalSequence.ToString(CultureInfo.InvariantCulture),
                    requests = requests.Select(request => new
                    {
                        sequence = request.Sequence.ToString(CultureInfo.InvariantCulture),
                        nodeId = request.NodeId.ToString("D")
                    })
                });
                return new(CompiledPreviewProtocol.CreateControl(payload), CompiledPreviewKind.Control, publication);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                return Error(publication, ex.Message);
            }
        }
        if (force || !sameSignalStream)
        {
            _signalDocument = publication.DocumentId;
            _signalGraph = publication.Graph;
            _signalSequence = publication.SignalRequestSequence;
        }
        if (!force && _failed is not null && SameGraph(_failed, publication) &&
            _failed.SignalRequestSequence == publication.SignalRequestSequence) return null;
        var playback = !force && _good is not null && SameGraph(_good, publication) &&
            _good.Playback?.DebugDrawStopped == publication.Playback?.DebugDrawStopped &&
            _lastSent < publication.Sequence;
        if (playback)
            return CompiledPreviewProtocol.CreatePayload(publication, CompiledPreviewKind.Playback, [],
                checked((ulong)_lastSent));
        try
        {
            if (SkeletalPoseSchema.ContainsSource(publication.Graph))
                throw new NotSupportedException("Godot package preview does not support SOURCE skeletal poses.");
            if (!force && supportsParameterDelta && _good is { } prev &&
                prev.DocumentId == publication.DocumentId &&
                _lastTransport is not null && CompiledPreviewProtocol.SamePlayback(_lastTransport, publication) &&
                prev.IncludePreviewDebugDraw == publication.IncludePreviewDebugDraw &&
                prev.Playback?.DebugDrawStopped == publication.Playback?.DebugDrawStopped &&
                PackageParameterDelta.TryCreatePayload(prev.Graph, publication.Graph, publication.Sequence, out var deltaBytes, supportsCurveDelta))
            {
                return new(deltaBytes!, CompiledPreviewKind.Scene, publication);
            }
            var compileGraph = publication.Graph.AutoDestroyWhenIdle &&
                GpuProgramDeliveryService.Containers(publication.Graph).Any()
                ? publication.Graph with { AutoDestroyWhenIdle = false }
                : publication.Graph;
            var package = SpawnableGroupPackage.Compile(compileGraph, includePreviewSignalKeys: true,
                options: new VfxCompileOptions
                {
                    IncludePreviewDebugDraw = publication.IncludePreviewDebugDraw &&
                        publication.Playback?.DebugDrawStopped != true
                }, resourceContext: resources);
            var bytes = SpawnableGroupPackageBinary.Serialize(package);
            if (bytes.Length > MaxPackageBytes)
                throw new InvalidDataException("Godot preview package exceeds the 16 MiB packet limit.");
            return CompiledPreviewProtocol.CreatePayload(publication, CompiledPreviewKind.Program, bytes, 0);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return Error(publication, ex.Message);
        }
    }

    public void Sent(CompiledPreviewMessage message)
    {
        if (message.Kind is CompiledPreviewKind.Program or CompiledPreviewKind.Playback or CompiledPreviewKind.Scene or CompiledPreviewKind.Control)
        {
            _lastSent = message.Publication.Sequence;
            _lastTransport = message.Publication;
        }
        _signalSequence = message.Publication.SignalRequestSequence;
        if (message.Kind is CompiledPreviewKind.Program or CompiledPreviewKind.Scene) { _good = message.Publication; _failed = null; }
        else if (message.Kind == CompiledPreviewKind.CompileError) _failed = message.Publication;
        else _failed = null;
    }

    private static bool SameGraph(PreviewPublication a, PreviewPublication b) =>
        a.DocumentId == b.DocumentId && ReferenceEquals(a.Graph, b.Graph) &&
        a.IncludePreviewDebugDraw == b.IncludePreviewDebugDraw &&
        a.Playback?.DebugDrawStopped == b.Playback?.DebugDrawStopped;

    private static double PlaybackTime(PreviewPublication publication)
    {
        var playback = publication.Playback;
        if (playback is null) return 0;
        if (!double.IsFinite(playback.FramesPerSecond) || playback.FramesPerSecond <= 0)
            throw new InvalidDataException("Godot package preview needs a positive finite playback frame rate.");
        var time = playback.Frame / playback.FramesPerSecond;
        if (!double.IsFinite(time) || time < 0)
            throw new InvalidDataException("Godot package preview needs a finite non-negative playback time.");
        return time;
    }

    private static CompiledPreviewMessage Error(PreviewPublication publication, string diagnostic)
    {
        var error = new byte[CompiledPreviewProtocol.MaxErrorBytes];
        Encoding.UTF8.GetEncoder().Convert(diagnostic.AsSpan(), error, true, out _, out var written, out _);
        return CompiledPreviewProtocol.CreatePayload(publication, CompiledPreviewKind.CompileError,
            error[..written], 0);
    }
}
