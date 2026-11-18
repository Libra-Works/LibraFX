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
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
