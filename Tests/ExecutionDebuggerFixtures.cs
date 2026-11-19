using System.Text.Json;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;

internal static class ExecutionDebuggerFixtures
{
    public static void Write(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Use an isolated absolute fixture path.");
        GraphNodeSnapshot Node(string kind, string parameter, double value)
        {
            var definition = GraphNodeDefinition.All.Single(item => item.Kind.Value == kind);
            return new(Guid.NewGuid(), definition.Kind, kind, 0, 0, definition.PortType, definition.PortCategory,
                Parameters: [new(parameter, "float", JsonSerializer.SerializeToElement(value))]);
        }
        var left = Node("Float", "value", 3);
        var right = Node("Float", "value", 4);
        var add = Node("Add", "b", 0);
        var multiply = Node("Multiply", "b", 2);
        var program = VfxProgram.Compile(new GraphSnapshot([left, right, add, multiply],
        [
            new(left.Id, add.Id, "a"), new(right.Id, add.Id, "b"),
            new(add.Id, multiply.Id, "a")
        ]));
        if (program.BinaryVersion != 1) throw new InvalidDataException("Expected a legacy scalar debugger fixture.");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "DebugScalar.lfxb"), program.ToBinary());
        WriteChildPackage(directory);
    }

    private static void WriteChildPackage(string directory)
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager()) { AutoDestroyWhenIdle = false };
        var start = graph.CreateNode(new("OnStart"), 0, 0);
        var child = graph.CreateNode(GraphNodeKind.SpawnableGroup, 300, 0);
        graph.RenameNode(child, "Debug Child");
        Connect(start, child, "spawn");
        if (!graph.EnterGraph(child))
        {
            throw new InvalidOperationException("Could not open debugger fixture child graph.");
        }

        var time = graph.CreateNode(GraphNodeKind.Time, 0, 0);
        var multiply = graph.CreateNode(new("Multiply"), 300, 0);
        graph.RenameNode(multiply, "Child Scale");
        graph.SetParameter(multiply, "b", JsonSerializer.SerializeToElement(2));
        var add = graph.CreateNode(new("Add"), 600, 0);
        graph.RenameNode(add, "Child Offset");
        graph.SetParameter(add, "b", JsonSerializer.SerializeToElement(3));
        var lifetime = graph.CreateNode(new("LifeTime"), 0, 250);
        graph.SetParameter(lifetime, "seconds", JsonSerializer.SerializeToElement(120));
        Connect(time, multiply, "a");
        Connect(multiply, add, "a");
        graph.NavigateToGraph(0);

        var package = SpawnableGroupPackage.Compile(graph.CreateSnapshot());
        File.WriteAllBytes(Path.Combine(directory, "DebugChild.lfxp"), SpawnableGroupPackageBinary.Serialize(package));
        using var project = new MemoryStream();
        new ProjectFileService().SaveAsync(project, graph).GetAwaiter().GetResult();
        File.WriteAllBytes(Path.Combine(directory, "DebugChild.lfxproj"), project.ToArray());
        File.WriteAllText(Path.Combine(directory, "DebugChild.container"), child.Id.ToString("D"));

        void Connect(GraphNodeViewModel source, GraphNodeViewModel target, string port)
        {
            if (!graph.TryConnectNodes(source, target, out var error, targetPortId: port,
                sourcePortId: source.OutputPorts.Count == 1 ? source.OutputPorts[0].Id : null))
            {
                throw new InvalidOperationException(error);
            }
        }
    }
}
