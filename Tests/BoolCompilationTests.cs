using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;

namespace LiberaFx.Tests;

public static class BoolCompilationTests
{
    public static void Run()
    {
        foreach (var enabled in new[] { false, true })
        {
            var source = Node("Bool", new NodeParameterSnapshot("value", "bool", JsonSerializer.SerializeToElement(enabled)));
            var conversion = Node("BoolToFloat");
            var graph = new GraphSnapshot([source, conversion], [new(source.Id, conversion.Id, "a")]);
            var program = VfxProgram.Compile(graph);
            Require(program.Instructions.Single(item => item.NodeId == source.Id).DefaultA == (enabled ? 1 : 0),
                "Bool literals must reach the runtime as canonical false/true values.");
            var absolute = Node("Abs");
            var mixed = VfxProgram.Compile(graph with
            {
                Nodes = [source, conversion, absolute],
                Connections = [.. graph.Connections, new(conversion.Id, absolute.Id, "a")]
            });
            Require(mixed.Instructions.Single(item => item.NodeId == source.Id).DefaultA == (enabled ? 1 : 0),
                "Custom-node compilation must preserve Bool literals too.");

            var input = Node("GroupInputBool");
            var output = Node("GroupOutputBool");
            var group = Node("GraphGroup") with { ChildGraph = new([input, output], [new(input.Id, output.Id)]) };
            var grouped = VfxProgram.Compile(new([source, group, conversion],
                [new(source.Id, group.Id, GroupBoundarySchema.InputPortId(input.Id)),
                 new(group.Id, conversion.Id, "a", GroupBoundarySchema.OutputPortId(output.Id))]));
            Require(grouped.Instructions.Count == 4, "Bool boundaries must flatten into runtime relays.");
        }

        var property = Node("InputProperty", new("name", "string", JsonSerializer.SerializeToElement("Enabled")),
            new("valueType", "enum:1e309707-1fa0-4d38-aed9-42d000000001", JsonSerializer.SerializeToElement(6)));
        var cast = Node("BoolToFloat");
        var gameIo = VfxProgram.Compile(new([property, cast], [new(property.Id, cast.Id, "a", "value")]));
        Require(gameIo.GameIoPorts.Single().Width == 1, "Game I/O Bool properties require one runtime lane.");
        Require(GameIoSchema.Effective(GraphNodeDefinition.Find(property.Kind)!, property.Parameters).PortType == "Bool",
            "Game I/O must expose Bool pins rather than Float pins.");

        var wrong = Node("Float");
        try
        {
            VfxProgram.Compile(new([wrong, cast], [new(wrong.Id, cast.Id, "a")]));
            throw new InvalidOperationException("Float was implicitly accepted as Bool.");
        }
        catch (InvalidDataException) { }

        var corrupt = Node("Bool", new NodeParameterSnapshot("value", "bool", JsonSerializer.SerializeToElement(1)));
        try
        {
            VfxProgram.Compile(new([corrupt], []));
            throw new InvalidOperationException("A numeric Bool literal was accepted.");
        }
        catch (InvalidDataException) { }
    }

    private static GraphNodeSnapshot Node(string kind, params NodeParameterSnapshot[] parameters)
    {
        var definition = GraphNodeDefinition.All.Single(item => item.Kind.Value == kind);
        return new(Guid.NewGuid(), definition.Kind, definition.Name, 0, 0,
            definition.PortType, definition.PortCategory, Parameters: parameters);
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
