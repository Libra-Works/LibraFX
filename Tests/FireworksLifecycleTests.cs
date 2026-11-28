using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Mcp;
using LiberaFx.Services.Preview;
using LiberaFx.Services.Project;
using LiberaFx.Services.Samples;
using LiberaFx.ViewModels.Graph;
using LiberaFx.Services.Compilation;

internal static class FireworksLifecycleTests
{
    public static void Run(string output, bool fixturesOnly = false)
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
        var binaryPackage = SpawnablePackageBinaryTests.Verify(package);
        File.WriteAllBytes(Path.Combine(output, "Fireworks.lfxp"), binaryPackage);
        File.WriteAllBytes(Path.Combine(output, "Fireworks.persistent.lfxp"),
            SpawnableGroupPackageBinary.Serialize(package with { AutoDestroyWhenIdle = false }));
        WriteCustomSpawnableFixture(output, graph, launch);
        using (var render = JsonDocument.Parse(package.Templates.Single(template => template.ContainerId == launch.Id).RenderConfiguration))
            Require(render.RootElement.GetProperty("nodes").GetArrayLength() == 1, "Launch render binding missing.");
        using (var render = JsonDocument.Parse(package.Templates.Single(template => template.ContainerId == burst.Id).RenderConfiguration))
            Require(render.RootElement.GetProperty("nodes").GetArrayLength() == FireworksSample.BurstParticleCount, "Burst render binding count incorrect.");
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
        var nestedTemplateId = Guid.NewGuid();
        var nested = graph with { Nodes = graph.Nodes.Select(node => node.Id == launch.Id ? node with
        {
            ChildGraph = node.ChildGraph! with { Nodes = node.ChildGraph.Nodes.Concat([
                burst with { Id = nestedTemplateId, ChildGraph = new GraphSnapshot([], []) }]).ToArray() }
        } : node).ToArray() };
        var nestedPackage = SpawnableGroupPackage.Compile(nested);
        Require(nestedPackage.Templates.Any(template => template.ContainerId == nestedTemplateId &&
            template.ParentContainerId == launch.Id),
            "Nested Spawnable compilation must retain its Launch parent template.");
        Reject(() => SpawnableGroupPackage.Compile(ChangeLife(graph, launch.Id, -1)), "negative Life Time");
        if (fixturesOnly)
        {
            Console.WriteLine("Fireworks compiler fixtures generated; native lifecycle checks were not run.");
            return;
        }
        var library = Environment.GetEnvironmentVariable("LIBERAFX_NATIVE_TEST_LIBRARY")
            ?? throw new InvalidOperationException("Actual Fireworks execution requires LIBERAFX_NATIVE_TEST_LIBRARY.");
        var unusedStart = GraphDefaults.CreateEffect();
        Require(VfxProgram.Compile(unusedStart).Instructions.Count == 0,
            "An unwired default OnStart must not force native preview or break standalone conversion.");
        var connectedStart = new GraphWorkspaceViewModel(new UndoRedoManager());
        connectedStart.InitializeFromSnapshot(unusedStart);
        var startDelay = connectedStart.CreateNode(new GraphNodeKind("Delay"));
        Require(connectedStart.TryConnectNodes(connectedStart.Nodes.Single(node => node.Definition.Kind.Value == "OnStart"),
            startDelay, out _, "signal", "then"),
            "A connected OnStart must still select actual lifecycle dispatch.");
        Reject(() => VfxProgram.Compile(connectedStart.CreateSnapshot()), "connected OnStart without a lifecycle package");
        VerifyLiveInputs(library, output);
        VerifyExtendedInputs(library);
        VerifySignalInputsAndOutputs(library);
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
        var clockFixture = Path.Combine(output, "clock-fixture.lfxp");
        File.WriteAllBytes(clockFixture, EffectBinary.Compile(standaloneClocks.CreateSnapshot()));
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
        var earlySequence = infiniteOrderWorkspace.CreateNode(GraphNodeKind.Sequence);
        infiniteOrderWorkspace.DisconnectConnection(infiniteOrderWorkspace.Connections.Single(connection =>
            connection.Source.Id == startId && connection.Target.Id == launch.Id));
        Require(infiniteOrderWorkspace.TryConnectNodes(infiniteOrderWorkspace.GetNode(startId),
                    earlySequence, out _, "signal", "then") &&
                infiniteOrderWorkspace.TryConnectNodes(earlySequence,
                    infiniteOrderWorkspace.GetNode(launch.Id), out _, "spawn", "then_0") &&
                infiniteOrderWorkspace.TryConnectNodes(earlySequence,
                    infiniteOrderWorkspace.GetNode(earlyGroupId), out _, "spawn", "then_1"),
            "Cannot connect the early-expiring sibling.");
        var infiniteOrderGraph = ChangeLife(infiniteOrderWorkspace.CreateSnapshot(), earlyGroupId, 1) with
        { AutoDestroyWhenIdle = false };
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
        var rootLife = rootWorkspace.CreateSnapshot().Nodes.Single(node => node.Id == rootLifeId);
        Require(rootWorkspace.GetNode(rootLifeId).HasDiagnostics &&
                rootWorkspace.History.Undo() && !rootWorkspace.Nodes.Any(node => node.Id == rootLifeId) &&
                rootWorkspace.History.Redo() && rootWorkspace.GetNode(rootLifeId).HasDiagnostics,
            "Root Life Time must remain visible as an authoring diagnostic through Undo/Redo.");
        Reject(() => SpawnableGroupPackage.Compile(rootWorkspace.CreateSnapshot()), "root Life Time");
        rootWorkspace.DeleteNode(rootWorkspace.GetNode(rootLifeId));
        var rootTime = rootWorkspace.CreateNode(GraphNodeKind.Time);
        var rootNormalized = rootWorkspace.CreateNode(new GraphNodeKind("InstanceNormalizedAge"));
        GraphSnapshot rootGraph;
        SpawnableGroupPackage rootPackage;
        using (var rootFile = new MemoryStream())
        {
            files.SaveAsync(rootFile, rootWorkspace).GetAwaiter().GetResult();
            rootFile.Position = 0;
            rootGraph = files.LoadAsync(rootFile).GetAwaiter().GetResult().Graph;
            rootPackage = SpawnableGroupPackage.Compile(rootGraph);
            Require(rootPackage.AutoDestroyWhenIdle && rootPackage.Templates.Count == 0,
                "A root without Spawnable templates must remain infinite even when auto-destroy is enabled.");
            using var root = new NativeSpawnableSession(rootPackage, library);
            root.Advance(0);
            Require(root.RootVisible && root.ReadRoot(rootTime.Id).Lanes[0] == 0, "Root must start at zero.");
            Require(root.ReadRoot(rootNormalized.Id).Lanes[0] == 0, "An infinite root has zero normalized age.");
            root.Advance(1);
            Require(root.RootVisible && root.ReadRoot(rootTime.Id).Lanes[0] == 1 &&
                    root.ReadRoot(rootNormalized.Id).Lanes[0] == 0,
                "An infinite root must keep advancing without a normalized lifetime.");
            root.Advance(3);
            Require(root.RootVisible && root.ReadRoot(rootTime.Id).Lanes[0] == 3,
                "An effect with no Spawnables must remain infinite.");
        }
        var idleWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        idleWorkspace.CreateNode(GraphNodeKind.SpawnableGroup);
        using (var idleRoot = new NativeSpawnableSession(
            SpawnableGroupPackage.Compile(idleWorkspace.CreateSnapshot()), library))
        {
            idleRoot.Advance(0);
            Require(idleRoot.ActiveCount == 0 && !idleRoot.RootVisible,
                "An idle effect with Spawnable templates must finish once no work remains.");
            idleRoot.Advance(0.1);
            Require(!idleRoot.RootVisible, "An idle root without work must finish.");
        }
        var settingWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        Require(settingWorkspace.AutoDestroyWhenIdle, "New effects must default Auto Destroy When Idle on.");
        settingWorkspace.AutoDestroyWhenIdle = false;
        Require(!settingWorkspace.AutoDestroyWhenIdle && settingWorkspace.History.Undo() &&
                settingWorkspace.AutoDestroyWhenIdle && settingWorkspace.History.Redo() &&
                !settingWorkspace.AutoDestroyWhenIdle,
            "Auto Destroy When Idle must be undoable.");
        using (var settingFile = new MemoryStream())
        {
            files.SaveAsync(settingFile, settingWorkspace).GetAwaiter().GetResult();
            settingFile.Position = 0;
            Require(!files.LoadAsync(settingFile).GetAwaiter().GetResult().Graph.AutoDestroyWhenIdle,
                "Project persistence lost Auto Destroy When Idle.");
            var legacyWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
            using var legacyFile = new MemoryStream();
            files.SaveAsync(legacyFile, legacyWorkspace).GetAwaiter().GetResult();
            var legacySettings = JsonNode.Parse(legacyFile.ToArray())!.AsObject();
            legacySettings["FormatVersion"] = 10;
            legacySettings.Remove("AutoDestroyWhenIdle");
            using var oldSettings = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(legacySettings.ToJsonString()));
            Require(files.LoadAsync(oldSettings).GetAwaiter().GetResult().Graph.AutoDestroyWhenIdle,
                "Pre-setting project formats must default Auto Destroy When Idle on.");
        }
        Reject(() => SpawnableGroupPackage.Compile(rootGraph with
        {
            Nodes = rootGraph.Nodes.Concat([rootLife]).ToArray()
        }), "root Life Time");
        Reject(() => SpawnableGroupPackage.Compile(rootGraph with
        {
            Nodes = rootGraph.Nodes.Concat([rootLife, rootLife with { Id = Guid.NewGuid() }]).ToArray()
        }), "multiple root Life Times");
        using (var stopped = new NativeSpawnableSession(rootPackage, library))
        {
            stopped.Advance(0);
            stopped.Stop();
            Require(!stopped.RootVisible, "Manual stop must hide the root.");
        }
        var curveWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        curveWorkspace.CreateNode(new GraphNodeKind("OnStart"));
        var curveGroup = curveWorkspace.CreateNode(GraphNodeKind.SpawnableGroup);
        Require(curveWorkspace.EnterGraph(curveGroup), "Cannot edit curve-lifetime fixture.");
        curveWorkspace.CreateNode(new GraphNodeKind("LifeTime"));
        var longCurve = curveWorkspace.CreateNode(GraphNodeKind.FloatFCurve);
        curveWorkspace.SetCurve(longCurve, new FCurveSnapshot([
                new(Guid.NewGuid(), 0, 0, 0, 0, CurveInterpolation.Linear),
                new(Guid.NewGuid(), 120, 1, 0, 0, CurveInterpolation.Linear)
            ]), "Long curve");
        curveWorkspace.NavigateToGraph(0);
        var curveStart = curveWorkspace.Nodes.Single(node => node.Definition.Kind.Value == "OnStart");
        Require(curveWorkspace.TryConnectNodes(curveStart, curveWorkspace.GetNode(curveGroup.Id),
            out _, "spawn", "then"), "Cannot start curve-lifetime fixture.");
        using (var independentCurve = new NativeSpawnableSession(
            SpawnableGroupPackage.Compile(curveWorkspace.CreateSnapshot()), library))
        {
            independentCurve.Advance(3);
            Require(!independentCurve.RootVisible, "FCurve duration must not retain a naturally completed effect.");
        }
        {
            GraphSnapshot WithAutoDestroy(bool enabled) => graph with { AutoDestroyWhenIdle = enabled };
            using (var parentWaits = new NativeSpawnableSession(SpawnableGroupPackage.Compile(WithAutoDestroy(true)), library))
            {
                parentWaits.Advance(0);
                parentWaits.Advance(2);
                Require(parentWaits.ActiveCount == 1 && parentWaits.RootVisible &&
                        parentWaits.Events.Count(item => item.ContainerId == launch.Id && item.Kind == 3) == 1,
                    "Idle root shutdown must wait while child Destroy spawns a sibling.");
                parentWaits.Advance(3.5);
                Require(parentWaits.ActiveCount == 0 &&
                        parentWaits.Events.Count(item => item.ContainerId == burst.Id && item.Kind == 3) == 1,
                    "The parent must wait until the sibling naturally expires.");
            }
            using (var stoppedWhileDraining = new NativeSpawnableSession(SpawnableGroupPackage.Compile(WithAutoDestroy(true)), library))
            {
                stoppedWhileDraining.Advance(1);
                stoppedWhileDraining.Stop();
                Require(!stoppedWhileDraining.RootVisible && stoppedWhileDraining.ActiveCount == 0 &&
                        stoppedWhileDraining.Events.All(item => item.Kind != 3),
                    "Explicit stop during the parent's drain must not emit a natural child Destroy.");
            }
            var persistentWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
            persistentWorkspace.InitializeFromSnapshot(graph);
            var persistentSpawner = persistentWorkspace.CreateNode(GraphNodeKind.SpawnableGroup);
            Require(persistentWorkspace.EnterGraph(persistentSpawner),
                "Cannot edit the persistent root-signal target.");
            var persistentLifetime = persistentWorkspace.CreateNode(new GraphNodeKind("LifeTime"));
            persistentWorkspace.SetParameter(persistentLifetime, "seconds", JsonSerializer.SerializeToElement(2d));
            persistentWorkspace.NavigateToGraph(0);
            persistentSpawner = persistentWorkspace.GetNode(persistentSpawner.Id);
            var gameSignal = persistentWorkspace.CreateNode(GraphNodeKind.Signal);
            Require(persistentWorkspace.TryConnectNodes(gameSignal,
                persistentSpawner, out _, targetPortId: "spawn"),
                "Cannot connect a persistent effect's game signal.");
            using (var persistentRoot = new NativeSpawnableSession(
                    SpawnableGroupPackage.Compile(persistentWorkspace.CreateSnapshot() with
                    { AutoDestroyWhenIdle = false }), library))
            {
                persistentRoot.Advance(0);
                Require(persistentRoot.RootVisible && persistentRoot.ActiveCount == 1,
                    "A persistent root must accept game signals while idle.");
                persistentRoot.DispatchSignal(gameSignal.Id);
                Require(persistentRoot.ActiveCount == 2, "A persistent root must accept repeated game signals.");
                persistentRoot.End();
                var rejectedSignal = false;
                try { persistentRoot.DispatchSignal(gameSignal.Id); }
                catch (InvalidOperationException) { rejectedSignal = true; }
                Require(rejectedSignal, "An explicit natural end must reject new external root signals.");
                persistentRoot.Advance(3.5);
                Require(persistentRoot.ActiveCount == 0 && !persistentRoot.RootVisible,
                    $"An explicitly ended root must wait for all spawned children before finishing " +
                    $"(visible={persistentRoot.RootVisible}, active={persistentRoot.ActiveCount}, " +
                    $"events={string.Join(',', persistentRoot.Events.Select(item => $"{item.Kind}@{item.Time}"))}).");
                persistentRoot.Advance(3.6);
                Require(!persistentRoot.RootVisible, "A naturally finished root must become invisible after its final frame.");
            }
            var generalDelayWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
            generalDelayWorkspace.InitializeFromSnapshot(WithAutoDestroy(true));
            var rootStart = generalDelayWorkspace.Nodes.Single(node => node.Definition.Kind.Value == "OnStart");
            generalDelayWorkspace.DisconnectConnection(generalDelayWorkspace.Connections.Single(edge =>
                edge.Source.Id == rootStart.Id && edge.Target.Id == launch.Id && edge.TargetPortId == "spawn"));
            var firstDelay = generalDelayWorkspace.CreateNode(new GraphNodeKind("Delay"));
            var secondDelay = generalDelayWorkspace.CreateNode(new GraphNodeKind("Delay"));
            generalDelayWorkspace.SetParameter(firstDelay, "seconds", JsonSerializer.SerializeToElement(2d));
            Require(generalDelayWorkspace.TryConnectNodes(rootStart, firstDelay, out _, "signal", "then") &&
                    generalDelayWorkspace.TryConnectNodes(firstDelay, secondDelay, out _, "signal", "then") &&
                    generalDelayWorkspace.TryConnectNodes(secondDelay, generalDelayWorkspace.GetNode(launch.Id),
                        out _, "spawn", "then"), "Cannot connect general root Delay signals.");
            var generalDelayPackage = SpawnableGroupPackage.Compile(generalDelayWorkspace.CreateSnapshot());
            Require(generalDelayPackage.Version == SpawnableGroupPackage.FormatVersion && generalDelayPackage.HasDeferredSignals,
                "General Delay requires a new package version.");
            var oldLibrary = Environment.GetEnvironmentVariable("LIBERAFX_OLD_NATIVE_TEST_LIBRARY");
            if (!string.IsNullOrEmpty(oldLibrary))
            {
                var rejected = false;
                try { using var unsupported = new NativeSpawnableSession(generalDelayPackage, oldLibrary); }
                catch (NotSupportedException) { rejected = true; }
                Require(rejected, "An older native runtime must explicitly reject general Delay packages.");
            }
            using (var generalDelay = new NativeSpawnableSession(generalDelayPackage, library))
            {
                generalDelay.Advance(1, 0.1);
                Require(generalDelay.RootVisible && generalDelay.ActiveCount == 0,
                    "A pending root Delay must retain its expired owner.");
                generalDelay.Advance(2, 0.1);
                Require(generalDelay.RootVisible && generalDelay.ActiveCount == 0,
                    "A chained Delay must retain its owner.");
                generalDelay.Advance(3, 0.1);
                var launched = generalDelay.ActiveInstances.Single(key => key.Container == launch.Id);
                Require(generalDelay.InstanceStartTime(launch.Id, launched.Instance) == 3,
                    "Chained Delay must resume the signal at the effect-local due time.");
                generalDelay.Advance(6.5, 0.1);
                Require(generalDelay.ActiveCount == 0 && generalDelay.Events.Count(item => item.Kind == 3) == 2,
                    "The parent must wait for delayed work and its spawned descendants.");
            }
            using (var stoppedGeneralDelay = new NativeSpawnableSession(generalDelayPackage, library))
            {
                stoppedGeneralDelay.Advance(1, 0.1);
                stoppedGeneralDelay.Stop();
                Require(!stoppedGeneralDelay.RootVisible && stoppedGeneralDelay.ActiveCount == 0 &&
                        stoppedGeneralDelay.Events.Count == 0, "Manual stop must cancel pending Delay signals.");
            }
            var groupedDelayWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
            groupedDelayWorkspace.InitializeFromSnapshot(WithAutoDestroy(true));
            groupedDelayWorkspace.DisconnectConnection(groupedDelayWorkspace.Connections.Single(edge =>
                edge.Source.Id == rootStart.Id && edge.Target.Id == launch.Id && edge.TargetPortId == "spawn"));
            var delayGroup = groupedDelayWorkspace.CreateNode(GraphNodeKind.GraphGroup);
            Require(groupedDelayWorkspace.EnterGraph(delayGroup), "Cannot edit grouped Delay fixture.");
            var groupedSignalInput = groupedDelayWorkspace.CreateNode(new GraphNodeKind("GroupInputSignal"));
            var groupedDelay = groupedDelayWorkspace.CreateNode(new GraphNodeKind("Delay"));
            var groupedSignalOutput = groupedDelayWorkspace.CreateNode(new GraphNodeKind("GroupOutputSignal"));
            Require(groupedDelayWorkspace.TryConnectNodes(groupedSignalInput, groupedDelay, out _, "signal") &&
                    groupedDelayWorkspace.TryConnectNodes(groupedDelay, groupedSignalOutput, out _, sourcePortId: "then"),
                "Cannot connect Delay through ordinary group boundaries.");
            groupedDelayWorkspace.NavigateToGraph(0);
            Require(groupedDelayWorkspace.TryConnectNodes(groupedDelayWorkspace.GetNode(rootStart.Id),
                        groupedDelayWorkspace.GetNode(delayGroup.Id), out _,
                        GroupBoundarySchema.InputPortId(groupedSignalInput.Id), "then") &&
                    groupedDelayWorkspace.TryConnectNodes(groupedDelayWorkspace.GetNode(delayGroup.Id),
                        groupedDelayWorkspace.GetNode(launch.Id), out _, "spawn",
                        GroupBoundarySchema.OutputPortId(groupedSignalOutput.Id)),
                "Cannot connect grouped Delay at the effect root.");
            var groupedDelayPackage = SpawnableGroupPackage.Compile(groupedDelayWorkspace.CreateSnapshot());
            Require(groupedDelayPackage.HasDeferredSignals && groupedDelayPackage.Version == SpawnableGroupPackage.FormatVersion,
                "Delay inside an ordinary group must retain its enclosing effect clock and package requirement.");
            using (var groupedDelaySession = new NativeSpawnableSession(groupedDelayPackage, library))
            {
                groupedDelaySession.Advance(0);
                Require(groupedDelaySession.ActiveCount == 0, "Grouped Delay must not run its continuation immediately.");
                groupedDelaySession.Advance(1);
                var active = groupedDelaySession.ActiveInstances.Single(key => key.Container == launch.Id);
                Require(groupedDelaySession.InstanceStartTime(launch.Id, active.Instance) == 1,
                    "Ordinary groups must not create another instance clock.");
                groupedDelaySession.Advance(5);
                Require(!groupedDelaySession.RootVisible && groupedDelaySession.ActiveCount == 0,
                    "Grouped Delay work must drain before the root finishes.");
            }
            var childDelayWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
            childDelayWorkspace.InitializeFromSnapshot(WithAutoDestroy(true));
            Require(childDelayWorkspace.EnterGraph(childDelayWorkspace.GetNode(launch.Id)), "Cannot edit child Delay fixture.");
            var childStart = childDelayWorkspace.CreateNode(new GraphNodeKind("OnStart"));
            var childDelay = childDelayWorkspace.CreateNode(new GraphNodeKind("Delay"));
            var childFollowup = childDelayWorkspace.CreateNode(new GraphNodeKind("Delay"));
            var delayedAge = childDelayWorkspace.CreateNode(new GraphNodeKind("InstanceAge"));
            var delayedGameDelta = childDelayWorkspace.CreateNode(new GraphNodeKind("DeltaTime"));
            childDelayWorkspace.SetParameter(childDelay, "seconds", JsonSerializer.SerializeToElement(3d));
            Require(childDelayWorkspace.TryConnectNodes(childStart, childDelay, out _, "signal", "then") &&
                    childDelayWorkspace.TryConnectNodes(childDelay, childFollowup, out _, "signal", "then"),
                "Cannot create a child-local Delay chain.");
            Require(childDelayWorkspace.History.Undo() && childDelayWorkspace.History.Redo(),
                "Child Delay wiring must support Undo/Redo.");
            childDelayWorkspace.NavigateToGraph(0);
            using var childDelayFile = new MemoryStream();
            files.SaveAsync(childDelayFile, childDelayWorkspace).GetAwaiter().GetResult();
            childDelayFile.Position = 0;
            var childDelayGraph = files.LoadAsync(childDelayFile).GetAwaiter().GetResult().Graph;
            var childDelayPackage = SpawnableGroupPackage.Compile(childDelayGraph);
            Require(childDelayPackage.Version == SpawnableGroupPackage.FormatVersion && childDelayPackage.HasChildDeferredSignals,
                "Child Delay must require an explicit package version.");
            var delayedTemplate = childDelayPackage.Templates.Single(item => item.ContainerId == launch.Id);
            File.WriteAllBytes(Path.Combine(output, "child-delay.lfxp"), SpawnableGroupPackageBinary.Serialize(childDelayPackage));
            File.WriteAllLines(Path.Combine(output, "child-delay.lfxp.ids"), [
                delayedTemplate.Lifecycle!.OnStartNodeId!.Value.ToString("D"),
                delayedTemplate.Lifecycle.LifeNodeId!.Value.ToString("D"),
                delayedAge.Id.ToString("D"), delayedGameDelta.Id.ToString("D"), final.Id.ToString("D"),
                launchInput.Id.ToString("D")
            ]);
            if (!string.IsNullOrEmpty(oldLibrary))
            {
                var rejected = false;
                try { using var unsupported = new NativeSpawnableSession(childDelayPackage, oldLibrary); }
                catch (NotSupportedException) { rejected = true; }
                Require(rejected, "An older native runtime must explicitly reject child Delay packages.");
            }
            using (var childDrains = new NativeSpawnableSession(childDelayPackage, library))
            {
                childDrains.Advance(2.5, 0.125);
                var active = childDrains.ActiveInstances.Single(key => key.Container == launch.Id);
                Near(childDrains.Read(launch.Id, active.Instance, delayedAge.Id).Lanes, [2.5],
                    "Child clock must continue after its lifetime deadline while Delay is pending");
                Near(childDrains.Read(launch.Id, active.Instance, delayedGameDelta.Id).Lanes, [0.125],
                    "Draining child must retain the game frame delta");
                Require(childDrains.Events.All(item => item.Kind != 3),
                    "A child must not Destroy while its Delay is pending.");
                childDrains.Advance(3, 0.25);
                Require(childDrains.ActiveInstances.Single().Container == launch.Id,
                    "The continuation's second Delay must also retain the child.");
                childDrains.Advance(4, 0.5);
                var spawned = childDrains.ActiveInstances.Single(key => key.Container == burst.Id);
                Require(childDrains.InstanceStartTime(burst.Id, spawned.Instance) == 4 &&
                        childDrains.Events.Single(item => item.Kind == 3 && item.ContainerId == launch.Id).Time == 4,
                    "Natural Destroy and its sibling Spawn must use the actual drained completion time.");
                Near(childDrains.Read(burst.Id, spawned.Instance, burstInput.Id).Lanes, [0, 0, 500],
                    "Final output capture must use the child's drained final evaluation");
                childDrains.Advance(6, 0.25);
                Require(childDrains.ActiveCount == 0 && !childDrains.RootVisible &&
                        childDrains.Events.Where(item => item.Kind == 3).Select(item => item.Time).SequenceEqual([4d, 5.5d]),
                    "The root must finish only after delayed child work and its descendants drain.");
            }
            using (var stoppedChildDelay = new NativeSpawnableSession(childDelayPackage, library))
            {
                stoppedChildDelay.Advance(2.5);
                stoppedChildDelay.Stop();
                Require(stoppedChildDelay.ActiveCount == 0 && stoppedChildDelay.Events.All(item => item.Kind != 3),
                    "Manual Stop must cancel a child's pending Delay after its deadline without natural Destroy.");
            }
            using (var zeroLifeDelay = new NativeSpawnableSession(
                SpawnableGroupPackage.Compile(ChangeLife(childDelayGraph, launch.Id, 0)), library))
            {
                zeroLifeDelay.Advance(0);
                Require(zeroLifeDelay.ActiveCount == 1 && zeroLifeDelay.Events.All(item => item.Kind != 3),
                    "A zero-lifetime child must still drain Delay reserved by OnStart.");
                zeroLifeDelay.Advance(4);
                Require(zeroLifeDelay.Events.Single(item => item.ContainerId == launch.Id && item.Kind == 3).Time == 4,
                    "Zero lifetime must not suppress or advance the delayed continuation.");
            }
            var ownerDelayWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
            ownerDelayWorkspace.InitializeFromSnapshot(childDelayGraph);
            Require(ownerDelayWorkspace.EnterGraph(ownerDelayWorkspace.GetNode(launch.Id)),
                "Cannot edit Owner Destroy Delay fixture.");
            ownerDelayWorkspace.DeleteNode(ownerDelayWorkspace.GetNode(childStart.Id));
            var delayedOwnerDestroy = ownerDelayWorkspace.CreateNode(new GraphNodeKind("OwnerDestroy"));
            Require(ownerDelayWorkspace.TryConnectNodes(delayedOwnerDestroy, ownerDelayWorkspace.GetNode(childDelay.Id),
                out _, "signal", "then"), "Cannot connect Owner Destroy to child-local Delay.");
            ownerDelayWorkspace.NavigateToGraph(0);
            using (var ownerDelay = new NativeSpawnableSession(
                SpawnableGroupPackage.Compile(ownerDelayWorkspace.CreateSnapshot()), library))
            {
                ownerDelay.Advance(2.5);
                Require(ownerDelay.ActiveCount == 1 && ownerDelay.Events.All(item => item.Kind != 3),
                    $"An Owner Destroy request must not force-stop child-local delayed work (active={ownerDelay.ActiveCount}, events={string.Join(',', ownerDelay.Events.Select(item => $"{item.Kind}@{item.Time}:{item.ContainerId}"))}).");
                ownerDelay.Advance(5);
                var ownerDelayDestroy = ownerDelay.Events.Where(item =>
                    item.ContainerId == launch.Id && item.Kind == 3).ToArray();
                Require(ownerDelayDestroy.Length == 1 && ownerDelayDestroy[0].Time == 4,
                    "Owner Destroy Delay must start at the root's idle deadline and be sent only once.");
                ownerDelay.Advance(7);
                Require(!ownerDelay.RootVisible && ownerDelay.ActiveCount == 0,
                    "Owner Destroy delayed descendants must eventually finish normally.");
            }
            var destroyDelayWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
            destroyDelayWorkspace.InitializeFromSnapshot(WithAutoDestroy(true));
            destroyDelayWorkspace.DisconnectConnection(destroyDelayWorkspace.Connections.Single(edge =>
                edge.Source.Id == rootStart.Id && edge.Target.Id == launch.Id && edge.TargetPortId == "spawn"));
            var rootDestroyDelay = destroyDelayWorkspace.CreateNode(new GraphNodeKind("OnDestroy"));
            var afterDestroyDelay = destroyDelayWorkspace.CreateNode(new GraphNodeKind("Delay"));
            destroyDelayWorkspace.SetParameter(afterDestroyDelay, "seconds", JsonSerializer.SerializeToElement(2d));
            Require(destroyDelayWorkspace.TryConnectNodes(rootDestroyDelay, afterDestroyDelay, out _, "signal", "then") &&
                    destroyDelayWorkspace.TryConnectNodes(afterDestroyDelay, destroyDelayWorkspace.GetNode(launch.Id),
                        out _, "spawn", "then"), "Cannot connect On Destroy through Delay.");
            var destroyDelayGraph = destroyDelayWorkspace.CreateSnapshot();
            using (var destroyDelay = new NativeSpawnableSession(SpawnableGroupPackage.Compile(destroyDelayGraph), library))
            {
                destroyDelay.Advance(1, 0.5);
                Require(destroyDelay.RootVisible && destroyDelay.ActiveCount == 0,
                    $"Delay created by On Destroy must retain the root " +
                    $"(visible={destroyDelay.RootVisible}, active={destroyDelay.ActiveCount}, " +
                    $"events={string.Join(',', destroyDelay.Events.Select(item => $"{item.Kind}@{item.Time}"))}).");
                destroyDelay.Advance(2, 0.25);
                var launched = destroyDelay.ActiveInstances.Single(key => key.Container == launch.Id);
                Require(destroyDelay.InstanceStartTime(launch.Id, launched.Instance) == 2,
                    "On Destroy continuation must use effect-local time even when frame delta changes.");
                destroyDelay.Advance(5.5, 0.5);
                Require(destroyDelay.Events.Count(item => item.Kind == 3) == 2,
                    "On Destroy must not be sent again after its delayed descendants finish.");
            }
            var zeroGeneralGraph = destroyDelayGraph with
            {
                Nodes = destroyDelayGraph.Nodes.Select(node => node.Id == afterDestroyDelay.Id ? node with
                {
                    Parameters = [new("seconds", "float", JsonSerializer.SerializeToElement(0d))]
                } : node).ToArray()
            };
            using (var zeroGeneral = new NativeSpawnableSession(SpawnableGroupPackage.Compile(zeroGeneralGraph), library))
            {
                zeroGeneral.Advance(1);
                var launched = zeroGeneral.ActiveInstances.Single(key => key.Container == launch.Id);
                Require(zeroGeneral.InstanceStartTime(launch.Id, launched.Instance) == 0,
                    "Zero Delay must resume at the same local time.");
            }
            var negativeGeneralGraph = destroyDelayGraph with
            {
                Nodes = destroyDelayGraph.Nodes.Select(node => node.Id == afterDestroyDelay.Id ? node with
                {
                    Parameters = [new("seconds", "float", JsonSerializer.SerializeToElement(-1d))]
                } : node).ToArray()
            };
            using (var negativeGeneral = new NativeSpawnableSession(SpawnableGroupPackage.Compile(negativeGeneralGraph), library))
            {
                var rejected = false;
                try { negativeGeneral.Advance(1); }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "Negative general Delay must be rejected by the runtime.");
            }
            var delayWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
            delayWorkspace.InitializeFromSnapshot(WithAutoDestroy(true));
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
            var delayEffectAge = delayWorkspace.CreateNode(new GraphNodeKind("EffectAge"));
            var delayFrameDelta = delayWorkspace.CreateNode(new GraphNodeKind("DeltaTime"));
            Require(delayWorkspace.EnterGraph(delayWorkspace.GetNode(burst.Id)), "Cannot enter delayed sibling.");
            var delayedParentInput = delayWorkspace.CreateNode(new GraphNodeKind("SpawnInputFloat"));
            var delayedEffectAge = delayWorkspace.CreateNode(new GraphNodeKind("SpawnInputFloat"));
            var delayedFrameDelta = delayWorkspace.CreateNode(new GraphNodeKind("SpawnInputFloat"));
            delayWorkspace.NavigateToGraph(0);
            Require(delayWorkspace.TryConnectNodes(delayWorkspace.GetNode(delayRootTime.Id), delayWorkspace.GetNode(burst.Id),
                        out _, SpawnableGroupSchema.PortId(delayedParentInput.Id)) &&
                    delayWorkspace.TryConnectNodes(delayWorkspace.GetNode(delayEffectAge.Id), delayWorkspace.GetNode(burst.Id),
                        out _, SpawnableGroupSchema.PortId(delayedEffectAge.Id), "value") &&
                    delayWorkspace.TryConnectNodes(delayWorkspace.GetNode(delayFrameDelta.Id), delayWorkspace.GetNode(burst.Id),
                        out _, SpawnableGroupSchema.PortId(delayedFrameDelta.Id), "value"),
                "Cannot capture parent clocks at delayed spawn.");
            using var delayFile = new MemoryStream();
            files.SaveAsync(delayFile, delayWorkspace).GetAwaiter().GetResult();
            delayFile.Position = 0;
            var delayedGraph = files.LoadAsync(delayFile).GetAwaiter().GetResult().Graph;
            var delayedPackage = SpawnableGroupPackage.Compile(delayedGraph);
            Require(delayedPackage.Version == SpawnableGroupPackage.FormatVersion && delayedPackage.DestroyRoutes.Single().DelayNodeId == delayId,
                "Delayed route identity must survive persistence and compilation.");
            using (var delayed = new NativeSpawnableSession(delayedPackage, library))
            {
                delayed.Advance(2, 0.1);
                Require(delayed.ActiveCount == 0 && delayed.RootVisible &&
                        delayed.Events.All(item => item.ContainerId != burst.Id || item.Kind != 1),
                    "Pending Delay must keep the parent alive without spawning early.");
                delayed.Advance(3, 0.2);
                var delayedChild = delayed.ActiveInstances.Single();
                Require(delayedChild.Container == burst.Id && delayed.InstanceStartTime(burst.Id, delayedChild.Instance) == 3,
                    "Delay must spawn the sibling at its scheduled time.");
                Near(delayed.Read(burst.Id, delayedChild.Instance, burstInput.Id).Lanes, [0, 0, 260],
                    "Delay must preserve the expired child's value");
                Near(delayed.Read(burst.Id, delayedChild.Instance, delayedParentInput.Id).Lanes, [3],
                    "Delay must capture the parent value at spawn");
                Near(delayed.Read(burst.Id, delayedChild.Instance, delayedEffectAge.Id).Lanes, [3],
                    "Delay must capture effect age at spawn");
                Near(delayed.Read(burst.Id, delayedChild.Instance, delayedFrameDelta.Id).Lanes, [0.2],
                    "Delay must capture the game frame delta at spawn");
                delayed.Advance(4.5);
                Require(delayed.ActiveCount == 0 && delayed.Events.Count(item => item.Kind == 3) == 2,
                    "The parent must wait until the delayed child naturally expires.");
            }
            using (var scaledDelay = new NativeSpawnableSession(delayedPackage, library))
            {
                scaledDelay.Advance(0, 0.5);
                for (var frame = 1; frame <= 11; frame++)
                    scaledDelay.Advance(frame * 0.5 * 0.5, 0.5);
                Require(scaledDelay.Events.All(item => item.ContainerId != burst.Id || item.Kind != 1),
                    "A slower effect must not use the unscaled game clock for Delay.");
                scaledDelay.Advance(12 * 0.5 * 0.5, 0.5);
                var spawned = scaledDelay.ActiveInstances.Single(key => key.Container == burst.Id);
                Require(scaledDelay.InstanceStartTime(burst.Id, spawned.Instance) == 3,
                    "Delay must use the owning effect's scaled elapsed time.");
                Near(scaledDelay.Read(burst.Id, spawned.Instance, delayedFrameDelta.Id).Lanes, [0.5],
                    "Game frame delta must not be scaled with effect time");
            }
            var fanoutWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
            fanoutWorkspace.InitializeFromSnapshot(delayedGraph);
            var secondTarget = fanoutWorkspace.CreateNode(GraphNodeKind.SpawnableGroup);
            var vectorSource = fanoutWorkspace.CreateNode(GraphNodeKind.FloatX3);
            fanoutWorkspace.SetParameter(vectorSource, "value", JsonSerializer.SerializeToElement(new[] { 1d, 6d, 9d }));
            var breakSource = fanoutWorkspace.CreateNode(GraphNodeKind.BreakVector3);
            Require(fanoutWorkspace.TryConnectNodes(vectorSource, breakSource, out _, "a"),
                "Cannot prepare a named component capture.");
            Require(fanoutWorkspace.EnterGraph(secondTarget), "Cannot edit the second delayed sibling.");
            fanoutWorkspace.CreateNode(new GraphNodeKind("LifeTime"));
            var secondInput = fanoutWorkspace.CreateNode(new GraphNodeKind("SpawnInputFloat"));
            var componentInput = fanoutWorkspace.CreateNode(new GraphNodeKind("SpawnInputFloat"));
            fanoutWorkspace.NavigateToGraph(0);
            var delayedSequence = fanoutWorkspace.CreateNode(GraphNodeKind.Sequence);
            fanoutWorkspace.DisconnectConnection(fanoutWorkspace.Connections.Single(connection =>
                connection.Source.Id == delayId && connection.Target.Id == burst.Id && connection.TargetPortId == "spawn"));
            Require(fanoutWorkspace.TryConnectNodes(fanoutWorkspace.GetNode(delayId), delayedSequence, out _, "signal", "then") &&
                    fanoutWorkspace.TryConnectNodes(delayedSequence, fanoutWorkspace.GetNode(burst.Id), out _, "spawn", "then_0") &&
                    fanoutWorkspace.TryConnectNodes(delayedSequence, fanoutWorkspace.GetNode(secondTarget.Id), out _, "spawn", "then_1") &&
                    fanoutWorkspace.TryConnectNodes(fanoutWorkspace.GetNode(delayRootTime.Id), fanoutWorkspace.GetNode(secondTarget.Id), out _,
                        SpawnableGroupSchema.PortId(secondInput.Id)) &&
                    fanoutWorkspace.TryConnectNodes(fanoutWorkspace.GetNode(breakSource.Id), fanoutWorkspace.GetNode(secondTarget.Id), out _,
                        SpawnableGroupSchema.PortId(componentInput.Id), "y"),
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
                Near(fanout.Read(secondTarget.Id, second.Instance, componentInput.Id).Lanes, [6],
                    "Delayed capture must preserve the selected BreakVector output");
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
            var infiniteDelayedGraph = delayedGraph with { AutoDestroyWhenIdle = false };
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
            {
                var rejected = false;
                try
                {
                    invalidDelay.Advance(2);
                }
                catch (NativeHostException error) when (error.Result == 1)
                {
                    rejected = true;
                }
                Require(rejected, "The native package host must reject a negative Delay as invalid argument.");
            }
            using (var childExpiresFirst = new NativeSpawnableSession(SpawnableGroupPackage.Compile(WithAutoDestroy(true)), library))
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
            rootDestroyWorkspace.InitializeFromSnapshot(WithAutoDestroy(true));
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
            Require(rootDestroyPackage.OnDestroyNodeId == rootDestroyId &&
                    rootDestroyPackage.Version == SpawnableGroupPackage.FormatVersion,
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
            using (var stepped = new NativeSpawnableSession(SpawnableGroupPackage.Compile(WithAutoDestroy(true)), library))
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
            using (var coincident = new NativeSpawnableSession(SpawnableGroupPackage.Compile(WithAutoDestroy(true)), library))
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
            ownerWorkspace.InitializeFromSnapshot(WithAutoDestroy(true));
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
            var coincidentOwnerGraph = ownerGraph with { AutoDestroyWhenIdle = false };
            using (var requestedAtExpiry = new NativeSpawnableSession(SpawnableGroupPackage.Compile(coincidentOwnerGraph), library))
            {
                requestedAtExpiry.Advance(0);
                requestedAtExpiry.Advance(2);
                requestedAtExpiry.End();
                Require(requestedAtExpiry.ActiveCount == 1 && requestedAtExpiry.RootVisible &&
                        requestedAtExpiry.Events.Any(item => item.ContainerId == launch.Id && item.Kind == 3),
                    "A child Destroy at explicit root end must create the sibling before owner notification.");
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
                foreach (var renderNode in render.RootElement.GetProperty("nodes").EnumerateArray())
                {
                    var binding = renderNode.GetProperty("inputs").EnumerateArray().Single(input => input.GetProperty("id").GetString() == "position");
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
        Console.WriteLine($"Fireworks authoring/persistence/package and actual native execution passed. Sample: {sample}");
        Console.WriteLine("No pixel validation claimed here.");
    }
    private static void WriteCustomSpawnableFixture(string output, GraphSnapshot graph, GraphNodeSnapshot launch)
    {
        var definitions = GraphNodeDefinition.LoadDirectory(
            LiberaFx.Tests.CustomNodeCompilationTests.FindExampleDirectory(), out var warnings);
        Require(warnings.Count == 0, "Custom node fixtures could not be loaded.");
        var scale = definitions.Single(definition => definition.TypeName == "CustomScale");
        GraphNodeSnapshot Node(string id) => new(Guid.Parse(id), scale.Kind, scale.Name, 0, 0,
            scale.PortType, scale.PortCategory);

        var rootNode = Node("a1000000-0000-0000-0000-000000000001");
        var childNode = Node("a2000000-0000-0000-0000-000000000002");
        var customGraph = graph with
        {
            Nodes = graph.Nodes.Select(node => node.Id == launch.Id
                ? node with { ChildGraph = node.ChildGraph! with { Nodes = [.. node.ChildGraph!.Nodes, childNode] } }
                : node).Append(rootNode).ToArray()
        };
        var package = SpawnableGroupPackage.Compile(customGraph);
        File.WriteAllBytes(Path.Combine(output, "CustomSpawnable.lfxp"), SpawnableGroupPackageBinary.Serialize(package));
    }

    private static void VerifyLiveInputs(string library, string output)
    {
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        var start = workspace.CreateNode(new GraphNodeKind("OnStart"));
        var age = workspace.CreateNode(new GraphNodeKind("EffectAge"));
        var duration = workspace.CreateNode(GraphNodeKind.Add);
        workspace.SetParameter(duration, "b", JsonSerializer.SerializeToElement(2d));
        Require(workspace.TryConnectNodes(age, duration, out _, "a", "value"), "Cannot connect Live duration.");
        var sourceGroup = workspace.CreateNode(GraphNodeKind.GraphGroup);
        Require(workspace.EnterGraph(sourceGroup), "Cannot edit Live source group.");
        var groupedAge = workspace.CreateNode(new GraphNodeKind("EffectAge"));
        var broadcast = workspace.CreateNode(new GraphNodeKind("ScalarToVector3"));
        var offset = workspace.CreateNode(new GraphNodeKind("AddVector3"));
        workspace.SetParameter(offset, "b", JsonSerializer.SerializeToElement(new[] { 10d, 20d, 30d }));
        var outputVector = workspace.CreateNode(new GraphNodeKind("GroupOutputVector3"));
        Require(workspace.TryConnectNodes(groupedAge, broadcast, out _, "a", "value") &&
                workspace.TryConnectNodes(broadcast, offset, out _, "a") &&
                workspace.TryConnectNodes(offset, outputVector, out _), "Cannot connect grouped Live vector.");
        workspace.NavigateToGraph(0);
        var components = workspace.CreateNode(new GraphNodeKind("BreakVector3"));
        Require(workspace.TryConnectNodes(workspace.GetNode(sourceGroup.Id), components, out _, "a",
            GroupBoundarySchema.OutputPortId(outputVector.Id)), "Cannot connect named Live source.");
        var spawnable = workspace.CreateNode(GraphNodeKind.SpawnableGroup);
        Require(workspace.EnterGraph(spawnable), "Cannot edit Live template.");
        var captured = workspace.CreateNode(new GraphNodeKind("SpawnInputFloat"));
        var live = workspace.CreateNode(new GraphNodeKind("SpawnInputFloat"));
        var lifeInput = workspace.CreateNode(new GraphNodeKind("SpawnInputFloat"));
        var liveVector = workspace.CreateNode(new GraphNodeKind("SpawnInputVector3"));
        workspace.SetParameter(live, "bindingMode", JsonSerializer.SerializeToElement(1));
        Require(workspace.History.Undo() &&
                !SpawnableGroupSchema.IsLive(workspace.CreateCurrentSnapshot().Nodes.Single(node => node.Id == live.Id)) &&
                workspace.History.Redo(), "Binding mode changes must support Undo/Redo.");
        lifeInput = workspace.GetNode(lifeInput.Id);
        liveVector = workspace.GetNode(liveVector.Id);
        workspace.SetParameter(lifeInput, "bindingMode", JsonSerializer.SerializeToElement(1));
        workspace.SetParameter(liveVector, "bindingMode", JsonSerializer.SerializeToElement(1));
        var lifetime = workspace.CreateNode(new GraphNodeKind("LifeTime"));
        var delayed = workspace.CreateNode(new GraphNodeKind("Delay"));
        Require(workspace.TryConnectNodes(lifeInput, lifetime, out _, "seconds") &&
                workspace.TryConnectNodes(lifeInput, delayed, out _, "seconds") &&
                workspace.TryConnectNodes(workspace.Nodes.Single(node => node.Definition.Kind.Value == "OnStart"),
                    delayed, out _, "signal", "then"), "Cannot connect initial Live values to lifecycle inputs.");
        workspace.NavigateToGraph(0);
        spawnable = workspace.GetNode(spawnable.Id);
        Require(workspace.TryConnectNodes(workspace.GetNode(start.Id), spawnable, out _, "spawn", "then") &&
                workspace.TryConnectNodes(workspace.GetNode(components.Id), spawnable, out _, SpawnableGroupSchema.PortId(captured.Id), "y") &&
                workspace.TryConnectNodes(workspace.GetNode(components.Id), spawnable, out _, SpawnableGroupSchema.PortId(live.Id), "y") &&
                workspace.TryConnectNodes(workspace.GetNode(duration.Id), spawnable, out _, SpawnableGroupSchema.PortId(lifeInput.Id)) &&
                workspace.TryConnectNodes(workspace.GetNode(sourceGroup.Id), spawnable, out _,
                    SpawnableGroupSchema.PortId(liveVector.Id), GroupBoundarySchema.OutputPortId(outputVector.Id)),
            "Cannot connect Capture/Live pins at the root.");
        var files = new ProjectFileService();
        using var saved = new MemoryStream();
        files.SaveAsync(saved, workspace).GetAwaiter().GetResult();
        File.WriteAllBytes(Path.Combine(output, "LiveInputs.lfxproj"), saved.ToArray());
        saved.Position = 0;
        var graph = files.LoadAsync(saved).GetAwaiter().GetResult().Graph;
        var copy = new GraphWorkspaceViewModel(new UndoRedoManager());
        copy.PasteSnapshot(graph, 100, 100);
        var copiedGraph = copy.CreateSnapshot();
        var copiedGroup = copiedGraph.Nodes.Single(node => node.Kind == GraphNodeKind.SpawnableGroup);
        var copiedInputs = copiedGroup.ChildGraph!.Nodes.Where(node => SpawnableGroupSchema.IsInput(node.Kind)).ToArray();
        Require(copiedInputs.Select(SpawnableGroupSchema.IsLive).SequenceEqual([false, true, true, true]),
            "Copy/paste must preserve per-pin binding mode and mapped group outputs.");
        using (var copiedSession = new NativeSpawnableSession(SpawnableGroupPackage.Compile(copiedGraph), library))
        {
            copiedSession.Advance(1);
            Near(copiedSession.Read(copiedGroup.Id, copiedSession.ActiveInstances.Single().Instance, copiedInputs[1].Id).Lanes,
                [21], "Copied Live source lane and boundary IDs");
        }
        var package = SpawnableGroupPackage.Compile(graph);
        Require(package.Version == SpawnableGroupPackage.FormatVersion && package.HasLiveInputs &&
                package.Templates.Single().LiveInputs!.Count == 3,
            "Live pin bindings need an explicit package version and source mappings.");
        var compileMcp = LiberaFxMcpTools.CompileSpawnable(new ActiveGraphProvider(workspace));
        Dispatcher.UIThread.RunJobs();
        using (var result = JsonDocument.Parse(compileMcp.GetAwaiter().GetResult()))
        {
            var template = result.RootElement.GetProperty("templates").EnumerateArray().Single();
            Require(result.RootElement.GetProperty("version").GetInt32() == SpawnableGroupPackage.FormatVersion &&
                    result.RootElement.GetProperty("hasLiveInputs").GetBoolean() &&
                    template.GetProperty("capturePolicy").GetString() == "PerPin" &&
                    template.GetProperty("liveInputs").GetArrayLength() == 3,
                "MCP compilation must expose per-pin Live mappings, not claim capture-only execution.");
        }
        using var session = new NativeSpawnableSession(package, library);
        session.Advance(0);
        var instance = session.ActiveInstances.Single();
        Near(session.Read(spawnable.Id, instance.Instance, captured.Id).Lanes, [20], "Initial captured Y");
        Near(session.Read(spawnable.Id, instance.Instance, live.Id).Lanes, [20], "Initial Live Y");
        Near(session.Read(spawnable.Id, instance.Instance, lifeInput.Id).Lanes, [2], "Live inputs before OnStart");
        session.Advance(1);
        Near(session.Read(spawnable.Id, instance.Instance, captured.Id).Lanes, [20], "Capture must remain frozen");
        Near(session.Read(spawnable.Id, instance.Instance, live.Id).Lanes, [21], "Named Y output must update Live");
        Near(session.Read(spawnable.Id, instance.Instance, lifeInput.Id).Lanes, [3], "Live input must continue updating");
        Near(session.Read(spawnable.Id, instance.Instance, liveVector.Id).Lanes, [11, 21, 31],
            "Live vectors must resolve ordinary group outputs");
        session.Advance(3);
        Require(session.ActiveCount == 0 && session.Events.Count(item => item.Kind == 3) == 1 &&
                session.Events.Single(item => item.Kind == 3).Time == 2,
            "Life Time and OnStart Delay must capture initial Live duration once, not move the deadline on updates.");
        var legacy = FireworksSample.Create();
        var burst = legacy.Nodes.Single(node => node.Name == "Burst");
        var input = burst.ChildGraph!.Nodes.Single(node => SpawnableGroupSchema.IsInput(node.Kind));
        var invalidLiveRoute = legacy with { Nodes = legacy.Nodes.Select(node => node.Id == burst.Id ? node with
        {
            ChildGraph = node.ChildGraph! with { Nodes = node.ChildGraph.Nodes.Select(child => child.Id == input.Id ? child with
            {
                Parameters = [new("bindingMode", "enum:c06d13eb-4717-411e-ae92-92ea8ee57b65",
                    JsonSerializer.SerializeToElement(1))]
            } : child).ToArray() }
        } : node).ToArray() };
        Reject(() => SpawnableGroupPackage.Compile(invalidLiveRoute), "Live on a retired Spawnable output");
    }

    private static void VerifyExtendedInputs(string library)
    {
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        var start = workspace.CreateNode(new GraphNodeKind("OnStart"));
        var sources = Enumerable.Range(0, 5).Select(index =>
        {
            var source = workspace.CreateNode(GraphNodeKind.Float);
            workspace.SetParameter(source, "value", JsonSerializer.SerializeToElement(10d + index));
            return source;
        }).ToArray();
        var group = workspace.CreateNode(GraphNodeKind.SpawnableGroup);
        Require(workspace.EnterGraph(group), "Cannot edit extended-input template.");
        var inputs = Enumerable.Range(0, 5)
            .Select(_ => workspace.CreateNode(new GraphNodeKind("SpawnInputFloat"))).ToArray();
        var outputFloat = workspace.CreateNode(new GraphNodeKind("SpawnOutputFloat"));
        var outputVector2 = workspace.CreateNode(new GraphNodeKind("SpawnOutputVector2"));
        var outputVector3 = workspace.CreateNode(new GraphNodeKind("SpawnOutputVector3"));
        var outputVector4 = workspace.CreateNode(new GraphNodeKind("SpawnOutputVector4"));
        var outputColor = workspace.CreateNode(new GraphNodeKind("SpawnOutputColor"));
        workspace.SetParameter(outputVector2, "value", JsonSerializer.SerializeToElement(new[] { 2d, 3d }));
        workspace.SetParameter(outputVector3, "value", JsonSerializer.SerializeToElement(new[] { 1d, 2d, 3d }));
        workspace.SetParameter(outputVector4, "value", JsonSerializer.SerializeToElement(new[] { 4d, 5d, 6d, 7d }));
        workspace.SetParameter(outputColor, "value", JsonSerializer.SerializeToElement(new[] { 0.1d, 0.2d, 0.3d, 0.4d }));
        Require(workspace.TryConnectNodes(inputs[0], outputFloat, out _, "a"), "Cannot connect typed Spawn output.");
        workspace.NavigateToGraph(0);
        group = workspace.GetNode(group.Id);
        Require(workspace.TryConnectNodes(workspace.GetNode(start.Id), group, out _, "spawn", "then"),
            "Cannot connect extended-input Spawn signal.");
        for (var index = 0; index < inputs.Length; ++index)
            Require(workspace.TryConnectNodes(workspace.GetNode(sources[index].Id), group, out _,
                SpawnableGroupSchema.PortId(inputs[index].Id)), "Cannot connect extended CaptureAtSpawn input.");

        var graph = workspace.CreateSnapshot();
        var package = SpawnableGroupPackage.Compile(graph);
        var template = package.Templates.Single();
        Require(package.Version == SpawnableGroupPackage.FormatVersion && template.UsesExtendedCaptureInputs &&
                template.CapturedInputs?.Count == inputs.Length &&
                template.Outputs?.Select(output => output.PortType).SequenceEqual(
                    ["Float", "FloatX2", "FloatX3", "FloatX4", "Color"]) == true,
            "Extended Spawn inputs and typed outputs must be represented in package v7.");
        using (var session = new NativeSpawnableSession(package, library))
        {
            session.Advance(0);
            var instance = session.ActiveInstances.Single();
            for (var index = 0; index < inputs.Length; ++index)
                Near(session.Read(group.Id, instance.Instance, inputs[index].Id).Lanes, [10d + index],
                    $"Extended captured input {index}");
            Near(session.Read(group.Id, instance.Instance, outputFloat.Id).Lanes, [10],
                "Spawn Output Float");
            Near(session.Read(group.Id, instance.Instance, outputVector2.Id).Lanes, [2, 3],
                "Spawn Output Vector2");
            Near(session.Read(group.Id, instance.Instance, outputVector3.Id).Lanes, [1, 2, 3],
                "Spawn Output Vector3");
            Near(session.Read(group.Id, instance.Instance, outputVector4.Id).Lanes, [4, 5, 6, 7],
                "Spawn Output Vector4");
            Near(session.Read(group.Id, instance.Instance, outputColor.Id).Lanes, [0.1, 0.2, 0.3, 0.4],
                "Spawn Output Color");
        }

        var groupSnapshot = graph.Nodes.Single(node => node.Id == group.Id);
        var oversizedChild = groupSnapshot.ChildGraph! with
        {
            Nodes = groupSnapshot.ChildGraph.Nodes.Concat(Enumerable.Range(0,
                SpawnableGroupSchema.MaxInputLanes - inputs.Length + 1)
                .Select(_ => groupSnapshot.ChildGraph.Nodes.Single(node => node.Id == inputs[0].Id)
                    with { Id = Guid.NewGuid() })).ToArray()
        };
        var oversized = graph with
        {
            Nodes = graph.Nodes.Select(node => node.Id == group.Id
                ? node with { ChildGraph = oversizedChild } : node).ToArray()
        };
        Reject(() => GraphValidator.Validate(oversized), "more than 256 Spawn input lanes");
    }

    private static void VerifySignalInputsAndOutputs(string library)
    {
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        var inputSignal = workspace.CreateNode(GraphNodeKind.Signal);
        var sourceGroup = workspace.CreateNode(GraphNodeKind.SpawnableGroup);
        Require(workspace.EnterGraph(sourceGroup), "Cannot edit Signal I/O template.");
        var signalInput = workspace.CreateNode(new GraphNodeKind("SpawnInputSignal"));
        var signalOutput = workspace.CreateNode(new GraphNodeKind("SpawnOutputSignal"));
        Require(workspace.TryConnectNodes(signalInput, signalOutput, out _, "signal", "then"),
            "Cannot connect Spawnable local Signal I/O.");
        workspace.NavigateToGraph(0);
        inputSignal = workspace.GetNode(inputSignal.Id);
        sourceGroup = workspace.GetNode(sourceGroup.Id);
        var targetGroup = workspace.CreateNode(GraphNodeKind.SpawnableGroup);
        Require(workspace.TryConnectNodes(inputSignal, sourceGroup, out var inputConnectionError,
            targetPortId: SpawnableGroupSchema.PortId(signalInput.Id), sourcePortId: null),
            $"Cannot connect a Spawnable Signal input: {inputConnectionError}");
        Require(workspace.TryConnectNodes(sourceGroup, targetGroup, out _,
            targetPortId: "spawn", sourcePortId: SpawnableGroupSchema.OutputPortId(signalOutput.Id)),
            "Cannot connect a Spawnable Signal output.");

        workspace.AutoDestroyWhenIdle = false;
        var package = SpawnableGroupPackage.Compile(workspace.CreateSnapshot());
        var sourceTemplate = package.Templates.Single(template => template.ContainerId == sourceGroup.Id);
        Require(package.Version == SpawnableGroupPackage.FormatVersion && sourceTemplate.SignalInputs?.Count == 1 &&
                sourceTemplate.SignalOutputs?.Count == 1 && package.SignalRoutes.Count == 1,
            "Spawnable local Signal ports must be represented in package v9.");
        var compileTask = LiberaFxMcpTools.CompileSpawnable(new ActiveGraphProvider(workspace));
        Dispatcher.UIThread.RunJobs();
        using (var compiled = JsonDocument.Parse(compileTask.GetAwaiter().GetResult()))
        {
            var compiledRoot = compiled.RootElement;
            var compiledTemplate = compiledRoot.GetProperty("templates").EnumerateArray()
                .Single(item => item.GetProperty("containerId").GetGuid() == sourceGroup.Id);
            var autoDestroy = compiledRoot.GetProperty("autoDestroyWhenIdle").GetBoolean();
            var routeCount = compiledRoot.GetProperty("signalRoutes").GetArrayLength();
            var inputCount = compiledTemplate.GetProperty("signalInputs").GetArrayLength();
            var outputCount = compiledTemplate.GetProperty("signalOutputs").GetArrayLength();
            Require(!autoDestroy && routeCount == 1 && inputCount == 1 && outputCount == 1,
                $"MCP Spawnable compilation mismatch: autoDestroy={autoDestroy}, routes={routeCount}, " +
                $"signalInputs={inputCount}, signalOutputs={outputCount}.");
        }
        using var session = new NativeSpawnableSession(package, library);
        session.Advance(0);
        session.Advance(0.1);
        session.DispatchSignal(sourceGroup.Id);
        session.DispatchSignal(sourceGroup.Id);
        var sourceInstances = session.ActiveInstances.Where(item => item.Container == sourceGroup.Id).ToArray();
        Require(sourceInstances.Length == 2, "Spawn signal must create instances instead of broadcasting.");
        session.Advance(0.3);
        session.DispatchSignal(inputSignal.Id);
        var emitted = session.Events.Where(item => item.Kind == 4 && item.ContainerId == sourceGroup.Id).ToArray();
        Require(emitted.Length == 2 &&
                emitted.All(item => item.Time == 0.3) &&
                emitted.All(item => item.SignalNodeId == signalOutput.Id) &&
                emitted.Select(item => item.InstanceId).Order().SequenceEqual(
                    sourceInstances.Select(item => item.Instance).Order()) &&
                session.ActiveInstances.Count == 4,
            "A local signal must broadcast to active instances and route each output per source instance.");

        var drainingWorkspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        var rootStart = drainingWorkspace.CreateNode(new GraphNodeKind("OnStart"));
        var delayedSource = drainingWorkspace.CreateNode(GraphNodeKind.SpawnableGroup);
        Require(drainingWorkspace.EnterGraph(delayedSource), "Cannot edit delayed signal source.");
        var childStart = drainingWorkspace.Nodes.Single(node => node.Definition.Kind.Value == "OnStart");
        var childDelay = drainingWorkspace.CreateNode(new GraphNodeKind("Delay"));
        var delayedOutput = drainingWorkspace.CreateNode(new GraphNodeKind("SpawnOutputSignal"));
        var sourceLife = drainingWorkspace.CreateNode(new GraphNodeKind("LifeTime"));
        drainingWorkspace.SetParameter(childDelay, "seconds", JsonSerializer.SerializeToElement(1d));
        drainingWorkspace.SetParameter(sourceLife, "seconds", JsonSerializer.SerializeToElement(2d));
        Require(drainingWorkspace.TryConnectNodes(childStart, childDelay, out _, "signal", "then") &&
                drainingWorkspace.TryConnectNodes(childDelay, delayedOutput, out _, "signal", "then"),
            "Cannot delay a local signal output.");
        drainingWorkspace.NavigateToGraph(0);
        var delayedTarget = drainingWorkspace.CreateNode(GraphNodeKind.SpawnableGroup);
        Require(drainingWorkspace.EnterGraph(delayedTarget), "Cannot edit delayed signal target.");
        var targetLife = drainingWorkspace.CreateNode(new GraphNodeKind("LifeTime"));
        drainingWorkspace.SetParameter(targetLife, "seconds", JsonSerializer.SerializeToElement(1d));
        drainingWorkspace.NavigateToGraph(0);
        rootStart = drainingWorkspace.GetNode(rootStart.Id);
        Require(drainingWorkspace.TryConnectNodes(rootStart, drainingWorkspace.GetNode(delayedSource.Id),
                    out _, "spawn", "then") &&
                drainingWorkspace.TryConnectNodes(drainingWorkspace.GetNode(delayedSource.Id),
                    drainingWorkspace.GetNode(delayedTarget.Id), out _, targetPortId: "spawn",
                    sourcePortId: SpawnableGroupSchema.OutputPortId(delayedOutput.Id)),
            "Cannot connect delayed local signal routing.");
        var drainingPackage = SpawnableGroupPackage.Compile(drainingWorkspace.CreateSnapshot());
        Require(drainingPackage.AutoDestroyWhenIdle && drainingPackage.HasChildDeferredSignals &&
                drainingPackage.SignalRoutes.Count == 1,
            "The delayed local signal fixture must naturally end the root while child work is pending.");
        using var drainingSession = new NativeSpawnableSession(drainingPackage, library);
        drainingSession.Advance(0);
        Require(drainingSession.RootVisible &&
                drainingSession.ActiveInstances.Single().Container == delayedSource.Id,
            "Startup must spawn the source before the root begins draining.");
        drainingSession.Advance(1);
        Require(drainingSession.RootVisible &&
                drainingSession.ActiveInstances.Any(item => item.Container == delayedTarget.Id) &&
                drainingSession.Events.Any(item => item.Kind == 4 && item.ContainerId == delayedSource.Id),
            "Internal child signals must route through the root after natural shutdown begins.");
        drainingSession.Advance(2);
        Require(drainingSession.ActiveCount == 0 && !drainingSession.RootVisible &&
                drainingSession.Events.Count(item => item.Kind == 3 && item.Time == 2) == 2,
            "Natural root shutdown must finish only after both delayed signal descendants expire.");
        drainingSession.Advance(2.1);
        Require(!drainingSession.RootVisible, "The root must finish after routed child work drains.");
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
