namespace LiberaFx.Models.Graph;

/// <summary>
/// Describes new nodes and their connections as one validated, undoable graph edit.
/// </summary>
public sealed record LiberaGraphEditPlan(
    IReadOnlyList<LiberaGraphNodeSpec> Nodes,
    IReadOnlyList<LiberaGraphConnectionSpec> Connections);

public sealed record LiberaGraphNodeSpec(
    string Key,
    GraphNodeKind Kind,
    string Name,
    double X,
    double Y,
    IReadOnlyList<NodeParameterSnapshot>? Parameters = null,
    bool IsPolymorphic = false);

/// <summary>Connects two node keys declared in the same graph plan.</summary>
public sealed record LiberaGraphConnectionSpec(
    string SourceKey, string TargetKey, string? TargetPortId = null, string? SourcePortId = null);
