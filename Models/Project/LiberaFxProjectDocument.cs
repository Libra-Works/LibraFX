using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;

namespace LiberaFx.Models.Project;

public sealed record LiberaFxProjectDocument
{
    public int FormatVersion { get; init; } = 4;

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
    GraphSnapshot? ChildGraph = null);

public sealed record LiberaFxProjectConnection(Guid SourceNodeId, Guid TargetNodeId);

public sealed record LoadedLiberaFxProject(
    GraphSnapshot Graph,
    UndoRedoHistorySnapshot? History);

public sealed record SavedLiberaFxProject(
    GraphSnapshot Graph,
    UndoRedoHistorySnapshot History);
