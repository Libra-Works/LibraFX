using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Settings;

namespace LiberaFx.ViewModels.Graph;

public sealed partial class GraphWorkspaceViewModel
{
    public bool MoveNodesIntoGraph(IEnumerable<GraphNodeViewModel> selection, GraphNodeViewModel owner)
    {
        var moved = selection.Distinct().ToArray();
        if (!Nodes.Contains(owner) || owner.ChildGraph is null || moved.Length == 0 ||
            moved.Any(node => !Nodes.Contains(node) || ReferenceEquals(node, owner))) return false;
        var childWorkspace = OpenInlineGraph(owner);
        if (childWorkspace.IsReadOnly || moved.Any(node => !childWorkspace.CanCreateNode(node.BaseDefinition.Kind, out _)))
            throw new InvalidOperationException(LocalizationService.Shared.Get("Inline.DropNotAllowed"));
        var ids = moved.Select(node => node.Id).ToHashSet();
        var current = CreateCurrentSnapshot();
        if (current.Connections.Any(edge => ids.Contains(edge.SourceNodeId) != ids.Contains(edge.TargetNodeId)))
            throw new InvalidOperationException(LocalizationService.Shared.Get("Inline.DropExternalConnections"));
        if (current.ExposedItems?.Any(item => item.Targets.Any(target => ids.Contains(target.NodeId))) == true)
            throw new InvalidOperationException(LocalizationService.Shared.Get("Inline.DropExposed"));
        var left = moved.Min(node => node.X);
        var top = moved.Min(node => node.Y);
        var child = owner.ChildGraph with
        {
            Nodes = owner.ChildGraph.Nodes.Concat(current.Nodes.Where(node => ids.Contains(node.Id))
                .Select(node => node with { X = node.X - left + 80, Y = node.Y - top + 80 })).ToArray(),
            Connections = owner.ChildGraph.Connections.Concat(current.Connections
                .Where(edge => ids.Contains(edge.SourceNodeId))).ToArray()
        };
        var updated = current with
        {
            Nodes = current.Nodes.Where(node => !ids.Contains(node.Id))
                .Select(node => node.Id == owner.Id ? node with { ChildGraph = child } : node).ToArray(),
            Connections = current.Connections.Where(edge => !ids.Contains(edge.SourceNodeId)).ToArray()
        };
        var before = CreateSnapshot();
        var after = ReplaceGraph(before, _graphPath, 0, updated);
        ValidateSnapshot(after);
        CpuParticleSchema.ValidateContext(after);
        ExecuteEdit(new ReplaceGraphSnapshotCommand(this, before, after,
            LocalizationService.Shared.Get("Inline.History.MoveIntoGraph")));
        return true;
    }
}
