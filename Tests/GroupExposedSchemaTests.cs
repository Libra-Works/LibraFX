using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class GroupExposedSchemaTests
{
    public static void Run()
    {
        var source = new GraphWorkspaceViewModel(new UndoRedoManager());
        var scalar = source.CreateNode(GraphNodeKind.Float);
        var curve = source.CreateNode(GraphNodeKind.FloatFCurve);
        var missing = Guid.NewGuid();
        var item = new GroupExposedItem(Guid.NewGuid(), "Strength", "Description", "Motion", 1,
            "parameter", [new(scalar.Id, "value")], 0, 10, 0.1, "slider");
        var orphan = new GroupExposedItem(Guid.NewGuid(), "Missing", "", "", 2,
            "curve", [new(missing, null)]);
        var curveItem = new GroupExposedItem(Guid.NewGuid(), "Curve", "", "", 3,
            "curve", [new(curve.Id, null)]);
        var child = source.CreateSnapshot() with { ExposedItems = [item, orphan, curveItem] };
        var library = new GroupAssetLibrary(Path.Combine(AppDataDirectory.Root, "exposed-schema-tests"));
        var asset = library.Create("Exposed schema", child);
        if (!GraphSnapshot.ContentEquals(child, library.Load(asset.Id).Graph))
            throw new InvalidOperationException("The group asset lost exposed item metadata.");

        var group = GraphNodeDefinition.All.Single(definition => definition.Kind == GraphNodeKind.GraphGroup);
        var original = new GraphNodeSnapshot(Guid.NewGuid(), group.Kind, "Group", 0, 0, group.PortType,
            group.PortCategory, ChildGraph: child);
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        graph.InitializeFromSnapshot(new GraphSnapshot([original], []));
        graph.SelectNode(graph.GetNode(original.Id));
        graph.PasteSnapshot(graph.CreateSelectionSnapshot(), 200, 200);
        var pasted = graph.SelectedNode!;
        var copy = pasted.ChildGraph!;
        if (!GraphSnapshot.EquivalentIgnoringIds(child, copy) ||
            copy.ExposedItems![0].Id != item.Id ||
            copy.ExposedItems[0].Targets[0].NodeId != copy.Nodes.Single(node => node.Kind == GraphNodeKind.Float).Id ||
            copy.ExposedItems[1].Targets[0].NodeId != missing)
            throw new InvalidOperationException("Copying a group did not preserve exposed item identity and references.");
        var changed = copy with { ExposedItems = [copy.ExposedItems[0] with { Name = "Renamed" }, copy.ExposedItems[1], copy.ExposedItems[2]] };
        if (GraphSnapshot.EquivalentIgnoringIds(child, changed))
            throw new InvalidOperationException("A change to exposed metadata was not detected.");
        if (!graph.EnterGraph(pasted)) throw new InvalidOperationException("Cannot enter exposed group.");
        graph.SetParameter(graph.GetNode(copy.Nodes.Single(node => node.Kind == GraphNodeKind.Float).Id),
            "value", System.Text.Json.JsonSerializer.SerializeToElement(4d));
        if (graph.CreateCurrentSnapshot().ExposedItems?.Count != 3)
            throw new InvalidOperationException("Editing a child graph dropped exposed metadata.");
        if (!graph.History.Undo() || graph.CreateCurrentSnapshot().ExposedItems?.Count != 3 ||
            !graph.History.Redo() || graph.CreateCurrentSnapshot().ExposedItems?.Count != 3)
            throw new InvalidOperationException("History dropped exposed metadata.");
        graph.NavigateToGraph(0);
        using var stream = new MemoryStream();
        new ProjectFileService().SaveAsync(stream, graph).GetAwaiter().GetResult();
        stream.Position = 0;
        var loaded = ProjectDocumentReader.LoadAsync(stream).GetAwaiter().GetResult();
        if (!GraphSnapshot.ContentEquals(graph.CreateSnapshot(), loaded.Graph))
            throw new InvalidOperationException("Project persistence lost exposed metadata.");
        var downgraded = JsonNode.Parse(stream.ToArray())!.AsObject();
        downgraded["FormatVersion"] = 9;
        using var oldStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(downgraded.ToJsonString()));
        try
        {
            ProjectDocumentReader.LoadAsync(oldStream).GetAwaiter().GetResult();
            throw new Exception("A project with exposed items accepted an old format version.");
        }
        catch (InvalidDataException) { }

        var linked = original with { GroupAsset = new(asset.Id, asset.Revision, false) };
        var resolved = new GroupCompilationResolver([library]).Resolve(new GraphSnapshot([linked], []));
        var resolvedChild = resolved.Nodes.Single().ChildGraph!;
        if (!GraphSnapshot.EquivalentIgnoringIds(child, resolvedChild) ||
            resolvedChild.ExposedItems![0].Targets[0].NodeId !=
            resolvedChild.Nodes.Single(node => node.Kind == GraphNodeKind.Float).Id)
            throw new InvalidOperationException("Group resolution changed exposed metadata.");

        VerifyInstanceValues(library, asset, original, item, curveItem, scalar.Id, curve.Id);
        VerifyNestedAndSpawnable();
        try
        {
            GroupExposedItems.Validate([item with { Step = 0 }]);
            throw new Exception("Invalid exposed metadata was accepted.");
        }
        catch (InvalidDataException) { }
    }

    private static void VerifyNestedAndSpawnable()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var outer = graph.CreateNode(GraphNodeKind.GraphGroup);
        var outerId = outer.Id;
        if (!graph.EnterGraph(outer)) throw new InvalidOperationException("Cannot enter outer group.");
        var inner = graph.CreateNode(GraphNodeKind.GraphGroup);
        var innerId = inner.Id;
        if (!graph.EnterGraph(inner)) throw new InvalidOperationException("Cannot enter inner group.");
        var scalar = graph.CreateNode(GraphNodeKind.Float);
        var scalarId = scalar.Id;
        var item = graph.PublishGroupParameter(scalar, "value");
        graph.NavigateToGraph(1);
        graph.SetGroupInstanceValue(graph.GetNode(innerId), new(item, [new(JsonSerializer.SerializeToElement(6d))]));
        graph.NavigateToGraph(0);
        var original = graph.CreateSnapshot();
        if (VfxProgram.Compile(original).TypedInstructions.Single(instruction => instruction.NodeId == scalarId).DefaultA[0] != 6)
            throw new InvalidOperationException("Nested group instance value did not reach compilation.");
        graph.SelectNode(graph.GetNode(outerId));
        graph.PasteSnapshot(graph.CreateSelectionSnapshot(), 200, 200);
        var pasted = graph.SelectedNode!.ChildGraph!.Nodes.Single();
        var copiedScalar = pasted.ChildGraph!.Nodes.Single();
        if (pasted.GroupOverrides?.Single().ItemId != item ||
            pasted.ChildGraph.ExposedItems!.Single().Targets.Single().NodeId != copiedScalar.Id ||
            VfxProgram.Compile(graph.CreateSnapshot()).TypedInstructions.Single(instruction =>
                instruction.NodeId == copiedScalar.Id).DefaultA[0] != 6)
            throw new InvalidOperationException("Copying a nested group lost the instance value or its remapped target.");

        var spawnGraph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var spawn = spawnGraph.CreateNode(GraphNodeKind.SpawnableGroup);
        var spawnId = spawn.Id;
        if (!spawnGraph.EnterGraph(spawn)) throw new InvalidOperationException("Cannot enter Spawnable group.");
        var spawnScalar = spawnGraph.CreateNode(GraphNodeKind.Float);
        var spawnScalarId = spawnScalar.Id;
        var spawnItem = spawnGraph.PublishGroupParameter(spawnScalar, "value");
        spawnGraph.NavigateToGraph(0);
        spawnGraph.SetGroupInstanceValue(spawnGraph.GetNode(spawnId),
            new(spawnItem, [new(JsonSerializer.SerializeToElement(5d))]));
        var snapshot = spawnGraph.CreateSnapshot();
        var materialized = GroupExposedItems.Materialize(snapshot).Nodes.Single().ChildGraph!;
        if (VfxProgram.Compile(materialized).TypedInstructions.Single(instruction =>
                instruction.NodeId == spawnScalarId).DefaultA[0] != 5 ||
            SpawnableGroupPackage.Compile(snapshot).Templates.Single().Bytecode.Length == 0)
            throw new InvalidOperationException("Spawnable instance value did not reach its template.");
    }

    private static void VerifyInstanceValues(GroupAssetLibrary library, GroupAsset asset, GraphNodeSnapshot original,
        GroupExposedItem item, GroupExposedItem curveItem, Guid scalarId, Guid curveId)
    {
        var profiles = new TeamProfilesService(Path.Combine(AppDataDirectory.Root, "exposed-profile.json"));
        if (!profiles.Save(null, "Exposed", Path.Combine(AppDataDirectory.Root, "exposed-schema-tests")))
            throw new InvalidOperationException(profiles.Status);
        using var catalog = new GroupAssetCatalog(profiles);
        library.SetLocked(asset.Id, true);
        var locked = original with { GroupAsset = new(asset.Id, asset.Revision, true) };
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager(), catalog);
        graph.InitializeFromSnapshot(new GraphSnapshot([locked], []));
        var group = graph.GetNode(locked.Id);
        var scalarValue = new GroupInstanceValue(item.Id, [new(JsonSerializer.SerializeToElement(7d))]);
        graph.SetGroupInstanceValue(group, scalarValue);
        var lockedRevision = graph.History.CurrentNodeId;
        try
        {
            graph.UpdateGroupExposedItem(group, item with { Name = "Not allowed" });
            throw new Exception("A locked group allowed exposure metadata changes.");
        }
        catch (InvalidOperationException) { }
        if (graph.History.CurrentNodeId != lockedRevision)
            throw new InvalidOperationException("Rejecting locked metadata changes affected history.");
        if (group.ChildGraph!.Nodes.Single(node => node.Id == scalarId).Parameters?.Any() == true ||
            library.Load(asset.Id).Graph.Nodes.Single(node => node.Id == scalarId).Parameters?.Any() == true)
            throw new InvalidOperationException("Instance editing changed the shared group graph.");
        var revision = graph.History.CurrentNodeId;
        try
        {
            graph.SetGroupInstanceValue(group, new(item.Id, [new(JsonSerializer.SerializeToElement("bad"))]));
            throw new Exception("An invalid instance value was accepted.");
        }
        catch (InvalidDataException) { }
        if (graph.History.CurrentNodeId != revision)
            throw new InvalidOperationException("Rejecting an instance value changed history.");
        library.SetLocked(asset.Id, false);
        try
        {
            graph.UpdateGroupExposedItem(group, item with { Maximum = 6 });
            throw new Exception("A range excluding an existing instance value was accepted.");
        }
        catch (InvalidDataException) { }
        if (graph.History.CurrentNodeId != revision)
            throw new InvalidOperationException("Rejecting a range change affected history.");
        var newCurve = new FCurveSnapshot([
            new(Guid.NewGuid(), 0, 3, 0, 0, CurveInterpolation.Linear),
            new(Guid.NewGuid(), 1, 5, 0, 0, CurveInterpolation.Linear)
        ]);
        graph.SetGroupInstanceValue(group, new(curveItem.Id, [new(Curve: newCurve)]));
        var compiled = VfxProgram.Compile(graph.CreateSnapshot());
        if (compiled.TypedInstructions.Single(instruction => instruction.NodeId == scalarId).DefaultA[0] != 7 ||
            !compiled.CurveInstructions.Single(instruction => instruction.NodeId == curveId).Keys.SequenceEqual(newCurve.Keys))
            throw new InvalidOperationException("Instance values did not reach compiled bytecode.");
        if (!graph.History.Undo() || graph.GetNode(locked.Id).GroupOverrides?.Count != 1 ||
            !graph.History.Redo() || graph.GetNode(locked.Id).GroupOverrides?.Count != 2)
            throw new InvalidOperationException("Instance value editing was not undoable.");
        using var stream = new MemoryStream();
        new ProjectFileService().SaveAsync(stream, graph).GetAwaiter().GetResult();
        stream.Position = 0;
        var loaded = ProjectDocumentReader.LoadAsync(stream).GetAwaiter().GetResult();
        if (!GraphSnapshot.ContentEquals(loaded.Graph, graph.CreateSnapshot()))
            throw new InvalidOperationException("Instance values did not survive project persistence.");
        var resolved = new GroupCompilationResolver([library]).Resolve(loaded.Graph);
        var resolvedProgram = VfxProgram.Compile(resolved);
        if (!resolvedProgram.TypedInstructions.Any(instruction => instruction.DefaultA[0] == 7) ||
            !resolvedProgram.CurveInstructions.Any(instruction => instruction.Keys.SequenceEqual(newCurve.Keys)))
            throw new InvalidOperationException("Linked group resolution lost instance values.");
        library.Save(asset.Id, asset.Revision, asset.Name, asset.Graph);
        catalog.Refresh();
        var update = graph.FindGroupUpdates().Single(candidate => candidate.NodeId == locked.Id);
        if (!update.HasLocalChanges || graph.ApplyGroupUpdates([update]) != 0 ||
            graph.GetNode(locked.Id).GroupAsset?.Revision != asset.Revision)
            throw new InvalidOperationException("A group update replaced locally edited instance values.");
        try
        {
            new GroupCompilationResolver([library]).Resolve(loaded.Graph);
            throw new Exception("A newer group revision silently replaced instance values.");
        }
        catch (InvalidDataException) { }
        group = graph.GetNode(locked.Id);
        graph.ResetGroupInstanceValue(group, item.Id);
        if (graph.GetNode(locked.Id).GroupOverrides?.Any(value => value.ItemId == item.Id) != false ||
            !graph.History.Undo() || graph.GetNode(locked.Id).GroupOverrides?.Count != 2)
            throw new InvalidOperationException("Resetting an instance value was not undoable.");
    }
}
