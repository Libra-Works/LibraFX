using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Project;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;
using LiberaFx.Views.Graph;
using LiberaFx.Services.Compilation;

namespace LiberaFx.Tests;

public static class VectorArithmeticTests
{
    public static void Run()
    {
        VerifyVectorArithmeticIsTyped();
        VerifyConversionsAndMasks();
        VerifyNestedGroupsFlatten();
        VerifyMalformedMasksAreRejected();
        VerifyScalarBytecodeRemainsVersionOne();
        VerifyBreakPinsThroughEditorPersistence();
    }

    private static void VerifyBreakPinsThroughEditorPersistence()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        GraphNodeViewModel Create(string kind) =>
            graph.CreateNode(GraphNodeDefinition.All.Single(item => item.Kind.Value == kind).Kind);
        var vector = Create("FloatX4");
        var split = Create("BreakVector4");
        Require(graph.TryConnectNodes(vector, split, out _, "a"), "Break must accept a Vector4 input.");
        var channels = new[] { "x", "y", "z", "w" };
        var consumers = channels.Select(channel =>
        {
            var consumer = Create("ScalarToVector2");
            graph.BeginConnection(split, channel);
            graph.CompleteConnection(consumer, "a");
            Require(graph.Connections.Any(edge => edge.Target == consumer && edge.SourcePortId == channel),
                "Each Break output pin must connect independently.");
            return consumer.Id;
        }).ToArray();
        Require(graph.History.Undo() && graph.Connections.Count == 4 &&
                graph.History.Redo() && graph.Connections.Count == 5,
            "Undo/redo must preserve the identity of a multi-output connection.");
        var code = VfxProgram.Compile(graph.CreateSnapshot()).TypedInstructions;
        for (var index = 0; index < consumers.Length; index++)
            Require(code.Single(item => item.NodeId == consumers[index]).InputASourceComponent == index,
                "Break consumers must compile to their own source components.");
        using var project = new MemoryStream();
        new ProjectFileService().SaveAsync(project, graph).GetAwaiter().GetResult();
        project.Position = 0;
        var loaded = ProjectDocumentReader.LoadAsync(project).GetAwaiter().GetResult();
        Require(loaded.Graph.Connections.Where(edge => edge.SourceNodeId == split.Id)
                    .Select(edge => edge.SourcePortId).SequenceEqual(channels),
            "Project saves must retain all Break output pin identities.");
        var library = new GroupAssetLibrary(Path.Combine(AppDataDirectory.Root, "break-pin-groups"));
        var asset = library.Create("Named output pins", graph.CreateSnapshot());
        Require(GraphSnapshot.ContentEquals(library.Load(asset.Id).Graph, graph.CreateSnapshot()),
            "Reusable groups must retain their named output connections.");
        graph.SetSelection(graph.Nodes.ToArray());
        graph.PasteSnapshot(graph.CreateSelectionSnapshot(), 900, 0);
        Require(graph.Connections.Where(edge => edge.Source.Id != split.Id &&
                    edge.Source.Definition.Kind.Value == "BreakVector4").Select(edge => edge.SourcePortId)
                    .ToHashSet(StringComparer.Ordinal).SetEquals(channels),
            "Copy/paste must preserve named Break outputs while remapping node IDs.");
        var current = graph.GetNode(split.Id);
        var view = new GraphNodeView { DataContext = current };
        var window = new Window { Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var pins = view.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Tag is GraphOutputPortDefinition && border.IsEffectivelyVisible).ToArray();
            Require(pins.Length == 4 &&
                    pins.Select(pin => ((GraphOutputPortDefinition)pin.Tag!).Id).SequenceEqual(channels) &&
                    pins.All(pin => pin.Bounds.Height > 0),
                "Break Vector4 must visibly expose four real output pins, not a component selector.");
        }
        finally { window.Close(); }
    }

    private static void VerifyVectorArithmeticIsTyped()
    {
        var left = Node("FloatX2", new double[] { 0.0, 0.0 }, 1);
        var right = Node("FloatX2", new double[] { 0.0, 0.0 }, 2);
        var add = Node("AddVector2", null, 3);
        var graph = new GraphSnapshot([left, right, add],
        [
            new(left.Id, add.Id, "a"),
            new(right.Id, add.Id, "b")
        ]);
        var bytes = VfxProgram.Compile(graph).ToBinary();
        Require(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)) == 2, "Vector compilation must select bytecode v2.");
        Require(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12)) == 96, "Typed instruction size must be explicit.");
        Require(bytes[16 + 2 * 96 + 16] == (byte)VfxOpcode.AddVector2, "The vector operation must be encoded.");
        Require(bytes[16 + 2 * 96 + 17] == 2, "Vector2 output width must be encoded.");
    }

    private static void VerifyConversionsAndMasks()
    {
        var vector = Node("FloatX4", new double[] { 0.0, 0.0, 0.0, 0.0 }, 10);
        var mask = Node("MaskVector2", null, 11,
            ("channels", "string", JsonSerializer.SerializeToElement("wy")));
        var breakNode = Node("BreakVector4", null, 12);
        var scalar = Node("ScalarToVector2", null, 14);
        var extended = Node("Vector2ToVector4", null, 13,
            ("fill", "float", JsonSerializer.SerializeToElement(7.0)));
        var graph = new GraphSnapshot([vector, mask, breakNode, scalar, extended],
        [
            new(vector.Id, mask.Id, "a"),
            new(vector.Id, breakNode.Id, "a"),
            new(mask.Id, extended.Id, "a"),
            new(breakNode.Id, scalar.Id, "a", "w")
        ]);
        var json = JsonSerializer.Serialize(graph, new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });
        var loaded = JsonSerializer.Deserialize<GraphSnapshot>(json, new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        })!;
        Require(GraphSnapshot.ContentEquals(graph, loaded) && loaded.Connections.Single(connection =>
                connection.TargetNodeId == scalar.Id).SourcePortId == "w",
            "Multi-output connection IDs must survive snapshot serialization.");
        var code = VfxProgram.Compile(loaded).TypedInstructions;
        Require(code.Single(item => item.NodeId == mask.Id).DefaultA.Take(2).SequenceEqual([3.0, 1.0]),
            "Mask channels must be stored in selected order.");
        Require(code.Single(item => item.NodeId == breakNode.Id).OutputWidth == 4,
            "Break must preserve all components for its output pins.");
        Require(code.Single(item => item.NodeId == scalar.Id).InputASourceComponent == 3,
            "A connection from the W output must select its source lane.");
        Require(code.Single(item => item.NodeId == extended.Id).DefaultA[0] == 7,
            "Dimension extension must preserve its explicit fill value.");
    }

    private static void VerifyNestedGroupsFlatten()
    {
        var value = Node("Float", 2.0, 21);
        var inner = new GraphSnapshot([value], []);
        var groupDefinition = GraphNodeDefinition.All.Single(item => item.Kind.Value == "GraphGroup");
        var group = new GraphNodeSnapshot(Guid.Parse("00000000-0000-0000-0000-000000000022"),
            groupDefinition.Kind, groupDefinition.Name, 0, 0, groupDefinition.PortType,
            groupDefinition.PortCategory, ChildGraph: inner);
        var bytes = VfxProgram.Compile(new GraphSnapshot([group], [])).ToBinary();
        Require(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)) == 1,
            "Independent nested group nodes must be included in the flattened program.");
    }

    private static void VerifyMalformedMasksAreRejected()
    {
        var vector = Node("FloatX4", new double[] { 0.0, 0.0, 0.0, 0.0 }, 31);
        var mask = Node("MaskVector2", null, 32,
            ("channels", "string", JsonSerializer.SerializeToElement("x")));
        var graph = new GraphSnapshot([vector, mask], [new(vector.Id, mask.Id, "a")]);
        try
        {
            VfxProgram.Compile(graph);
            throw new InvalidOperationException("Invalid channel lengths should be rejected.");
        }
        catch (InvalidDataException)
        {
        }
    }

    private static void VerifyScalarBytecodeRemainsVersionOne()
    {
        var value = Node("Float", 1.25, 41);
        var bytes = VfxProgram.Compile(new GraphSnapshot([value], [])).ToBinary();
        Require(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)) == 1,
            "Scalar-only graphs must keep emitting compatible v1 bytecode.");
    }

    private static GraphNodeSnapshot Node(string kind, object? value, int id,
        params (string Id, string Type, JsonElement Value)[] parameters)
    {
        var definition = GraphNodeDefinition.All.Single(item => item.Kind.Value == kind);
        IReadOnlyList<NodeParameterSnapshot>? snapshots = parameters.Length > 0
            ? parameters.Select(parameter => new NodeParameterSnapshot(parameter.Id, parameter.Type, parameter.Value)).ToArray()
            : null;
        if (value is not null)
        {
            var parameterDefinition = definition.Parameters.Single(item => item.Id == "value");
            var element = JsonSerializer.SerializeToElement(value);
            snapshots = [new NodeParameterSnapshot("value", parameterDefinition.Type, element)];
        }
        return new GraphNodeSnapshot(Guid.Parse($"00000000-0000-0000-0000-{id:x12}"),
            definition.Kind, definition.Name, 0, 0, definition.PortType, definition.PortCategory,
            Parameters: snapshots);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
