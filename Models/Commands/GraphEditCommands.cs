using LiberaFx.ViewModels;
using LiberaFx.Models.Graph;

namespace LiberaFx.Models.Commands;

public sealed class SetFCurveCommand(
    GraphNodeViewModel node,
    FCurveSnapshot oldCurve,
    FCurveSnapshot newCurve,
    string description) : IUndoableCommand
{
    public string Description { get; } = description;
    public void Execute() => node.SetCurveValue(newCurve);
    public void Undo() => node.SetCurveValue(oldCurve);
}

public sealed class AddGraphNodeCommand : IUndoableCommand
{
    private readonly GraphWorkspaceViewModel _graph;
    private readonly GraphNodeViewModel _node;
    private readonly int _index;

    public string Description => $"Add {_node.Definition.Name} node";

    public AddGraphNodeCommand(GraphWorkspaceViewModel graph, GraphNodeViewModel node, int index)
    {
        _graph = graph;
        _node = node;
        _index = index;
    }

    public void Execute()
    {
        _graph.AddNode(_node, _index);
    }

    public void Undo()
    {
        _graph.RemoveNode(_node);
    }
}

public sealed class DeleteGraphNodeCommand : IUndoableCommand
{
    private readonly GraphWorkspaceViewModel _graph;
    private readonly GraphNodeViewModel _node;
    private int _nodeIndex;
    private List<(GraphConnectionViewModel Connection, int Index)> _connections = [];

    public string Description => $"Delete {_node.Name} node";

    public DeleteGraphNodeCommand(GraphWorkspaceViewModel graph, GraphNodeViewModel node)
    {
        _graph = graph;
        _node = node;
    }

    public void Execute()
    {
        _nodeIndex = _graph.Nodes.IndexOf(_node);
        if (_nodeIndex < 0)
        {
            throw new InvalidOperationException("The node is no longer in the graph.");
        }

        _connections = _graph.Connections
            .Select((connection, index) => (Connection: connection, Index: index))
            .Where(item => item.Connection.Source == _node || item.Connection.Target == _node)
            .ToList();

        foreach (var item in _connections.OrderByDescending(item => item.Index))
        {
            _graph.RemoveConnection(item.Connection);
        }

        _graph.RemoveNode(_node);
    }

    public void Undo()
    {
        _graph.AddNode(_node, _nodeIndex);
        foreach (var item in _connections.OrderBy(item => item.Index))
        {
            _graph.AddConnection(item.Connection, item.Index);
        }
    }
}

public sealed class MoveGraphNodeCommand : IUndoableCommand
{
    private readonly GraphNodeViewModel _node;
    private readonly double _oldX;
    private readonly double _oldY;
    private readonly double _newX;
    private readonly double _newY;

    public string Description => $"Move {_node.Name} node";

    public MoveGraphNodeCommand(
        GraphNodeViewModel node,
        double oldX,
        double oldY,
        double newX,
        double newY)
    {
        _node = node;
        _oldX = oldX;
        _oldY = oldY;
        _newX = newX;
        _newY = newY;
    }

    public void Execute()
    {
        _node.SetPosition(_newX, _newY);
    }

    public void Undo()
    {
        _node.SetPosition(_oldX, _oldY);
    }
}

public sealed class RenameGraphNodeCommand : IUndoableCommand
{
    private readonly GraphNodeViewModel _node;
    private readonly string _oldName;
    private readonly string _newName;

    public string Description => $"Rename {_oldName} node";

    public RenameGraphNodeCommand(GraphNodeViewModel node, string oldName, string newName)
    {
        _node = node;
        _oldName = oldName;
        _newName = newName;
    }

    public void Execute()
    {
        _node.SetNameValue(_newName);
    }

    public void Undo()
    {
        _node.SetNameValue(_oldName);
    }
}

public sealed class AddGraphConnectionCommand : IUndoableCommand
{
    private readonly GraphWorkspaceViewModel _graph;
    private readonly GraphConnectionViewModel _connection;
    private int _index;

    public string Description => $"Connect {_connection.Source.Name} to {_connection.Target.Name}";

    public AddGraphConnectionCommand(
        GraphWorkspaceViewModel graph,
        GraphNodeViewModel source,
        GraphNodeViewModel target,
        int? index = null)
    {
        _graph = graph;
        _connection = new GraphConnectionViewModel(source, target);
        _index = index ?? graph.Connections.Count;
    }

    public void Execute()
    {
        _index = _graph.AddConnection(_connection, _index);
    }

    public void Undo()
    {
        _graph.RemoveConnection(_connection);
    }
}

public sealed class RemoveGraphConnectionCommand : IUndoableCommand
{
    private readonly GraphWorkspaceViewModel _graph;
    private readonly GraphConnectionViewModel _connection;
    private int _index;

    public string Description => $"Disconnect {_connection.Source.Name} from {_connection.Target.Name}";

    public RemoveGraphConnectionCommand(GraphWorkspaceViewModel graph, GraphConnectionViewModel connection)
    {
        _graph = graph;
        _connection = connection;
    }

    public void Execute()
    {
        _index = _graph.RemoveConnection(_connection);
        if (_index < 0)
        {
            throw new InvalidOperationException("The connection is no longer in the graph.");
        }
    }

    public void Undo() => _graph.AddConnection(_connection, _index);
}
