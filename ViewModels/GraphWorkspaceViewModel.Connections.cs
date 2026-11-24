using Avalonia;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed partial class GraphWorkspaceViewModel
{
    public void BeginConnection(GraphNodeViewModel source, string? sourcePortId = null)
    {
        SelectNode(source);
        _pendingOutputPortId = sourcePortId;
        _pendingOutputNode = source;
        OnPropertyChanged(nameof(PendingOutputNode));
        OnPropertyChanged(nameof(PendingOutputPortId));
        SetConnectionStatus("Messages.ChooseInput", source.Name,
            GraphNodeDefinition.DisplayPortType(source.Definition.GetOutputPortType(sourcePortId) ?? source.PortType));
    }

    public void PreviewConnection(Point point)
    {
        if (_pendingOutputNode is not null)
        {
            ConnectionPreviewPoint = point;
        }
    }

    public void CancelConnection()
    {
        _pendingOutputNode = null;
        _pendingOutputPortId = null;
        ConnectionPreviewPoint = null;
        OnPropertyChanged(nameof(PendingOutputNode));
        OnPropertyChanged(nameof(PendingOutputPortId));
        ReportStatus(string.Empty);
    }

    public void CompleteConnection(GraphNodeViewModel target, string? targetPortId = null, string? sourcePortId = null)
    {
        SelectNode(target);

        if (_pendingOutputNode is null)
        {
            SetConnectionStatus("Messages.ChooseOutputFirst");
            return;
        }

        var source = _pendingOutputNode;
        sourcePortId ??= _pendingOutputPortId;
        _pendingOutputNode = null;
        _pendingOutputPortId = null;
        ConnectionPreviewPoint = null;
        OnPropertyChanged(nameof(PendingOutputNode));
        OnPropertyChanged(nameof(PendingOutputPortId));
        if (TryConnectNodes(source, target, out var error, targetPortId, sourcePortId))
        {
            SetConnectionStatus("Messages.NodesConnected", source.Name, target.Name);
        }
        else
        {
            if (error is null)
            {
                SetConnectionStatus("Messages.ConnectionFailed");
            }
            else if (MessageLocalization.ErrorKey(error) is { } key)
            {
                SetConnectionStatus(key);
            }
            else
            {
                ConnectionStatus = error;
            }
        }
    }

    public bool TryConnectNodes(
        GraphNodeViewModel source,
        GraphNodeViewModel target,
        out string? error,
        string? targetPortId = null,
        string? sourcePortId = null)
    {
        if (sourcePortId is null && source.BaseDefinition.Kind.Value == "InstanceAge") sourcePortId = "value";
        if (!CanConnectNodes(source, target, out error, targetPortId, sourcePortId)) return false;
        targetPortId ??= target.Definition.InputPorts?.FirstOrDefault()?.Id;
        ExecuteEdit(new AddGraphConnectionCommand(this, source, target,
            targetPortId: targetPortId, sourcePortId: sourcePortId));
        return true;
    }

    public bool CanConnectNodes(GraphNodeViewModel source, GraphNodeViewModel target, out string? error,
        string? targetPortId = null, string? sourcePortId = null)
    {
        if (sourcePortId is null && source.BaseDefinition.Kind.Value == "InstanceAge") sourcePortId = "value";
        if (IsReadOnly) { error = LocalizationService.Shared.Get("Inline.Locked"); return false; }
        if (!Nodes.Contains(source) || !Nodes.Contains(target))
        {
            error = "Both nodes must belong to this graph.";
            return false;
        }

        if (source == target)
        {
            error = "A node cannot connect to itself.";
            return false;
        }

        if (source.IsComment || target.IsComment)
        {
            error = "Comment nodes do not have ports.";
            return false;
        }
        if (target.Definition.InputPorts is { } ports)
        {
            targetPortId ??= ports.FirstOrDefault()?.Id;
            if (targetPortId is null || !ports.Any(port => port.Id == targetPortId))
            {
                error = "The target input port does not exist.";
                return false;
            }
            if (target.Definition.GetInputPortCategory(targetPortId) != GraphPortCategory.Signal &&
                Connections.Any(connection => connection.Target == target && connection.TargetPortId == targetPortId))
            {
                error = "The target input is already connected.";
                return false;
            }
        }
        else if (targetPortId is not null)
        {
            error = "The target input port does not exist.";
            return false;
        }

        if (source.Definition.OutputPorts is { } outputPorts)
        {
            if (sourcePortId is null || !outputPorts.Any(port => port.Id == sourcePortId))
            {
                error = "The source output port does not exist.";
                return false;
            }
        }
        else if (sourcePortId is not null)
        {
            error = "The source output port does not exist.";
            return false;
        }

        var sourceEffective = source.IsReroute
            ? ResolveReroutePort(source)
            : (Category: source.Definition.GetOutputPortCategory(sourcePortId),
               PortType: source.Definition.GetOutputPortType(sourcePortId) ?? source.PortType);
        var targetEffective = target.IsReroute
            ? ResolveReroutePort(target)
            : (Category: target.Definition.GetInputPortCategory(targetPortId),
               PortType: target.Definition.GetInputPortType(targetPortId) ?? target.PortType);

        if (sourceEffective?.Category == GraphPortCategory.Signal &&
            Connections.Any(connection => connection.Source == source && connection.SourcePortId == sourcePortId))
        {
            error = LocalizationService.Shared.Get("Editor.Sequence.SingleDestination");
            return false;
        }

        if (sourceEffective is { } srcEff && targetEffective is { } dstEff &&
            (srcEff.Category != dstEff.Category ||
             (!source.IsPolymorphic && !target.IsPolymorphic && !source.IsReroute && !target.IsReroute &&
              srcEff.PortType != dstEff.PortType) ||
             ((source.IsReroute || target.IsReroute) && srcEff.PortType != dstEff.PortType)))
        {
            error = "Port categories and data types must match.";
            if (srcEff.Category == GraphPortCategory.Parameter && dstEff.Category == GraphPortCategory.Parameter)
            {
                error = LocalizationService.Shared.Get("Messages.Error.PortTypeMismatch") + " " +
                    GraphConversionPresentation.Describe(
                        new GraphConversionRegistry(AvailableNodeDefinitions).Resolve(
                            srcEff.PortType, dstEff.PortType));
            }

            return false;
        }

        if (Connections.Any(connection =>
                connection.Source == source && connection.Target == target &&
                connection.TargetPortId == targetPortId && connection.SourcePortId == sourcePortId))
        {
            error = "These ports are already connected.";
            return false;
        }

        if (WouldCreateCycle(source.Id, target.Id,
                Connections.Select(connection => (connection.Source.Id, connection.Target.Id))))
        {
            error = "Connections cannot create a cycle.";
            return false;
        }

        error = null;
        return true;
    }

    public void DisconnectConnection(GraphConnectionViewModel connection)
    {
        if (Connections.Contains(connection))
        {
            ExecuteEdit(new RemoveGraphConnectionCommand(this, connection));
            SetConnectionStatus("Messages.ConnectionRemoved");
        }
    }

    private void RefreshSelectedConnections()
    {
        SelectedConnections.Clear();
        if (SelectedNode is null)
        {
            return;
        }

        foreach (var connection in Connections.Where(connection =>
                     connection.Source == SelectedNode || connection.Target == SelectedNode))
        {
            SelectedConnections.Add(connection);
        }
    }

    private static bool WouldCreateCycle(
        Guid sourceId,
        Guid targetId,
        IEnumerable<(Guid SourceId, Guid TargetId)> connections)
    {
        var outgoing = connections.ToLookup(connection => connection.SourceId, connection => connection.TargetId);
        var visited = new HashSet<Guid>();
        var pending = new Stack<Guid>();
        pending.Push(targetId);
        while (pending.TryPop(out var node))
        {
            if (node == sourceId)
            {
                return true;
            }

            if (visited.Add(node))
            {
                foreach (var next in outgoing[node])
                {
                    pending.Push(next);
                }
            }
        }

        return false;
    }

    public GraphNodeViewModel? InsertRerouteOnConnection(GraphConnectionViewModel connection, double x, double y)
    {
        if (IsReadOnly || !Connections.Contains(connection)) return null;
        var definition = AvailableNodeDefinitions.FirstOrDefault(item => item.Kind.Value == "Reroute");
        if (definition is null) return null;

        var reroute = new GraphNodeViewModel(
            this,
            definition,
            definition.Name,
            x - 43,
            y - 22,
            id: Guid.NewGuid());
        var first = new GraphConnectionViewModel(connection.Source, reroute, "in", connection.SourcePortId);
        var second = new GraphConnectionViewModel(reroute, connection.Target, connection.TargetPortId, "out");
        ExecuteEdit(new InsertRerouteCommand(this, connection, reroute, first, second));
        SelectNode(reroute);
        return reroute;
    }

    private (GraphPortCategory Category, string PortType)? ResolveReroutePort(
        GraphNodeViewModel node, HashSet<Guid>? visited = null)
    {
        visited ??= [];
        if (!visited.Add(node.Id)) return null;
        foreach (var edge in Connections)
        {
            if (edge.Target == node)
            {
                if (!edge.Source.IsReroute)
                {
                    return (edge.Source.Definition.GetOutputPortCategory(edge.SourcePortId),
                        edge.Source.Definition.GetOutputPortType(edge.SourcePortId) ?? edge.Source.PortType);
                }
                if (ResolveReroutePort(edge.Source, visited) is { } upstream) return upstream;
            }
            if (edge.Source == node)
            {
                if (!edge.Target.IsReroute)
                {
                    return (edge.Target.Definition.GetInputPortCategory(edge.TargetPortId),
                        edge.Target.Definition.GetInputPortType(edge.TargetPortId) ?? edge.Target.PortType);
                }
                if (ResolveReroutePort(edge.Target, visited) is { } downstream) return downstream;
            }
        }
        return null;
    }

    private sealed class InsertRerouteCommand(
        GraphWorkspaceViewModel graph,
        GraphConnectionViewModel original,
        GraphNodeViewModel reroute,
        GraphConnectionViewModel first,
        GraphConnectionViewModel second) : IUndoableCommand
    {
        private int _originalIndex;
        public string Description => $"Insert reroute between {original.Source.Name} and {original.Target.Name}";

        public void Execute()
        {
            _originalIndex = graph.Connections.IndexOf(original);
            graph.RemoveConnection(original);
            graph.AddNode(reroute, graph.Nodes.Count);
            graph.AddConnection(first, graph.Connections.Count);
            graph.AddConnection(second, graph.Connections.Count);
        }

        public void Undo()
        {
            graph.RemoveConnection(second);
            graph.RemoveConnection(first);
            graph.RemoveNode(reroute);
            graph.AddConnection(original, _originalIndex >= 0 ? _originalIndex : graph.Connections.Count);
        }
    }
}
