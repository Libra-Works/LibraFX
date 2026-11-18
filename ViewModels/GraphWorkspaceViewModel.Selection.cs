using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed partial class GraphWorkspaceViewModel
{
    public void SelectNode(GraphNodeViewModel? node)
    {
        SetSelection(node is null ? [] : [node]);
    }

    public void ToggleSelection(GraphNodeViewModel node)
    {
        if (!Nodes.Contains(node))
        {
            return;
        }

        SetSelection(_selectedNodes.Contains(node)
            ? _selectedNodes.Where(selected => selected != node).ToArray()
            : _selectedNodes.Append(node).ToArray());
    }

    public void SetSelection(IEnumerable<GraphNodeViewModel> nodes)
    {
        var selection = nodes.Where(Nodes.Contains).Distinct().ToArray();
        if (SelectedNode is not null)
        {
            SelectedNode.PropertyChanged -= OnSelectedNodePropertyChanged;
        }

        foreach (var old in _selectedNodes)
        {
            old.SetSelected(false);
        }

        _selectedNodes.Clear();
        _selectedNodes.AddRange(selection);
        foreach (var selected in _selectedNodes)
        {
            selected.SetSelected(true);
        }

        SelectedNode = _selectedNodes.LastOrDefault();
        if (SelectedNode is not null)
        {
            SelectedNode.PropertyChanged += OnSelectedNodePropertyChanged;
        }

        OnPropertyChanged(nameof(SelectionCount));
    }

    public GraphSnapshot CreateSelectionSnapshot()
    {
        var ids = _selectedNodes.Select(node => node.Id).ToHashSet();
        var snapshot = CreateCurrentSnapshot();
        return new GraphSnapshot(
            snapshot.Nodes.Where(node => ids.Contains(node.Id)).ToArray(),
            snapshot.Connections.Where(connection =>
                ids.Contains(connection.SourceNodeId) && ids.Contains(connection.TargetNodeId)).ToArray())
        {
            ResourceIdentities = CopySelectedResourceIdentities(ids)
        };
    }

    public void PasteSnapshot(GraphSnapshot snapshot, double x, double y)
    {
        ValidateSnapshot(snapshot, new HashSet<Guid>(), _graphPath.Count);
        ValidateSpawnPlacement(snapshot.Nodes.Select(node => node.Kind));
        if (InsideSpawnable() && ContainsSpawnable(snapshot))
        {
            throw new InvalidOperationException("Nested Spawnable Groups are not supported.");
        }

        if (snapshot.Nodes.Count == 0 || !double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentException("Pasted nodes and a finite paste position are required.");
        }

        var minX = snapshot.Nodes.Min(node => node.X);
        var minY = snapshot.Nodes.Min(node => node.Y);
        var nodesByOldId = new Dictionary<Guid, GraphNodeViewModel>();
        var commands = new List<IUndoableCommand>();
        foreach (var specification in snapshot.Nodes)
        {
            var newX = x + (specification.X - minX);
            var newY = y + (specification.Y - minY);
            if (!double.IsFinite(newX) || !double.IsFinite(newY))
            {
                throw new ArgumentException("Pasted node coordinates are invalid.");
            }

            var definition = GraphNodeDefinition.All.Single(item => item.Kind == specification.Kind);
            var node = new GraphNodeViewModel(this, definition, specification.Name, newX, newY,
                curve: specification.Curve, childGraph: specification.ChildGraph is null ? null : CloneGraph(specification.ChildGraph),
                parameters: specification.Parameters, groupAsset: specification.GroupAsset,
                isPolymorphic: specification.IsPolymorphic, groupOverrides: specification.GroupOverrides,
                signalKeys: specification.SignalKeys);
            nodesByOldId.Add(specification.Id, node);
            commands.Add(new AddGraphNodeCommand(this, node, Nodes.Count + commands.Count));
        }

        var connectionIndex = Connections.Count;
        foreach (var connection in snapshot.Connections)
        {
            var oldTarget = snapshot.Nodes.Single(node => node.Id == connection.TargetNodeId);
            var newTarget = nodesByOldId[connection.TargetNodeId];
            commands.Add(new AddGraphConnectionCommand(
                this, nodesByOldId[connection.SourceNodeId], newTarget, connectionIndex++,
                targetPortId: SpawnableGroupSchema.RemapPort(oldTarget, newTarget.ChildGraph, connection.TargetPortId),
                sourcePortId: SpawnableGroupSchema.RemapPort(snapshot.Nodes.Single(node => node.Id == connection.SourceNodeId),
                    nodesByOldId[connection.SourceNodeId].ChildGraph, connection.SourcePortId)));
        }

        var pastedIdentities = RemapPastedResourceIdentities(snapshot, nodesByOldId);
        ExecuteEdit(new CompositeUndoableCommand(LocalizationService.Shared.Get("Messages.History.PasteNodes"), commands),
            updatedResourceIdentities: pastedIdentities);
        _nextNodeIndex += snapshot.Nodes.Count;
        SetSelection(snapshot.Nodes.Select(node => nodesByOldId[node.Id]));
        SetConnectionStatus("Messages.NodesPasted", snapshot.Nodes.Count);
    }

    public void DeleteNodes(IEnumerable<GraphNodeViewModel> nodes)
    {
        var selected = nodes.Where(Nodes.Contains).Distinct().ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        ExecuteEdit(new CompositeUndoableCommand(
            LocalizationService.Shared.Get("Messages.History.DeleteNodes"),
            selected.Select(node => new DeleteGraphNodeCommand(this, node))),
            updatedResourceIdentities: RemoveSelectedResourceUses(selected.Select(node => node.Id).ToHashSet()));
    }

    public void DeleteNode(GraphNodeViewModel node)
    {
        if (!Nodes.Contains(node))
        {
            throw new InvalidOperationException("The node is not in this graph.");
        }

        ExecuteEdit(new DeleteGraphNodeCommand(this, node),
            updatedResourceIdentities: RemoveSelectedResourceUses(new HashSet<Guid> { node.Id }));
    }

    private void DeleteSelectedNode()
    {
        DeleteNodes(_selectedNodes.ToArray());
    }

    private void OnSelectedNodePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphNodeViewModel.Name))
        {
            OnPropertyChanged(nameof(SelectedNodeName));
        }
    }
}
