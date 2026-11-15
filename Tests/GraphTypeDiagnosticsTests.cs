using System.Text.Json;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;

namespace LiberaFx.Tests;

public static class GraphTypeDiagnosticsTests
{
    public static void Run()
    {
        GraphNodeSnapshot Node(GraphNodeKind kind)
        {
            var definition = GraphNodeDefinition.All.Single(item => item.Kind == kind);
            return new(Guid.NewGuid(), kind, definition.Name, 0, 0,
                definition.PortType, definition.PortCategory);
        }

        var vec2 = Node(GraphNodeKind.FloatX2);
        var vec3 = Node(GraphNodeKind.FloatX3);
        var add = Node(GraphNodeKind.Add);
        var other = Node(GraphNodeKind.Multiply);
        var unrelated = Node(GraphNodeKind.Time);
        var graph = new GraphSnapshot([vec2, vec3, add, other, unrelated],
        [
            new(vec2.Id, add.Id, "a"),
            new(vec3.Id, add.Id, "b"),
            new(vec2.Id, other.Id, "a")
        ]);
        GraphValidator.Validate(graph);
        var decoded = JsonSerializer.Deserialize<GraphSnapshot>(JsonSerializer.Serialize(graph))!;
        GraphValidator.Validate(decoded);
        Require(GraphTypeDiagnostics.Analyze(decoded).Count == 3, "All mismatched edges must be reported.");
        try { VfxProgram.Compile(decoded); }
        catch (InvalidDataException error)
        {
            Require(error.Message.Contains(add.Id.ToString()) && error.Message.Contains(other.Id.ToString()),
                "Compilation errors must identify all affected nodes.");
            goto compilationRejected;
        }
        throw new InvalidOperationException("Compilation accepted incompatible types.");

    compilationRejected:
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        workspace.InitializeFromSnapshot(decoded);
        Require(workspace.TypeProblems.Count == 3 &&
                workspace.Nodes.Single(node => node.Id == add.Id).HasDiagnostics &&
                workspace.Nodes.Single(node => node.Id == other.Id).HasDiagnostics &&
                !workspace.Nodes.Single(node => node.Id == unrelated.Id).HasDiagnostics,
            "Diagnostics must be specific to the affected nodes.");
        workspace.TypeProblems[0].SelectCommand.Execute(null);
        Require(workspace.SelectedNode?.Id == add.Id, "Selecting a problem must select its node.");
        workspace.DisconnectConnection(workspace.Connections.Single(edge =>
            edge.Target.Id == add.Id && edge.TargetPortId == "a"));
        Require(workspace.TypeProblems.Count == 2, "Disconnecting must clear only its own error.");
        Require(workspace.History.Undo() && workspace.TypeProblems.Count == 3,
            "Undo must restore mismatched edges and diagnostics.");
        workspace.DisconnectConnection(workspace.Connections.Single(edge =>
            edge.Target.Id == add.Id && edge.TargetPortId == "b"));
        workspace.DisconnectConnection(workspace.Connections.Single(edge =>
            edge.Target.Id == add.Id && edge.TargetPortId == "a"));
        Require(!workspace.Nodes.Single(node => node.Id == add.Id).HasDiagnostics &&
                workspace.TypeProblems.Count == 1, "Fixing a node must clear its red state.");

        var corrupt = graph with { Connections = [new(vec2.Id, add.Id, "a", "missing")] };
        try { GraphValidator.Validate(corrupt); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("A nonexistent source port must remain structurally invalid.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
