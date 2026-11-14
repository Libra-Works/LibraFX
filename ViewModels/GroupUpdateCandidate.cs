using LiberaFx.Models.Graph;

namespace LiberaFx.ViewModels;

public sealed record GroupUpdateCandidate(
    Guid NodeId,
    Guid AssetId,
    string Name,
    long CurrentRevision,
    long LatestRevision,
    bool HasLocalChanges);

public sealed record PreparedGroupUpdates(GraphSnapshot Before, GraphSnapshot After, int Count);
