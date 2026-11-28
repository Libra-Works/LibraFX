using System.Text.Json;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Project;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;
using LiberaFx.ViewModels.Panels;
using LiberaFx.Services.Compilation;

internal static class AuthoredLifecycleTests
{
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        VerifyPhases(directory);
        VerifyPreset(directory);
        foreach (var kind in new[] { "ForceCurrent", "ForceEffect" })
        {
            var graph = new GraphWorkspaceViewModel(new UndoRedoManager()) { AutoDestroyWhenIdle = false };
            var start = graph.CreateNode(new("OnStart"));
            var spawn = graph.CreateNode(GraphNodeKind.SpawnableGroup);
            Connect(start, spawn, "spawn");
            graph.EnterGraph(spawn);
            graph.CreateNode(new("LifeTime"));
            var signal = graph.CreateNode(new("SpawnInputSignal"));
            var force = graph.CreateNode(new(kind));
            Connect(signal, force, "signal");
            var childForceId = force.Id;
            graph.NavigateToGraph(0);
            var rootForce = graph.CreateNode(new("ForceEffect"));
            var snapshot = graph.CreateSnapshot();
            graph.History.Undo();
            if (graph.Nodes.Any(node => node.Id == rootForce.Id) || !graph.History.Redo())
                throw new Exception("Force authoring did not survive Undo/Redo.");
            var clone = new GraphWorkspaceViewModel(new UndoRedoManager());
            clone.PasteSnapshot(snapshot, 1000, 1000);
            if (!clone.Nodes.Any(node => node.ChildGraph?.Nodes.Any(child => child.Kind.Value == kind) == true))
                throw new Exception("Copying a Group discarded its force signal.");
            using var stream = new MemoryStream();
            new ProjectFileService().SaveAsync(stream, graph).GetAwaiter().GetResult();
            stream.Position = 0;
            var document = ProjectDocumentReader.LoadAsync(stream).GetAwaiter().GetResult();
            if (!GraphSnapshot.ContentEquals(snapshot, document.Graph))
                throw new Exception("Force signals did not survive project persistence.");
            var package = SpawnableGroupPackage.Compile(snapshot);
            File.WriteAllBytes(Path.Combine(directory, kind + ".lfxp"), SpawnableGroupPackageBinary.Serialize(package));
            File.WriteAllBytes(Path.Combine(directory, kind + ".lfxproj"), stream.ToArray());
            File.WriteAllText(Path.Combine(directory, kind + ".json"), JsonSerializer.Serialize(new
            {
                spawn = spawn.Id, childForce = childForceId, rootForce = rootForce.Id
            }));
            File.WriteAllBytes(Path.Combine(directory, kind + ".ids"), spawn.Id.ToByteArray(bigEndian: true)
                .Concat(childForceId.ToByteArray(bigEndian: true)).Concat(rootForce.Id.ToByteArray(bigEndian: true)).ToArray());
            continue;

            void Connect(GraphNodeViewModel source, GraphNodeViewModel target, string port)
            {
                if (!graph.TryConnectNodes(source, target, out var error, targetPortId: port,
                    sourcePortId: source.OutputPorts.Count == 1 ? source.OutputPorts[0].Id : null))
                    throw new Exception(error);
            }
        }

    }

    private static void VerifyPreset(string directory)
    {
        var profiles = new TeamProfilesService(Path.Combine(directory, "phase-profiles.json"));
        using var catalog = new GroupAssetCatalog(profiles);
        var id = Guid.Parse("37000000-0000-4000-8000-000000000001");
        if (catalog.Warnings.Length != 0 || !catalog.Assets.Any(asset => asset.Id == id))
            throw new Exception("The authored lifecycle preset did not load: " + catalog.Warnings);
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager(), catalog) { AutoDestroyWhenIdle = false };
        var group = graph.CreateGroupAssetNode(id, 100, 100);
        var pane = new NodePropertiesPaneViewModel(new ActiveGraphProvider(graph));
        if (pane.Parameters.Count != 5 || group.ChildGraph?.Nodes.Any(node =>
            node.Kind == GraphNodeKind.SpawnableGroup || node.Kind.Value == "LifeTime") == true)
            throw new Exception("The lifecycle preset must expose its intervals without adding Spawnables or Life Time.");
        var package = SpawnableGroupPackage.Compile(graph.CreateSnapshot());
        File.WriteAllBytes(Path.Combine(directory, "PhasePreset.lfxp"), SpawnableGroupPackageBinary.Serialize(package));
        var phase = graph.CreateNode(new("CurrentPhase"));
        var selectorKind = GraphNodeDefinition.All.Single(definition =>
            GraphEnumBranchSchema.TryGetSelector(definition.Kind, out var type, out var output, out _) &&
            type == Guid.Parse("4a94e882-39e8-4efb-92c7-180000000030") && output == "FloatX4").Kind;
        var position = graph.CreateNode(selectorKind);
        var tint = graph.CreateNode(selectorKind);
        var vector = graph.CreateNode(new("Vector4ToVector3"));
        var draw = graph.CreateNode(new("ModelDraw"));
        graph.SetParameter(draw, "assetPath", JsonSerializer.SerializeToElement("/Engine/BasicShapes/Cube.Cube"));
        Connect(phase, position, "selector", "phase");
        Connect(phase, tint, "selector", "phase");
        for (var index = 0; index < 3; index++)
        {
            var location = graph.CreateNode(new("FloatX4"));
            var color = graph.CreateNode(new("FloatX4"));
            graph.SetParameter(location, "value", JsonSerializer.SerializeToElement(new[] { index - 1.0, 0, 0, 0 }));
            var rgba = new double[] { 0, 0, 0, 1 };
            rgba[index] = 1;
            graph.SetParameter(color, "value", JsonSerializer.SerializeToElement(rgba));
            Connect(location, position, "case_" + index);
            Connect(color, tint, "case_" + index);
        }
        Connect(position, vector, "a");
        Connect(vector, draw, "position");
        Connect(tint, draw, "tint");
        File.WriteAllBytes(Path.Combine(directory, "PhasePresetRender.lfxp"),
            SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(graph.CreateSnapshot())));
        using var renderedProject = new MemoryStream();
        new ProjectFileService().SaveAsync(renderedProject, graph).GetAwaiter().GetResult();
        File.WriteAllBytes(Path.Combine(directory, "PhasePresetRender.lfxproj"), renderedProject.ToArray());

        void Connect(GraphNodeViewModel source, GraphNodeViewModel target, string port, string? output = null)
        {
            if (!graph.TryConnectNodes(source, target, out var error, targetPortId: port,
                sourcePortId: output ?? (source.OutputPorts.Count == 1 ? source.OutputPorts[0].Id : null)))
                throw new Exception(error);
        }
    }

    private static void VerifyPhases(string directory)
        {
            var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
            var phase = graph.CreateNode(new("CurrentPhase"));
            var time = graph.CreateNode(new("PhaseTime"));
            var clock = graph.CreateNode(new("LoopClock"));
            var enterLoop = graph.CreateNode(new("EnterLoop"));
            var enterEnd = graph.CreateNode(new("EnterEnd"));
            var enterStart = graph.CreateNode(new("EnterStart"));
            var reenterEnd = graph.CreateNode(new("EnterEnd"));
            var arm = graph.CreateNode(GraphNodeKind.Signal);
            var reset = graph.CreateNode(GraphNodeKind.Signal);
            var startValue = graph.CreateNode(new("FloatX4"));
            var endValue = graph.CreateNode(new("FloatX4"));
            var phaseType = Guid.Parse("4a94e882-39e8-4efb-92c7-180000000030");
            var selector = graph.CreateNode(GraphNodeDefinition.All.Single(definition =>
                GraphEnumBranchSchema.TryGetSelector(definition.Kind, out var type, out var output, out _) &&
                type == phaseType && output == "FloatX4").Kind);
            var debugTime = graph.CreateNode(GraphNodeKind.Add);
            Connect(phase, selector, "selector", "phase");
            Connect(startValue, selector, "case_0");
            Connect(clock, selector, "case_1", "value");
            Connect(endValue, selector, "case_2");
            Connect(time, clock, "time");
            Connect(time, debugTime, "a");
            Connect(arm, clock, "arm");
            Connect(reset, clock, "reset");
            Connect(clock, enterEnd, "signal", "armedBoundary");
            if (!graph.History.Undo() || graph.Connections.Any(edge => edge.Target.Id == enterEnd.Id) ||
                !graph.History.Redo())
                throw new Exception("Authored phase boundary wiring did not survive Undo/Redo.");
            var snapshot = graph.CreateSnapshot();
            GraphValidator.Validate(snapshot);
            var clone = new GraphWorkspaceViewModel(new UndoRedoManager());
            clone.PasteSnapshot(snapshot, 1000, 1000);
            if (clone.Connections.Count != snapshot.Connections.Count ||
                !clone.Connections.Any(edge => edge.SourcePortId == "armedBoundary"))
                throw new Exception("Copying authored phases discarded named outputs.");
            using var stream = new MemoryStream();
            new ProjectFileService().SaveAsync(stream, graph).GetAwaiter().GetResult();
            stream.Position = 0;
            if (!GraphSnapshot.ContentEquals(snapshot,
                ProjectDocumentReader.LoadAsync(stream).GetAwaiter().GetResult().Graph))
                throw new Exception("Authored phases did not survive project persistence.");
            var program = VfxProgram.Compile(snapshot);
            if (program.BinaryVersion != 11)
                throw new Exception("Phase state must use explicitly typed bytecode.");
            File.WriteAllBytes(Path.Combine(directory, "AuthoredPhases.lfxp"),
                SpawnableGroupPackageBinary.Serialize(new SpawnableGroupPackage(program.ToPackageBinary(), [])));
            File.WriteAllBytes(Path.Combine(directory, "AuthoredPhases.ids"),
                new[] { phase.Id, time.Id, selector.Id, clock.Id, enterLoop.Id, enterEnd.Id, enterStart.Id, arm.Id, reset.Id, reenterEnd.Id }
                    .SelectMany(id => id.ToByteArray(bigEndian: true)).ToArray());
            File.WriteAllBytes(Path.Combine(directory, "AuthoredPhases.lfxproj"), stream.ToArray());

            void Connect(GraphNodeViewModel source, GraphNodeViewModel target, string port, string? output = null)
            {
                if (!graph.TryConnectNodes(source, target, out var error, targetPortId: port,
                    sourcePortId: output ?? (source.OutputPorts.Count == 1 ? source.OutputPorts[0].Id : null)))
                    throw new Exception(error);
        }
    }
}
