using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Resources;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Settings;

namespace LiberaFx.ViewModels.Graph;

public sealed partial class GraphWorkspaceViewModel
{
    public GraphNodeViewModel CreateGroupAssetNode(Guid assetId, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentException("A finite position is required.");
        }

        var catalog = GroupAssets ?? throw new InvalidOperationException("The group library is unavailable.");
        var asset = catalog.Load(assetId);
        ValidateSnapshot(asset.Graph, new HashSet<Guid>(), _graphPath.Count + 1);
        var definition = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.GraphGroup);
        var reference = new GroupAssetReference(asset.Id, asset.Revision, asset.Locked);
        var displayName = LocalizationService.Shared.GetEnglishOrDefault(catalog.Metadata(asset.Id)?.NameKey, asset.Name);
        var node = new GraphNodeViewModel(this, definition, displayName, x, y,
            childGraph: CloneGraph(asset.Graph), groupAsset: reference);
        ExecuteEdit(new AddGraphNodeCommand(this, node, Nodes.Count));
        SelectNode(node);
        SetConnectionStatus("Messages.GroupAdded", asset.Name, asset.Revision);
        return node;
    }

    public void SaveSelectedGroupAsset()
    {
        Avalonia.Threading.Dispatcher.UIThread.VerifyAccess();
        _document.CreateSnapshot();
        EnsurePathEditable();
        if (SelectedNode is not { IsGraphGroup: true } node || node.ChildGraph is null)
        {
            throw new InvalidOperationException("Select one Graph Group to save.");
        }

        var catalog = GroupAssets ?? throw new InvalidOperationException("The group library is unavailable.");
        GroupAsset asset;
        if (node.GroupAsset is { } reference)
        {
            if (catalog.Load(reference.Id).Origin == GroupAssetOrigin.Standard)
            {
                throw new InvalidOperationException("Standard group assets are read-only; clone the group into a project library first.");
            }

            var library = catalog.Library();
            asset = library.Save(reference.Id, reference.Revision, node.Name, node.ChildGraph);
        }
        else
        {
            asset = catalog.Library().Create(node.Name, node.ChildGraph);
        }

        var current = new GroupAssetReference(asset.Id, asset.Revision, asset.Locked);
        ExecuteEdit(new SetGroupAssetCommand(node, node.GroupAsset, current));
        catalog.Refresh();
        SetConnectionStatus("Messages.GroupSaved", asset.Name, asset.Revision);
    }

    public GroupAsset CloneSelectedStandardGroupToProject()
    {
        Avalonia.Threading.Dispatcher.UIThread.VerifyAccess();
        _document.CreateSnapshot();
        EnsurePathEditable();
        if (SelectedNode is not { IsGraphGroup: true, GroupAsset: { } reference } node)
        {
            throw new InvalidOperationException("Select a standard Graph Group to clone.");
        }

        var catalog = GroupAssets ?? throw new InvalidOperationException("The group library is unavailable.");
        var asset = catalog.Load(reference.Id);
        if (asset.Origin != GroupAssetOrigin.Standard)
        {
            throw new InvalidOperationException("Only a standard group asset can be cloned with this operation.");
        }

        var clone = catalog.CloneToProject(asset.Id, node.Name);
        ExecuteEdit(new SetGroupAssetCommand(node, node.GroupAsset,
            new GroupAssetReference(clone.Id, clone.Revision, clone.Locked)));
        catalog.Refresh();
        SetConnectionStatus("Messages.GroupSaved", clone.Name, clone.Revision);
        return clone;
    }

    public void ToggleSelectedGroupAssetLock()
    {
        Avalonia.Threading.Dispatcher.UIThread.VerifyAccess();
        _document.CreateSnapshot();
        EnsurePathEditable();
        if (SelectedNode?.GroupAsset is not { } reference)
        {
            throw new InvalidOperationException("Select a saved group asset.");
        }

        var catalog = GroupAssets ?? throw new InvalidOperationException("The group library is unavailable.");
        if (catalog.Load(reference.Id).Origin == GroupAssetOrigin.Standard)
        {
            throw new InvalidOperationException("Standard group assets are permanently locked; clone the group into a project library first.");
        }

        var library = catalog.Library();
        var asset = library.Load(reference.Id);
        asset = library.SetLocked(reference.Id, !asset.Locked);
        catalog.Refresh();
        SetConnectionStatus(asset.Locked ? "Messages.GroupNowLocked" : "Messages.GroupNowUnlocked", asset.Name);
    }

    public void UnlinkSelectedGroupAsset()
    {
        Avalonia.Threading.Dispatcher.UIThread.VerifyAccess();
        _document.CreateSnapshot();
        EnsurePathEditable();
        if (SelectedNode is not { IsGraphGroup: true, GroupAsset: { } reference } node)
        {
            throw new InvalidOperationException("Select a linked Graph Group to unlink.");
        }

        ExecuteEdit(new SetGroupAssetCommand(node, reference, null));
        SetConnectionStatus("Messages.GroupUnlinked", node.Name);
    }

    public IReadOnlyList<GroupUpdateCandidate> FindGroupUpdates()
    {
        var catalog = GroupAssets;
        if (catalog is null || catalog.Assets.Count == 0)
        {
            return [];
        }

        var latest = catalog.Assets.ToDictionary(asset => asset.Id);
        var results = new List<GroupUpdateCandidate>();
        bool Visit(GraphSnapshot graph)
        {
            var anyConflict = false;
            foreach (var node in graph.Nodes)
            {
                var childConflict = node.ChildGraph is not null && Visit(node.ChildGraph);
                if (node.GroupAsset is { } reference && node.ChildGraph is { } child &&
                    latest.TryGetValue(reference.Id, out var asset) && asset.Revision > reference.Revision)
                {
                    var changed = true;
                    try
                    {
                        var baseline = catalog.LoadRevision(reference.Id, reference.Revision);
                        changed = !GraphSnapshot.EquivalentIgnoringIds(baseline.Graph, child);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
                    {
                    }
                    changed |= childConflict || node.GroupOverrides is { Count: > 0 };
                    results.Add(new GroupUpdateCandidate(node.Id, reference.Id, node.Name,
                        reference.Revision, asset.Revision, changed));
                    anyConflict |= changed;
                }
                anyConflict |= childConflict;
            }
            return anyConflict;
        }
        Visit(CreateSnapshot());
        return results;
    }

    public PreparedGroupUpdates? PrepareGroupUpdates(IReadOnlyList<GroupUpdateCandidate> candidates)
    {
        var catalog = GroupAssets ?? throw new InvalidOperationException("The group library is unavailable.");
        var requested = candidates.Where(candidate => !candidate.HasLocalChanges).ToDictionary(candidate => candidate.NodeId);
        if (requested.Count == 0)
        {
            return null;
        }

        var currentCandidates = FindGroupUpdates().ToDictionary(candidate => candidate.NodeId);
        if (requested.Any(item => !currentCandidates.TryGetValue(item.Key, out var current) || current != item.Value))
        {
            throw new InvalidOperationException("The graph or group library changed; check for updates again.");
        }

        var approved = requested.Values.Select(item => (item.AssetId, item.CurrentRevision, item.LatestRevision)).ToHashSet();
        var before = CreateSnapshot();
        var count = 0;
        static Dictionary<string, string?> PortMap(GraphSnapshot baseline, GraphSnapshot oldChild,
            GraphSnapshot latest, GraphSnapshot newChild)
        {
            var map = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var i = 0; i < baseline.Nodes.Count; ++i)
            {
                var old = oldChild.Nodes[i];
                var groupOutput = GroupBoundarySchema.IsOutput(old.Kind);
                var groupInput = GroupBoundarySchema.IsInput(old.Kind);
                var isOutput = SpawnableGroupSchema.IsOutput(old.Kind);
                if (!SpawnableGroupSchema.IsInput(old.Kind) && !isOutput && !groupInput && !groupOutput)
                {
                    continue;
                }

                var latestIndex = latest.Nodes.ToList().FindIndex(node => node.Id == baseline.Nodes[i].Id &&
                    node.Kind == old.Kind);
                string Port(Guid id) => groupOutput ? GroupBoundarySchema.OutputPortId(id) :
                    groupInput ? GroupBoundarySchema.InputPortId(id) :
                    isOutput ? SpawnableGroupSchema.OutputPortId(id) : SpawnableGroupSchema.PortId(id);
                map[Port(old.Id)] = latestIndex < 0 ? null : Port(newChild.Nodes[latestIndex].Id);
            }
            return map;
        }
        static GraphSnapshot WithMappedConnections(GraphSnapshot graph, GraphNodeSnapshot[] nodes,
            Dictionary<Guid, Dictionary<string, string?>> maps)
        {
            var edges = new List<GraphConnectionSnapshot>();
            foreach (var edge in graph.Connections)
            {
                var mapped = edge;
                if (edge.TargetPortId is { } port && maps.TryGetValue(edge.TargetNodeId, out var map) &&
                    map.TryGetValue(port, out var newPort))
                {
                    if (newPort is null)
                    {
                        continue;
                    }

                    mapped = mapped with { TargetPortId = newPort };
                }
                if (edge.SourcePortId is { } sourcePort && maps.TryGetValue(edge.SourceNodeId, out var sourceMap) &&
                    sourceMap.TryGetValue(sourcePort, out var newSource))
                {
                    if (newSource is null)
                    {
                        continue;
                    }

                    mapped = mapped with { SourcePortId = newSource };
                }
                edges.Add(mapped);
            }
            return graph with { Nodes = nodes, Connections = edges };
        }
        GraphSnapshot RewriteNewChildren(GraphSnapshot graph, HashSet<Guid> ancestors)
        {
            var maps = new Dictionary<Guid, Dictionary<string, string?>>();
            var nodes = graph.Nodes.Select(node =>
            {
                if (node.ChildGraph is not { } child)
                {
                    return node;
                }

                if (node.GroupAsset is not { } reference)
                {
                    return node with { ChildGraph = RewriteNewChildren(child, ancestors) };
                }

                if (!ancestors.Add(reference.Id))
                {
                    return node;
                }

                try
                {
                    var approvedUpdate = approved.FirstOrDefault(item =>
                        item.AssetId == reference.Id && item.CurrentRevision == reference.Revision);
                    if (approvedUpdate != default)
                    {
                        var baseline = catalog.LoadRevision(reference.Id, reference.Revision);
                        var current = catalog.Load(reference.Id);
                        if (current.Revision != approvedUpdate.LatestRevision)
                        {
                            throw new InvalidOperationException($"Group '{node.Name}' changed again; check for updates again.");
                        }

                        if (node.GroupOverrides is not { Count: > 0 } &&
                            GraphSnapshot.EquivalentIgnoringIds(baseline.Graph, child))
                        {
                            var replacement = CloneGraph(current.Graph);
                            if (node.Kind == GraphNodeKind.SpawnableGroup || node.Kind == GraphNodeKind.GraphGroup)
                            {
                                maps[node.Id] = PortMap(baseline.Graph, child, current.Graph, replacement);
                            }

                            child = replacement;
                            reference = new GroupAssetReference(current.Id, current.Revision, current.Locked);
                            count++;
                        }
                    }
                    return node with { ChildGraph = RewriteNewChildren(child, ancestors), GroupAsset = reference };
                }
                finally { ancestors.Remove(reference.Id); }
            }).ToArray();
            return WithMappedConnections(graph, nodes, maps);
        }
        GraphSnapshot Rewrite(GraphSnapshot graph)
        {
            var maps = new Dictionary<Guid, Dictionary<string, string?>>();
            var nodes = graph.Nodes.Select(node =>
            {
                if (node.GroupAsset is { } reference && node.ChildGraph is { } child &&
                    requested.TryGetValue(node.Id, out var candidate) &&
                    reference.Id == candidate.AssetId && reference.Revision == candidate.CurrentRevision)
                {
                    var baseline = catalog.LoadRevision(reference.Id, reference.Revision);
                    var current = catalog.Load(reference.Id);
                    if (current.Revision != candidate.LatestRevision)
                    {
                        throw new InvalidOperationException($"Group '{node.Name}' changed again; check for updates again.");
                    }

                    if (!GraphSnapshot.EquivalentIgnoringIds(baseline.Graph, child))
                    {
                        throw new InvalidOperationException($"Group '{node.Name}' has local changes.");
                    }

                    count++;
                    var replacement = RewriteNewChildren(CloneGraph(current.Graph), [current.Id]);
                    if (node.Kind == GraphNodeKind.SpawnableGroup || node.Kind == GraphNodeKind.GraphGroup)
                    {
                        maps[node.Id] = PortMap(baseline.Graph, child, current.Graph, replacement);
                    }

                    return node with
                    {
                        ChildGraph = replacement,
                        GroupAsset = new GroupAssetReference(current.Id, current.Revision, current.Locked)
                    };
                }
                return node.ChildGraph is null ? node : node with { ChildGraph = Rewrite(node.ChildGraph) };
            }).ToArray();
            return WithMappedConnections(graph, nodes, maps);
        }
        var after = Rewrite(before);
        if (count == 0)
        {
            return null;
        }

        ValidateSnapshot(after);
        return new PreparedGroupUpdates(before, after, count);
    }

    public int CommitGroupUpdates(PreparedGroupUpdates prepared)
    {
        if (!GraphSnapshot.ContentEquals(prepared.Before, CreateSnapshot()))
        {
            throw new InvalidOperationException("The graph changed; check for updates again.");
        }

        ExecuteEdit(new ReplaceGraphSnapshotCommand(this, prepared.Before, prepared.After));
        SetConnectionStatus("Messages.LinkedGroupsUpdated", prepared.Count);
        return prepared.Count;
    }

    public int ApplyGroupUpdates(IReadOnlyList<GroupUpdateCandidate> candidates)
    {
        var prepared = PrepareGroupUpdates(candidates);
        return prepared is null ? 0 : CommitGroupUpdates(prepared);
    }
}
