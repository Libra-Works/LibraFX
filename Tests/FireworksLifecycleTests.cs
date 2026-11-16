using System.Text.Json;
using Avalonia.Threading;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;

internal static class FireworksLifecycleTests
{
    public static void Run(string output)
    {
        if (!Path.IsPathFullyQualified(output)) throw new ArgumentException("Use an absolute isolated fixture directory.");
        Directory.CreateDirectory(output);
        var graph = FireworksSample.Create();
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        workspace.InitializeFromSnapshot(graph);
        var launch = graph.Nodes.Single(node => node.Name == "Launch");
        var burst = graph.Nodes.Single(node => node.Name == "Burst");
        var final = launch.ChildGraph!.Nodes.Single(node => SpawnableGroupSchema.IsOutput(node.Kind));
        var launchInput = launch.ChildGraph.Nodes.Single(node => SpawnableGroupSchema.IsInput(node.Kind));
        var burstInput = burst.ChildGraph!.Nodes.Single(node => SpawnableGroupSchema.IsInput(node.Kind));
        var group = workspace.Nodes.Single(node => node.Id == launch.Id);
        Require(group.OutputPorts.Any(port => port.Id == "destroy") &&
            group.OutputPorts.Any(port => port.Id == SpawnableGroupSchema.OutputPortId(final.Id)), "Visible group Destroy/final outputs missing.");
        Require(workspace.EnterGraph(group), "Cannot edit Launch template.");
        workspace.DeleteNode(workspace.GetNode(final.Id));
        Require(!workspace.CreateSnapshot().Connections.Any(edge => edge.SourceNodeId == launch.Id &&
            edge.SourcePortId == SpawnableGroupSchema.OutputPortId(final.Id)), "Deleting local output left a dangling root wire.");
        Require(workspace.History.Undo(), "Deleting local output is not undoable.");
        workspace.NavigateToGraph(0);
        Require(GraphSnapshot.ContentEquals(graph, workspace.CreateSnapshot()), "Undo did not restore output identity/root routing.");
        var pasted = new GraphWorkspaceViewModel(new UndoRedoManager());
        pasted.PasteSnapshot(graph, 100, 100);
        GraphValidator.Validate(pasted.CreateSnapshot());
        _ = SpawnableGroupPackage.Compile(pasted.CreateSnapshot());
        Require(pasted.History.Undo() && pasted.Nodes.Count == 0 && pasted.History.Redo(), "Copy/paste history failed.");
        var files = new ProjectFileService();
        var sample = Path.Combine(output, "Fireworks.lfxproj");
        using (var stream = new MemoryStream())
        {
            files.SaveAsync(stream, workspace).GetAwaiter().GetResult();
            File.WriteAllBytes(sample, stream.ToArray());
        }
        using (var stream = new MemoryStream(File.ReadAllBytes(sample)))
            Require(GraphSnapshot.ContentEquals(graph, files.LoadAsync(stream).GetAwaiter().GetResult().Graph), "Project persistence changed template IDs or routes.");
        var package = SpawnableGroupPackage.Compile(graph);
        Require(package.Templates.Count == 2 && package.DestroyRoutes.Count == 1 && package.OnStartNodeId is not null, "Lifecycle package incomplete.");
        using (var render = JsonDocument.Parse(package.Templates.Single(template => template.ContainerId == launch.Id).RenderConfiguration))
            Require(render.RootElement.GetProperty("nodes").GetArrayLength() == 1, "Launch render binding missing.");
        using (var render = JsonDocument.Parse(package.Templates.Single(template => template.ContainerId == burst.Id).RenderConfiguration))
            Require(render.RootElement.GetProperty("nodes").GetArrayLength() == FireworksSample.BurstParticleCount, "Burst render binding count incorrect.");
        File.WriteAllBytes(Path.Combine(output, "parent.lfxb"), package.ParentBytecode);
        foreach (var template in package.Templates)
            File.WriteAllBytes(Path.Combine(output, graph.Nodes.Single(node => node.Id == template.ContainerId).Name + ".lfxb"), template.Bytecode);
        File.WriteAllText(Path.Combine(output, "Fireworks.package.json"), JsonSerializer.Serialize(package, new JsonSerializerOptions { WriteIndented = true }));
        var launchTemplate = package.Templates.Single(template => template.ContainerId == launch.Id);
        var burstTemplate = package.Templates.Single(template => template.ContainerId == burst.Id);
        var burstPosition = burst.ChildGraph.Nodes.Single(node => node.Name == "Burst 1 expanding position");
        using (var fixture = new BinaryWriter(File.Create(Path.Combine(output, "Fireworks.native-fixture"))))
        {
            fixture.Write("LFXF"u8);
            fixture.Write(1u);
            foreach (var id in new[] { package.OnStartNodeId!.Value,
                launch.Id, launchTemplate.SignalTypeId, launchTemplate.Lifecycle!.LifeNodeId!.Value, launchInput.Id, final.Id,
                burst.Id, burstTemplate.SignalTypeId, burstTemplate.Lifecycle!.LifeNodeId!.Value, burstInput.Id, burstPosition.Id })
                fixture.Write(id.ToByteArray(bigEndian: true));
        }
        var multiple = graph with { Nodes = graph.Nodes.Select(node => node.Id == launch.Id ? node with
        {
            ChildGraph = node.ChildGraph! with { Nodes = node.ChildGraph.Nodes.Concat([
                node.ChildGraph.Nodes.Single(item => item.Kind.Value == "LifeTime") with { Id = Guid.NewGuid() }]).ToArray() }
        } : node).ToArray() };
        Reject(() => SpawnableGroupPackage.Compile(multiple), "multiple Life Times");
        var nested = graph with { Nodes = graph.Nodes.Select(node => node.Id == launch.Id ? node with
        {
            ChildGraph = node.ChildGraph! with { Nodes = node.ChildGraph.Nodes.Concat([
                burst with { Id = Guid.NewGuid(), ChildGraph = new GraphSnapshot([], []) }]).ToArray() }
        } : node).ToArray() };
        Reject(() => SpawnableGroupPackage.Compile(nested), "nested Spawnable");
        Reject(() => SpawnableGroupPackage.Compile(ChangeLife(graph, launch.Id, -1)), "negative Life Time");
        var library = Environment.GetEnvironmentVariable("LIBERAFX_NATIVE_TEST_LIBRARY")
            ?? throw new InvalidOperationException("Actual Fireworks execution requires LIBERAFX_NATIVE_TEST_LIBRARY.");
        var ageWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        ageWorkspace.InitializeFromSnapshot(graph);
        Require(ageWorkspace.EnterGraph(ageWorkspace.GetNode(launch.Id)), "Cannot enter age fixture.");
        var createAge = LiberaFxMcpTools.CreateNode(new ActiveGraphProvider(ageWorkspace), "InstanceAge");
        Dispatcher.UIThread.RunJobs();
        using var createdAge = JsonDocument.Parse(createAge.GetAwaiter().GetResult());
        var ageId = createdAge.RootElement.GetProperty("id").GetGuid();
        Require(ageWorkspace.History.Undo() && !ageWorkspace.Nodes.Any(node => node.Id == ageId) &&
            ageWorkspace.History.Redo() && ageWorkspace.GetNode(ageId).Definition.Kind.Value == "InstanceAge",
            "MCP Instance Age creation must support Undo/Redo.");
        ageWorkspace.NavigateToGraph(0);
        using (var ageFile = new MemoryStream())
        {
            files.SaveAsync(ageFile, ageWorkspace).GetAwaiter().GetResult();
            ageFile.Position = 0;
            var ageGraph = files.LoadAsync(ageFile).GetAwaiter().GetResult().Graph;
            using var ageSession = new NativeSpawnableSession(SpawnableGroupPackage.Compile(ageGraph), library);
            ageSession.Advance(0);
            var instance = ageSession.ActiveInstances.Single();
            Near(ageSession.Read(launch.Id, instance.Instance, ageId).Lanes, [0], "Instance Age at start");
            ageSession.Advance(1);
            Near(ageSession.Read(launch.Id, instance.Instance, ageId).Lanes, [1], "Instance Age after one second");
        }
        var invalidAge = new GraphWorkspaceViewModel(new UndoRedoManager());
        invalidAge.CreateNode(new GraphNodeKind("InstanceAge"));
        Reject(() => SpawnableGroupPackage.Compile(invalidAge.CreateSnapshot()), "Instance Age outside Spawnable");
        using (var immediate = new NativeSpawnableSession(
            SpawnableGroupPackage.Compile(ChangeLife(graph, launch.Id, 0)), library))
        {
            immediate.Advance(0);
            Require(immediate.Events.Count(value => value.ContainerId == launch.Id && value.Kind == 3 && value.Time == 0) == 1,
                "Zero Life Time must produce one natural Destroy immediately after start.");
            var active = immediate.ActiveInstances.Single();
            Require(active.Container == burst.Id, "Zero Life Time did not route Destroy to the sibling.");
            Near(immediate.Read(burst.Id, active.Instance, burstInput.Id).Lanes, [0, 0, 20], "Zero Life Time final capture");
            immediate.Advance(1);
            Require(immediate.Events.Count(value => value.ContainerId == launch.Id && value.Kind == 3) == 1,
                "Zero Life Time emitted Destroy more than once.");
        }
        var groups = new GroupAssetLibrary(Path.Combine(output, "group-library"));
        var asset = groups.Create("Fireworks launch template", launch.ChildGraph);
        var linkedGraph = graph with { Nodes = graph.Nodes.Select(node => node.Id == launch.Id ?
            node with { GroupAsset = new(asset.Id, asset.Revision, false) } : node).ToArray() };
        var resolved = new GroupCompilationResolver([groups]).Resolve(linkedGraph);
        GraphValidator.Validate(resolved);
        using (var linked = new NativeSpawnableSession(SpawnableGroupPackage.Compile(resolved), library))
        {
            linked.Advance(2);
            var active = linked.ActiveInstances.Single();
            Near(linked.Read(burst.Id, active.Instance, burstInput.Id).Lanes, [0, 0, 260],
                "Linked group revision remapping lost the frozen output route");
        }
        using (var session = new NativeSpawnableSession(package, library))
        {
            session.Advance(0);
            var instance = session.ActiveInstances.Single();
            Require(instance.Container == launch.Id, "OnStart did not spawn only Launch.");
            Near(session.Read(launch.Id, instance.Instance, final.Id).Lanes, [0, 0, 20], "Initial capture");
            session.Advance(1);
            Near(session.Read(launch.Id, instance.Instance, final.Id).Lanes, [0, 0, 140], "Rising launch");
            Near(session.Read(launch.Id, instance.Instance, launchInput.Id).Lanes, [0, 0, 20], "Captured initial position changed");
            session.Advance(2);
            Require(session.Events.Count(value => value.ContainerId == launch.Id && value.Kind == 3) == 1, "Launch Destroy must emit once.");
            var burstInstance = session.ActiveInstances.Single();
            Require(burstInstance.Container == burst.Id, "Destroy did not spawn Burst.");
            Near(session.Read(burst.Id, burstInstance.Instance, burstInput.Id).Lanes, [0, 0, 260], "Destroy final center");
            session.Advance(2.5);
            Near(session.Read(burst.Id, burstInstance.Instance, burstInput.Id).Lanes, [0, 0, 260], "Burst center must stay captured");
            var position = burst.ChildGraph.Nodes.Single(node => node.Name == "Burst 1 expanding position");
            Near(session.Read(burst.Id, burstInstance.Instance, position.Id).Lanes, [55, 0, 230], "Actual expanding burst particle");
            using (var render = JsonDocument.Parse(burstTemplate.RenderConfiguration))
                foreach (var draw in render.RootElement.GetProperty("nodes").EnumerateArray())
                {
                    var binding = draw.GetProperty("inputs").EnumerateArray().Single(input => input.GetProperty("id").GetString() == "position");
                    var result = session.Read(burst.Id, burstInstance.Instance, binding.GetProperty("sourceNodeId").GetGuid());
                    Require(result.Width == 3 && result.Lanes.All(double.IsFinite), "Compiled render position is not readable for the actual Burst instance.");
                }
            session.Advance(3.5);
            Require(session.ActiveCount == 0 && session.Events.Count(value => value.Kind == 3) == 2 &&
                session.Events.Count(value => value.Kind == 1) == 2, "Natural lifecycles did not finish cleanly.");
            Console.WriteLine("Native Fireworks: Launch z=20 -> z=140 at 1s -> Destroy at 2s z=260 -> Burst center z=260 -> 12 expanding particles -> Destroy at 3.5s, active=0.");
        }
        using (var skipped = new NativeSpawnableSession(package, library))
        {
            skipped.Advance(2.5);
            var active = skipped.ActiveInstances.Single();
            Near(skipped.Read(burst.Id, active.Instance, burstInput.Id).Lanes, [0, 0, 260], "Skipped frame final position");
            Require(skipped.Events.Single(value => value.Kind == 3).Time == 2, "Expiry timestamp must not drift to the overshooting frame.");
        }
        using (var stopped = new NativeSpawnableSession(package, library))
        {
            stopped.Advance(1);
            stopped.Stop();
            Require(stopped.ActiveCount == 0 && !stopped.Events.Any(value => value.Kind == 3), "Stop/Reset emitted Destroy.");
        }
        using (var infinite = new NativeSpawnableSession(SpawnableGroupPackage.Compile(RemoveLife(graph, launch.Id)), library))
        {
            infinite.Advance(100);
            Require(infinite.ActiveInstances.Single().Container == launch.Id && !infinite.Events.Any(value => value.Kind == 3), "Absent Life Time must mean infinity.");
            infinite.Stop();
        }
        using (var reset = new NativeSpawnableSession(package, library))
        {
            reset.Advance(0);
            var id = reset.ActiveInstances.Single();
            Require(id.Instance == 1, "New session reset must restart deterministic instance identity.");
            Near(reset.Read(launch.Id, id.Instance, final.Id).Lanes, [0, 0, 20], "Deterministic reset");
        }
        using var sender = new CompiledPreviewSender(Path.Combine(output, "missing-native-preview.dll"));
        var publication = new PreviewPublication(1, "fireworks-validation", graph, GraphNodeDefinition.All);
        var message = sender.Prepare(publication)!;
        Require(message.Kind == CompiledPreviewKind.CompileError, "Missing native preview library must not misrepresent the lifecycle sample as working.");
        Console.WriteLine($"Fireworks authoring/persistence/package and actual native execution passed. Sample: {sample}");
        Console.WriteLine("UE-owned lifecycle remains unsupported; editor-native bridge is covered separately by --fireworks-native-preview. No pixel validation claimed here.");
    }
    private static GraphSnapshot ChangeLife(GraphSnapshot graph, Guid container, double seconds) => graph with
    {
        Nodes = graph.Nodes.Select(node => node.Id != container ? node : node with
        {
            ChildGraph = node.ChildGraph! with { Nodes = node.ChildGraph.Nodes.Select(child => child.Kind.Value != "LifeTime" ? child :
                child with { Parameters = [new("seconds", "float", JsonSerializer.SerializeToElement(seconds))] }).ToArray() }
        }).ToArray()
    };
    private static GraphSnapshot RemoveLife(GraphSnapshot graph, Guid container) => graph with
    {
        Nodes = graph.Nodes.Select(node => node.Id != container ? node : node with
        { ChildGraph = node.ChildGraph! with { Nodes = node.ChildGraph.Nodes.Where(child => child.Kind.Value != "LifeTime").ToArray() } }).ToArray()
    };
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Near(double[] value, double[] expected, string message) =>
        Require(expected.Select((lane, index) => Math.Abs(value[index] - lane) < 1e-8).All(ok => ok),
            $"{message}: actual=[{string.Join(",", value)}], expected=[{string.Join(",", expected)}]");
    private static void Reject(Action action, string name)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException($"Unsupported {name} was accepted.");
    }
}
