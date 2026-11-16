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
        var standaloneClocks = new GraphWorkspaceViewModel(new UndoRedoManager());
        var clockProvider = new ActiveGraphProvider(standaloneClocks);
        var createEffectAge = LiberaFxMcpTools.CreateNode(clockProvider, "EffectAge");
        Dispatcher.UIThread.RunJobs();
        using var createdEffectAge = JsonDocument.Parse(createEffectAge.GetAwaiter().GetResult());
        var standaloneEffectAge = standaloneClocks.GetNode(createdEffectAge.RootElement.GetProperty("id").GetGuid());
        var createDelta = LiberaFxMcpTools.CreateNode(clockProvider, "DeltaTime");
        Dispatcher.UIThread.RunJobs();
        using var createdDelta = JsonDocument.Parse(createDelta.GetAwaiter().GetResult());
        var standaloneDelta = standaloneClocks.GetNode(createdDelta.RootElement.GetProperty("id").GetGuid());
        var standaloneInstanceAge = standaloneClocks.CreateNode(new GraphNodeKind("InstanceAge"));
        var standaloneColor = standaloneClocks.CreateNode(GraphNodeKind.FloatX4);
        var clockFixture = Path.Combine(output, "clock-fixture.lfxb");
        File.WriteAllBytes(clockFixture, VfxProgram.Compile(standaloneClocks.CreateSnapshot()).ToBinary());
        File.WriteAllLines(clockFixture + ".ids", [standaloneEffectAge.Id.ToString("D"),
            standaloneDelta.Id.ToString("D"), standaloneInstanceAge.Id.ToString("D"), standaloneColor.Id.ToString("D")]);
        var clocksWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        clocksWorkspace.InitializeFromSnapshot(graph);
        var rootEffectAge = clocksWorkspace.CreateNode(new GraphNodeKind("EffectAge"));
        var rootDelta = clocksWorkspace.CreateNode(new GraphNodeKind("DeltaTime"));
        var rootInstanceAge = clocksWorkspace.CreateNode(new GraphNodeKind("InstanceAge"));
        var clockSum = clocksWorkspace.CreateNode(GraphNodeKind.Add);
        Require(clocksWorkspace.TryConnectNodes(rootEffectAge, clockSum, out _, "a", "value") &&
                clocksWorkspace.TryConnectNodes(rootDelta, clockSum, out _, "b", "value") &&
                clocksWorkspace.History.Undo() && clocksWorkspace.History.Redo(),
            "Clock outputs must connect and support Undo/Redo.");
        Require(clocksWorkspace.EnterGraph(clocksWorkspace.GetNode(burst.Id)), "Cannot edit clock fixture.");
        var childEffectAge = clocksWorkspace.CreateNode(new GraphNodeKind("EffectAge"));
        var childDelta = clocksWorkspace.CreateNode(new GraphNodeKind("DeltaTime"));
        var childInstanceAge = clocksWorkspace.CreateNode(new GraphNodeKind("InstanceAge"));
        var nestedGroup = clocksWorkspace.CreateNode(GraphNodeKind.GraphGroup);
        Require(clocksWorkspace.EnterGraph(nestedGroup), "Cannot edit nested clock fixture.");
        var nestedInstanceAge = clocksWorkspace.CreateNode(new GraphNodeKind("InstanceAge"));
        clocksWorkspace.NavigateToGraph(0);
        using var clocksFile = new MemoryStream();
        files.SaveAsync(clocksFile, clocksWorkspace).GetAwaiter().GetResult();
        clocksFile.Position = 0;
        var clocksGraph = files.LoadAsync(clocksFile).GetAwaiter().GetResult().Graph;
        using (var clocks = new NativeSpawnableSession(SpawnableGroupPackage.Compile(clocksGraph), library))
        {
            clocks.Advance(0, 0.02);
            Near(clocks.ReadRoot(rootEffectAge.Id).Lanes, [0], "Root effect age");
            Near(clocks.ReadRoot(rootDelta.Id).Lanes, [0.02], "Initial game frame delta");
            Near(clocks.ReadRoot(rootInstanceAge.Id).Lanes, [0], "Root instance age");
            clocks.Advance(2, 0.125);
            var child = clocks.ActiveInstances.Single(key => key.Container == burst.Id);
            Near(clocks.Read(burst.Id, child.Instance, childEffectAge.Id).Lanes, [2], "Effect age at child spawn");
            Near(clocks.Read(burst.Id, child.Instance, childDelta.Id).Lanes, [0.125], "Game delta at child spawn");
            Near(clocks.Read(burst.Id, child.Instance, childInstanceAge.Id).Lanes, [0], "Child age at spawn");
            clocks.Advance(2.5, 0.25);
            Near(clocks.ReadRoot(rootEffectAge.Id).Lanes, [2.5], "Root effect age after update");
            Near(clocks.ReadRoot(rootInstanceAge.Id).Lanes, [2.5], "Root instance age after update");
            Near(clocks.ReadRoot(clockSum.Id).Lanes, [2.75], "Connected clock outputs");
            Near(clocks.Read(burst.Id, child.Instance, childEffectAge.Id).Lanes, [2.5], "Global effect age in child");
            Near(clocks.Read(burst.Id, child.Instance, childDelta.Id).Lanes, [0.25], "Game delta in child");
            Near(clocks.Read(burst.Id, child.Instance, childInstanceAge.Id).Lanes, [0.5], "Local child age");
            Near(clocks.Read(burst.Id, child.Instance, nestedInstanceAge.Id).Lanes, [0.5], "Ordinary group must inherit its enclosing Spawnable age");
        }
        using (var skippedClocks = new NativeSpawnableSession(SpawnableGroupPackage.Compile(clocksGraph), library))
        {
            skippedClocks.Advance(0, 0.02);
            skippedClocks.Advance(2.5, 0.125);
            var child = skippedClocks.ActiveInstances.Single(key => key.Container == burst.Id);
            Near(skippedClocks.Read(burst.Id, child.Instance, childDelta.Id).Lanes, [0.125],
                "Internal expiry substeps must retain the game frame delta");
            Near(skippedClocks.Read(burst.Id, child.Instance, childEffectAge.Id).Lanes, [2.5],
                "Skipped child effect age");
        }
        var infiniteOrderWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        infiniteOrderWorkspace.InitializeFromSnapshot(graph);
        var earlyGroup = infiniteOrderWorkspace.CreateNode(GraphNodeKind.SpawnableGroup);
        var earlyGroupId = earlyGroup.Id;
        Require(infiniteOrderWorkspace.EnterGraph(earlyGroup), "Cannot edit early-expiring sibling.");
        infiniteOrderWorkspace.CreateNode(new GraphNodeKind("LifeTime"));
        infiniteOrderWorkspace.NavigateToGraph(0);
        var startId = graph.Nodes.Single(node => node.Kind.Value == "OnStart").Id;
        Require(infiniteOrderWorkspace.TryConnectNodes(infiniteOrderWorkspace.GetNode(startId),
            infiniteOrderWorkspace.GetNode(earlyGroupId), out _, "spawn", "then"),
            "Cannot connect the early-expiring sibling.");
        var infiniteOrderGraph = ChangeLife(infiniteOrderWorkspace.CreateSnapshot(), earlyGroupId, 1);
        using (var infiniteOrder = new NativeSpawnableSession(SpawnableGroupPackage.Compile(infiniteOrderGraph), library))
        {
            infiniteOrder.Advance(3);
            var expiries = infiniteOrder.Events.Where(item => item.Kind == 3).Select(item => item.Time).ToArray();
            Require(expiries.SequenceEqual([1d, 2d]),
                "An infinite parent must process skipped child expiries in chronological order.");
            infiniteOrder.Advance(4);
            Require(infiniteOrder.RootVisible && infiniteOrder.ActiveCount == 0 &&
                    infiniteOrder.Events.Where(item => item.Kind == 3).Select(item => item.Time)
                        .SequenceEqual([1d, 2d, 3.5d]),
                "A sibling spawned by Destroy must also expire in order while the infinite parent remains alive.");
        }
        var rootWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        var createRootLife = LiberaFxMcpTools.CreateNode(new ActiveGraphProvider(rootWorkspace), "LifeTime");
        Dispatcher.UIThread.RunJobs();
        using var createdRootLife = JsonDocument.Parse(createRootLife.GetAwaiter().GetResult());
        var rootLifeId = createdRootLife.RootElement.GetProperty("id").GetGuid();
        Require(rootWorkspace.History.Undo() && !rootWorkspace.Nodes.Any(node => node.Id == rootLifeId) &&
                rootWorkspace.History.Redo(), "Root Life Time must support MCP creation and Undo/Redo.");
        var rootTime = rootWorkspace.CreateNode(GraphNodeKind.Time);
        var rootRemaining = rootWorkspace.CreateNode(new GraphNodeKind("InstanceRemaining"));
        var rootNormalized = rootWorkspace.CreateNode(new GraphNodeKind("InstanceNormalizedAge"));
        using (var rootFile = new MemoryStream())
        {
            files.SaveAsync(rootFile, rootWorkspace).GetAwaiter().GetResult();
            rootFile.Position = 0;
            var rootGraph = files.LoadAsync(rootFile).GetAwaiter().GetResult().Graph;
            var rootPackage = SpawnableGroupPackage.Compile(rootGraph);
            Require(rootPackage.LifeNodeId == rootLifeId, "Root Life Time identity was lost in the package.");
            using var root = new NativeSpawnableSession(rootPackage, library);
            root.Advance(0);
            Require(root.RootVisible && root.ReadRoot(rootTime.Id).Lanes[0] == 0, "Root must start at zero.");
            Require(root.ReadRoot(rootRemaining.Id).Lanes[0] == 2 && root.ReadRoot(rootNormalized.Id).Lanes[0] == 0,
                "Root lifetime samples are incorrect at start.");
            root.Advance(1);
            Require(root.RootVisible && root.ReadRoot(rootTime.Id).Lanes[0] == 1, "Root did not advance.");
            Require(root.ReadRoot(rootRemaining.Id).Lanes[0] == 1 && root.ReadRoot(rootNormalized.Id).Lanes[0] == 0.5,
                "Root lifetime samples are incorrect mid-play.");
            root.Advance(3);
            Require(!root.RootVisible && root.ReadRoot(rootTime.Id).Lanes[0] == 2, "Root expiry must clamp the final evaluation.");
            Require(root.ReadRoot(rootRemaining.Id).Lanes[0] == 0 && root.ReadRoot(rootNormalized.Id).Lanes[0] == 1,
                "Root lifetime samples are incorrect at expiry.");
            root.Advance(4);
            Require(root.ReadRoot(rootTime.Id).Lanes[0] == 2, "Expired root was evaluated again.");
            var zeroGraph = rootGraph with
            {
                Nodes = rootGraph.Nodes.Select(node => node.Id == rootLifeId ? node with
                {
                    Parameters = [new("seconds", "float", JsonSerializer.SerializeToElement(0d))]
                } : node).ToArray()
            };
            using (var zero = new NativeSpawnableSession(SpawnableGroupPackage.Compile(zeroGraph), library))
            {
                zero.Advance(0);
                Require(zero.ReadRoot(rootRemaining.Id).Lanes[0] == 0 &&
                        zero.ReadRoot(rootNormalized.Id).Lanes[0] == 1, "Zero lifetime samples must be 0 and 1.");
            }
            var infinite = rootGraph with
            {
                Nodes = rootGraph.Nodes.Where(node => node.Id != rootLifeId && node.Id != rootRemaining.Id).ToArray()
            };
            using (var forever = new NativeSpawnableSession(SpawnableGroupPackage.Compile(infinite), library))
            {
                forever.Advance(10);
                Require(forever.ReadRoot(rootNormalized.Id).Lanes[0] == 0, "Infinite normalized age must remain zero.");
            }
            Reject(() => SpawnableGroupPackage.Compile(infinite with
            {
                Nodes = infinite.Nodes.Concat([rootGraph.Nodes.Single(node => node.Id == rootRemaining.Id)]).ToArray()
            }), "infinite remaining lifetime");
            Reject(() => SpawnableGroupPackage.Compile(rootGraph with
            {
                Connections = rootGraph.Connections.Concat([new(rootNormalized.Id, rootLifeId, "seconds")]).ToArray()
            }), "cyclic lifetime");
            using var stopped = new NativeSpawnableSession(rootPackage, library);
            stopped.Advance(0);
            stopped.Stop();
            Require(!stopped.RootVisible, "Manual stop must hide the root.");
            var curveWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
            curveWorkspace.CreateNode(new GraphNodeKind("LifeTime"));
            var longCurve = curveWorkspace.CreateNode(GraphNodeKind.FloatFCurve);
            curveWorkspace.SetCurve(longCurve, new FCurveSnapshot([
                new(Guid.NewGuid(), 0, 0, 0, 0, CurveInterpolation.Linear),
                new(Guid.NewGuid(), 120, 1, 0, 0, CurveInterpolation.Linear)
            ]), "Long curve");
            using (var independentCurve = new NativeSpawnableSession(SpawnableGroupPackage.Compile(curveWorkspace.CreateSnapshot()), library))
            {
                independentCurve.Advance(3);
                Require(!independentCurve.RootVisible, "FCurve duration must not retain an expired effect.");
            }
            var draw = launch.ChildGraph!.Nodes.First(node => node.Kind.Value == "ModelDraw") with { Id = Guid.NewGuid() };
            var previewGraph = rootGraph with { Nodes = rootGraph.Nodes.Concat([draw]).ToArray() };
            using var preview = new EditorNativePreviewSession(previewGraph, library);
            int DrawCount(double time)
            {
                using var scene = JsonDocument.Parse(preview.EvaluateScene(time, 1));
                return scene.RootElement.GetProperty("nodes").GetArrayLength();
            }
            Require(DrawCount(0) == 1 && DrawCount(2) == 1 && DrawCount(2 + 1d / 60) == 0 &&
                    DrawCount(0) == 1, "Root preview must show its final frame, expire, and recreate on seek.");
            var rootLife = rootGraph.Nodes.Single(node => node.Id == rootLifeId);
            GraphSnapshot WithRootLife(double seconds) => graph with
            {
                Nodes = graph.Nodes.Concat([rootLife with
                {
                    Parameters = [new("seconds", "float", JsonSerializer.SerializeToElement(seconds))]
                }]).ToArray()
            };
            using (var parentWaits = new NativeSpawnableSession(SpawnableGroupPackage.Compile(WithRootLife(1)), library))
            {
                parentWaits.Advance(0);
                parentWaits.Advance(2);
                Require(parentWaits.ActiveCount == 1 && parentWaits.RootVisible &&
                        parentWaits.Events.Count(item => item.ContainerId == launch.Id && item.Kind == 3) == 1,
                    "Root deadline must wait while child Destroy spawns a sibling.");
                parentWaits.Advance(3.5);
                Require(parentWaits.ActiveCount == 0 &&
                        parentWaits.Events.Count(item => item.ContainerId == burst.Id && item.Kind == 3) == 1,
                    "The parent must wait until the sibling naturally expires.");
            }
            using (var stoppedWhileDraining = new NativeSpawnableSession(SpawnableGroupPackage.Compile(WithRootLife(1)), library))
            {
                stoppedWhileDraining.Advance(1);
                stoppedWhileDraining.Stop();
                Require(!stoppedWhileDraining.RootVisible && stoppedWhileDraining.ActiveCount == 0 &&
                        stoppedWhileDraining.Events.All(item => item.Kind != 3),
                    "Explicit stop during the parent's drain must not emit a natural child Destroy.");
            }
            var delayWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
            delayWorkspace.InitializeFromSnapshot(WithRootLife(1));
            var createDelay = LiberaFxMcpTools.CreateNode(new ActiveGraphProvider(delayWorkspace), "Delay");
            Dispatcher.UIThread.RunJobs();
            using var createdDelay = JsonDocument.Parse(createDelay.GetAwaiter().GetResult());
            var delayId = createdDelay.RootElement.GetProperty("id").GetGuid();
            Require(delayWorkspace.History.Undo() && !delayWorkspace.Nodes.Any(node => node.Id == delayId) &&
                    delayWorkspace.History.Redo(), "Delay must support MCP creation and Undo/Redo.");
            delayWorkspace.DisconnectConnection(delayWorkspace.Connections.Single(edge =>
                edge.Source.Id == launch.Id && edge.Target.Id == burst.Id && edge.SourcePortId == "destroy"));
            Require(delayWorkspace.TryConnectNodes(delayWorkspace.GetNode(launch.Id), delayWorkspace.GetNode(delayId),
                        out _, "signal", "destroy") &&
                    delayWorkspace.TryConnectNodes(delayWorkspace.GetNode(delayId), delayWorkspace.GetNode(burst.Id),
                        out _, "spawn", "then"), "Delay must connect between child Destroy and sibling Spawn.");
            var delayRootTime = delayWorkspace.CreateNode(GraphNodeKind.Time);
            Require(delayWorkspace.EnterGraph(delayWorkspace.GetNode(burst.Id)), "Cannot enter delayed sibling.");
            var delayedParentInput = delayWorkspace.CreateNode(new GraphNodeKind("SpawnInputFloat"));
            delayWorkspace.NavigateToGraph(0);
            Require(delayWorkspace.TryConnectNodes(delayWorkspace.GetNode(delayRootTime.Id), delayWorkspace.GetNode(burst.Id),
                out _, SpawnableGroupSchema.PortId(delayedParentInput.Id)), "Cannot capture parent time at delayed spawn.");
            using var delayFile = new MemoryStream();
            files.SaveAsync(delayFile, delayWorkspace).GetAwaiter().GetResult();
            delayFile.Position = 0;
            var delayedGraph = files.LoadAsync(delayFile).GetAwaiter().GetResult().Graph;
            var delayedPackage = SpawnableGroupPackage.Compile(delayedGraph);
            Require(delayedPackage.Version == 3 && delayedPackage.DestroyRoutes.Single().DelayNodeId == delayId,
                "Delayed route identity must survive persistence and compilation.");
            using (var delayed = new NativeSpawnableSession(delayedPackage, library))
            {
                delayed.Advance(2);
                Require(delayed.ActiveCount == 0 && delayed.RootVisible &&
                        delayed.Events.All(item => item.ContainerId != burst.Id || item.Kind != 1),
                    "Pending Delay must keep the parent alive without spawning early.");
                delayed.Advance(3);
                var delayedChild = delayed.ActiveInstances.Single();
                Require(delayedChild.Container == burst.Id && delayed.InstanceStartTime(burst.Id, delayedChild.Instance) == 3,
                    "Delay must spawn the sibling at its scheduled time.");
                Near(delayed.Read(burst.Id, delayedChild.Instance, burstInput.Id).Lanes, [0, 0, 260],
                    "Delay must preserve the expired child's value");
                Near(delayed.Read(burst.Id, delayedChild.Instance, delayedParentInput.Id).Lanes, [3],
                    "Delay must capture the parent value at spawn");
                delayed.Advance(4.5);
                Require(delayed.ActiveCount == 0 && delayed.Events.Count(item => item.Kind == 3) == 2,
                    "The parent must wait until the delayed child naturally expires.");
            }
            var fanoutWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
            fanoutWorkspace.InitializeFromSnapshot(delayedGraph);
            var secondTarget = fanoutWorkspace.CreateNode(GraphNodeKind.SpawnableGroup);
            Require(fanoutWorkspace.EnterGraph(secondTarget), "Cannot edit the second delayed sibling.");
            fanoutWorkspace.CreateNode(new GraphNodeKind("LifeTime"));
            var secondInput = fanoutWorkspace.CreateNode(new GraphNodeKind("SpawnInputFloat"));
            fanoutWorkspace.NavigateToGraph(0);
            Require(fanoutWorkspace.TryConnectNodes(fanoutWorkspace.GetNode(delayId), fanoutWorkspace.GetNode(secondTarget.Id), out _, "spawn", "then") &&
                    fanoutWorkspace.TryConnectNodes(fanoutWorkspace.GetNode(delayRootTime.Id), fanoutWorkspace.GetNode(secondTarget.Id), out _,
                        SpawnableGroupSchema.PortId(secondInput.Id)),
                "Delay must allow multiple sibling Spawn targets.");
            Require(fanoutWorkspace.History.Undo() && fanoutWorkspace.History.Redo(),
                "Delayed fan-out input connection must support Undo/Redo.");
            using var fanoutFile = new MemoryStream();
            files.SaveAsync(fanoutFile, fanoutWorkspace).GetAwaiter().GetResult();
            fanoutFile.Position = 0;
            var fanoutPackage = SpawnableGroupPackage.Compile(files.LoadAsync(fanoutFile).GetAwaiter().GetResult().Graph);
            Require(fanoutPackage.DestroyRoutes.Count(route => route.DelayNodeId == delayId) == 2,
                "Both delayed branches must be compiled.");
            using (var fanout = new NativeSpawnableSession(fanoutPackage, library))
            {
                fanout.Advance(2);
                Require(fanout.ActiveCount == 0 && fanout.RootVisible, "Pending branches must retain the parent.");
                fanout.Advance(3);
                var first = fanout.ActiveInstances.Single(key => key.Container == burst.Id);
                var second = fanout.ActiveInstances.Single(key => key.Container == secondTarget.Id);
                Near(fanout.Read(burst.Id, first.Instance, burstInput.Id).Lanes, [0, 0, 260],
                    "Fan-out must preserve the expired child's value");
                Near(fanout.Read(secondTarget.Id, second.Instance, secondInput.Id).Lanes, [3],
                    "Every delayed branch must capture its parent at spawn");
                fanout.Advance(4.5);
                Require(fanout.ActiveCount == 1 && fanout.RootVisible,
                    "The parent must wait for the longest-lived delayed branch.");
                fanout.Advance(5);
                Require(fanout.ActiveCount == 0 && fanout.Events.Count(item => item.Kind == 3) == 3,
                    "Both delayed branches must naturally expire.");
            }
            using (var skippedDelay = new NativeSpawnableSession(delayedPackage, library))
            {
                skippedDelay.Advance(4);
                var delayedChild = skippedDelay.ActiveInstances.Single();
                Require(skippedDelay.InstanceStartTime(burst.Id, delayedChild.Instance) == 3,
                    "Skipped frames must deliver Delay at its exact scheduled time.");
                Near(skippedDelay.Read(burst.Id, delayedChild.Instance, delayedParentInput.Id).Lanes, [3],
                    "Skipped frames must capture the parent at the scheduled time");
            }
            using (var delayPreview = new EditorNativePreviewSession(delayedGraph, library))
            {
                int DelayedDrawCount(double time)
                {
                    using var scene = JsonDocument.Parse(delayPreview.EvaluateScene(time, 1));
                    return scene.RootElement.GetProperty("nodes").GetArrayLength();
                }
                Require(DelayedDrawCount(2.5) == 0 && DelayedDrawCount(3) == FireworksSample.BurstParticleCount &&
                        DelayedDrawCount(4.5) == 0 && DelayedDrawCount(2.5) == 0 &&
                        DelayedDrawCount(3) == FireworksSample.BurstParticleCount,
                    "Preview seek must reconstruct the pending delay and delayed sibling.");
            }
            using (var stoppedDelay = new NativeSpawnableSession(delayedPackage, library))
            {
                stoppedDelay.Advance(2);
                stoppedDelay.Stop();
                Require(!stoppedDelay.RootVisible && stoppedDelay.Events.All(item => item.ContainerId != burst.Id),
                    "Explicit stop must cancel pending continuations.");
            }
            var dynamicDelayGraph = delayedGraph with
            {
                Connections = delayedGraph.Connections.Concat([new(delayRootTime.Id, delayId, "seconds")]).ToArray()
            };
            using (var dynamicDelay = new NativeSpawnableSession(SpawnableGroupPackage.Compile(dynamicDelayGraph), library))
            {
                dynamicDelay.Advance(4);
                var delayedChild = dynamicDelay.ActiveInstances.Single();
                Require(dynamicDelay.InstanceStartTime(burst.Id, delayedChild.Instance) == 4,
                    "Connected Delay duration must be sampled when Destroy fires.");
                Near(dynamicDelay.Read(burst.Id, delayedChild.Instance, delayedParentInput.Id).Lanes, [4],
                    "Parent inputs must be sampled at the dynamically delayed spawn");
            }
            var zeroDelayGraph = delayedGraph with
            {
                Nodes = delayedGraph.Nodes.Select(node => node.Id == delayId ? node with
                {
                    Parameters = [new("seconds", "float", JsonSerializer.SerializeToElement(0d))]
                } : node).ToArray()
            };
            using (var zeroDelay = new NativeSpawnableSession(SpawnableGroupPackage.Compile(zeroDelayGraph), library))
            {
                zeroDelay.Advance(2);
                var delayedChild = zeroDelay.ActiveInstances.Single();
                Require(zeroDelay.InstanceStartTime(burst.Id, delayedChild.Instance) == 2,
                    "Zero Delay must dispatch at the Destroy timestamp.");
            }
            var infiniteDelayedGraph = delayedGraph with
            {
                Nodes = delayedGraph.Nodes.Where(node => node.Id != rootLifeId).ToArray()
            };
            using (var infiniteDelay = new NativeSpawnableSession(SpawnableGroupPackage.Compile(infiniteDelayedGraph), library))
            {
                infiniteDelay.Advance(4.5);
                Require(infiniteDelay.ActiveCount == 0 && infiniteDelay.RootVisible,
                    "Delay must also work when the parent has infinite lifetime.");
            }
            var negativeDelayGraph = delayedGraph with
            {
                Nodes = delayedGraph.Nodes.Select(node => node.Id == delayId ? node with
                {
                    Parameters = [new("seconds", "float", JsonSerializer.SerializeToElement(-1d))]
                } : node).ToArray()
            };
            using (var invalidDelay = new NativeSpawnableSession(SpawnableGroupPackage.Compile(negativeDelayGraph), library))
                Reject(() => invalidDelay.Advance(2), "negative Delay");
            using (var childExpiresFirst = new NativeSpawnableSession(SpawnableGroupPackage.Compile(WithRootLife(2.5)), library))
            {
                childExpiresFirst.Advance(0);
                childExpiresFirst.Advance(3);
                Require(childExpiresFirst.ActiveCount == 1 && childExpiresFirst.RootVisible &&
                        childExpiresFirst.Events.Count(item => item.ContainerId == launch.Id && item.Kind == 3 && item.Time == 2) == 1 &&
                        childExpiresFirst.Events.All(item => item.ContainerId != burst.Id || item.Kind != 3),
                    "Root must remain active while the sibling remains alive.");
                childExpiresFirst.Advance(3.5);
                Require(childExpiresFirst.ActiveCount == 0 &&
                        childExpiresFirst.Events.Count(item => item.ContainerId == burst.Id && item.Kind == 3) == 1,
                    "Root may finish only after the remaining sibling naturally expires.");
            }
            var rootDestroyWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
            rootDestroyWorkspace.InitializeFromSnapshot(WithRootLife(1));
            rootDestroyWorkspace.CreateNode(GraphNodeKind.FloatFCurve);
            var createDestroy = LiberaFxMcpTools.CreateNode(new ActiveGraphProvider(rootDestroyWorkspace), "OnDestroy");
            Dispatcher.UIThread.RunJobs();
            using var createdDestroy = JsonDocument.Parse(createDestroy.GetAwaiter().GetResult());
            var rootDestroyId = createdDestroy.RootElement.GetProperty("id").GetGuid();
            Require(rootDestroyWorkspace.History.Undo() && !rootDestroyWorkspace.Nodes.Any(node => node.Id == rootDestroyId) &&
                    rootDestroyWorkspace.History.Redo(), "Root On Destroy must support MCP creation and Undo/Redo.");
            var finalGroup = rootDestroyWorkspace.CreateNode(GraphNodeKind.SpawnableGroup);
            var finalGroupId = finalGroup.Id;
            Require(rootDestroyWorkspace.EnterGraph(finalGroup), "Cannot enter root Destroy target.");
            rootDestroyWorkspace.CreateNode(new GraphNodeKind("LifeTime"));
            rootDestroyWorkspace.CreateNode(new GraphNodeKind("OwnerDestroy"));
            rootDestroyWorkspace.NavigateToGraph(0);
            Require(rootDestroyWorkspace.TryConnectNodes(rootDestroyWorkspace.GetNode(rootDestroyId),
                rootDestroyWorkspace.GetNode(finalGroupId), out _, "spawn", "then"),
                "Root On Destroy must connect to a Spawnable.");
            using var rootDestroyFile = new MemoryStream();
            files.SaveAsync(rootDestroyFile, rootDestroyWorkspace).GetAwaiter().GetResult();
            rootDestroyFile.Position = 0;
            var rootDestroyGraph = files.LoadAsync(rootDestroyFile).GetAwaiter().GetResult().Graph;
            var rootDestroyPackage = SpawnableGroupPackage.Compile(rootDestroyGraph);
            Require(rootDestroyPackage.OnDestroyNodeId == rootDestroyId && rootDestroyPackage.Version == 3,
                "Root On Destroy must survive persistence and compilation.");
            Require(rootDestroyPackage.ParentBytecode[4] >= 4, "Fixture must include a curve-bearing signal program.");
            using (var rootDestroy = new NativeSpawnableSession(rootDestroyPackage, library))
            {
                rootDestroy.Advance(3);
                Require(rootDestroy.Events.All(item => item.ContainerId != finalGroupId),
                    "Root On Destroy must wait for existing children.");
                rootDestroy.Advance(3.5);
                Require(rootDestroy.RootVisible && rootDestroy.ActiveCount == 1 &&
                        rootDestroy.Events.Count(item => item.ContainerId == finalGroupId && item.Kind == 1) == 1,
                    "Root On Destroy must run once and wait for its spawned child.");
                rootDestroy.Advance(5.5);
                Require(rootDestroy.ActiveCount == 0 &&
                        rootDestroy.Events.Count(item => item.ContainerId == finalGroupId && item.Kind == 1) == 1 &&
                        rootDestroy.Events.Count(item => item.ContainerId == finalGroupId && item.Kind == 3) == 1,
                    "Root On Destroy must not refire while its child drains.");
            }
            using (var stoppedRootDestroy = new NativeSpawnableSession(rootDestroyPackage, library))
            {
                stoppedRootDestroy.Advance(1);
                stoppedRootDestroy.Stop();
                Require(stoppedRootDestroy.Events.All(item => item.ContainerId != finalGroupId),
                    "Manual Stop must not dispatch root On Destroy.");
            }
            using (var stepped = new NativeSpawnableSession(SpawnableGroupPackage.Compile(WithRootLife(2.5)), library))
            {
                stepped.Advance(0);
                stepped.Advance(1);
                stepped.Advance(2);
                stepped.Advance(2.5);
                Require(stepped.ActiveCount == 1 && stepped.RootVisible &&
                        stepped.Events.Count(item => item.ContainerId == launch.Id && item.Kind == 3 && item.Time == 2) == 1 &&
                        stepped.Events.All(item => item.ContainerId != burst.Id || item.Kind != 3),
                    "Stepped playback must keep the parent alive for the sibling.");
                stepped.Advance(3.5);
                Require(stepped.ActiveCount == 0 && stepped.Events.Count(item => item.ContainerId == burst.Id && item.Kind == 3) == 1,
                    "Stepped playback must finish after the sibling expires.");
            }
            using (var coincident = new NativeSpawnableSession(SpawnableGroupPackage.Compile(WithRootLife(2)), library))
            {
                coincident.Advance(0);
                coincident.Advance(2);
                Require(coincident.ActiveCount == 1 && coincident.RootVisible &&
                        coincident.Events.Count(item => item.ContainerId == launch.Id && item.Kind == 3) == 1 &&
                        coincident.Events.All(item => item.ContainerId != burst.Id || item.Kind != 3),
                    "Coincident child Destroy must create a sibling before the parent finishes.");
                coincident.Advance(3.5);
                Require(coincident.ActiveCount == 0 && coincident.Events.Count(item => item.ContainerId == burst.Id && item.Kind == 3) == 1,
                    "Coincident root completion must wait for the spawned sibling.");
            }
            var ownerWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
            ownerWorkspace.InitializeFromSnapshot(WithRootLife(1));
            Require(ownerWorkspace.EnterGraph(ownerWorkspace.GetNode(launch.Id)), "Cannot enter Owner Destroy fixture.");
            var createOwner = LiberaFxMcpTools.CreateNode(new ActiveGraphProvider(ownerWorkspace), "OwnerDestroy");
            Dispatcher.UIThread.RunJobs();
            using var createdOwner = JsonDocument.Parse(createOwner.GetAwaiter().GetResult());
            var ownerId = createdOwner.RootElement.GetProperty("id").GetGuid();
            Require(ownerWorkspace.History.Undo() && !ownerWorkspace.Nodes.Any(node => node.Id == ownerId) &&
                    ownerWorkspace.History.Redo(), "Owner Destroy must support MCP creation and Undo/Redo.");
            var ownerSignal = ownerWorkspace.CreateNode(GraphNodeKind.Signal);
            Require(ownerWorkspace.TryConnectNodes(ownerWorkspace.GetNode(ownerId), ownerSignal, out _, sourcePortId: "then"),
                "Owner Destroy must connect to a signal chain.");
            ownerWorkspace.NavigateToGraph(0);
            Require(ownerWorkspace.EnterGraph(ownerWorkspace.GetNode(burst.Id)), "Cannot enter sibling fixture.");
            var siblingOwner = ownerWorkspace.CreateNode(new GraphNodeKind("OwnerDestroy"));
            ownerWorkspace.NavigateToGraph(0);
            using var ownerFile = new MemoryStream();
            files.SaveAsync(ownerFile, ownerWorkspace).GetAwaiter().GetResult();
            ownerFile.Position = 0;
            var ownerGraph = files.LoadAsync(ownerFile).GetAwaiter().GetResult().Graph;
            var ownerPackage = SpawnableGroupPackage.Compile(ownerGraph);
            Require(ownerPackage.Templates.Single(item => item.ContainerId == launch.Id).Lifecycle!.OwnerDestroyNodeId == ownerId &&
                    ownerPackage.Templates.Single(item => item.ContainerId == burst.Id).Lifecycle!.OwnerDestroyNodeId == siblingOwner.Id,
                "Owner Destroy identities must survive persistence and compilation.");
            using (var requested = new NativeSpawnableSession(ownerPackage, library))
            {
                requested.Advance(0);
                requested.Advance(1);
                Require(requested.ActiveCount == 1 && requested.RootVisible, "Owner end request must not stop the child.");
                requested.Advance(2);
                Require(requested.ActiveCount == 1 && requested.RootVisible, "A sibling spawned while draining must receive the request.");
                requested.Advance(3.5);
                Require(requested.ActiveCount == 0, "Owner requests must preserve natural child destruction.");
            }
            var coincidentOwnerGraph = ownerGraph with
            {
                Nodes = ownerGraph.Nodes.Select(node => node.Id == rootLifeId ? node with
                {
                    Parameters = [new("seconds", "float", JsonSerializer.SerializeToElement(2d))]
                } : node).ToArray()
            };
            using (var requestedAtExpiry = new NativeSpawnableSession(SpawnableGroupPackage.Compile(coincidentOwnerGraph), library))
            {
                requestedAtExpiry.Advance(0);
                requestedAtExpiry.Advance(2);
                Require(requestedAtExpiry.ActiveCount == 1 && requestedAtExpiry.RootVisible &&
                        requestedAtExpiry.Events.Any(item => item.ContainerId == launch.Id && item.Kind == 3),
                    "Coincident child Destroy must precede the sibling's owner request.");
                requestedAtExpiry.Advance(3.5);
                Require(requestedAtExpiry.ActiveCount == 0, "The sibling must still expire naturally.");
            }
            var ownerNode = ownerGraph.Nodes.Single(node => node.Id == launch.Id).ChildGraph!.Nodes.Single(node => node.Id == ownerId);
            Reject(() => SpawnableGroupPackage.Compile(ownerGraph with
            {
                Nodes = ownerGraph.Nodes.Concat([ownerNode with { Id = Guid.NewGuid() }]).ToArray()
            }), "root Owner Destroy");
            Reject(() => SpawnableGroupPackage.Compile(ownerGraph with
            {
                Nodes = ownerGraph.Nodes.Select(node => node.Id == launch.Id ? node with
                {
                    ChildGraph = node.ChildGraph! with
                    {
                        Nodes = node.ChildGraph.Nodes.Concat([ownerNode with { Id = Guid.NewGuid() }]).ToArray()
                    }
                } : node).ToArray()
            }), "duplicate Owner Destroy");
            Reject(() => SpawnableGroupPackage.Compile(WithRootLife(-1)), "negative root Life Time");
            Reject(() => SpawnableGroupPackage.Compile(rootGraph with
            {
                Nodes = rootGraph.Nodes.Concat([rootLife with { Id = Guid.NewGuid() }]).ToArray()
            }), "multiple root Life Times");
            var input = rootWorkspace.CreateNode(GraphNodeKind.Float);
            Require(rootWorkspace.TryConnectNodes(input, rootWorkspace.GetNode(rootLifeId), out _, "seconds"),
                "Root Life Time must accept an authored input.");
            var inputGraph = rootWorkspace.CreateSnapshot();
            inputGraph = inputGraph with
            {
                Nodes = inputGraph.Nodes.Select(node => node.Id == input.Id ? node with
                {
                    Parameters = [new("value", "float", JsonSerializer.SerializeToElement(3d))]
                } : node).ToArray()
            };
            using var inputSession = new NativeSpawnableSession(SpawnableGroupPackage.Compile(inputGraph), library);
            inputSession.Advance(2);
            Require(inputSession.RootVisible, "Connected root Life Time was not captured.");
            inputSession.Advance(4);
            Require(!inputSession.RootVisible && inputSession.ReadRoot(rootTime.Id).Lanes[0] == 3,
                "Connected root Life Time did not expire at its captured value.");
        }
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
        var createRemaining = LiberaFxMcpTools.CreateNode(new ActiveGraphProvider(ageWorkspace), "InstanceRemaining");
        Dispatcher.UIThread.RunJobs();
        using var createdRemaining = JsonDocument.Parse(createRemaining.GetAwaiter().GetResult());
        var remainingId = createdRemaining.RootElement.GetProperty("id").GetGuid();
        var createNormalized = LiberaFxMcpTools.CreateNode(new ActiveGraphProvider(ageWorkspace), "InstanceNormalizedAge");
        Dispatcher.UIThread.RunJobs();
        using var createdNormalized = JsonDocument.Parse(createNormalized.GetAwaiter().GetResult());
        var normalizedId = createdNormalized.RootElement.GetProperty("id").GetGuid();
        Require(ageWorkspace.History.Undo() && !ageWorkspace.Nodes.Any(node => node.Id == normalizedId) &&
                ageWorkspace.History.Redo(), "MCP normalized age must support Undo/Redo.");
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
            Near(ageSession.Read(launch.Id, instance.Instance, remainingId).Lanes, [2], "Remaining at start");
            Near(ageSession.Read(launch.Id, instance.Instance, normalizedId).Lanes, [0], "Normalized age at start");
            ageSession.Advance(1);
            Near(ageSession.Read(launch.Id, instance.Instance, ageId).Lanes, [1], "Instance Age after one second");
            Near(ageSession.Read(launch.Id, instance.Instance, remainingId).Lanes, [1], "Remaining after one second");
            Near(ageSession.Read(launch.Id, instance.Instance, normalizedId).Lanes, [0.5], "Normalized age after one second");
        }
        var invalidAge = new GraphWorkspaceViewModel(new UndoRedoManager());
        var rootAge = invalidAge.CreateNode(new GraphNodeKind("InstanceAge"));
        using (var ageRoot = new NativeSpawnableSession(SpawnableGroupPackage.Compile(invalidAge.CreateSnapshot()), library))
        {
            ageRoot.Advance(1);
            Require(ageRoot.ReadRoot(rootAge.Id).Lanes[0] == 1, "Root Instance Age must use effect elapsed time.");
        }
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
