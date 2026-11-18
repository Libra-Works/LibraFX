using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;

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
    }
}
