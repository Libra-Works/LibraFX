using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Threading;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Mcp;
using LiberaFx.Services.Preview;
using LiberaFx.Services.Project;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;
using LiberaFx.Services.Compilation;

namespace LiberaFx.Tests;

public static class PolymorphicMathTests
{
    public static void Run()
    {
        VerifyCatalog();
        VerifyInferenceAndPersistence();
        VerifyFanoutAndPlan();
        VerifyMcp();
        VerifyUnsupportedType();
        VerifyNativeBroadcast();
        Console.WriteLine("Polymorphic palette, inference, persistence, MCP and native broadcast checks passed.");
    }

    private static void VerifyMcp()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var provider = new ActiveGraphProvider(graph);
        Complete(LiberaFxMcpTools.CreateNode(provider, "Multiply"));
        Complete(LiberaFxMcpTools.CreateNode(provider, "Multiply", isPolymorphic: false));
        Require(graph.Nodes.Count(node => node.IsPolymorphic) == 1 &&
                graph.Nodes.Count(node => node.Definition.Kind == GraphNodeKind.Multiply) == 2,
            "MCP must default family nodes to generic but permit explicit legacy creation.");
        Complete(LiberaFxMcpTools.ApplyGraphPlan(provider,
        [
            new("v", "FloatX3", "mcp vector", 0, 0),
            new("s", "Float", "mcp scalar", 0, 0),
            new("op", "Multiply", "mcp operator", 0, 0)
        ],
        [
            new("v", "op", "a"),
            new("s", "op", "b")
        ]));
        var op = graph.Nodes.Single(node => node.Name == "mcp operator");
        Require(op.IsPolymorphic && op.PortType == "FloatX3" && !op.HasDiagnostics,
            "MCP plans must use the same inferred and broadcast rules.");
        Complete(LiberaFxMcpTools.SetParameter(provider, op.Id.ToString("D"), "b",
            JsonSerializer.SerializeToElement(4.0)));
        Require(op.PortType == "FloatX3" && !op.HasDiagnostics && graph.Connections.Count == 2,
            "MCP parameter edits must preserve inferred types and connections.");
    }

    private static T Complete<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var registration = timeout.Token.Register(() => Dispatcher.UIThread.Post(() => frame.Continue = false));
            _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
        }
        if (!task.IsCompleted) throw new TimeoutException("MCP graph operation timed out.");
        return task.GetAwaiter().GetResult();
    }

    private static void VerifyCatalog()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var catalog = new NodeCatalogViewModel(new ActiveGraphProvider(graph));
        var entries = catalog.Groups.SelectMany(group => group.Nodes).ToArray();
        foreach (var family in GraphPolymorphism.Families)
        {
            Require(entries.Count(entry => entry.KindName == family.BaseKind.Value && entry.IsPolymorphic) == 1,
                $"{family.BaseKind} must have one generic palette entry.");
            foreach (var variant in family.Variants.Values.Where(item => item.Kind != family.BaseKind))
                Require(entries.All(entry => entry.KindName != variant.Kind.Value), "Variants must not clutter the palette.");
        }
        Require(entries.Any(entry => entry.KindName == "BreakVector3"), "Dimension-specific constructors remain visible.");
    }

    private static void VerifyInferenceAndPersistence()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var scalar = graph.CreateNode(GraphNodeKind.Float);
        var vec2 = graph.CreateNode(GraphNodeKind.FloatX2);
        var vec3 = graph.CreateNode(GraphNodeKind.FloatX3);
        var op = graph.CreateNode(GraphNodeKind.Multiply, isPolymorphic: true);
        var downstream = graph.CreateNode(GraphNodeKind.AddVector2);
        var untouched = graph.CreateNode(GraphNodeKind.Time);
        Require(graph.TryConnectNodes(scalar, op, out _, "a"), "Scalar must connect first.");
        Require(graph.TryConnectNodes(op, downstream, out _, "a"), "A generic output may connect before inference.");
        Require(downstream.HasDiagnostics, "A scalar output must diagnose an incompatible vector consumer.");
        Require(graph.TryConnectNodes(vec2, op, out _, "b"), "Vector must promote the generic operation.");
        Require(op.PortType == "FloatX2" && !op.HasDiagnostics && !downstream.HasDiagnostics &&
                graph.Connections.Count == 3 && !untouched.HasDiagnostics, "Promotion must retain and revalidate all wires.");
        var originalId = op.Id;
        var encoded = JsonSerializer.Serialize(graph.CreateSnapshot());
        var decoded = JsonSerializer.Deserialize<GraphSnapshot>(encoded)!;
        GraphValidator.Validate(decoded);
        var restored = new GraphWorkspaceViewModel(new UndoRedoManager());
        restored.InitializeFromSnapshot(decoded);
        Require(restored.GetNode(originalId).IsPolymorphic && restored.GetNode(originalId).PortType == "FloatX2" &&
                restored.Connections.Count == 3, "Generic marker, GUID and connections must survive serialization.");
        var edge = graph.Connections.Single(item => item.Target == op && item.TargetPortId == "b");
        graph.DisconnectConnection(edge);
        Require(op.PortType == "Float" && downstream.HasDiagnostics, "Unplugging must infer from remaining sources.");
        Require(graph.History.Undo() && graph.GetNode(originalId).PortType == "FloatX2",
            "Undo must restore the inferred domain.");
        op = graph.GetNode(originalId);
        downstream = graph.GetNode(downstream.Id);
        graph.DisconnectConnection(graph.Connections.Single(item => item.Target == op && item.TargetPortId == "a"));
        Require(graph.TryConnectNodes(vec3 = graph.GetNode(vec3.Id), op, out _, "a"), "Incompatible wire must remain editable.");
        Require(op.PortType == "FloatX2" && op.HasDiagnostics && graph.Connections.Count == 3,
            "Different vector dimensions must be diagnosed without deleting the wire.");
        var invalid = graph.CreateSnapshot();
        GraphValidator.Validate(invalid);
        Require(GraphTypeDiagnostics.Analyze(invalid).Count == 1, "The incompatible input must be identified.");
        using (var stream = new MemoryStream())
        {
            new ProjectFileService().SaveAsync(stream, graph).GetAwaiter().GetResult();
            stream.Position = 0;
            var loaded = ProjectDocumentReader.LoadAsync(stream).GetAwaiter().GetResult();
            var invalidRestored = new GraphWorkspaceViewModel(new UndoRedoManager());
            invalidRestored.InitializeFromSnapshot(loaded.Graph);
            Require(invalidRestored.GetNode(originalId).HasDiagnostics &&
                    invalidRestored.Connections.Count == 3, "Invalid editing state must save and reload intact.");
            invalidRestored.History.LoadSnapshot(loaded.History!);
            Require(invalidRestored.History.Undo() && !invalidRestored.GetNode(originalId).HasDiagnostics &&
                    invalidRestored.History.Redo() && invalidRestored.GetNode(originalId).HasDiagnostics,
                "Saved invalid history must support undo and redo.");
        }
        var library = new GroupAssetLibrary(Path.Combine(AppDataDirectory.Root, "poly-group-tests"));
        var group = library.Create("Invalid generic graph", invalid);
        Require(GraphSnapshot.ContentEquals(library.Load(group.Id).Graph, invalid),
            "Group assets must retain generic markers and incompatible wires.");
        try { VfxProgram.Compile(invalid); throw new InvalidOperationException("Invalid graph compiled."); }
        catch (InvalidDataException ex) { Require(ex.Message.Contains(originalId.ToString()), "Error must identify its node."); }
        var localization = LocalizationService.Shared;
        var previousLanguage = localization.SelectedLanguage;
        try
        {
            localization.SelectedLanguage = localization.Languages.Single(language => language.Id == "ja");
            try { VfxProgram.Compile(invalid); throw new InvalidOperationException("Invalid graph compiled."); }
            catch (InvalidDataException ex)
            {
                Require(ex.Message.Contains("ノード") && ex.Message.Contains(originalId.ToString()),
                    "Compilation errors must be localized and identify the node.");
            }
        }
        finally { localization.SelectedLanguage = previousLanguage; }
        graph.DisconnectConnection(graph.Connections.Single(item => item.Target == op && item.TargetPortId == "a"));
        Require(!op.HasDiagnostics && !downstream.HasDiagnostics, "Fixing a mismatch must clear its node.");

        graph.SetSelection([op]);
        graph.PasteSnapshot(graph.CreateSelectionSnapshot(), 800, 0);
        Require(graph.Nodes.Count(item => item.IsPolymorphic) == 2, "Copy/paste must preserve the marker.");
        var legacy = graph.CreateNode(GraphNodeKind.AddVector3);
        Require(!legacy.IsPolymorphic && legacy.Definition.Kind == GraphNodeKind.AddVector3,
            "Explicit legacy variants must remain unchanged.");
        var legacyVector = graph.CreateNode(GraphNodeKind.FloatX3);
        var legacyScalar = graph.CreateNode(GraphNodeKind.Float);
        Require(!graph.TryConnectNodes(legacyScalar, legacy, out _, "a") &&
                graph.TryConnectNodes(legacyVector, legacy, out _, "a"),
            "Explicit legacy variants must keep strict input types.");
        Require(VfxProgram.Compile(graph.CreateSnapshot()).TypedInstructions.Single(item => item.NodeId == legacy.Id)
                    .Opcode == VfxOpcode.AddVector3,
            "Legacy variant nodes must retain their exact backend opcode.");
    }

    private static void VerifyFanoutAndPlan()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var first = graph.CreateNode(GraphNodeKind.FloatX3);
        var scalar = graph.CreateNode(GraphNodeKind.Float);
        var generic = graph.CreateNode(GraphNodeKind.Add, isPolymorphic: true);
        var consumer2 = graph.CreateNode(GraphNodeKind.AddVector2);
        var consumer3 = graph.CreateNode(GraphNodeKind.AddVector3);
        Require(graph.TryConnectNodes(first, generic, out _, "a") &&
                graph.TryConnectNodes(scalar, generic, out _, "b") &&
                graph.TryConnectNodes(generic, consumer2, out _, "a") &&
                graph.TryConnectNodes(generic, consumer3, out _, "a"),
            "Vector-first inference and fanout connections must succeed.");
        Require(generic.PortType == "FloatX3" && !generic.HasDiagnostics &&
                consumer2.HasDiagnostics && !consumer3.HasDiagnostics, "Only incompatible consumers must be diagnosed.");
        graph.DisconnectConnection(graph.Connections.Single(edge => edge.Target == generic && edge.TargetPortId == "a"));
        Require(generic.PortType == "Float" && consumer2.HasDiagnostics && consumer3.HasDiagnostics &&
                graph.Connections.Count == 3, "Changing the domain must revalidate every consumer without removing edges.");
        Require(graph.History.Undo() && graph.GetNode(generic.Id).PortType == "FloatX3" &&
                !graph.GetNode(consumer3.Id).HasDiagnostics, "Undo must recompute downstream diagnostics.");
        var dot = graph.CreateNode(new GraphNodeKind("DotVector2"), isPolymorphic: true);
        Require(graph.TryConnectNodes(graph.GetNode(first.Id), dot, out _, "a") &&
                dot.Definition.GetInputPortType("a") == "FloatX3" && dot.PortType == "Float",
            "Vector-only families must infer their input domain while retaining scalar outputs.");
        var planGraph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var plan = new LiberaGraphEditPlan(
        [
            new("v", GraphNodeKind.FloatX2, "v", 0, 0),
            new("s", GraphNodeKind.Float, "s", 0, 0),
            new("op", GraphNodeKind.Multiply, "op", 0, 0, IsPolymorphic: true)
        ],
        [
            new("v", "op", "a"),
            new("s", "op", "b")
        ]);
        var planned = planGraph.ApplyPlan(plan);
        Require(planGraph.Nodes.Single(node => node.Name == "op").PortType == "FloatX2" &&
                GraphTypeDiagnostics.Analyze(planned).Count == 0 &&
                planGraph.History.Undo() && planGraph.Nodes.Count == 0,
            "Plans must infer and undo atomically.");
    }

    private static void VerifyUnsupportedType()
    {
        var definitions = GraphNodeDefinition.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures"), out var warnings);
        Require(warnings.Count == 0, "Quaternion fixture must load.");
        var custom = definitions.Single();
        var source = new GraphNodeSnapshot(Guid.NewGuid(), custom.Kind, custom.Name, 0, 0, custom.PortType, custom.PortCategory);
        var basis = GraphNodeDefinition.Core.Single(item => item.Kind == GraphNodeKind.Add);
        var target = new GraphNodeSnapshot(Guid.NewGuid(), basis.Kind, basis.Name, 0, 0,
            basis.PortType, basis.PortCategory, IsPolymorphic: true);
        var snapshot = new GraphSnapshot([source, target], [new(source.Id, target.Id, "a")]);
        GraphValidator.Validate(snapshot);
        Require(GraphPolymorphism.Resolve(snapshot)[target.Id].PortType == "Float" &&
                GraphTypeDiagnostics.Analyze(snapshot).Single().SourceType == "Quaternion",
            "Quaternion must not be interpreted as a four-lane vector.");
    }

    private static void VerifyNativeBroadcast()
    {
        var path = Environment.GetEnvironmentVariable("LIBERAFX_NATIVE_TEST_LIBRARY");
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.WriteLine("Native broadcast checks skipped: set LIBERAFX_NATIVE_TEST_LIBRARY to the interop library.");
            return;
        }
        double[] Run(GraphWorkspaceViewModel graph, GraphNodeViewModel op)
        {
            using var host = new NativeSpawnableSession(SpawnableGroupPackage.Compile(graph.CreateSnapshot()), path);
            host.Advance(0);
            return host.ReadRoot(op.Id).Lanes;
        }
        for (var width = 2; width <= 4; width++)
        {
            foreach (var kind in new[] { GraphNodeKind.Multiply, GraphNodeKind.Subtract, GraphNodeKind.Divide, new GraphNodeKind("Min") })
            {
                foreach (var scalarFirst in new[] { true, false })
                {
                    var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
                    var scalar = graph.CreateNode(GraphNodeKind.Float);
                    var vector = graph.CreateNode(new GraphNodeKind($"FloatX{width}"));
                    var op = graph.CreateNode(kind, isPolymorphic: true);
                    graph.SetParameter(scalar, "value", JsonSerializer.SerializeToElement(2.5));
                    graph.SetParameter(vector, "value", JsonSerializer.SerializeToElement(
                        Enumerable.Range(1, width).Select(value => (double)value).ToArray()));
                    Require(graph.TryConnectNodes(scalarFirst ? scalar : vector, op, out _, "a") &&
                            graph.TryConnectNodes(scalarFirst ? vector : scalar, op, out _, "b"),
                        "Broadcast inputs must connect in both orders.");
                    var actual = Run(graph, op);
                    for (var lane = 0; lane < width; lane++)
                    {
                        var a = scalarFirst ? 2.5 : lane + 1;
                        var b = scalarFirst ? lane + 1 : 2.5;
                        var expected = kind.Value switch
                        {
                            "Multiply" => a * b,
                            "Subtract" => a - b,
                            "Divide" => a / b,
                            _ => Math.Min(a, b)
                        };
                        Require(Math.Abs(actual[lane] - expected) < 1e-9, $"Native broadcast lane {lane} failed.");
                    }
                }
            }
        }
        var dotGraph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var vector3 = dotGraph.CreateNode(GraphNodeKind.FloatX3);
        var dot = dotGraph.CreateNode(new GraphNodeKind("DotVector2"), isPolymorphic: true);
        dotGraph.SetParameter(vector3, "value", JsonSerializer.SerializeToElement(new[] { 1.0, 2.0, 3.0 }));
        dotGraph.SetParameter(dot, "b", JsonSerializer.SerializeToElement(new[] { 7.0, 9.0 }));
        Require(dotGraph.TryConnectNodes(vector3, dot, out _, "a"), "Dot input must infer Vector3.");
        Require(Math.Abs(Run(dotGraph, dot)[0] - 25.0) < 1e-9,
            "Vector-only default migration must preserve lanes and use the new variant's extra default.");
        var defaultGraph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var defaultVector = defaultGraph.CreateNode(GraphNodeKind.FloatX3);
        var multiply = defaultGraph.CreateNode(GraphNodeKind.Multiply, isPolymorphic: true);
        defaultGraph.SetParameter(defaultVector, "value", JsonSerializer.SerializeToElement(new[] { 1.0, 2.0, 3.0 }));
        defaultGraph.SetParameter(multiply, "a", JsonSerializer.SerializeToElement(2.5));
        Require(defaultGraph.TryConnectNodes(defaultVector, multiply, out _, "b"), "Default test must infer Vector3.");
        Require(Run(defaultGraph, multiply).Take(3).SequenceEqual(new[] { 2.5, 5.0, 7.5 }),
            "Unconnected scalar defaults must migrate to every vector lane.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
