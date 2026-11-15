using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;

namespace LiberaFx.Models.Project;

public sealed record LiberaFxProjectDocument
{
    public int FormatVersion { get; init; } = 9;

    public List<LiberaFxProjectNode>? Nodes { get; init; } = [];

    public List<LiberaFxProjectConnection>? Connections { get; init; } = [];

    public UndoRedoHistorySnapshot? History { get; init; }
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
    bool IsPolymorphic = false);

public sealed record LiberaFxProjectConnection(
    Guid SourceNodeId, Guid TargetNodeId, string? TargetPortId = null, string? SourcePortId = null);

public sealed record LoadedLiberaFxProject(
    GraphSnapshot Graph,
    UndoRedoHistorySnapshot? History);

public sealed record SavedLiberaFxProject(
    GraphSnapshot Graph,
    UndoRedoHistorySnapshot History);
