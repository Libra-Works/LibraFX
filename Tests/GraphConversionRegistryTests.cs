using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;
using LiberaFx.Views;

namespace LiberaFx.Tests;

public static class GraphConversionRegistryTests
{
    public static void Run()
    {
        VerifyExactTypesAndNamedOutputs();
        VerifyExplicitEditorWorkflow();
        VerifyExternalDefinitions();
    }

    private static void VerifyExactTypesAndNamedOutputs()
    {
        var registry = new GraphConversionRegistry(GraphNodeDefinition.Core);
        Require(registry.Resolve("FloatX4", "Color") is
            { Status: GraphConversionStatus.Unique, Candidates: [{ Node.Kind.Value: "Vector4ToColor" }] },
            "Color conversion must be discovered from its external node definition.");
        Require(registry.Resolve("Matrix", "Color").Status == GraphConversionStatus.Missing &&
                registry.Resolve("Color", "Matrix").Status == GraphConversionStatus.Missing &&
                registry.Resolve("Quaternion", "FloatX4").Status == GraphConversionStatus.Missing,
            "Semantic types must never be treated as compatible by component width.");
        var channels = registry.Resolve("FloatX3", "Float");
        Require(channels.Status == GraphConversionStatus.Ambiguous &&
                channels.Candidates.Select(rule => rule.OutputPortId).ToHashSet().SetEquals(["x", "y", "z"]),
            "A Break conversion must require explicit selection of its persistent output ID.");
    }

    private static void VerifyExplicitEditorWorkflow()
    {
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        var source = workspace.CreateNode(GraphNodeKind.FloatX4);
        var consumer = workspace.CreateNode(GraphNodeKind.ColorToVector4);
        Require(!workspace.TryConnectNodes(source, consumer, out var error, "a") &&
                error!.Contains("Vector4ToColor") && workspace.Nodes.Count == 2 &&
                workspace.Connections.Count == 0,
            "A registered conversion must be suggested, never inserted implicitly.");
        var invalid = workspace.CreateSnapshot() with
        { Connections = [new(source.Id, consumer.Id, "a")] };
        var diagnostic = GraphTypeDiagnostics.Analyze(invalid).Single();
        Require(diagnostic.Conversion.Status == GraphConversionStatus.Unique,
            "Stored incompatible edges must retain actionable conversion diagnostics.");
        try
        {
            VfxProgram.Compile(invalid);
            throw new InvalidOperationException("Compilation silently converted an incompatible edge.");
        }
        catch (InvalidDataException ex)
        {
            Require(ex.Message.Contains("Vector4ToColor"),
                "Compilation must explain which explicit conversion is required.");
        }

        var converter = workspace.CreateNode(GraphNodeKind.Vector4ToColor);
        Require(workspace.TryConnectNodes(source, converter, out _, "a") &&
                workspace.TryConnectNodes(converter, consumer, out _, "a") &&
                workspace.TypeProblems.Count == 0,
            "An explicitly authored conversion must connect and clear type diagnostics.");
        Require(workspace.History.Undo() && workspace.Connections.Count == 1 &&
                workspace.History.Redo() && workspace.Connections.Count == 2,
            "Conversion connections must use the existing undoable graph commands.");
        var snapshot = workspace.CreateSnapshot();
        var program = VfxProgram.Compile(snapshot);
        Require(program.TypedInstructions.Single(item => item.NodeId == converter.Id).Opcode ==
                VfxOpcode.Vector4ToColor && GraphSnapshot.ContentEquals(snapshot, workspace.CreateSnapshot()),
            "Compilation must execute the explicit node without rewriting the authored graph.");
        using (var stream = new MemoryStream())
        {
            new ProjectFileService().SaveAsync(stream, workspace).GetAwaiter().GetResult();
            stream.Position = 0;
            Require(GraphSnapshot.ContentEquals(snapshot,
                ProjectDocumentReader.LoadAsync(stream).GetAwaiter().GetResult().Graph),
                "Explicit conversion identity must survive project/history persistence.");
        }

        var catalog = new NodeCatalogViewModel(new ActiveGraphProvider(workspace));
        var entry = catalog.Groups.SelectMany(group => group.Nodes).Single(node =>
            node.KindName == GraphNodeKind.Vector4ToColor.Value);
        Require(entry.HasConversions && entry.ConversionDescription.Contains("Vector4") &&
                entry.ConversionDescription.Contains("Color"),
            "Conversion signatures must be discoverable in the node catalog.");
        var view = new NodeCatalogView { DataContext = catalog };
        var window = new Window { Content = view, Width = 400, Height = 600 };
        window.Show();
        try
        {
            catalog.SearchText = entry.OriginalName;
            Dispatcher.UIThread.RunJobs();
            Require(view.GetVisualDescendants().OfType<TextBlock>().Any(text =>
                text.IsEffectivelyVisible && text.Text == entry.ConversionDescription),
                "The actual catalog view must display the conversion signature.");
        }
        finally { window.Close(); }
    }

    private static void VerifyExternalDefinitions()
    {
        var root = Path.Combine(AppDataDirectory.Root, "conversion-registry-tests", Guid.NewGuid().ToString("N"));
        var firstDirectory = Path.Combine(root, "first");
        var secondDirectory = Path.Combine(root, "second");
        var invalidDirectory = Path.Combine(root, "invalid");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        Directory.CreateDirectory(invalidDirectory);
        File.WriteAllText(Path.Combine(firstDirectory, "conversion.json"), ExternalDefinition().ToJsonString());
        File.WriteAllText(Path.Combine(secondDirectory, "conversion.json"), ExternalDefinition().ToJsonString());
        var first = GraphNodeDefinition.LoadDirectory(firstDirectory, out var warnings).Single();
        Require(warnings.Count == 0, "A valid external conversion declaration must load.");
        var second = GraphNodeDefinition.LoadDirectory(secondDirectory, out warnings).Single();
        Require(warnings.Count == 0, "Alternative explicit converters must be permitted.");
        Require(new GraphConversionRegistry([first]).Resolve("Float", "FloatX2").Status ==
                GraphConversionStatus.Unique &&
                new GraphConversionRegistry([first, second]).Resolve("Float", "FloatX2").Status ==
                GraphConversionStatus.Ambiguous,
            "Registry resolution must diagnose alternatives instead of choosing one silently.");

        var scalar = Snapshot(GraphNodeDefinition.Core.Single(node => node.Kind == GraphNodeKind.Float));
        var conversion = Snapshot(first);
        var program = VfxProgram.Compile(new([scalar, conversion], [new(scalar.Id, conversion.Id, "a")]));
        var instruction = program.CustomInstructions.Single(item => item.NodeId == conversion.Id);
        Require(instruction.TypeId == first.TypeId && instruction.OutputWidth == 2 &&
                instruction.Inputs.Single().SourceWidth == 1,
            "External conversions must compile through their actual custom runtime ABI, not a width cast.");

        var profiles = new TeamProfilesService(Path.Combine(root, "profiles.json"));
        Require(profiles.Save(null, "Conversion A", "", firstDirectory), profiles.Status);
        var firstProfile = profiles.Active.Id;
        using var catalog = new NodeDefinitionCatalog(profiles);
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager(), nodeDefinitions: catalog);
        var source = Snapshot(GraphNodeDefinition.Core.Single(node => node.Kind == GraphNodeKind.Float));
        var target = Snapshot(GraphNodeDefinition.Core.Single(node => node.Kind == GraphNodeKind.AddVector2));
        workspace.InitializeFromSnapshot(new([source, target], [new(source.Id, target.Id, "a")]));
        var original = workspace.CreateSnapshot();
        Require(workspace.TypeProblems.Single().Message.Contains(first.QualifiedName) &&
                !workspace.TypeProblems.Single().Message.Contains(second.QualifiedName),
            "Suggestions must use active project definitions, not the retained global registry.");
        Require(profiles.Save(null, "Conversion B", "", secondDirectory), profiles.Status);
        Require(workspace.TypeProblems.Single().Message.Contains(second.QualifiedName) &&
                !workspace.TypeProblems.Single().Message.Contains(first.QualifiedName) &&
                GraphSnapshot.ContentEquals(original, workspace.CreateSnapshot()),
            "Project switching must refresh conversion diagnostics without editing the graph.");
        profiles.Select(firstProfile);

        foreach (var mutate in new Action<JsonObject>[]
        {
            node => node["conversions"]![0]!["mode"] = "implicit",
            node => node["conversions"]![0]!["inputPortId"] = "missing",
            node => node["conversions"]![0]!["outputPortId"] = "missing",
            node => node["conversions"]![0]!["outputPortId"] = null,
            node => ((JsonArray)node["conversions"]!).Add(node["conversions"]![0]!.DeepClone()),
            node => node["outputPorts"]![0]!["portType"] = "Float"
        })
        {
            var invalid = ExternalDefinition();
            mutate(invalid);
            File.WriteAllText(Path.Combine(invalidDirectory, "invalid.json"), invalid.ToJsonString());
            Require(GraphNodeDefinition.LoadDirectory(invalidDirectory, out warnings).Count == 0 &&
                    warnings.Any(message => message.Contains("conversion", StringComparison.OrdinalIgnoreCase)),
                "Invalid, duplicate, implicit and identity conversion declarations must be rejected.");
        }
    }

    private static JsonObject ExternalDefinition() => new()
    {
        ["schemaVersion"] = 1, ["typeId"] = Guid.NewGuid().ToString("D"),
        ["namespace"] = "ConversionTests", ["kind"] = "Convert_" + Guid.NewGuid().ToString("N"),
        ["name"] = "Explicit fixture conversion", ["group"] = "Conversions",
        ["portType"] = "FloatX2", ["portCategory"] = "Parameter",
        ["inputPorts"] = new JsonArray(new JsonObject
        { ["id"] = "a", ["name"] = "Value", ["portType"] = "Float" }),
        ["outputPorts"] = new JsonArray(new JsonObject
        { ["id"] = "value", ["name"] = "Value", ["portType"] = "FloatX2" }),
        ["runtime"] = new JsonObject
        { ["kind"] = "custom", ["abiVersion"] = 1, ["execution"] = "parameter" },
        ["conversions"] = new JsonArray(new JsonObject
        { ["inputPortId"] = "a", ["outputPortId"] = "value", ["mode"] = "explicit" })
    };

    private static GraphNodeSnapshot Snapshot(GraphNodeDefinition node) =>
        new(Guid.NewGuid(), node.Kind, node.Name, 0, 0, node.PortType, node.PortCategory);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
