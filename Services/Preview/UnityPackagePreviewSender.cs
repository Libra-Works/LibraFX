using System.Buffers.Binary;
using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Gpu;

namespace LiberaFx.Services.Preview;

internal sealed class UnityPackagePreviewSender
{
    private const int MaxFrameBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private PreviewPublication? _last;
    private bool _loaded;
    private long _signalSequence;

    public CompiledPreviewMessage? Prepare(PreviewPublication publication, bool force,
        ResourceIdentityRenderContext? resources, bool supportsCurveDelta = false)
    {
        var sameGraph = _last is { } last && last.DocumentId == publication.DocumentId &&
            ReferenceEquals(last.Graph, publication.Graph) &&
            last.IncludePreviewDebugDraw == publication.IncludePreviewDebugDraw &&
            last.Playback?.DebugDrawStopped == publication.Playback?.DebugDrawStopped;
        if (sameGraph && !force)
        {
            if (publication.SignalRequestSequence != _signalSequence)
            {
                if (!_loaded)
                    throw new NotSupportedException("Unity WSS package preview has no loaded program for Editor Send.");
                var requests = publication.SignalRequests
                    .Where(request => request.Sequence > _signalSequence).ToArray();
                if (publication.SignalRequestSequence < _signalSequence ||
                    publication.SignalRequestSequence - _signalSequence != requests.Length ||
                    requests.Length > 32 ||
                    requests.Where((request, index) => request.Sequence != _signalSequence + index + 1).Any())
                    throw new InvalidDataException("Preview Send history exceeded its bounded window; reconnect the Unity preview before sending again.");
                _signalSequence = publication.SignalRequestSequence;
                var signalPayload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type = "unity_preview_signals",
                    requests = requests.Select(request => new { nodeId = request.NodeId.ToString("D") })
                }, JsonOptions);
                return new(CompiledPreviewProtocol.CreateControl(signalPayload), CompiledPreviewKind.Control, publication);
            }
            return _loaded && _last!.Sequence < publication.Sequence
                ? CompiledPreviewProtocol.CreatePayload(publication, CompiledPreviewKind.Playback, [],
                    checked((ulong)_last.Sequence))
                : null;
        }

        if (SkeletalPoseSchema.ContainsSource(publication.Graph))
        {
            throw new NotSupportedException("Unity WSS package preview does not support SOURCE skeletal poses.");
        }
        if (!force && _loaded && _last is { } prev &&
            prev.DocumentId == publication.DocumentId &&
            CompiledPreviewProtocol.SamePlayback(prev, publication) &&
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
        var metadata = JsonSerializer.SerializeToUtf8Bytes(resources?.UnityPreviewBindings(), JsonOptions);
        if (metadata.Length > CompiledPreviewProtocol.MaxSceneBytes ||
            (long)metadata.Length + bytes.Length + 8 + CompiledPreviewProtocol.HeaderSize > MaxFrameBytes)
        {
            throw new InvalidDataException("Unity preview package exceeds the transport limit.");
        }
        var payload = new byte[8 + metadata.Length + bytes.Length];
        "LFXU"u8.CopyTo(payload);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), metadata.Length);
        metadata.CopyTo(payload, 8);
        bytes.CopyTo(payload, 8 + metadata.Length);
        return CompiledPreviewProtocol.CreatePayload(publication, CompiledPreviewKind.Program, payload, 0);
    }

    public void Sent(CompiledPreviewMessage message)
    {
        _last = message.Publication;
        _loaded = message.Kind != CompiledPreviewKind.CompileError;
        _signalSequence = message.Publication.SignalRequestSequence;
    }
}
