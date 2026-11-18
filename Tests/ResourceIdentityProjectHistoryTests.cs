using System.Text.Json;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Resources;
using LiberaFx.Services;
using LiberaFx.ViewModels;

// Parent test runner should call Run(); the suite is isolated to in-memory projects.
internal static class ResourceIdentityProjectHistoryTests
{
    public static void Run()
    {
        var nodeDefinition = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.Float);
        var nodeId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var project = new EngineProjectIdentity("Unreal", Guid.NewGuid());
        var graph = new GraphSnapshot(
        [
            new GraphNodeSnapshot(nodeId, nodeDefinition.Kind, "Value", 0, 0,
                nodeDefinition.PortType, nodeDefinition.PortCategory)
        ], []);
        var identities = new ResourceIdentityDocument(1,
            [new ResourceDefinition(resourceId, "model", "Explicit model")],
            [new ProjectResourceBinding(project, resourceId, "model",
                new OpaqueBinding("unreal.object-path", "/Game/Explicit/Asset.Asset"))],
            [], [new ResourceUse(nodeId, "assetPath", resourceId)], []);

        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        workspace.InitializeFromSnapshot(graph);
        workspace.SetResourceIdentityDocument(identities, "Register explicit resource");
        Require(workspace.ResourceIdentities is not null, "Identity state was not installed.");
        Require(workspace.History.Undo(), "Identity registration was not undoable.");
        Require(workspace.ResourceIdentities is null, "Undo did not restore the unregistered state.");
        Require(workspace.History.Redo(), "Identity registration was not redoable.");
        Require(workspace.ResourceIdentities?.Resources.Single().Id == resourceId,
            "Redo did not restore stable IDs.");

        using var saved = new MemoryStream();
        var result = new ProjectFileService().SaveAsync(saved, workspace).GetAwaiter().GetResult();
        saved.Position = 0;
        var loaded = ProjectDocumentReader.LoadAsync(saved).GetAwaiter().GetResult();
        Require(GraphSnapshot.ContentEquals(result.Graph, loaded.Graph),
            "Project save/load changed graph or resource identity state.");
        Require(loaded.History is not null &&
            loaded.History.Entries.Any(entry => entry.State.ResourceIdentities is null) &&
            loaded.History.Entries.Any(entry => entry.State.ResourceIdentities is not null),
            "Resource registration did not persist across history branches.");

        var restored = new GraphWorkspaceViewModel(new UndoRedoManager());
        restored.InitializeFromSnapshot(loaded.Graph);
        restored.History.LoadSnapshot(loaded.History!);
        Require(restored.ResourceIdentities?.Uses.Single().ResourceId == resourceId,
            "History restore lost the project mapping.");
        Require(restored.History.Undo() && restored.ResourceIdentities is null,
            "Loaded history cannot undo resource registration.");
        Require(restored.History.Redo() && restored.ResourceIdentities?.Resources.Single().Id == resourceId,
            "Loaded history cannot redo resource registration.");

        // A pre-identity project remains references-first: a legacy path is retained verbatim
        // and does not become an ID or a binding merely because it looks like an engine path.
        var legacyDefinition = GraphNodeDefinition.All.Single(item => item.TypeName == "ModelDraw");
        var legacyId = Guid.NewGuid();
        var legacyNode = new LiberaFx.Models.Project.LiberaFxProjectNode(legacyId, "Model",
            legacyDefinition.Kind.Value, 0, 0, Parameters:
            [new("assetPath", "string", JsonSerializer.SerializeToElement("/Game/Old/Old.Old"))]);
        var legacyState = new GraphSnapshot(
        [
            new GraphNodeSnapshot(legacyId, legacyDefinition.Kind, "Model", 0, 0,
                legacyDefinition.PortType, legacyDefinition.PortCategory,
                Parameters: legacyNode.Parameters)
        ], []);
        var historyId = Guid.NewGuid();
        var legacy = new LiberaFx.Models.Project.LiberaFxProjectDocument
        {
            FormatVersion = 12,
            Nodes = [legacyNode],
            Connections = [],
            History = new UndoRedoHistorySnapshot(historyId, historyId, Guid.NewGuid(), 1,
                [new UndoRedoHistoryEntry(historyId, null, null, "Start", "Main", false, legacyState)])
        };
        using var legacyStream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(legacy));
        var migrated = ProjectDocumentReader.LoadAsync(legacyStream).GetAwaiter().GetResult();
        Require(migrated.Graph.ResourceIdentities is null &&
            migrated.Graph.Nodes.Single().Parameters!.Single(item => item.Id == "assetPath")
                .Value.GetString() == "/Game/Old/Old.Old",
            "Legacy resource data was erased or implicitly migrated.");

        VerifyScopedClipboard(legacyState, identities);
    }

    private static void VerifyScopedClipboard(GraphSnapshot child, ResourceIdentityDocument registry)
    {
        var group = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.GraphGroup);
        var parentId = Guid.NewGuid();
        var sourceUse = registry.Uses.Single() with { NodeId = child.Nodes.Single().Id, InstancePath = [parentId] };
        var source = new GraphWorkspaceViewModel(new UndoRedoManager());
        source.InitializeFromSnapshot(new GraphSnapshot(
            [new(parentId, group.Kind, "Group", 0, 0, group.PortType, group.PortCategory, ChildGraph: child)], [])
        {
            ResourceIdentities = registry with { Uses = [sourceUse] }
        });
        source.SelectNode(source.GetNode(parentId));
        var copied = JsonSerializer.Deserialize<GraphSnapshot>(JsonSerializer.Serialize(source.CreateSelectionSnapshot()))!;

        var target = new GraphWorkspaceViewModel(new UndoRedoManager());
        target.InitializeFromSnapshot(new GraphSnapshot([], []));
        target.PasteSnapshot(copied, 100, 100);
        var pasted = target.Nodes.Single();
        var pastedUse = target.ResourceIdentities!.Uses.Single();
        Require(pastedUse.ResourceId == sourceUse.ResourceId && pastedUse.NodeId != sourceUse.NodeId &&
            pastedUse.NodeId == pasted.ChildGraph!.Nodes.Single().Id &&
            pastedUse.InstancePath!.SequenceEqual(new[] { pasted.Id }) && pasted.Id != parentId,
            "Pasting a nested graph did not remap its scoped resource use.");
        Require(target.ResourceIdentities.ResourceBindings.SequenceEqual(registry.ResourceBindings),
            "Clipboard lost the exact project binding.");
        Require(target.History.Undo() && target.Nodes.Count == 0 && target.ResourceIdentities is null,
            "Paste did not undo nodes and copied registry together.");
        Require(target.History.Redo() && target.ResourceIdentities!.Uses.Single().NodeId == pastedUse.NodeId,
            "Paste redo did not restore the resource use.");
        target.DeleteNode(target.GetNode(pasted.Id));
        Require(target.ResourceIdentities!.Uses.Count == 0 && target.ResourceIdentities.Resources.Count == 1,
            "Deleting a group must remove its descendant uses without unregistering the resource.");
        Require(target.History.Undo() && target.ResourceIdentities!.Uses.Count == 1,
            "Undo did not restore deleted scoped uses.");

        var conflicting = new GraphWorkspaceViewModel(new UndoRedoManager());
        var existing = registry with
        {
            Uses = [],
            ResourceBindings = [registry.ResourceBindings.Single() with
            {
                Binding = new("unreal.object-path", "/Game/Different/Different.Different")
            }]
        };
        conflicting.InitializeFromSnapshot(new GraphSnapshot([], []) { ResourceIdentities = existing });
        var before = conflicting.CreateSnapshot();
        try
        {
            conflicting.PasteSnapshot(copied, 100, 100);
            throw new InvalidOperationException("Paste overwrote a conflicting resource binding.");
        }
        catch (InvalidDataException)
        {
            Require(GraphSnapshot.ContentEquals(before, conflicting.CreateSnapshot()) && !conflicting.History.Undo(),
                "A conflicting paste changed the document or added a history entry.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
