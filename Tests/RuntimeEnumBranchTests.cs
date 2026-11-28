using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Mcp;
using LiberaFx.Services.Project;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;
using LiberaFx.ViewModels.Panels;
using LiberaFx.Views.Panels;
using LiberaFx.Services.Compilation;

namespace LiberaFx.Tests;

internal static class RuntimeEnumBranchTests
{
    public static void Run(string? fixtureDirectory = null)
    {
        var enumeration = GraphEnumDefinition.All.Values.FirstOrDefault(item => item.Values.Count >= 2)
            ?? throw new InvalidOperationException("The graph has no Enum with at least two values.");
        var enumType = GraphEnumBranchSchema.EnumPortType(enumeration.TypeId);
        var valueKind = new GraphNodeKind(GraphEnumBranchSchema.ValueKind(enumeration.TypeId));
        var selectKind = GraphNodeDefinition.All.Single(item =>
            GraphEnumBranchSchema.TryGetSelector(item.Kind, out var typeId, out var outputType, out _) &&
            typeId == enumeration.TypeId && outputType == "Float").Kind;
        var value = CreateNode(valueKind, "Mode", enumType);
        var positive = CreateNode(GraphNodeKind.Float, "Selected", "Float");
        var numerator = CreateNode(GraphNodeKind.Float, "Numerator", "Float");
        var denominator = CreateNode(GraphNodeKind.Float, "Zero", "Float");
        var divide = CreateNode(GraphNodeKind.Divide, "Inactive divide", "Float");
        var select = CreateNode(selectKind, "Select", "Float");
        positive = SetParameter(positive, "value", JsonSerializer.SerializeToElement(9d));
        numerator = SetParameter(numerator, "value", JsonSerializer.SerializeToElement(1d));
        denominator = SetParameter(denominator, "value", JsonSerializer.SerializeToElement(0d));

        var divideInputs = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.Divide)
            .InputPorts ?? throw new InvalidOperationException("Divide has no input ports.");
        if (divideInputs.Count < 2)
            throw new InvalidOperationException("Divide does not expose both parameter inputs.");
        var firstCase = enumeration.Values[0];
        var secondCase = enumeration.Values[1];
        var graph = new GraphSnapshot(
            [value, positive, numerator, denominator, divide, select],
            [
                new(value.Id, select.Id, "selector"),
                new(positive.Id, select.Id, GraphEnumBranchSchema.CasePortId(firstCase.Value)),
                new(divide.Id, select.Id, GraphEnumBranchSchema.CasePortId(secondCase.Value)),
                new(numerator.Id, divide.Id, divideInputs[0].Id),
                new(denominator.Id, divide.Id, divideInputs[1].Id)
            ]);

        GraphValidator.Validate(graph);
        var program = VfxProgram.Compile(graph);
        var binary = program.ToBinary();
        if (binary.Length < 16 || binary[4] != 8 || !FindBytes(binary, "LFEB"u8))
            throw new InvalidOperationException("Enum branches must produce LFXB v8 with LFEB metadata.");

        var enumValueDefinition = GraphNodeDefinition.All.Single(item => item.Kind == valueKind);
        var enumValueId = enumValueDefinition.TypeId;
        if (enumValueId == Guid.Empty || !GroupBoundarySchema.IsInput(
                new GraphNodeKind(GraphEnumBranchSchema.GroupInputKind(enumeration.TypeId))))
            throw new InvalidOperationException("Enum graph type IDs or Group Input boundaries are unstable.");
        var boundary = CreateNode(
            new GraphNodeKind(GraphEnumBranchSchema.GroupInputKind(enumeration.TypeId)),
            "Enum input", enumType);
        var lowered = GroupBoundaryLowering.LowerGroupContents(new GraphSnapshot([boundary], []));
        if (lowered.Nodes.Single().Kind != valueKind ||
            !(GraphNodeDefinition.All.Single(item => item.Kind == valueKind).InputPorts ?? [])
                .Any(port => port.Id == "value"))
            throw new InvalidOperationException("Enum Group boundaries must lower to a connectable typed Enum value.");

        var serialized = JsonSerializer.Serialize(graph);
        var roundTrip = JsonSerializer.Deserialize<GraphSnapshot>(serialized)
            ?? throw new InvalidOperationException("Enum branch graph serialization returned null.");
        GraphValidator.Validate(roundTrip);
        if (!GraphSnapshot.ContentEquals(graph, roundTrip))
            throw new InvalidOperationException("Enum branch graph serialization changed the graph.");

        VerifyGroupAuthoring(enumeration, valueKind, selectKind, fixtureDirectory);

        if (fixtureDirectory is not null)
        {
            if (!Path.IsPathFullyQualified(fixtureDirectory))
                throw new ArgumentException("Enum branch fixture output must be an absolute path.", nameof(fixtureDirectory));
            Directory.CreateDirectory(fixtureDirectory);
            File.WriteAllBytes(Path.Combine(fixtureDirectory, "enum-branch-divide.lfxp"),
                SpawnableGroupPackageBinary.Serialize(new SpawnableGroupPackage(program.ToPackageBinary(), [])));
            var index = program.TypedInstructions.ToList().FindIndex(item => item.NodeId == select.Id);
            if (index < 0) throw new InvalidOperationException("The selected Enum output has no runtime binding.");
            File.WriteAllText(Path.Combine(fixtureDirectory, "enum-branch-divide.checks"),
                $"0 {index} 1 9\n0.5 {index} 1 9\n2 {index} 1 9\n");
            var exported = new GraphWorkspaceViewModel(new UndoRedoManager());
            exported.InitializeFromSnapshot(graph);
            using var stream = new MemoryStream();
            new ProjectFileService().SaveAsync(stream, exported).GetAwaiter().GetResult();
            File.WriteAllBytes(Path.Combine(fixtureDirectory, "enum-branch-divide.lfxproj"), stream.ToArray());
        }
    }

    private static void VerifyGroupAuthoring(GraphEnumDefinition enumeration, GraphNodeKind valueKind,
        GraphNodeKind selectKind, string? fixtureDirectory)
    {
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        var group = workspace.CreateNode(GraphNodeKind.GraphGroup);
        if (!workspace.EnterGraph(group))
            throw new InvalidOperationException("The editable Enum Group could not be entered.");
        var input = workspace.CreateNode(new(GraphEnumBranchSchema.GroupInputKind(enumeration.TypeId)));
        var output = workspace.CreateNode(new(GraphEnumBranchSchema.GroupOutputKind(enumeration.TypeId)));
        if (!workspace.TryConnectNodes(input, output, out _))
            throw new InvalidOperationException("Matching typed Enum Group boundaries could not be wired.");
        workspace.PublishGroupParameter(input, "value");
        var exposed = workspace.CreateCurrentSnapshot().ExposedItems!.Single();
        workspace.NavigateToGraph(0);
        var source = workspace.CreateNode(valueKind);
        var select = workspace.CreateNode(selectKind);
        group = workspace.GetNode(group.Id);
        if (!workspace.TryConnectNodes(source, group, out _,
                GroupBoundarySchema.InputPortId(input.Id)) ||
            !workspace.TryConnectNodes(group, select, out _, "selector",
                GroupBoundarySchema.OutputPortId(output.Id)))
            throw new InvalidOperationException("Enum identity did not pass through the actual Group input/output pins.");
        var otherEnum = GraphEnumDefinition.All.Values.First(item => item.TypeId != enumeration.TypeId);
        var wrong = workspace.CreateNode(new(GraphEnumBranchSchema.ValueKind(otherEnum.TypeId)));
        var history = workspace.History.CurrentNodeId;
        if (workspace.TryConnectNodes(wrong, select, out _, "selector") ||
            workspace.History.CurrentNodeId != history)
            throw new InvalidOperationException("Different Enum type identities were accepted or changed history.");
        workspace.DeleteNodes([wrong]);
        workspace.SelectNode(workspace.GetNode(group.Id));
        var active = new ActiveGraphProvider(workspace);
        var pane = new NodePropertiesPaneViewModel(active);
        _ = new ThemeSettingsService();
        var inspector = new NodePropertiesView { DataContext = pane };
        var window = new Window
        {
            Title = "LIBERA FX isolated Enum Group inspector",
            Width = 520, Height = 720, Content = inspector
        };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var picker = inspector.GetVisualDescendants().OfType<ComboBox>()
                .Single(control => control.DataContext is NodeParameterEditorViewModel parameter &&
                    parameter.Id == "value");
            var selected = enumeration.Values[^1].Value;
            picker.SelectedItem = picker.ItemsSource!.Cast<GraphEnumValue>()
                .Single(item => item.Value == selected);
            Dispatcher.UIThread.RunJobs();
            if (workspace.GetNode(group.Id).GroupOverrides?.Single().Components.Single().Value?.GetInt32() != selected ||
                !workspace.History.Undo() || !workspace.History.Redo())
                throw new InvalidOperationException("The real Group Enum picker did not preserve typed instance edits and Undo/Redo.");
            var edit = LiberaFxMcpTools.SetGroupInstanceValue(active, group.Id.ToString(),
                exposed.Id.ToString(), JsonSerializer.SerializeToElement(enumeration.Values[0].Value));
            Dispatcher.UIThread.RunJobs();
            edit.GetAwaiter().GetResult();
            var materialized = GroupExposedItems.Materialize(workspace.CreateSnapshot());
            if (materialized.Nodes.Single(node => node.Id == group.Id).ChildGraph!.Nodes
                    .Single(node => node.Id == input.Id).Parameters!.Single(parameter => parameter.Id == "value")
                    .Value.GetInt32() != enumeration.Values[0].Value)
                throw new InvalidOperationException("MCP lost the published Enum's type or instance value.");
        }
        finally { window.Close(); }

        var snapshot = workspace.CreateSnapshot();
        _ = VfxProgram.Compile(snapshot);
        using var stream = new MemoryStream();
        new ProjectFileService().SaveAsync(stream, workspace).GetAwaiter().GetResult();
        stream.Position = 0;
        var restored = ProjectDocumentReader.LoadAsync(stream).GetAwaiter().GetResult();
        if (!GraphSnapshot.ContentEquals(snapshot, restored.Graph))
            throw new InvalidOperationException("Typed Enum Group boundaries/overrides did not survive project persistence.");
        workspace.SetSelection([workspace.GetNode(group.Id), workspace.GetNode(source.Id), workspace.GetNode(select.Id)]);
        workspace.PasteSnapshot(workspace.CreateSelectionSnapshot(), 800, 400);
        if (workspace.CreateSelectionSnapshot().Connections.Count != 2)
            throw new InvalidOperationException("Copy/paste lost typed Enum Group boundary connections.");
        _ = VfxProgram.Compile(workspace.CreateSnapshot());
        if (!workspace.History.Undo() || !workspace.History.Redo())
            throw new InvalidOperationException("Pasting a typed Enum Group did not Undo/Redo.");
        if (fixtureDirectory is not null)
        {
            Directory.CreateDirectory(fixtureDirectory);
            File.WriteAllBytes(Path.Combine(fixtureDirectory, "enum-group-authoring.lfxproj"), stream.ToArray());
            var library = new GroupAssetLibrary(Path.Combine(fixtureDirectory, "enum-group-library"));
            var asset = library.Create("Enum boundary", workspace.GetNode(group.Id).ChildGraph!);
            var revision = library.Save(asset.Id, asset.Revision, asset.Name, asset.Graph);
            if (revision.Revision != asset.Revision + 1 ||
                !GraphSnapshot.ContentEquals(asset.Graph, library.Load(asset.Id).Graph))
                throw new InvalidOperationException("Enum Group assets lost their typed graph across a revision.");
        }
    }

    private static GraphNodeSnapshot CreateNode(GraphNodeKind kind, string name, string portType)
    {
        var definition = GraphNodeDefinition.All.Single(item => item.Kind == kind);
        return new(Guid.NewGuid(), kind, name, 0, 0, portType, GraphPortCategory.Parameter,
            Parameters: definition.Parameters.Select(parameter =>
                new NodeParameterSnapshot(parameter.Id, parameter.Type, parameter.DefaultValue.Clone())).ToArray());
    }

    private static GraphNodeSnapshot SetParameter(
        GraphNodeSnapshot node, string id, JsonElement value)
    {
        var parameters = node.Parameters?.ToList() ?? [];
        var index = parameters.FindIndex(parameter => parameter.Id == id);
        if (index < 0) throw new InvalidOperationException($"Node {node.Kind} has no parameter '{id}'.");
        parameters[index] = parameters[index] with { Value = value };
        return node with { Parameters = parameters };
    }

    private static bool FindBytes(byte[] data, ReadOnlySpan<byte> needle)
    {
        for (var index = 0; index <= data.Length - needle.Length; index++)
            if (data.AsSpan(index, needle.Length).SequenceEqual(needle)) return true;
        return false;
    }
}
