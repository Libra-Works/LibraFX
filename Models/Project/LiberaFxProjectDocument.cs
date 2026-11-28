using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Resources;
using LiberaFx.Services.Assets;

namespace LiberaFx.Models.Project;

public sealed record LiberaFxProjectDocument
{
    public int FormatVersion { get; init; } = 14;

    public bool AutoDestroyWhenIdle { get; init; } = true;

    public List<LiberaFxProjectNode>? Nodes { get; init; } = [];

    public List<LiberaFxProjectConnection>? Connections { get; init; } = [];

    public UndoRedoHistorySnapshot? History { get; init; }

    // The top-level copy mirrors the current graph state. History entries carry their own
    // resource identity documents inside GraphSnapshot so undo/redo restores mappings too.
    public ResourceIdentityDocument? ResourceIdentities { get; init; }
}

public sealed record LiberaFxProjectNode(
    Guid Id,
    string Name,
    string Kind,
    double X,
    double Y,
    FCurveSnapshot? Curve = null,
    GraphSnapshot? ChildGraph = null,
    IReadOnlyList<NodeParameterSnapshot>? Parameters = null,
    GroupAssetReference? GroupAsset = null,
    bool IsPolymorphic = false,
    IReadOnlyList<GroupInstanceValue>? GroupOverrides = null)
{
    public IReadOnlyList<SignalTimelineKey>? SignalKeys { get; init; }
}

public sealed record LiberaFxProjectConnection(
    Guid SourceNodeId, Guid TargetNodeId, string? TargetPortId = null, string? SourcePortId = null);

public sealed record LoadedLiberaFxProject(
    GraphSnapshot Graph,
    UndoRedoHistorySnapshot? History);

public sealed record SavedLiberaFxProject(
    GraphSnapshot Graph,
    UndoRedoHistorySnapshot History);
