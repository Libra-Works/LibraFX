using System.Text.Json;
using Avalonia;
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

internal static class LogicalSignalTests
{
    private static readonly GraphNodeDefinition CompareDefinition =
        GraphNodeDefinition.All.Single(item => item.TypeId == GraphLogicalSchema.CompareTypeId);
    private static readonly GraphNodeDefinition AndDefinition =
        GraphNodeDefinition.All.Single(item => item.TypeId == GraphLogicalSchema.SignalAndTypeId);
    private static readonly GraphNodeDefinition OrDefinition =
        GraphNodeDefinition.All.Single(item => item.TypeId == GraphLogicalSchema.SignalOrTypeId);
    private static readonly GraphNodeDefinition SignalToNumberDefinition =
        GraphNodeDefinition.All.Single(item => item.TypeId == GraphLogicalSchema.SignalToNumberTypeId);

    public static void Run(string? fixtureDirectory = null)
    {
        VerifyAuthoringAndPersistence();
        VerifyValidationAndCompilerOutput(fixtureDirectory);
    }

    private static void VerifyAuthoringAndPersistence()
    {
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        var compare = workspace.CreateNode(CompareDefinition.Kind, 40, 40);
        var and = workspace.CreateNode(AndDefinition.Kind, 320, 40);
        var toNumber = workspace.CreateNode(SignalToNumberDefinition.Kind, 600, 40);
        var add = workspace.CreateNode(GraphNodeKind.Add, 900, 40);

        Require(compare.OutputPorts.Select(port => port.Id)
                .SequenceEqual([GraphLogicalSchema.ConditionOutput, GraphLogicalSchema.EventOutput]),
            "Compare named outputs or their order changed.");
        Require(and.InputPorts.Select(port => port.Id)
                .SequenceEqual(GraphLogicalSchema.GateSignalInputPortIds),
            "Signal AND must expose its two stable event slots and explicit Reset.");
        Require(and.OutputPorts.Select(port => port.Id)
                .SequenceEqual(GraphLogicalSchema.GateOutputPortIds),
            "Signal AND named Pulse/Signal outputs changed.");
        Require(toNumber.InputPorts.Select(port => port.Id)
                .SequenceEqual(GraphLogicalSchema.SignalToNumberInputPortIds) &&
            toNumber.OutputPorts.Single().Id == GraphLogicalSchema.ValueOutput,
            "Signal To Number must expose typed Signal/Reset inputs and a named numeric value.");

        Require(workspace.TryConnectNodes(compare, and, out _, targetPortId: GraphLogicalSchema.InputA,
                sourcePortId: GraphLogicalSchema.EventOutput),
            "The UI graph model rejected a named Signal event connection.");
        Require(workspace.TryConnectNodes(and, toNumber, out _, targetPortId: GraphLogicalSchema.SignalInput,
                sourcePortId: GraphLogicalSchema.SignalOutput),
            "The UI graph model rejected a named Signal output connection.");
        Require(workspace.TryConnectNodes(toNumber, add, out _, targetPortId: "a",
                sourcePortId: GraphLogicalSchema.ValueOutput),
            "The UI graph model rejected a numeric Signal To Number output.");
        var sourceSignal = workspace.CreateNode(GraphNodeKind.Signal, 40, 360);
        var historyBeforeBadWire = workspace.History.CurrentNodeId;
        Require(!workspace.TryConnectNodes(sourceSignal, add, out _, targetPortId: "b") &&
            workspace.History.CurrentNodeId == historyBeforeBadWire,
            "The UI graph model accepted an implicit Signal-to-Float conversion.");
        var connection = workspace.Connections.Single(item =>
            item.Source.Id == and.Id && item.Target.Id == toNumber.Id);
        Require(connection.SourcePortId == GraphLogicalSchema.SignalOutput &&
            connection.TargetPortId == GraphLogicalSchema.SignalInput,
            "Named Signal port identity was lost in the live graph.");

        var undoState = workspace.History.CurrentNodeId;
        var reset = workspace.CreateNode(GraphNodeKind.Signal, 40, 300);
        Require(workspace.TryConnectNodes(reset, and, out _, targetPortId: GraphLogicalSchema.ResetInput),
            "The explicit Reset Signal input could not be wired.");
        Require(workspace.History.Undo() && !workspace.Connections.Any(item =>
                item.Source.Id == reset.Id && item.Target.Id == and.Id) &&
            workspace.History.Redo() && workspace.Connections.Any(item =>
                item.Source.Id == reset.Id && item.Target.Id == and.Id),
            "Signal Reset wiring did not survive Undo/Redo.");
        Require(workspace.History.Undo() && workspace.History.Undo() &&
            workspace.History.CurrentNodeId == undoState,
            "Undo did not reverse logical connection and node authoring steps.");
        Require(workspace.History.Redo() && workspace.History.Redo(),
            "Redo did not restore logical nodes and named connections.");

        var snapshot = workspace.CreateSnapshot();
        GraphValidator.Validate(snapshot);
        var pasted = new GraphWorkspaceViewModel(new UndoRedoManager());
        pasted.PasteSnapshot(snapshot, 1200, 100);
        var pastedAnd = pasted.Nodes.Single(item => item.Definition.TypeId == GraphLogicalSchema.SignalAndTypeId);
        var pastedConnection = pasted.Connections.Single(item => item.Target.Id == pastedAnd.Id &&
            item.TargetPortId == GraphLogicalSchema.InputA);
        Require(pastedConnection.SourcePortId == GraphLogicalSchema.EventOutput,
            "Copy/paste changed a logical named output identity.");

        using var project = new MemoryStream();
        new ProjectFileService().SaveAsync(project, workspace).GetAwaiter().GetResult();
        project.Position = 0;
        var loaded = ProjectDocumentReader.LoadAsync(project).GetAwaiter().GetResult();
        Require(GraphSnapshot.ContentEquals(snapshot, loaded.Graph),
            "Project save/load changed logical modes, types or named connections.");

        var provider = new ActiveGraphProvider(workspace);
        var nodeTypesTask = LiberaFxMcpTools.ListNodeTypes(provider, summaryOnly: false,
            kinds: [GraphLogicalSchema.CompareTypeId.ToString("D"), SignalToNumberDefinition.Kind.Value]);
        Dispatcher.UIThread.RunJobs();
        var nodeTypes = nodeTypesTask.GetAwaiter().GetResult();
        Require(nodeTypes.Contains(GraphLogicalSchema.CompareTypeId.ToString("D"), StringComparison.Ordinal) &&
            nodeTypes.Contains(GraphLogicalSchema.EventOutput, StringComparison.Ordinal) &&
            nodeTypes.Contains(GraphLogicalSchema.SignalToNumberTypeId.ToString("D"), StringComparison.Ordinal),
            "MCP node catalog did not expose logical types and named outputs.");
        var createTask = LiberaFxMcpTools.CreateNode(provider, SignalToNumberDefinition.Kind.Value);
        Dispatcher.UIThread.RunJobs();
        _ = createTask.GetAwaiter().GetResult();
        Require(workspace.Nodes.Count(item => item.Definition.TypeId == GraphLogicalSchema.SignalToNumberTypeId) == 2,
            "MCP could not create Signal To Number from the active catalog.");

        VerifyRenderedPins(compare, and);
    }

    private static void VerifyRenderedPins(GraphNodeViewModel compare, GraphNodeViewModel gate)
    {
        if (Application.Current is null)
            throw new InvalidOperationException("The Avalonia application must be initialized for the UI pin check.");
        var compareView = new GraphNodeView { DataContext = compare };
        var gateView = new GraphNodeView { DataContext = gate };
        var window = new Window
        {
            Width = 900,
            Height = 600,
            Content = new StackPanel { Children = { compareView, gateView } }
        };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var outputs = compareView.GetVisualDescendants().OfType<Border>()
                .Where(border => border.IsEffectivelyVisible)
                .Select(border => border.Tag).OfType<GraphOutputPortDefinition>()
                .Select(port => port.Id).ToHashSet(StringComparer.Ordinal);
            var gateInputs = gateView.GetVisualDescendants().OfType<Button>()
                .Where(button => button.IsEffectivelyVisible)
                .Select(button => button.Tag).OfType<GraphInputPortDefinition>()
                .Select(port => port.Id).ToHashSet(StringComparer.Ordinal);
            var gateOutputs = gateView.GetVisualDescendants().OfType<Border>()
                .Where(border => border.IsEffectivelyVisible)
                .Select(border => border.Tag).OfType<GraphOutputPortDefinition>()
                .Select(port => port.Id).ToHashSet(StringComparer.Ordinal);
            Require(outputs.SetEquals([GraphLogicalSchema.ConditionOutput, GraphLogicalSchema.EventOutput]),
                "The real node view did not render both named Compare outputs.");
            Require(gateInputs.SetEquals(GraphLogicalSchema.GateSignalInputPortIds) &&
                gateOutputs.SetEquals(GraphLogicalSchema.GateOutputPortIds),
                "The real node view did not render the logical Signal slots and separate numeric/Signal outputs.");
        }
        finally { window.Close(); }
    }

    private static void VerifyValidationAndCompilerOutput(string? fixtureDirectory)
    {
        var graph = BuildRuntimeGraph();
        GraphValidator.Validate(graph);
        var legacyGraph = new GraphSnapshot(
            [Float(Guid.Parse("35100000-0000-4000-8000-0000000000ff"), "Legacy scalar", 2)], []);
        var legacyBinary = VfxProgram.Compile(legacyGraph).ToBinary();
        Require(legacyBinary[4] == 1,
            "A graph without logical instructions must retain its legacy scalar bytecode version.");

        var invalidType = graph with
        {
            Connections = graph.Connections.Concat([
                new GraphConnectionSnapshot(FixtureIds.Compare, FixtureIds.SignalAnd,
                    GraphLogicalSchema.InputB, GraphLogicalSchema.ConditionOutput)
            ]).ToArray()
        };
        try
        {
            GraphValidator.Validate(invalidType);
            throw new InvalidOperationException("A numeric Compare output connected directly to a Signal input.");
        }
        catch (InvalidDataException) { }
        var invalidSignalToNumber = graph with
        {
            Connections = graph.Connections.Concat([
                new GraphConnectionSnapshot(FixtureIds.AndA, FixtureIds.AndAdd, "a")
            ]).ToArray()
        };
        try
        {
            GraphValidator.Validate(invalidSignalToNumber);
            throw new InvalidOperationException("A Signal input connected directly to a numeric node.");
        }
        catch (InvalidDataException) { }

        var invalidOperation = ReplaceParameter(
            graph, FixtureIds.Compare, GraphLogicalSchema.OperationParameterId, JsonSerializer.SerializeToElement(6));
        try
        {
            GraphLogicalSchema.ValidateNodeParameters(CompareDefinition,
                invalidOperation.Nodes.Single(item => item.Id == FixtureIds.Compare).Parameters);
            throw new InvalidOperationException("An unsupported comparison operator was accepted.");
        }
        catch (InvalidDataException) { }

        var binary = VfxProgram.Compile(graph).ToBinary();
        Require(binary.Length >= 16 && binary.AsSpan(0, 4).SequenceEqual("LFXB"u8) &&
            binary[4] == 11,
            "Logical operations must compile to current internal bytecode v11.");
        Require(binary.AsSpan().IndexOf("LFLE"u8) >= 0,
            "Compiled logical nodes are missing their LFLE routing metadata.");
        using var renderConfig = JsonDocument.Parse(RenderProgramBindings.CreateScene(graph, 1));
        var model = renderConfig.RootElement.GetProperty("nodes").EnumerateArray().Single();
        var position = model.GetProperty("inputs").EnumerateArray().Single(input =>
            input.GetProperty("id").GetString() == "position");
        Require(position.GetProperty("sourceNodeId").GetGuid() == FixtureIds.AndVector,
            "The render binding did not retain the same-update numeric logical dependency.");
        var groupGraph = BuildGroupIsolationGraph();
        GraphValidator.Validate(groupGraph);
        var groupBinary = VfxProgram.Compile(groupGraph).ToBinary();
        Require(groupBinary[4] == 11,
            "Logical instructions inside Group instances must use the versioned logical bytecode format.");
        var groupWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        groupWorkspace.InitializeFromSnapshot(groupGraph);
        byte[] groupProjectBytes;
        using (var groupProject = new MemoryStream())
        {
            new ProjectFileService().SaveAsync(groupProject, groupWorkspace).GetAwaiter().GetResult();
            groupProjectBytes = groupProject.ToArray();
            groupProject.Position = 0;
            Require(GraphSnapshot.ContentEquals(groupGraph,
                    ProjectDocumentReader.LoadAsync(groupProject).GetAwaiter().GetResult().Graph),
                "Logical nodes or named ports inside Group snapshots changed during project persistence.");
        }
        var groupCopy = new GraphWorkspaceViewModel(new UndoRedoManager());
        groupCopy.PasteSnapshot(groupGraph, 100, 100);
        var copiedGroupGraph = groupCopy.CreateSnapshot();
        GraphValidator.Validate(copiedGroupGraph);
        _ = VfxProgram.Compile(copiedGroupGraph);
        var guardedGraph = BuildGuardedLogicalGraph();
        GraphValidator.Validate(guardedGraph);
        var guardedBinary = VfxProgram.Compile(guardedGraph).ToBinary();
        Require(guardedBinary[4] == 11 && guardedBinary.AsSpan().IndexOf("LFEB"u8) >= 0 &&
            guardedBinary.AsSpan().IndexOf("LFLE"u8) >= 0,
            "Enum-guarded logical instructions must preserve both branch and logical routing metadata.");
        var guardedWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        guardedWorkspace.InitializeFromSnapshot(guardedGraph);
        using var guardedProject = new MemoryStream();
        new ProjectFileService().SaveAsync(guardedProject, guardedWorkspace).GetAwaiter().GetResult();
        var hostGraph = BuildHostIntegrationGraph();
        GraphValidator.Validate(hostGraph);
        var hostBinary = VfxProgram.Compile(hostGraph).ToBinary();
        Require(hostBinary[4] == 11 && hostBinary.AsSpan().IndexOf("LFLE"u8) >= 0,
            "The host integration fixture must compile logical routing metadata.");
        var hostWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        hostWorkspace.InitializeFromSnapshot(hostGraph);
        using var hostProject = new MemoryStream();
        new ProjectFileService().SaveAsync(hostProject, hostWorkspace).GetAwaiter().GetResult();

        var matrixGraph = BuildMatrixDependentGraph();
        GraphValidator.Validate(matrixGraph);
        var matrixBinary = VfxProgram.Compile(matrixGraph).ToBinary();
        Require(matrixBinary[4] == 11 && matrixBinary.AsSpan().IndexOf("LFLE"u8) >= 0,
            "The Signal To Number → Transform → Decompose graph must compile to logical bytecode.");
        using var matrixScene = JsonDocument.Parse(RenderProgramBindings.CreateScene(matrixGraph, 1));
        var drawBinding = matrixScene.RootElement.GetProperty("nodes").EnumerateArray().Single()
            .GetProperty("inputs").EnumerateArray().Single(input =>
                input.GetProperty("id").GetString() == "position");
        Require(drawBinding.GetProperty("sourceNodeId").GetGuid() == FixtureIds.MatrixDecompose &&
            drawBinding.GetProperty("sourceComponent").GetInt32() == 0,
            "The compiled matrix dependency must remain connected to the renderer's position input.");
        var matrixWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        matrixWorkspace.InitializeFromSnapshot(matrixGraph);
        using var matrixProject = new MemoryStream();
        new ProjectFileService().SaveAsync(matrixProject, matrixWorkspace).GetAwaiter().GetResult();

        VerifyUnsupportedLateNumericDependencies();

        if (fixtureDirectory is null) return;
        if (!Path.IsPathFullyQualified(fixtureDirectory))
            throw new ArgumentException("Logical native fixtures require an absolute isolated directory.",
                nameof(fixtureDirectory));
        Directory.CreateDirectory(fixtureDirectory);
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "logical-runtime.lfxp"),
            SpawnableGroupPackageBinary.Serialize(new SpawnableGroupPackage(VfxProgram.Compile(graph).ToPackageBinary(), [])));
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "logical-groups.lfxp"),
            SpawnableGroupPackageBinary.Serialize(new SpawnableGroupPackage(VfxProgram.Compile(groupGraph).ToPackageBinary(), [])));
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "logical-groups.lfxproj"), groupProjectBytes);
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "logical-guards.lfxp"),
            SpawnableGroupPackageBinary.Serialize(new SpawnableGroupPackage(VfxProgram.Compile(guardedGraph).ToPackageBinary(), [])));
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "logical-guards.lfxproj"), guardedProject.ToArray());
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "logical-host.lfxp"),
            SpawnableGroupPackageBinary.Serialize(new SpawnableGroupPackage(VfxProgram.Compile(hostGraph).ToPackageBinary(), [])));
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "logical-host.lfxproj"), hostProject.ToArray());
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "logical-matrix.lfxp"),
            SpawnableGroupPackageBinary.Serialize(new SpawnableGroupPackage(VfxProgram.Compile(matrixGraph).ToPackageBinary(), [])));
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "logical-matrix.lfxproj"), matrixProject.ToArray());
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "logical-matrix-render-config.json"),
            RenderProgramBindings.CreateScene(matrixGraph, 1));
        File.WriteAllText(Path.Combine(fixtureDirectory, "logical-matrix.expected.json"),
            JsonSerializer.Serialize(new
            {
                frames = new[] { 0.0, 0.5, 2.0 }.Select((time, index) => new
                {
                    timeSeconds = time,
                    nodes = new[]
                    {
                        new
                        {
                            nodeId = FixtureIds.MatrixDraw,
                            position = new[] { index + 1.0, index + 1.0, index + 1.0 },
                            scale = new[] { 1.0, 1.0, 1.0 },
                            rotation = new[] { 0.0, 0.0, 0.0 },
                            forward = new[] { 1.0, 0.0, 0.0 },
                            up = new[] { 0.0, 0.0, 1.0 },
                            tree = false
                        }
                    }
                })
            }));
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "logical-render-config.json"),
            RenderProgramBindings.CreateScene(graph, 1));
        var fixtureWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        fixtureWorkspace.InitializeFromSnapshot(graph);
        using var project = new MemoryStream();
        new ProjectFileService().SaveAsync(project, fixtureWorkspace).GetAwaiter().GetResult();
        File.WriteAllBytes(Path.Combine(fixtureDirectory, "logical-runtime.lfxproj"), project.ToArray());
    }

    private static GraphSnapshot BuildGroupIsolationGraph()
    {
        static GraphNodeSnapshot Boundary(string kind, Guid id, string name) =>
            Node(GraphNodeDefinition.All.Single(item => item.Kind == new GraphNodeKind(kind)), id, name);

        static GraphNodeSnapshot GateGroup(Guid groupId, string name, Guid inputAId, Guid inputBId,
            Guid resetId, Guid signalOutputId, Guid floatOutputId, Guid andId)
        {
            var inputA = Boundary("GroupInputSignal", inputAId, "A");
            var inputB = Boundary("GroupInputSignal", inputBId, "B");
            var reset = Boundary("GroupInputSignal", resetId, "Reset");
            var signalOutput = Boundary("GroupOutputSignal", signalOutputId, "Then");
            var floatOutput = Boundary("GroupOutputFloat", floatOutputId, "Pulse");
            var and = SetParameter(Node(AndDefinition, andId, "Accumulate Until Both"),
                GraphLogicalSchema.ModeParameterId, 1);
            var child = new GraphSnapshot(
                [inputA, inputB, reset, signalOutput, floatOutput, and],
                [
                    new(inputAId, andId, GraphLogicalSchema.InputA),
                    new(inputBId, andId, GraphLogicalSchema.InputB),
                    new(resetId, andId, GraphLogicalSchema.ResetInput),
                    new(andId, signalOutputId, SourcePortId: GraphLogicalSchema.SignalOutput),
                    new(andId, floatOutputId, SourcePortId: GraphLogicalSchema.PulseOutput)
                ]);
            var definition = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.GraphGroup);
            return Node(definition, groupId, name) with { ChildGraph = child };
        }

        var groupOne = GateGroup(FixtureIds.GroupOne, "Gate group one",
            FixtureIds.GroupOneInputA, FixtureIds.GroupOneInputB, FixtureIds.GroupOneReset,
            FixtureIds.GroupOneSignalOutput, FixtureIds.GroupOneFloatOutput, FixtureIds.GroupOneAnd);
        var groupTwo = GateGroup(FixtureIds.GroupTwo, "Gate group two",
            FixtureIds.GroupTwoInputA, FixtureIds.GroupTwoInputB, FixtureIds.GroupTwoReset,
            FixtureIds.GroupTwoSignalOutput, FixtureIds.GroupTwoFloatOutput, FixtureIds.GroupTwoAnd);
        var counterDefinition = Node(SignalToNumberDefinition, FixtureIds.GroupOneCounter, "Group one event count");
        counterDefinition = SetParameter(counterDefinition, GraphLogicalSchema.ModeParameterId, 2);
        var secondCounter = SetParameter(
            Node(SignalToNumberDefinition, FixtureIds.GroupTwoCounter, "Group two event count"),
            GraphLogicalSchema.ModeParameterId, 2);
        var addDefinition = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.Add);
        var nodes = new[]
        {
            groupOne, groupTwo,
            Signal(FixtureIds.GroupOneSignalA, "Group one A"),
            Signal(FixtureIds.GroupOneSignalB, "Group one B"),
            Signal(FixtureIds.GroupOneResetSignal, "Group one reset"),
            Signal(FixtureIds.GroupTwoSignalA, "Group two A"),
            Signal(FixtureIds.GroupTwoSignalB, "Group two B"),
            Signal(FixtureIds.GroupTwoResetSignal, "Group two reset"),
            counterDefinition, secondCounter,
            Float(FixtureIds.GroupOneBias, "Group one bias", 10),
            Float(FixtureIds.GroupTwoBias, "Group two bias", 20),
            Node(addDefinition, FixtureIds.GroupOneAdd, "Group one pulse consumer"),
            Node(addDefinition, FixtureIds.GroupTwoAdd, "Group two pulse consumer")
        };
        var connections = new[]
        {
            new GraphConnectionSnapshot(FixtureIds.GroupOneSignalA, FixtureIds.GroupOne,
                GroupBoundarySchema.InputPortId(FixtureIds.GroupOneInputA)),
            new GraphConnectionSnapshot(FixtureIds.GroupOneSignalB, FixtureIds.GroupOne,
                GroupBoundarySchema.InputPortId(FixtureIds.GroupOneInputB)),
            new GraphConnectionSnapshot(FixtureIds.GroupOneResetSignal, FixtureIds.GroupOne,
                GroupBoundarySchema.InputPortId(FixtureIds.GroupOneReset)),
            new GraphConnectionSnapshot(FixtureIds.GroupTwoSignalA, FixtureIds.GroupTwo,
                GroupBoundarySchema.InputPortId(FixtureIds.GroupTwoInputA)),
            new GraphConnectionSnapshot(FixtureIds.GroupTwoSignalB, FixtureIds.GroupTwo,
                GroupBoundarySchema.InputPortId(FixtureIds.GroupTwoInputB)),
            new GraphConnectionSnapshot(FixtureIds.GroupTwoResetSignal, FixtureIds.GroupTwo,
                GroupBoundarySchema.InputPortId(FixtureIds.GroupTwoReset)),
            new GraphConnectionSnapshot(FixtureIds.GroupOne, FixtureIds.GroupOneCounter,
                GraphLogicalSchema.SignalInput, GroupBoundarySchema.OutputPortId(FixtureIds.GroupOneSignalOutput)),
            new GraphConnectionSnapshot(FixtureIds.GroupTwo, FixtureIds.GroupTwoCounter,
                GraphLogicalSchema.SignalInput, GroupBoundarySchema.OutputPortId(FixtureIds.GroupTwoSignalOutput)),
            new GraphConnectionSnapshot(FixtureIds.GroupOneBias, FixtureIds.GroupOneAdd, "a"),
            new GraphConnectionSnapshot(FixtureIds.GroupOne, FixtureIds.GroupOneAdd, "b",
                GroupBoundarySchema.OutputPortId(FixtureIds.GroupOneFloatOutput)),
            new GraphConnectionSnapshot(FixtureIds.GroupTwoBias, FixtureIds.GroupTwoAdd, "a"),
            new GraphConnectionSnapshot(FixtureIds.GroupTwo, FixtureIds.GroupTwoAdd, "b",
                GroupBoundarySchema.OutputPortId(FixtureIds.GroupTwoFloatOutput))
        };
        return new GraphSnapshot(nodes, connections);
    }

    private static GraphSnapshot BuildGuardedLogicalGraph()
    {
        var enumTypeId = GraphLogicalSchema.CompareModeTypeId;
        var enumType = GraphEnumBranchSchema.EnumPortType(enumTypeId);
        var enumValueKind = new GraphNodeKind(GraphEnumBranchSchema.ValueKind(enumTypeId));
        var enumSelectorKind = GraphNodeDefinition.All.Single(item =>
            GraphEnumBranchSchema.TryGetSelector(item.Kind, out var typeId, out var outputType, out _) &&
            typeId == enumTypeId && outputType == "Float").Kind;
        var enumValue = SetParameter(Node(
            GraphNodeDefinition.All.Single(item => item.Kind == enumValueKind), FixtureIds.GuardedMode, "Branch mode"),
            "value", 1);
        var enumSelect = Node(GraphNodeDefinition.All.Single(item => item.Kind == enumSelectorKind),
            FixtureIds.GuardedSelect, "Guarded logical outputs");
        var compare = Compare(FixtureIds.GuardedCompare, "Guarded compare", 0);
        var and = SetParameter(Node(AndDefinition, FixtureIds.GuardedAnd, "Guarded accumulating AND"),
            GraphLogicalSchema.ModeParameterId, 1);
        var compareCounter = SetParameter(
            Node(SignalToNumberDefinition, FixtureIds.GuardedCompareCounter, "Guarded compare count"),
            GraphLogicalSchema.ModeParameterId, 2);
        var andCounter = SetParameter(
            Node(SignalToNumberDefinition, FixtureIds.GuardedAndCounter, "Guarded AND count"),
            GraphLogicalSchema.ModeParameterId, 2);
        var inactiveCounter = SetParameter(
            Node(SignalToNumberDefinition, FixtureIds.GuardedInactiveCounter, "Guarded inactive count"),
            GraphLogicalSchema.ModeParameterId, 2);
        var addDefinition = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.Add);
        var addOne = Node(addDefinition, FixtureIds.GuardedAddOne, "Compare and pulse");
        var addTwo = Node(addDefinition, FixtureIds.GuardedAddTwo, "Compare count");
        var addThree = Node(addDefinition, FixtureIds.GuardedAddThree, "AND count");
        var nodes = new[]
        {
            enumValue, enumSelect, compare, and, compareCounter, andCounter, inactiveCounter,
            Float(FixtureIds.GuardedCompareA, "Guarded A", 1),
            Float(FixtureIds.GuardedCompareB, "Guarded B", 3),
            Signal(FixtureIds.GuardedAndA, "Guarded AND A"),
            Signal(FixtureIds.GuardedAndB, "Guarded AND B"),
            Signal(FixtureIds.GuardedAndReset, "Guarded AND reset"),
            Signal(FixtureIds.GuardedNumberSignal, "Guarded number event"),
            Signal(FixtureIds.GuardedNumberReset, "Guarded number reset"),
            addOne, addTwo, addThree
        };
        var connections = new[]
        {
            new GraphConnectionSnapshot(FixtureIds.GuardedMode, FixtureIds.GuardedSelect,
                "selector"),
            new GraphConnectionSnapshot(FixtureIds.GuardedCompareA, FixtureIds.GuardedCompare,
                GraphLogicalSchema.InputA),
            new GraphConnectionSnapshot(FixtureIds.GuardedCompareB, FixtureIds.GuardedCompare,
                GraphLogicalSchema.InputB),
            new GraphConnectionSnapshot(FixtureIds.GuardedCompare, FixtureIds.GuardedCompareCounter,
                GraphLogicalSchema.SignalInput, GraphLogicalSchema.EventOutput),
            new GraphConnectionSnapshot(FixtureIds.GuardedAndA, FixtureIds.GuardedAnd,
                GraphLogicalSchema.InputA),
            new GraphConnectionSnapshot(FixtureIds.GuardedAndB, FixtureIds.GuardedAnd,
                GraphLogicalSchema.InputB),
            new GraphConnectionSnapshot(FixtureIds.GuardedAndReset, FixtureIds.GuardedAnd,
                GraphLogicalSchema.ResetInput),
            new GraphConnectionSnapshot(FixtureIds.GuardedAnd, FixtureIds.GuardedAndCounter,
                GraphLogicalSchema.SignalInput, GraphLogicalSchema.SignalOutput),
            new GraphConnectionSnapshot(FixtureIds.GuardedNumberSignal, FixtureIds.GuardedInactiveCounter,
                GraphLogicalSchema.SignalInput),
            new GraphConnectionSnapshot(FixtureIds.GuardedNumberReset, FixtureIds.GuardedInactiveCounter,
                GraphLogicalSchema.ResetInput),
            new GraphConnectionSnapshot(FixtureIds.GuardedCompare, FixtureIds.GuardedAddOne,
                "a", GraphLogicalSchema.ConditionOutput),
            new GraphConnectionSnapshot(FixtureIds.GuardedAnd, FixtureIds.GuardedAddOne,
                "b", GraphLogicalSchema.PulseOutput),
            new GraphConnectionSnapshot(FixtureIds.GuardedAddOne, FixtureIds.GuardedAddTwo, "a"),
            new GraphConnectionSnapshot(FixtureIds.GuardedCompareCounter, FixtureIds.GuardedAddTwo,
                "b", GraphLogicalSchema.ValueOutput),
            new GraphConnectionSnapshot(FixtureIds.GuardedAddTwo, FixtureIds.GuardedAddThree, "a"),
            new GraphConnectionSnapshot(FixtureIds.GuardedAndCounter, FixtureIds.GuardedAddThree,
                "b", GraphLogicalSchema.ValueOutput),
            new GraphConnectionSnapshot(FixtureIds.GuardedAddThree, FixtureIds.GuardedSelect,
                GraphEnumBranchSchema.CasePortId(0)),
            new GraphConnectionSnapshot(FixtureIds.GuardedInactiveCounter, FixtureIds.GuardedSelect,
                GraphEnumBranchSchema.CasePortId(1), GraphLogicalSchema.ValueOutput)
        };
        for (var edgeIndex = 0; edgeIndex < connections.Length; edgeIndex++)
        {
            try { GraphValidator.Validate(new GraphSnapshot(nodes, connections.Take(edgeIndex + 1).ToArray())); }
            catch (InvalidDataException exception)
            {
                var edge = connections[edgeIndex];
                throw new InvalidDataException(
                    $"Invalid guarded fixture edge {edge.SourceNodeId}:{edge.SourcePortId} -> " +
                    $"{edge.TargetNodeId}:{edge.TargetPortId}.", exception);
            }
        }
        return new GraphSnapshot(nodes, connections);
    }

    private static GraphSnapshot BuildHostIntegrationGraph()
    {
        var startCompare = Compare(FixtureIds.HostStartCompare, "Initial true compare", 0);
        var startEachCompare = Compare(FixtureIds.HostStartEachCompare, "Initial update compare", 0, 1);
        var updateCompare = Compare(FixtureIds.HostUpdateCompare, "Updated threshold compare", 0);
        var updateEachCompare = Compare(FixtureIds.HostUpdateEachCompare, "Update pulse compare", 0, 1);
        var startAnd = Node(AndDefinition, FixtureIds.HostStartAnd, "Initial event gate");
        var updateAnd = Node(AndDefinition, FixtureIds.HostUpdateAnd, "Update event gate");
        GraphNodeSnapshot Counter(Guid id, string name) =>
            SetParameter(Node(SignalToNumberDefinition, id, name), GraphLogicalSchema.ModeParameterId, 2);
        var startAndCounter = Counter(FixtureIds.HostStartAndCounter, "Initial gate count");
        var updateAndCounter = Counter(FixtureIds.HostUpdateAndCounter, "Updated gate count");
        var manualCounter = Counter(FixtureIds.HostManualCounter, "Manual signal count");
        var inputPropertyDefinition = GraphNodeDefinition.All.Single(item =>
            item.Kind == GraphNodeKind.InputProperty);
        var inputSignalDefinition = GraphNodeDefinition.All.Single(item =>
            item.Kind == GraphNodeKind.InputSignal);
        var outputPropertyDefinition = GraphNodeDefinition.All.Single(item =>
            item.Kind == GraphNodeKind.OutputProperty);
        GraphNodeSnapshot GameIo(GraphNodeDefinition definition, Guid id, string name, int? valueType = null)
        {
            var node = Node(definition, id, name);
            node = SetParameter(node, "name", JsonSerializer.SerializeToElement(name));
            if (valueType is { } selected)
                node = SetParameter(node, "valueType", JsonSerializer.SerializeToElement(selected));
            return node;
        }
        var threshold = GameIo(inputPropertyDefinition, FixtureIds.HostThresholdInput, "Threshold", 0);
        var manualSignal = GameIo(inputSignalDefinition, FixtureIds.HostManualSignal, "Manual");
        var startCountProperty = GameIo(outputPropertyDefinition, FixtureIds.HostStartCountOutput, "StartCount", 0);
        var updateCountProperty = GameIo(outputPropertyDefinition, FixtureIds.HostUpdateCountOutput, "UpdateCount", 0);
        var manualCountProperty = GameIo(outputPropertyDefinition, FixtureIds.HostManualCountOutput, "ManualCount", 0);
        var startAddProperty = GameIo(outputPropertyDefinition, FixtureIds.HostStartAddOutput, "StartAdd", 0);
        var updateAddProperty = GameIo(outputPropertyDefinition, FixtureIds.HostUpdateAddOutput, "UpdateAdd", 0);
        var addDefinition = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.Add);
        var nodes = new[]
        {
            startCompare, startEachCompare, updateCompare, updateEachCompare, startAnd, updateAnd,
            startAndCounter, updateAndCounter, manualCounter,
            Float(FixtureIds.HostStartCompareA, "Initial A", 1),
            Float(FixtureIds.HostStartCompareB, "Initial B", 2),
            Float(FixtureIds.HostStartEachA, "Initial update A", 1),
            Float(FixtureIds.HostStartEachB, "Initial update B", 2),
            Float(FixtureIds.HostUpdateCompareA, "Update A", 1),
            threshold,
            Float(FixtureIds.HostUpdateEachA, "Update pulse A", 1),
            Float(FixtureIds.HostUpdateEachB, "Update pulse B", 2),
            Float(FixtureIds.HostUpdateBias, "Update bias", 5),
            manualSignal,
            Node(addDefinition, FixtureIds.HostStartAdd, "Manual pulse consumer"),
            Node(addDefinition, FixtureIds.HostUpdateAdd, "Late property consumer"),
            startCountProperty, updateCountProperty, manualCountProperty, startAddProperty, updateAddProperty
        };
        var connections = new[]
        {
            new GraphConnectionSnapshot(FixtureIds.HostStartCompareA, FixtureIds.HostStartCompare,
                GraphLogicalSchema.InputA),
            new GraphConnectionSnapshot(FixtureIds.HostStartCompareB, FixtureIds.HostStartCompare,
                GraphLogicalSchema.InputB),
            new GraphConnectionSnapshot(FixtureIds.HostStartEachA, FixtureIds.HostStartEachCompare,
                GraphLogicalSchema.InputA),
            new GraphConnectionSnapshot(FixtureIds.HostStartEachB, FixtureIds.HostStartEachCompare,
                GraphLogicalSchema.InputB),
            new GraphConnectionSnapshot(FixtureIds.HostUpdateCompareA, FixtureIds.HostUpdateCompare,
                GraphLogicalSchema.InputA),
            new GraphConnectionSnapshot(FixtureIds.HostThresholdInput, FixtureIds.HostUpdateCompare,
                GraphLogicalSchema.InputB, "value"),
            new GraphConnectionSnapshot(FixtureIds.HostUpdateEachA, FixtureIds.HostUpdateEachCompare,
                GraphLogicalSchema.InputA),
            new GraphConnectionSnapshot(FixtureIds.HostUpdateEachB, FixtureIds.HostUpdateEachCompare,
                GraphLogicalSchema.InputB),
            new GraphConnectionSnapshot(FixtureIds.HostStartCompare, FixtureIds.HostStartAnd,
                GraphLogicalSchema.InputA, GraphLogicalSchema.EventOutput),
            new GraphConnectionSnapshot(FixtureIds.HostStartEachCompare, FixtureIds.HostStartAnd,
                GraphLogicalSchema.InputB, GraphLogicalSchema.EventOutput),
            new GraphConnectionSnapshot(FixtureIds.HostStartAnd, FixtureIds.HostStartAndCounter,
                GraphLogicalSchema.SignalInput, GraphLogicalSchema.SignalOutput),
            new GraphConnectionSnapshot(FixtureIds.HostUpdateCompare, FixtureIds.HostUpdateAnd,
                GraphLogicalSchema.InputA, GraphLogicalSchema.EventOutput),
            new GraphConnectionSnapshot(FixtureIds.HostUpdateEachCompare, FixtureIds.HostUpdateAnd,
                GraphLogicalSchema.InputB, GraphLogicalSchema.EventOutput),
            new GraphConnectionSnapshot(FixtureIds.HostUpdateAnd, FixtureIds.HostUpdateAndCounter,
                GraphLogicalSchema.SignalInput, GraphLogicalSchema.SignalOutput),
            new GraphConnectionSnapshot(FixtureIds.HostManualSignal, FixtureIds.HostManualCounter,
                GraphLogicalSchema.SignalInput, "then"),
            new GraphConnectionSnapshot(FixtureIds.HostStartAnd, FixtureIds.HostStartAdd,
                "a", GraphLogicalSchema.PulseOutput),
            new GraphConnectionSnapshot(FixtureIds.HostManualCounter, FixtureIds.HostStartAdd,
                "b", GraphLogicalSchema.ValueOutput),
            new GraphConnectionSnapshot(FixtureIds.HostUpdateAnd, FixtureIds.HostUpdateAdd,
                "a", GraphLogicalSchema.PulseOutput),
            new GraphConnectionSnapshot(FixtureIds.HostUpdateBias, FixtureIds.HostUpdateAdd, "b"),
            new GraphConnectionSnapshot(FixtureIds.HostStartAndCounter, FixtureIds.HostStartCountOutput,
                "value", GraphLogicalSchema.ValueOutput),
            new GraphConnectionSnapshot(FixtureIds.HostUpdateAndCounter, FixtureIds.HostUpdateCountOutput,
                "value", GraphLogicalSchema.ValueOutput),
            new GraphConnectionSnapshot(FixtureIds.HostManualCounter, FixtureIds.HostManualCountOutput,
                "value", GraphLogicalSchema.ValueOutput),
            new GraphConnectionSnapshot(FixtureIds.HostStartAdd, FixtureIds.HostStartAddOutput, "value"),
            new GraphConnectionSnapshot(FixtureIds.HostUpdateAdd, FixtureIds.HostUpdateAddOutput, "value")
        };
        for (var edgeIndex = 0; edgeIndex < connections.Length; edgeIndex++)
        {
            try { GraphValidator.Validate(new GraphSnapshot(nodes, connections.Take(edgeIndex + 1).ToArray())); }
            catch (InvalidDataException exception)
            {
                var edge = connections[edgeIndex];
                throw new InvalidDataException(
                    $"Invalid host fixture edge {edge.SourceNodeId}:{edge.SourcePortId} -> " +
                    $"{edge.TargetNodeId}:{edge.TargetPortId}.", exception);
            }
        }
        return new GraphSnapshot(nodes, connections);
    }

    private static GraphSnapshot BuildMatrixDependentGraph()
    {
        var compare = Compare(FixtureIds.MatrixCompare, "Repeat numeric pulse", 0, 1);
        var counter = SetParameter(Node(SignalToNumberDefinition, FixtureIds.MatrixCounter,
            "Signal-derived position"), GraphLogicalSchema.ModeParameterId, 2);
        var scalarToVector = Node(GraphNodeDefinition.All.Single(item =>
            item.Kind == new GraphNodeKind("ScalarToVector3")), FixtureIds.MatrixVector, "Position vector");
        var transform = Node(GraphNodeDefinition.All.Single(item =>
            item.Kind == new GraphNodeKind("Transform")), FixtureIds.MatrixTransform, "Signal-driven transform");
        var decompose = Node(GraphNodeDefinition.All.Single(item =>
            item.Kind == new GraphNodeKind("DecomposeTransform")), FixtureIds.MatrixDecompose, "Renderer position");
        var drawDefinition = GraphNodeDefinition.All.Single(item => item.TypeName == "ModelDraw");
        var draw = SetParameter(Node(drawDefinition, FixtureIds.MatrixDraw, "Signal-driven renderer"),
            "assetPath", JsonSerializer.SerializeToElement(SolarSystemSample.CubeAsset));
        var nodes = new[]
        {
            compare, counter, scalarToVector, transform, decompose, draw,
            Float(FixtureIds.MatrixCompareA, "Condition A", 1),
            Float(FixtureIds.MatrixCompareB, "Condition B", 2)
        };
        var connections = new[]
        {
            new GraphConnectionSnapshot(FixtureIds.MatrixCompareA, FixtureIds.MatrixCompare,
                GraphLogicalSchema.InputA),
            new GraphConnectionSnapshot(FixtureIds.MatrixCompareB, FixtureIds.MatrixCompare,
                GraphLogicalSchema.InputB),
            new GraphConnectionSnapshot(FixtureIds.MatrixCompare, FixtureIds.MatrixCounter,
                GraphLogicalSchema.SignalInput, GraphLogicalSchema.EventOutput),
            new GraphConnectionSnapshot(FixtureIds.MatrixCounter, FixtureIds.MatrixVector,
                "a", GraphLogicalSchema.ValueOutput),
            new GraphConnectionSnapshot(FixtureIds.MatrixVector, FixtureIds.MatrixTransform, "position"),
            new GraphConnectionSnapshot(FixtureIds.MatrixTransform, FixtureIds.MatrixDecompose,
                "matrix", "value"),
            new GraphConnectionSnapshot(FixtureIds.MatrixDecompose, FixtureIds.MatrixDraw,
                "position", "position")
        };
        return new GraphSnapshot(nodes, connections);
    }

    private static void VerifyUnsupportedLateNumericDependencies()
    {
        var counter = SetParameter(Node(SignalToNumberDefinition, FixtureIds.RejectCounter,
            "Unsupported late counter"), GraphLogicalSchema.ModeParameterId, 2);
        var compare = Compare(FixtureIds.RejectCompare, "Late compare", 0);
        var compareGraph = new GraphSnapshot(
            [counter, compare, Float(FixtureIds.RejectCompareB, "Compare B", 1)],
            [
                new GraphConnectionSnapshot(FixtureIds.RejectCounter, FixtureIds.RejectCompare,
                    GraphLogicalSchema.InputA, GraphLogicalSchema.ValueOutput),
                new GraphConnectionSnapshot(FixtureIds.RejectCompareB, FixtureIds.RejectCompare,
                    GraphLogicalSchema.InputB)
            ]);
        GraphValidator.Validate(compareGraph);
        RequireLateDependencyRejected(compareGraph,
            "A Signal To Number → Compare dependency must fail instead of evaluating a stale condition or event.");

        var definitionDirectory = Path.Combine(Path.GetTempPath(),
            $"LiberaFxLogicalCustom-{Guid.NewGuid():N}");
        Directory.CreateDirectory(definitionDirectory);
        try
        {
            const string typeId = "099f5e72-978a-4a14-9e03-350000000001";
            var definitionPath = Path.Combine(definitionDirectory, "UnrefreshableSignalCustom.json");
            File.WriteAllText(definitionPath, $$"""
                {
                  "schemaVersion": 1,
                  "typeId": "{{typeId}}",
                  "namespace": "LogicalFixture",
                  "kind": "UnrefreshableSignalCustom",
                  "name": "Unrefreshable Signal Custom",
                  "group": "Fixture",
                  "portType": "Float",
                  "portCategory": "Parameter",
                  "runtime": { "kind": "custom", "abiVersion": 1, "execution": "parameter" },
                  "inputPorts": [{ "id": "a", "name": "A", "portType": "Float", "category": "Parameter" }],
                  "outputPorts": [{ "id": "value", "name": "Value", "portType": "Float", "category": "Parameter" }],
                  "parameters": [{ "id": "a", "name": "A", "type": "float", "defaultValue": 0.0 }]
                }
                """);
            var loaded = GraphNodeDefinition.LoadDirectory(definitionDirectory, out var warnings);
            Require(warnings.Count == 0,
                $"The synthetic unsupported custom definition must load cleanly: {string.Join("; ", warnings)}");
            var customDefinition = loaded.Single(item => item.TypeId == Guid.Parse(typeId));
            var custom = Node(customDefinition, FixtureIds.RejectCustom, "Unrefreshable custom parameter");
            var customGraph = new GraphSnapshot(
                [counter, custom],
                [new GraphConnectionSnapshot(FixtureIds.RejectCounter, FixtureIds.RejectCustom,
                    "a", GraphLogicalSchema.ValueOutput)]);
            GraphValidator.Validate(customGraph);
            RequireLateDependencyRejected(customGraph,
                "An arbitrary custom parameter consuming a Signal-derived value must be rejected explicitly.");
        }
        finally
        {
            Directory.Delete(definitionDirectory, recursive: true);
        }
    }

    private static void RequireLateDependencyRejected(GraphSnapshot graph, string message)
    {
        try
        {
            _ = VfxProgram.Compile(graph);
        }
        catch (InvalidDataException exception) when (
            exception.Message.Contains("same-update Signal-derived value", StringComparison.Ordinal) &&
            exception.Message.Contains("late pure refresh", StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static GraphSnapshot BuildRuntimeGraph()
    {
        var nodes = new List<GraphNodeSnapshot>
        {
            Node(CompareDefinition, FixtureIds.Compare, "Less than, initial true"),
            Float(FixtureIds.CompareA, "A", 1),
            Float(FixtureIds.CompareB, "B", 2),
            SetParameter(Node(SignalToNumberDefinition, FixtureIds.CompareCounter, "Compare event count"),
                GraphLogicalSchema.ModeParameterId, 2),
            Node(AndDefinition, FixtureIds.SignalAnd, "Same update"),
            Signal(FixtureIds.AndA, "AND A"),
            Signal(FixtureIds.AndB, "AND B"),
            Signal(FixtureIds.AndReset, "AND Reset"),
            SetParameter(Node(SignalToNumberDefinition, FixtureIds.AndCounter, "AND emitted count"),
                GraphLogicalSchema.ModeParameterId, 2),
            Node(GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.Add),
                FixtureIds.AndAdd, "Pulse consumer"),
            Float(FixtureIds.AndBias, "Pulse bias", 5),
            Node(GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.ScalarToVector3),
                FixtureIds.AndVector, "Render pulse"),
            Node(GraphNodeDefinition.All.Single(item => item.TypeName == "ModelDraw"),
                FixtureIds.ModelDraw, "Pulse render binding"),
            Node(OrDefinition, FixtureIds.SignalOr, "Same update OR"),
            Signal(FixtureIds.OrA, "OR A"),
            Signal(FixtureIds.OrB, "OR B"),
            Signal(FixtureIds.OrReset, "OR Reset"),
            SetParameter(Node(SignalToNumberDefinition, FixtureIds.OrCounter, "OR emitted count"),
                GraphLogicalSchema.ModeParameterId, 2),
            SetParameter(Node(AndDefinition, FixtureIds.AccumulatingAnd, "Accumulate until both"),
                GraphLogicalSchema.ModeParameterId, 1),
            Signal(FixtureIds.AccumulatingA, "Accumulating A"),
            Signal(FixtureIds.AccumulatingB, "Accumulating B"),
            Signal(FixtureIds.AccumulatingReset, "Accumulating Reset"),
            Compare(FixtureIds.CompareEach, "True each update", 5, 1),
            Float(FixtureIds.CompareEachA, "Each A", 3),
            Float(FixtureIds.CompareEachB, "Each B", 2),
            SetParameter(Node(SignalToNumberDefinition, FixtureIds.CompareEachCounter, "Each update count"),
                GraphLogicalSchema.ModeParameterId, 2),
            Compare(FixtureIds.CompareLessEqual, "Less or equal", 1),
            Float(FixtureIds.CompareLessEqualA, "Less equal A", 2),
            Float(FixtureIds.CompareLessEqualB, "Less equal B", 2),
            Compare(FixtureIds.CompareEqual, "Equal", 2),
            Float(FixtureIds.CompareEqualA, "Equal A", 2),
            Float(FixtureIds.CompareEqualB, "Equal B", 2),
            Compare(FixtureIds.CompareNotEqual, "Not equal", 3),
            Float(FixtureIds.CompareNotEqualA, "Not equal A", 1),
            Float(FixtureIds.CompareNotEqualB, "Not equal B", 2),
            Compare(FixtureIds.CompareGreaterEqual, "Greater or equal", 4),
            Float(FixtureIds.CompareGreaterEqualA, "Greater equal A", 2),
            Float(FixtureIds.CompareGreaterEqualB, "Greater equal B", 2),
            Compare(FixtureIds.CompareGreater, "Greater", 5),
            Float(FixtureIds.CompareGreaterA, "Greater A", 3),
            Float(FixtureIds.CompareGreaterB, "Greater B", 2),
            SetParameter(Node(SignalToNumberDefinition, FixtureIds.AccumulatingCounter, "Accumulated AND count"),
                GraphLogicalSchema.ModeParameterId, 2),
            SetParameter(Node(SignalToNumberDefinition, FixtureIds.Pulse, "Pulse mode"),
                GraphLogicalSchema.ModeParameterId, 0),
            Signal(FixtureIds.PulseSignal, "Pulse input"),
            Signal(FixtureIds.PulseReset, "Pulse reset"),
            SetParameter(Node(SignalToNumberDefinition, FixtureIds.Latch, "Latch mode"),
                GraphLogicalSchema.ModeParameterId, 1),
            Signal(FixtureIds.LatchSignal, "Latch input"),
            Signal(FixtureIds.LatchReset, "Latch reset"),
            SetParameter(Node(SignalToNumberDefinition, FixtureIds.Counter, "Counter mode"),
                GraphLogicalSchema.ModeParameterId, 2),
            Signal(FixtureIds.CounterSignal, "Counter input"),
            Signal(FixtureIds.CounterReset, "Counter reset")
        };
        nodes = nodes.GroupBy(node => node.Id).Select(group => group.First()).ToList();

        var connections = new List<GraphConnectionSnapshot>
        {
            new(FixtureIds.CompareA, FixtureIds.Compare, GraphLogicalSchema.InputA),
            new(FixtureIds.CompareB, FixtureIds.Compare, GraphLogicalSchema.InputB),
            new(FixtureIds.Compare, FixtureIds.CompareCounter, GraphLogicalSchema.SignalInput,
                GraphLogicalSchema.EventOutput),
            new(FixtureIds.CompareEachA, FixtureIds.CompareEach, GraphLogicalSchema.InputA),
            new(FixtureIds.CompareEachB, FixtureIds.CompareEach, GraphLogicalSchema.InputB),
            new(FixtureIds.CompareEach, FixtureIds.CompareEachCounter, GraphLogicalSchema.SignalInput,
                GraphLogicalSchema.EventOutput),
            new(FixtureIds.CompareLessEqualA, FixtureIds.CompareLessEqual, GraphLogicalSchema.InputA),
            new(FixtureIds.CompareLessEqualB, FixtureIds.CompareLessEqual, GraphLogicalSchema.InputB),
            new(FixtureIds.CompareEqualA, FixtureIds.CompareEqual, GraphLogicalSchema.InputA),
            new(FixtureIds.CompareEqualB, FixtureIds.CompareEqual, GraphLogicalSchema.InputB),
            new(FixtureIds.CompareNotEqualA, FixtureIds.CompareNotEqual, GraphLogicalSchema.InputA),
            new(FixtureIds.CompareNotEqualB, FixtureIds.CompareNotEqual, GraphLogicalSchema.InputB),
            new(FixtureIds.CompareGreaterEqualA, FixtureIds.CompareGreaterEqual, GraphLogicalSchema.InputA),
            new(FixtureIds.CompareGreaterEqualB, FixtureIds.CompareGreaterEqual, GraphLogicalSchema.InputB),
            new(FixtureIds.CompareGreaterA, FixtureIds.CompareGreater, GraphLogicalSchema.InputA),
            new(FixtureIds.CompareGreaterB, FixtureIds.CompareGreater, GraphLogicalSchema.InputB),
            new(FixtureIds.AndA, FixtureIds.SignalAnd, GraphLogicalSchema.InputA),
            new(FixtureIds.AndB, FixtureIds.SignalAnd, GraphLogicalSchema.InputB),
            new(FixtureIds.AndReset, FixtureIds.SignalAnd, GraphLogicalSchema.ResetInput),
            new(FixtureIds.SignalAnd, FixtureIds.AndCounter, GraphLogicalSchema.SignalInput,
                GraphLogicalSchema.SignalOutput),
            new(FixtureIds.SignalAnd, FixtureIds.AndAdd, "a", GraphLogicalSchema.PulseOutput),
            new(FixtureIds.AndBias, FixtureIds.AndAdd, "b"),
            new(FixtureIds.SignalAnd, FixtureIds.AndVector, "a", GraphLogicalSchema.PulseOutput),
            new(FixtureIds.AndVector, FixtureIds.ModelDraw, "position"),
            new(FixtureIds.OrA, FixtureIds.SignalOr, GraphLogicalSchema.InputA),
            new(FixtureIds.OrB, FixtureIds.SignalOr, GraphLogicalSchema.InputB),
            new(FixtureIds.OrReset, FixtureIds.SignalOr, GraphLogicalSchema.ResetInput),
            new(FixtureIds.SignalOr, FixtureIds.OrCounter, GraphLogicalSchema.SignalInput,
                GraphLogicalSchema.SignalOutput),
            new(FixtureIds.AccumulatingA, FixtureIds.AccumulatingAnd, GraphLogicalSchema.InputA),
            new(FixtureIds.AccumulatingB, FixtureIds.AccumulatingAnd, GraphLogicalSchema.InputB),
            new(FixtureIds.AccumulatingReset, FixtureIds.AccumulatingAnd, GraphLogicalSchema.ResetInput),
            new(FixtureIds.AccumulatingAnd, FixtureIds.AccumulatingCounter, GraphLogicalSchema.SignalInput,
                GraphLogicalSchema.SignalOutput),
            new(FixtureIds.PulseSignal, FixtureIds.Pulse, GraphLogicalSchema.SignalInput),
            new(FixtureIds.PulseReset, FixtureIds.Pulse, GraphLogicalSchema.ResetInput),
            new(FixtureIds.LatchSignal, FixtureIds.Latch, GraphLogicalSchema.SignalInput),
            new(FixtureIds.LatchReset, FixtureIds.Latch, GraphLogicalSchema.ResetInput),
            new(FixtureIds.CounterSignal, FixtureIds.Counter, GraphLogicalSchema.SignalInput),
            new(FixtureIds.CounterReset, FixtureIds.Counter, GraphLogicalSchema.ResetInput)
        };
        var model = nodes.Single(item => item.Id == FixtureIds.ModelDraw);
        nodes[nodes.IndexOf(model)] = SetParameter(model, "assetPath",
            JsonSerializer.SerializeToElement("/Engine/BasicShapes/Cube.Cube"));
        return new GraphSnapshot(nodes, connections);
    }

    private static GraphNodeSnapshot Node(GraphNodeDefinition definition, Guid id, string name) =>
        new(id, definition.Kind, name, 0, 0, definition.PortType, definition.PortCategory,
            Parameters: definition.Parameters.Select(parameter =>
                new NodeParameterSnapshot(parameter.Id, parameter.Type, parameter.DefaultValue.Clone())).ToArray());

    private static GraphNodeSnapshot Float(Guid id, string name, double value)
    {
        var definition = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.Float);
        return SetParameter(Node(definition, id, name), "value", JsonSerializer.SerializeToElement(value));
    }

    private static GraphNodeSnapshot Compare(Guid id, string name, int operation, int mode = 0)
    {
        var node = Node(CompareDefinition, id, name);
        node = SetParameter(node, GraphLogicalSchema.OperationParameterId, operation);
        node = SetParameter(node, GraphLogicalSchema.ModeParameterId, mode);
        return node;
    }

    private static GraphNodeSnapshot Signal(Guid id, string name) =>
        Node(GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.Signal), id, name);

    private static GraphNodeSnapshot SetParameter(GraphNodeSnapshot node, string id, int value) =>
        SetParameter(node, id, JsonSerializer.SerializeToElement(value));

    private static GraphNodeSnapshot SetParameter(GraphNodeSnapshot node, string id, JsonElement value)
    {
        var parameters = node.Parameters?.ToArray() ?? [];
        var index = Array.FindIndex(parameters, parameter => parameter.Id == id);
        if (index < 0) throw new InvalidOperationException($"Node {node.Name} has no parameter '{id}'.");
        parameters[index] = parameters[index] with { Value = value };
        return node with { Parameters = parameters };
    }

    private static GraphSnapshot ReplaceParameter(GraphSnapshot graph, Guid id, string parameter, JsonElement value) =>
        graph with { Nodes = graph.Nodes.Select(node => node.Id == id
            ? SetParameter(node, parameter, value) : node).ToArray() };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static class FixtureIds
    {
        private static Guid Create(int value) => Guid.Parse(
            $"35100000-0000-4000-8000-{value.ToString("x12", System.Globalization.CultureInfo.InvariantCulture)}");

        public static readonly Guid Compare = Create(1);
        public static readonly Guid CompareCounter = Create(2);
        public static readonly Guid SignalAnd = Create(3);
        public static readonly Guid AndCounter = Create(4);
        public static readonly Guid AndA = Create(5);
        public static readonly Guid AndB = Create(6);
        public static readonly Guid AndReset = Create(7);
        public static readonly Guid AndAdd = Create(8);
        public static readonly Guid AndVector = Create(9);
        public static readonly Guid CompareA = Create(10);
        public static readonly Guid CompareB = Create(11);
        public static readonly Guid AndBias = Create(12);
        public static readonly Guid ModelDraw = Create(13);
        public static readonly Guid SignalOr = Create(14);
        public static readonly Guid OrA = Create(15);
        public static readonly Guid OrB = Create(16);
        public static readonly Guid OrReset = Create(17);
        public static readonly Guid OrCounter = Create(18);
        public static readonly Guid AccumulatingAnd = Create(19);
        public static readonly Guid AccumulatingA = Create(20);
        public static readonly Guid AccumulatingB = Create(21);
        public static readonly Guid AccumulatingReset = Create(22);
        public static readonly Guid AccumulatingCounter = Create(23);
        public static readonly Guid Pulse = Create(24);
        public static readonly Guid PulseSignal = Create(25);
        public static readonly Guid PulseReset = Create(26);
        public static readonly Guid Latch = Create(27);
        public static readonly Guid LatchSignal = Create(28);
        public static readonly Guid LatchReset = Create(29);
        public static readonly Guid Counter = Create(30);
        public static readonly Guid CounterSignal = Create(31);
        public static readonly Guid CounterReset = Create(32);
        public static readonly Guid CompareEach = Create(33);
        public static readonly Guid CompareEachA = Create(34);
        public static readonly Guid CompareEachB = Create(35);
        public static readonly Guid CompareEachCounter = Create(36);
        public static readonly Guid CompareLessEqual = Create(37);
        public static readonly Guid CompareEqual = Create(38);
        public static readonly Guid CompareNotEqual = Create(39);
        public static readonly Guid CompareGreaterEqual = Create(40);
        public static readonly Guid CompareGreater = Create(41);
        public static readonly Guid CompareLessEqualA = Create(42);
        public static readonly Guid CompareLessEqualB = Create(43);
        public static readonly Guid CompareEqualA = Create(44);
        public static readonly Guid CompareEqualB = Create(45);
        public static readonly Guid CompareNotEqualA = Create(46);
        public static readonly Guid CompareNotEqualB = Create(47);
        public static readonly Guid CompareGreaterEqualA = Create(48);
        public static readonly Guid CompareGreaterEqualB = Create(49);
        public static readonly Guid CompareGreaterA = Create(50);
        public static readonly Guid CompareGreaterB = Create(51);
        public static readonly Guid GroupOne = Create(60);
        public static readonly Guid GroupOneInputA = Create(61);
        public static readonly Guid GroupOneInputB = Create(62);
        public static readonly Guid GroupOneReset = Create(63);
        public static readonly Guid GroupOneSignalOutput = Create(64);
        public static readonly Guid GroupOneFloatOutput = Create(65);
        public static readonly Guid GroupOneAnd = Create(66);
        public static readonly Guid GroupTwo = Create(70);
        public static readonly Guid GroupTwoInputA = Create(71);
        public static readonly Guid GroupTwoInputB = Create(72);
        public static readonly Guid GroupTwoReset = Create(73);
        public static readonly Guid GroupTwoSignalOutput = Create(74);
        public static readonly Guid GroupTwoFloatOutput = Create(75);
        public static readonly Guid GroupTwoAnd = Create(76);
        public static readonly Guid GroupOneSignalA = Create(80);
        public static readonly Guid GroupOneSignalB = Create(81);
        public static readonly Guid GroupOneResetSignal = Create(82);
        public static readonly Guid GroupTwoSignalA = Create(83);
        public static readonly Guid GroupTwoSignalB = Create(84);
        public static readonly Guid GroupTwoResetSignal = Create(85);
        public static readonly Guid GroupOneCounter = Create(90);
        public static readonly Guid GroupTwoCounter = Create(91);
        public static readonly Guid GroupOneBias = Create(92);
        public static readonly Guid GroupOneAdd = Create(93);
        public static readonly Guid GroupTwoBias = Create(94);
        public static readonly Guid GroupTwoAdd = Create(95);
        public static readonly Guid GuardedMode = Create(100);
        public static readonly Guid GuardedSelect = Create(101);
        public static readonly Guid GuardedCompare = Create(102);
        public static readonly Guid GuardedCompareA = Create(103);
        public static readonly Guid GuardedCompareB = Create(104);
        public static readonly Guid GuardedCompareCounter = Create(105);
        public static readonly Guid GuardedAnd = Create(106);
        public static readonly Guid GuardedAndA = Create(107);
        public static readonly Guid GuardedAndB = Create(108);
        public static readonly Guid GuardedAndReset = Create(109);
        public static readonly Guid GuardedAndCounter = Create(110);
        public static readonly Guid GuardedInactiveCounter = Create(111);
        public static readonly Guid GuardedNumberSignal = Create(112);
        public static readonly Guid GuardedNumberReset = Create(113);
        public static readonly Guid GuardedAddOne = Create(114);
        public static readonly Guid GuardedAddTwo = Create(115);
        public static readonly Guid GuardedAddThree = Create(116);
        public static readonly Guid HostStartCompare = Create(200);
        public static readonly Guid HostStartCompareA = Create(201);
        public static readonly Guid HostStartCompareB = Create(202);
        public static readonly Guid HostStartEachCompare = Create(203);
        public static readonly Guid HostStartEachA = Create(204);
        public static readonly Guid HostStartEachB = Create(205);
        public static readonly Guid HostStartAnd = Create(206);
        public static readonly Guid HostStartAndCounter = Create(207);
        public static readonly Guid HostUpdateCompare = Create(208);
        public static readonly Guid HostUpdateCompareA = Create(209);
        public static readonly Guid HostThresholdInput = Create(210);
        public static readonly Guid HostUpdateEachCompare = Create(211);
        public static readonly Guid HostUpdateEachA = Create(212);
        public static readonly Guid HostUpdateEachB = Create(213);
        public static readonly Guid HostUpdateAnd = Create(214);
        public static readonly Guid HostUpdateAndCounter = Create(215);
        public static readonly Guid HostManualCounter = Create(216);
        public static readonly Guid HostManualSignal = Create(217);
        public static readonly Guid HostUpdateBias = Create(218);
        public static readonly Guid HostStartAdd = Create(219);
        public static readonly Guid HostUpdateAdd = Create(220);
        public static readonly Guid HostStartCountOutput = Create(221);
        public static readonly Guid HostUpdateCountOutput = Create(222);
        public static readonly Guid HostManualCountOutput = Create(223);
        public static readonly Guid HostStartAddOutput = Create(224);
        public static readonly Guid HostUpdateAddOutput = Create(225);
        public static readonly Guid MatrixCompare = Create(230);
        public static readonly Guid MatrixCompareA = Create(231);
        public static readonly Guid MatrixCompareB = Create(232);
        public static readonly Guid MatrixCounter = Create(233);
        public static readonly Guid MatrixVector = Create(234);
        public static readonly Guid MatrixTransform = Create(235);
        public static readonly Guid MatrixDecompose = Create(236);
        public static readonly Guid MatrixDraw = Create(237);
        public static readonly Guid RejectCounter = Create(240);
        public static readonly Guid RejectCompare = Create(241);
        public static readonly Guid RejectCompareB = Create(242);
        public static readonly Guid RejectCustom = Create(243);
    }
}
