using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;

namespace LiberaFx.Tests;

internal static class SpawnableHierarchyTests
{
    public static void Run()
    {
        VerifyWorkspacePlacement();
        var legacy = SpawnableGroupPackage.Compile(new([Node("Float")], []));
        Require(legacy.Version == 11 &&
            BitConverter.ToUInt16(SpawnableGroupPackageBinary.Serialize(legacy), 4) == 3,
            "Root-only legacy exports must preserve package/container versions 11/3.");
        var rootCpu = SpawnableGroupPackage.Compile(new([CpuSystem()], []));
        Require(rootCpu.Version == 12 && rootCpu.CpuPrograms.Length > 0 &&
            BitConverter.ToUInt16(SpawnableGroupPackageBinary.Serialize(rootCpu), 4) == 4,
            "Root-only CPU exports must preserve package/container versions 12/4.");

        var retention = CreateRetentionFixture();
        GraphValidator.ValidateDocument(retention);
        var retentionPackage = SpawnableGroupPackage.Compile(retention);
        Require(retentionPackage.Version == 13 && retentionPackage.CpuPrograms.Length == 0 &&
            retentionPackage.HasChildDeferredSignals,
            "Template-local CPU/Delay data must use package version 13 and report child-local deferred signals.");
        var parent = retentionPackage.Templates.Single(template => template.ParentContainerId is null);
        var child = retentionPackage.Templates.Single(template => template.ParentContainerId == parent.ContainerId);
        Require(parent.CpuPrograms.Length > 0 && child.CpuPrograms.Length == 0 &&
            parent.Lifecycle?.LifeNodeId is not null && child.Lifecycle?.LifeNodeId is not null,
            "CPU payloads and lifetimes must belong to their own Spawnable execution scopes.");
        using (var scene = JsonDocument.Parse(parent.RenderConfiguration))
            Require(scene.RootElement.GetProperty("cpuParticles").GetArrayLength() == 1,
                "A template-local CPU system must contribute a draw only to that template's scene.");
        var retentionBytes = SpawnableGroupPackageBinary.Serialize(retentionPackage);
        Require(BitConverter.ToUInt16(retentionBytes, 4) == 5 &&
            SpawnableGroupPackageBinary.Deserialize(retentionBytes).Templates
                .Single(template => template.ContainerId == child.ContainerId).ParentContainerId == parent.ContainerId,
            "Nested ownership and template CPU payloads must roundtrip in container 5.");

        var captures = SpawnableGroupPackage.Compile(CreateCaptureFixture());
        Require(captures.Version == 13 && captures.Templates.Count == 4,
            "Two parent scopes and their two nested children must compile independently.");
        foreach (var parentTemplate in captures.Templates.Where(template => template.ParentContainerId is null))
        {
            var childTemplate = captures.Templates.Single(template =>
                template.ParentContainerId == parentTemplate.ContainerId);
            Require(childTemplate.CapturedInputs?.Count == 5 &&
                childTemplate.CapturedInputs.All(capture => capture.SourceContainerId is null) &&
                childTemplate.CapturedInputs.Select(capture => capture.SourceNodeId).Distinct().Count() == 5 &&
                childTemplate.LiveInputs?.Count == 1 &&
                childTemplate.LiveInputs[0].SourceContainerId is null &&
                childTemplate.LiveInputs[0].SourceNodeId != Guid.Empty,
                "Nested CaptureAtSpawn and Live inputs must bind to nodes in the immediate parent template scope.");
        }
        var captureBytes = SpawnableGroupPackageBinary.Serialize(captures);
        var decoded = SpawnableGroupPackageBinary.Deserialize(captureBytes);
        Require(decoded.Templates.Select(template => template.ParentContainerId)
                .SequenceEqual(captures.Templates.Select(template => template.ParentContainerId)) &&
            decoded.Templates.Zip(captures.Templates).All(pair =>
                pair.First.CpuPrograms.SequenceEqual(pair.Second.CpuPrograms)),
            "Hierarchical template ownership and scoped CPU data changed on roundtrip.");
        var malformedParent = captureBytes.ToArray();
        var templateTailBytes = captures.Templates.Sum(template =>
            sizeof(byte) + (template.ParentContainerId is null ? 0 : 16) +
            sizeof(int) + template.CpuPrograms.Length);
        var firstParentOffset = malformedParent.Length - templateTailBytes + sizeof(byte);
        Guid.NewGuid().TryWriteBytes(malformedParent.AsSpan(firstParentOffset, 16), bigEndian: true, out _);
        Reject(() => SpawnableGroupPackageBinary.Deserialize(malformedParent),
            "unknown parent reference in a decoded package");

        RejectPackage(new([1], [Template(Guid.NewGuid(), Guid.NewGuid()) with
        {
            ParentContainerId = Guid.NewGuid()
        }]), "missing template parent");
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        RejectPackage(new([1], [
            Template(firstId, Guid.NewGuid()) with { ParentContainerId = secondId },
            Template(secondId, Guid.NewGuid()) with { ParentContainerId = firstId }
        ]), "cyclic template ownership");
        RejectPackage(new([1], [Template(firstId, Guid.NewGuid()), Template(firstId, Guid.NewGuid())]),
            "duplicate template IDs");
        var chain = Enumerable.Range(0, 13).Select(_ => Guid.NewGuid()).ToArray();
        RejectPackage(new([1], chain.Select((id, index) =>
            Template(id, Guid.NewGuid()) with
            {
                ParentContainerId = index == 0 ? null : chain[index - 1]
            }).ToArray()), "over-depth template ownership");

        var sameParent = CreateSameTemplateCaptureFixture();
        var sameParentPackage = SpawnableGroupPackage.Compile(sameParent.Graph);
        var sameParentTemplate = sameParentPackage.Templates.Single(template =>
            template.ContainerId == sameParent.ParentTemplateId);
        var sameChildTemplate = sameParentPackage.Templates.Single(template =>
            template.ContainerId == sameParent.ChildTemplateId);
        Require(sameParentPackage.Templates.Count == 2 && sameParentTemplate.ParentContainerId is null &&
            sameChildTemplate.ParentContainerId == sameParentTemplate.ContainerId &&
            sameParentTemplate.Inputs.Single().TemplateNodeId == sameParent.ParentInputId &&
            sameChildTemplate.Inputs.Single().TemplateNodeId == sameParent.ChildInputId &&
            sameChildTemplate.Outputs?.Single().TemplateNodeId == sameParent.ChildOutputId &&
            sameParent.Graph.Connections.Count(edge => edge.TargetNodeId == sameParent.ParentTemplateId &&
                edge.TargetPortId == "spawn") == 2,
            "The same static parent template must be spawned twice and route its distinct per-event input into one nested child template.");

        var cpuRetention = CreateCpuOnlyRetentionFixture();
        var cpuRetentionPackage = SpawnableGroupPackage.Compile(cpuRetention.Graph);
        var cpuParent = cpuRetentionPackage.Templates.Single(template =>
            template.ContainerId == cpuRetention.ParentTemplateId);
        var cpuChild = cpuRetentionPackage.Templates.Single(template =>
            template.ContainerId == cpuRetention.ChildTemplateId);
        Require(cpuParent.Lifecycle?.LifeNodeId == cpuRetention.ParentLifeId &&
            cpuChild.Lifecycle?.LifeNodeId == cpuRetention.ChildLifeId &&
            cpuParent.CpuPrograms.Length == 0 && cpuChild.CpuPrograms.Length > 0 &&
            cpuRetention.ParticleLifeSeconds > cpuRetention.ParentLifeSeconds &&
            cpuRetention.ParticleLifeSeconds > cpuRetention.ChildLifeSeconds,
            "CPU-only retention fixture must put four-second particles in the child template past the parent's one-second and child's two-second lifetimes.");
    }

    private static void VerifyWorkspacePlacement()
    {
        var history = new UndoRedoManager();
        var workspace = new GraphWorkspaceViewModel(history);
        var parent = workspace.CreateNode(GraphNodeKind.SpawnableGroup, 80, 80);
        Require(workspace.EnterGraph(parent), "Could not enter parent Spawnable for workspace placement checks.");
        var sharedGameIo = GraphNodeDefinition.All.First(definition =>
            GameIoSchema.IsNode(definition.Kind) && !GameIoSchema.IsInstanceNode(definition.Kind));
        RejectPlacement(() => workspace.CreateNode(sharedGameIo.Kind),
            "shared Game I/O inside Spawnable");

        var child = workspace.CreateNode(GraphNodeKind.SpawnableGroup, 320, 80);
        var childId = child.Id;
        Require(workspace.Nodes.Count == 2 && workspace.Nodes.Any(node => node.Id == childId),
            "The workspace must allow a nested Spawnable inside a Spawnable template.");
        Require(history.Undo() && workspace.Nodes.Count == 1 &&
            history.Redo() && workspace.Nodes.Any(node => node.Id == childId),
            "Nested Spawnable creation must undo and redo as a graph edit.");

        var provider = new ActiveGraphProvider(workspace);
        var moved = LiberaFxMcpTools.MoveNode(provider, childId.ToString(), 480, 160);
        Dispatcher.UIThread.RunJobs();
        moved.GetAwaiter().GetResult();
        Require(workspace.GetNode(childId).X == 480 && workspace.GetNode(childId).Y == 160 &&
            history.Undo() && workspace.GetNode(childId).X == 320 &&
            history.Redo() && workspace.GetNode(childId).X == 480,
            "MCP MoveNode must preserve nested ownership and remain undoable.");

        var beforeMcpCreate = workspace.Nodes.Count;
        var mcpCreate = LiberaFxMcpTools.CreateNode(provider, "SpawnableGroup", 700, 160, false, "manual");
        Dispatcher.UIThread.RunJobs();
        _ = mcpCreate.GetAwaiter().GetResult();
        Require(workspace.Nodes.Count == beforeMcpCreate + 1 &&
            workspace.Nodes.Count(node => node.Definition.Kind == GraphNodeKind.SpawnableGroup) == 2,
            "MCP CreateNode must create a nested Spawnable in the active template graph.");
        Require(history.Undo() && workspace.Nodes.Count == beforeMcpCreate &&
            history.Redo() && workspace.Nodes.Count == beforeMcpCreate + 1,
            "MCP-created nested Spawnable must undo and redo.");

        Require(workspace.EnterGraph(workspace.GetNode(childId)),
            "Could not enter nested Spawnable for CPU placement checks.");
        var plan = LiberaFxMcpTools.ApplyGraphPlan(provider,
            [new("scopedCpu", "CpuParticle", "Template CPU", 400, 240)], [], "manual");
        Dispatcher.UIThread.RunJobs();
        _ = plan.GetAwaiter().GetResult();
        var cpu = workspace.Nodes.Single(node => CpuParticleSchema.IsContainer(node.Definition.Kind));
        Require(history.Undo() && !workspace.Nodes.Any(node => CpuParticleSchema.IsContainer(node.Definition.Kind)) &&
            history.Redo() && workspace.Nodes.Any(node => node.Id == cpu.Id),
            "MCP ApplyGraphPlan must allow CPU Particle inside a nested Spawnable and undo/redo it.");
        cpu = workspace.Nodes.Single(node => node.Id == cpu.Id);

        Require(workspace.EnterGraph(cpu), "Could not enter nested CPU Particle container.");
        RejectPlacement(() => workspace.CreateNode(GraphNodeKind.SpawnableGroup),
            "Spawnable inside CPU Particle container");
        RejectPlacement(() => workspace.CreateNode(GraphNodeDefinition.All.First(definition =>
            CpuParticleSchema.IsContainer(definition.Kind)).Kind), "CPU container inside CPU container");
        var spawnStage = workspace.Nodes.Single(node => CpuParticleSchema.Name(node.Definition.Kind) == "CpuParticleSpawn");
        Require(workspace.EnterGraph(spawnStage), "Could not enter CPU Spawn kernel.");
        RejectPlacement(() => workspace.CreateNode(GraphNodeKind.SpawnableGroup),
            "Spawnable inside CPU kernel");
        RejectPlacement(() => workspace.CreateNode(GraphNodeDefinition.All.Single(definition =>
            CpuParticleSchema.IsContainer(definition.Kind)).Kind), "CPU container inside CPU kernel");

        workspace.NavigateToGraph(0);
        GraphValidator.ValidateDocument(workspace.CreateSnapshot());
        var package = SpawnableGroupPackage.Compile(workspace.CreateSnapshot());
        Require(package.Templates.Count == 3 && package.Templates.Single(template =>
                template.ContainerId == childId).CpuPrograms.Length > 0,
            "Workspace-created nested Spawnable and its CPU program must compile in the same scope.");
    }

    private static void RejectPlacement(Action action, string description)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException($"{description} must be rejected by workspace placement validation.");
    }

    public static void WriteFixtures(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || Directory.Exists(directory))
            throw new ArgumentException("Supply an absolute NEW isolated fixture directory.");
        Directory.CreateDirectory(directory);
        Write("nested-cpu-retention", CreateRetentionFixture());
        Write("nested-capture-isolation", CreateCaptureFixture());

        void Write(string name, GraphSnapshot graph)
        {
            var package = SpawnableGroupPackage.Compile(graph);
            var bytes = SpawnableGroupPackageBinary.Serialize(package);
            var nodes = AllNodes(graph).ToDictionary(node => node.Id);
            File.WriteAllBytes(Path.Combine(directory, name + ".lfxp"), bytes);
            var manifest = new
            {
                file = name + ".lfxp",
                packageVersion = package.Version,
                containerVersion = BitConverter.ToUInt16(bytes, 4),
                rootOnStartNodeId = package.OnStartNodeId,
                rootCpuProgramsBytes = package.CpuPrograms.Length,
                lifetimes = nodes.Values.Where(node => node.Kind.Value == "LifeTime")
                    .Select(node => new
                    {
                        nodeId = node.Id,
                        seconds = node.Parameters?.SingleOrDefault(parameter => parameter.Id == "seconds")
                            ?.Value.GetRawText()
                    }),
                cpuSystems = nodes.Values.Where(node => CpuParticleSchema.IsContainer(node.Kind))
                    .Select(system => new
                    {
                        nodeId = system.Id,
                        rate = CpuParticleCompilation.Parameter(system, "rate").GetDouble(),
                        burst = CpuParticleCompilation.Parameter(system, "burst").GetInt32(),
                        particleLife = system.ChildGraph!.Nodes
                            .Single(stage => CpuParticleSchema.Name(stage.Kind) == "CpuParticleSpawn")
                            .ChildGraph!.Nodes.Where(node => CpuParticleSchema.Name(node.Kind) == "CpuParticleSetLife")
                            .Select(node => new
                            {
                                nodeId = node.Id,
                                value = CpuParticleCompilation.Parameter(node, "value").GetDouble()
                            })
                    }),
                templates = package.Templates.Select(template => new
                {
                    containerId = template.ContainerId,
                    parentContainerId = template.ParentContainerId,
                    signalTypeId = template.SignalTypeId,
                    lifecycle = template.Lifecycle,
                    cpuProgramsBytes = template.CpuPrograms.Length,
                    capturedInputs = template.CapturedInputs,
                    capturedSourceValues = template.CapturedInputs?.Select(capture => new
                    {
                        capture.TargetNodeId,
                        capture.SourceNodeId,
                        value = nodes.TryGetValue(capture.SourceNodeId, out var source)
                            ? source.Parameters?.SingleOrDefault(parameter => parameter.Id == "value")?.Value.GetRawText()
                            : null
                    }),
                    liveInputs = template.LiveInputs
                })
            };
            File.WriteAllBytes(Path.Combine(directory, name + ".ids.json"),
                JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public static void WriteAdditionalFixtures(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || Directory.Exists(directory))
            throw new ArgumentException("Supply an absolute NEW isolated fixture directory.");
        Directory.CreateDirectory(directory);
        var sameParent = CreateSameTemplateCaptureFixture();
        Write("same-parent-template-capture", sameParent.Graph, new
        {
            sameParent.ParentTemplateId,
            sameParent.ParentInputId,
            sameParent.ChildTemplateId,
            sameParent.ChildInputId,
            sameParent.ChildOutputId,
            sameParent.SequenceId,
            sameParent.CounterId,
            sameParent.RootOnStartId,
            sequenceOrder = new[]
            {
                new { output = "then_0", action = "increment counter" },
                new { output = "then_1", action = "spawn same parent template; captured input is 1" },
                new { output = "then_2", action = "increment counter" },
                new { output = "then_3", action = "spawn same parent template; captured input is 2" }
            },
            parentTemplateSpawnCount = 2,
            expectedChildCapturedValues = new[] { 1, 2 }
        });

        var cpuRetention = CreateCpuOnlyRetentionFixture();
        Write("nested-cpu-only-retention", cpuRetention.Graph, new
        {
            cpuRetention.ParentTemplateId,
            cpuRetention.ParentLifeId,
            parentLifeSeconds = cpuRetention.ParentLifeSeconds,
            cpuRetention.ChildTemplateId,
            cpuRetention.ChildLifeId,
            childLifeSeconds = cpuRetention.ChildLifeSeconds,
            cpuRetention.CpuSystemId,
            cpuRetention.ParticleLifeNodeId,
            cpuRetention.ParticleLifeSeconds,
            rate = 0,
            burst = 3
        });

        void Write(string name, GraphSnapshot graph, object scenario)
        {
            var package = SpawnableGroupPackage.Compile(graph);
            var bytes = SpawnableGroupPackageBinary.Serialize(package);
            var nodes = AllNodes(graph).ToDictionary(node => node.Id);
            File.WriteAllBytes(Path.Combine(directory, name + ".lfxp"), bytes);
            var manifest = new
            {
                file = name + ".lfxp",
                packageVersion = package.Version,
                containerVersion = BitConverter.ToUInt16(bytes, 4),
                rootOnStartNodeId = package.OnStartNodeId,
                rootCpuProgramsBytes = package.CpuPrograms.Length,
                scenario,
                templates = package.Templates.Select(template => new
                {
                    containerId = template.ContainerId,
                    parentContainerId = template.ParentContainerId,
                    signalTypeId = template.SignalTypeId,
                    lifecycle = template.Lifecycle,
                    cpuProgramsBytes = template.CpuPrograms.Length,
                    inputs = template.Inputs,
                    outputs = template.Outputs,
                    capturedInputs = template.CapturedInputs,
                    liveInputs = template.LiveInputs
                }),
                lifetimes = nodes.Values.Where(node => node.Kind.Value == "LifeTime")
                    .Select(node => new
                    {
                        nodeId = node.Id,
                        seconds = node.Parameters?.SingleOrDefault(parameter => parameter.Id == "seconds")
                            ?.Value.GetRawText()
                    }),
                cpuSystems = nodes.Values.Where(node => CpuParticleSchema.IsContainer(node.Kind))
                    .Select(system => new
                    {
                        nodeId = system.Id,
                        rate = CpuParticleCompilation.Parameter(system, "rate").GetDouble(),
                        burst = CpuParticleCompilation.Parameter(system, "burst").GetInt32(),
                        particleLife = system.ChildGraph!.Nodes
                            .Single(stage => CpuParticleSchema.Name(stage.Kind) == "CpuParticleSpawn")
                            .ChildGraph!.Nodes.Where(node => CpuParticleSchema.Name(node.Kind) == "CpuParticleSetLife")
                            .Select(node => new
                            {
                                nodeId = node.Id,
                                value = CpuParticleCompilation.Parameter(node, "value").GetDouble()
                            })
                    })
            };
            File.WriteAllBytes(Path.Combine(directory, name + ".ids.json"),
                JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static GraphSnapshot CreateRetentionFixture()
    {
        var rootStart = Node("OnStart");
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var childStart = Node("OnStart");
        var childLife = Node("LifeTime", parameters: [Parameter("seconds", "float", 10d)]);
        var child = Node("SpawnableGroup", childId, "Long-lived child", new(
            [childStart, childLife], []));
        var cpuSystem = CpuSystem();
        var spawnStage = cpuSystem.ChildGraph!.Nodes.Single(node =>
            CpuParticleSchema.Name(node.Kind) == "CpuParticleSpawn");
        var velocity = spawnStage.ChildGraph!.Nodes.Single(node =>
            CpuParticleSchema.Name(node.Kind) == "CpuParticleSetVelocity");
        var setLife = Node("CpuParticleSetLife", parameters: [Parameter("value", "float", 4d)]);
        spawnStage = spawnStage with
        {
            ChildGraph = spawnStage.ChildGraph with
            {
                Nodes = [.. spawnStage.ChildGraph.Nodes, setLife],
                Connections = [.. spawnStage.ChildGraph.Connections,
                    new(velocity.Id, setLife.Id, "signal", "then")]
            }
        };
        cpuSystem = cpuSystem with
        {
            ChildGraph = cpuSystem.ChildGraph with
            {
                Nodes = cpuSystem.ChildGraph.Nodes.Select(node =>
                    node.Id == spawnStage.Id ? spawnStage : node).ToArray()
            }
        };
        var parentStart = Node("OnStart");
        var parentLife = Node("LifeTime", parameters: [Parameter("seconds", "float", 1d)]);
        var deferred = Node("Delay");
        var parent = Node("SpawnableGroup", parentId, "Short-lived parent", new(
            [parentStart, parentLife, deferred, child, cpuSystem],
            [new(parentStart.Id, child.Id, "spawn", "then")]));
        return new([rootStart, parent], [new(rootStart.Id, parent.Id, "spawn", "then")]);
    }

    private static GraphNodeSnapshot CpuSystem()
    {
        var definition = GraphNodeDefinition.All.Single(item => CpuParticleSchema.IsContainer(item.Kind));
        var system = Node(definition.TypeName, parameters:
        [
            Parameter("rate", "float", 0d),
            Parameter("burst", "int", 3)
        ]);
        return system with { ChildGraph = CpuParticleSchema.CreateChild(system.Kind) };
    }

    private static GraphSnapshot CreateCaptureFixture()
    {
        var rootStart = Node("OnStart");
        var sequence = Node("Sequence");
        var rootNodes = new List<GraphNodeSnapshot> { rootStart, sequence };
        var rootEdges = new List<GraphConnectionSnapshot>
        {
            new(rootStart.Id, sequence.Id, "signal", "then")
        };
        for (var index = 0; index < 2; index++)
        {
            var parentId = Guid.NewGuid();
            var parentStart = Node("OnStart");
            var inputs = Enumerable.Range(0, 5)
                .Select(inputIndex => Node("SpawnInputFloat", nodeName: $"Captured {inputIndex}")).ToArray();
            var live = Node("SpawnInputFloat", nodeName: "Live",
                parameters: [Parameter("bindingMode", "enum:c06d13eb-4717-411e-ae92-92ea8ee57b65", 1)]);
            var childStart = Node("OnStart");
            var child = Node("SpawnableGroup", nodeName: $"Nested {index}", child: new(
                [childStart, .. inputs, live], []));
            var values = Enumerable.Range(0, 5).Select(valueIndex =>
                Node("Float", parameters: [Parameter("value", "float", index * 10d + valueIndex + 1)])).ToArray();
            var liveValue = Node("Time");
            var parentEdges = new List<GraphConnectionSnapshot>
            {
                new(parentStart.Id, child.Id, "spawn", "then")
            };
            for (var inputIndex = 0; inputIndex < inputs.Length; inputIndex++)
                parentEdges.Add(new(values[inputIndex].Id, child.Id,
                    SpawnableGroupSchema.PortId(inputs[inputIndex].Id)));
            parentEdges.Add(new(liveValue.Id, child.Id, SpawnableGroupSchema.PortId(live.Id)));
            var wrapper = Node("GraphGroup", child: new(
                [parentStart, child, .. values, liveValue], parentEdges));
            var parent = Node("SpawnableGroup", parentId, $"Parent {index}", new([wrapper], []));
            rootNodes.Add(parent);
            rootEdges.Add(new(sequence.Id, parent.Id, "spawn", $"then_{index}"));
        }
        return new(rootNodes, rootEdges);
    }

    private sealed record SameTemplateCaptureFixture(GraphSnapshot Graph, Guid RootOnStartId,
        Guid SequenceId, Guid CounterId, Guid ParentTemplateId, Guid ParentInputId,
        Guid ChildTemplateId, Guid ChildInputId, Guid ChildOutputId);

    private sealed record CpuOnlyRetentionFixture(GraphSnapshot Graph, Guid ParentTemplateId,
        Guid ParentLifeId, double ParentLifeSeconds, Guid ChildTemplateId, Guid ChildLifeId,
        double ChildLifeSeconds, Guid CpuSystemId, Guid ParticleLifeNodeId, double ParticleLifeSeconds);

    private static SameTemplateCaptureFixture CreateSameTemplateCaptureFixture()
    {
        var rootStart = Node("OnStart", Id("30000000-0000-4000-8000-000000000001"));
        var sequence = Node("Sequence", Id("30000000-0000-4000-8000-000000000002"),
            parameters: [Parameter("outputs", "int", 4)]);
        var counter = Node("SignalToNumber", Id("30000000-0000-4000-8000-000000000003"),
            parameters: [Parameter("mode", $"enum:{GraphLogicalSchema.SignalToNumberModeTypeId:D}", 2)]);
        var parentInput = Node("SpawnInputFloat", Id("30000000-0000-4000-8000-000000000005"),
            nodeName: "Dynamic parent value");
        var parentStart = Node("OnStart", Id("30000000-0000-4000-8000-000000000006"));
        var childInput = Node("SpawnInputFloat", Id("30000000-0000-4000-8000-000000000008"),
            nodeName: "Captured child value");
        var childOutput = Node("SpawnOutputFloat", Id("30000000-0000-4000-8000-000000000009"),
            nodeName: "Observed child value");
        var childGraph = new GraphSnapshot([childInput, childOutput],
            [new(childInput.Id, childOutput.Id, "a")]);
        var child = Node("SpawnableGroup", Id("30000000-0000-4000-8000-000000000007"),
            "Nested child template", childGraph);
        var parentGraph = new GraphSnapshot([parentStart, parentInput, child],
        [
            new(parentStart.Id, child.Id, "spawn", "then"),
            new(parentInput.Id, child.Id, SpawnableGroupSchema.PortId(childInput.Id))
        ]);
        var parent = Node("SpawnableGroup", Id("30000000-0000-4000-8000-000000000004"),
            "Shared parent template", parentGraph);
        var root = new GraphSnapshot([rootStart, sequence, counter, parent],
        [
            new(rootStart.Id, sequence.Id, "signal", "then"),
            new(sequence.Id, counter.Id, GraphLogicalSchema.SignalInput, "then_0"),
            new(sequence.Id, parent.Id, "spawn", "then_1"),
            new(sequence.Id, counter.Id, GraphLogicalSchema.SignalInput, "then_2"),
            new(sequence.Id, parent.Id, "spawn", "then_3"),
            new(counter.Id, parent.Id, SpawnableGroupSchema.PortId(parentInput.Id), "value")
        ]);
        return new(root, rootStart.Id, sequence.Id, counter.Id, parent.Id, parentInput.Id,
            child.Id, childInput.Id, childOutput.Id);
    }

    private static CpuOnlyRetentionFixture CreateCpuOnlyRetentionFixture()
    {
        var rootStart = Node("OnStart", Id("40000000-0000-4000-8000-000000000001"));
        var parentStart = Node("OnStart", Id("40000000-0000-4000-8000-000000000003"));
        var parentLife = Node("LifeTime", Id("40000000-0000-4000-8000-000000000004"),
            parameters: [Parameter("seconds", "float", 1d)]);
        var childStart = Node("OnStart", Id("40000000-0000-4000-8000-000000000006"));
        var childLife = Node("LifeTime", Id("40000000-0000-4000-8000-000000000007"),
            parameters: [Parameter("seconds", "float", 2d)]);
        var cpuDefinition = GraphNodeDefinition.All.Single(definition => CpuParticleSchema.IsContainer(definition.Kind));
        var cpuSystemId = Id("40000000-0000-4000-8000-000000000008");
        var cpuSystem = Node(cpuDefinition.TypeName, cpuSystemId, "Long-lived particles", parameters:
        [
            Parameter("rate", "float", 0d),
            Parameter("burst", "int", 3)
        ]);
        var stableChild = WithStableIds(CpuParticleSchema.CreateChild(cpuSystem.Kind), "cpu-only-retention-kernel");
        var spawnStage = stableChild.Nodes.Single(node => CpuParticleSchema.Name(node.Kind) == "CpuParticleSpawn");
        var velocity = spawnStage.ChildGraph!.Nodes.Single(node =>
            CpuParticleSchema.Name(node.Kind) == "CpuParticleSetVelocity");
        var setLife = Node("CpuParticleSetLife", Id("40000000-0000-4000-8000-000000000009"),
            "Four second particle", parameters: [Parameter("value", "float", 4d)]);
        spawnStage = spawnStage with
        {
            ChildGraph = spawnStage.ChildGraph with
            {
                Nodes = [.. spawnStage.ChildGraph.Nodes, setLife],
                Connections = [.. spawnStage.ChildGraph.Connections,
                    new(velocity.Id, setLife.Id, "signal", "then")]
            }
        };
        cpuSystem = cpuSystem with
        {
            ChildGraph = stableChild with
            {
                Nodes = stableChild.Nodes.Select(node =>
                    node.Id == spawnStage.Id ? spawnStage : node).ToArray()
            }
        };
        var child = Node("SpawnableGroup", Id("40000000-0000-4000-8000-000000000005"),
            "Two second child", new([childStart, childLife, cpuSystem], []));
        var parent = Node("SpawnableGroup", Id("40000000-0000-4000-8000-000000000002"),
            "One second parent", new([parentStart, parentLife, child],
                [new(parentStart.Id, child.Id, "spawn", "then")]));
        var graph = new GraphSnapshot([rootStart, parent],
            [new(rootStart.Id, parent.Id, "spawn", "then")]);
        return new(graph, parent.Id, parentLife.Id, 1, child.Id, childLife.Id, 2,
            cpuSystem.Id, setLife.Id, 4);
    }

    private static GraphSnapshot WithStableIds(GraphSnapshot graph, string seed)
    {
        var ordinal = 0;
        GraphSnapshot Rewrite(GraphSnapshot current)
        {
            var ids = new Dictionary<Guid, Guid>();
            foreach (var node in current.Nodes) ids.Add(node.Id, StableId(seed, ordinal++));
            var nodes = current.Nodes.Select(node => node with
            {
                Id = ids[node.Id],
                ChildGraph = node.ChildGraph is null ? null : Rewrite(node.ChildGraph)
            }).ToArray();
            return current with
            {
                Nodes = nodes,
                Connections = current.Connections.Select(edge => edge with
                {
                    SourceNodeId = ids[edge.SourceNodeId],
                    TargetNodeId = ids[edge.TargetNodeId]
                }).ToArray()
            };
        }
        return Rewrite(graph);
    }

    private static Guid StableId(string seed, int ordinal)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{ordinal}"));
        return new Guid(bytes.AsSpan(0, 16), bigEndian: true);
    }

    private static Guid Id(string value) => Guid.Parse(value);

    private static SpawnableTemplate Template(Guid id, Guid signalTypeId) =>
        new(id, signalTypeId, [1], [], [], [], null);

    private static IEnumerable<GraphNodeSnapshot> AllNodes(GraphSnapshot graph)
    {
        foreach (var node in graph.Nodes)
        {
            yield return node;
            if (node.ChildGraph is { } child)
                foreach (var nested in AllNodes(child)) yield return nested;
        }
    }

    private static GraphNodeSnapshot Node(string name, Guid? id = null, string? nodeName = null,
        GraphSnapshot? child = null, params NodeParameterSnapshot[] parameters)
    {
        var definition = GraphNodeDefinition.All.Single(item => item.TypeName == name);
        return new(id ?? Guid.NewGuid(), definition.Kind, nodeName ?? definition.Name, 80, 80,
            definition.PortType, definition.PortCategory, ChildGraph: child,
            Parameters: parameters.Length == 0 ? null : parameters);
    }

    private static NodeParameterSnapshot Parameter<T>(string id, string type, T value) =>
        new(id, type, JsonSerializer.SerializeToElement(value));

    private static void RejectPackage(SpawnableGroupPackage package, string description)
    {
        try { _ = SpawnableGroupPackageBinary.Serialize(package); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException($"Malformed {description} must be rejected.");
    }

    private static void Reject(Action action, string description)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException($"Malformed {description} must be rejected.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
