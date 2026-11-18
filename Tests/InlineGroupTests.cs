using System.Text.Json;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;

// Model/source regressions, intentionally no window, native engine, or user settings.
// Invoked by the existing GroupBoundaryTests suite.
internal static class InlineGroupTests
{
    public static void Run()
    {
        VerifySharedDocument();
        VerifyBoundaryAndHistory();
        VerifyLinkedLocks();
        VerifyActiveDocumentIntegration();
    }

    private static void VerifyActiveDocumentIntegration()
    {
        var main = new MainViewModel();
        try
        {
            var document = main.ActiveDocument!;
            var root = document.Workspace;
            var group = root.CreateNode(GraphNodeKind.GraphGroup);
            var child = root.OpenInlineGraph(group);
            child.ActivateEditingWorkspace();
            Require(ReferenceEquals(main.Graph, child) &&
                ReferenceEquals(main.ActiveGraphProvider.Graph, child) &&
                ReferenceEquals(main.ActiveDocument?.Workspace, root),
                "Inspector/MCP editing context must change without replacing the document canvas.");
            var asset = new EngineAsset("/Game/Inline/SM_Test.SM_Test", "Inline asset", "model", "Unreal");
            var node = RenderNodeAuthoring.CreateModel(child, asset);
            child.SelectNode(node);
            var browser = main.AssetBrowser;
            var target = ResourceAssignment.TargetForParameter(node, "assetPath")!;
            var request = browser.BeginForParameter(document, child, node, "assetPath", target, "model", () => true);
            Require(request is not null && browser.IsCurrent(request, null, requiresPeer: false),
                "An inline inspector resource field could not capture its own document/graph target.");
            root.ActivateEditingWorkspace();
            Require(ReferenceEquals(main.Graph, root) && request?.IsStale == true &&
                !browser.IsCurrent(request, null, requiresPeer: false),
                "Returning to the parent did not invalidate an inline resource caller.");
            var before = root.CreateSnapshot();
            main.UndoCommand.Execute(null);
            Require(!GraphSnapshot.ContentEquals(before, root.CreateSnapshot()) &&
                child.Nodes.All(candidate => candidate.Id != node.Id),
                "Document-level Undo did not restore the inline editing state.");
        }
        finally { main.DisposeStorageFiles(); }
    }

    private static void VerifySharedDocument()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var group = graph.CreateNode(GraphNodeKind.GraphGroup, 20, 30);
        var groupId = group.Id;
        var parent = graph.CreateNode(GraphNodeKind.Float, 800, 40);
        var parentId = parent.Id;
        graph.SetInlinePresentation(group, true, 720, 440);
        var child = graph.OpenInlineGraph(group);
        Require(ReferenceEquals(child, graph.OpenInlineGraph(group)) &&
                ReferenceEquals(child.History, graph.History) && ReferenceEquals(child.Document, graph),
            "Inline controls must share the path's editing state and document history.");
        var first = child.CreateNode(GraphNodeKind.Float, 50, 60);
        var firstId = first.Id;
        Require(graph.Nodes.Count == 2 && ReferenceEquals(graph.GetNode(groupId), group) &&
                group.ChildGraph!.Nodes.Single().Id == firstId,
            "Editing a child must retain the parent node/control and update its saved child immediately.");
        graph.MoveNode(parent, 920, 90);
        child.PublishGroupParameter(child.GetNode(firstId), "value");
        Require(child.CreateCurrentSnapshot().ExposedItems?.Single().Targets.Single().NodeId == firstId &&
                graph.GetNode(parentId).X == 920,
            "An inline snapshot-based command lost parent updates or exposed metadata.");
        child.MoveNode(child.GetNode(firstId), 100, 120);
        var nested = child.CreateNode(GraphNodeKind.GraphGroup, 300, 120);
        var nestedId = nested.Id;
        child.SetInlinePresentation(nested, true, 540, 300);
        var grandchild = child.OpenInlineGraph(nested);
        var leafId = grandchild.CreateNode(GraphNodeKind.Float, 80, 90).Id;
        child.RenameNode(child.GetNode(firstId), "Inline value");
        Require(grandchild.Nodes.Single().Id == leafId && graph.GetNode(parentId).X == 920,
            "A parent or sibling view overwrote a newer descendant edit.");
        Require(child.MaximizeInlineGraph(child.GetNode(nestedId)) &&
                graph.GraphPath.SequenceEqual(new[] { groupId, nestedId }) &&
                graph.Nodes.Single().Id == leafId,
            "Inline maximize did not use the document navigation.");
        var secondLeafId = graph.CreateNode(GraphNodeKind.Float, 350, 90).Id;
        Require(grandchild.Nodes.Any(node => node.Id == secondLeafId),
            "The inline workspace did not receive an edit made in the maximized workspace.");
        grandchild.SetParameter(grandchild.GetNode(leafId), "value", JsonSerializer.SerializeToElement(4d));
        Require(graph.GetNode(leafId).Parameters!.Single(p => p.Id == "value").Value.GetDouble() == 4,
            "The maximized workspace did not receive an inline edit.");
        graph.NavigateToGraph(0);
        Require(graph.GetNode(groupId).IsInlineExpanded && graph.GetNode(groupId).InlineWidth == 720 &&
                graph.GetNode(groupId).InlineHeight == 440 && graph.GetNode(parentId).Y == 90,
            "Returning from maximize lost dimensions or parent edits.");
        Require(graph.History.Undo() && grandchild.GetNode(leafId).Parameters?.Any(p =>
                    p.Id == "value" && p.Value.GetDouble() == 4) != true &&
                graph.GetNode(parentId).X == 920 && graph.History.Redo(),
            "Undo/redo must restore one full document, not a child-only timeline.");
        using var stream = new MemoryStream();
        new ProjectFileService().SaveAsync(stream, graph).GetAwaiter().GetResult();
        stream.Position = 0;
        var loaded = ProjectDocumentReader.LoadAsync(stream).GetAwaiter().GetResult();
        Require(GraphSnapshot.ContentEquals(graph.CreateSnapshot(), loaded.Graph),
            "Inline dimensions/nested edits did not persist through the existing project format.");
        graph.SetSelection([graph.GetNode(groupId)]);
        graph.PasteSnapshot(graph.CreateSelectionSnapshot(), 1400, 100);
        var copy = graph.SelectedNode!;
        Require(copy.Id != groupId && copy.IsInlineExpanded && copy.InlineWidth == 720 &&
                copy.ChildGraph!.Nodes.Single(n => n.Kind == GraphNodeKind.GraphGroup).Id != nestedId,
            "Copying an expanded group lost presentation or failed to remap child IDs.");
        var snapshot = graph.CreateSnapshot();
        GraphValidator.Validate(snapshot);
        var historyId = graph.History.CurrentNodeId;
        graph.CreateSnapshot();
        child.CreateSnapshot();
        graph.OpenInlineGraph(graph.GetNode(groupId));
        Require(graph.History.CurrentNodeId == historyId, "Opening/reading an inline view created a history entry.");
        var signal = child.CreateNode(GraphNodeKind.Signal);
        var forwarded = false;
        graph.PreviewSignalRequested += (sender, id) =>
            forwarded = ReferenceEquals(sender, child) && id == signal.Id;
        signal.SendPreviewSignalCommand.Execute(null);
        Require(forwarded, "Inline preview intent did not reach the document host with its source workspace.");
        child.ActivateEditingWorkspace();
        graph.DeleteNode(graph.GetNode(groupId));
        Require(child.IsReadOnly && grandchild.IsReadOnly, "A removed graph left a writable stale inline workspace.");
        Require(ReferenceEquals(graph.ActiveEditingWorkspace, graph),
            "A removed inline path remained the active inspector/MCP target.");
        Reject(() => child.CreateNode(GraphNodeKind.Float));
        Require(graph.History.Undo() && !child.IsReadOnly &&
                child.GetNode(nestedId).ChildGraph!.Nodes.Count == 2,
            "Undo did not reattach the existing inline workspace to its restored path.");
    }

    private static void VerifyBoundaryAndHistory()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var group = graph.CreateNode(GraphNodeKind.GraphGroup);
        var groupId = group.Id;
        graph.SetInlinePresentation(group, true, 680, 400);
        var child = graph.OpenInlineGraph(group);
        var input = child.CreateNode(new GraphNodeKind("GroupInputFloat"), 30, 30);
        var output = child.CreateNode(new GraphNodeKind("GroupOutputFloat"), 320, 30);
        var inputId = input.Id;
        var outputId = output.Id;
        Require(child.TryConnectNodes(input, output, out _), "Inline nodes could not be connected.");
        var source = graph.CreateNode(GraphNodeKind.Float, -300, 30);
        var target = graph.CreateNode(GraphNodeKind.Float, 900, 30);
        Require(graph.TryConnectNodes(source, group, out _, GroupBoundarySchema.InputPortId(inputId)) &&
                graph.TryConnectNodes(group, target, out _, sourcePortId: GroupBoundarySchema.OutputPortId(outputId)),
            "Inline boundary changes did not refresh the parent's external ports.");
        var edge = graph.Connections.Single(connection => connection.Source.Id == groupId);
        var oldX = edge.StartX;
        graph.SetInlinePresentation(group, true, 880, 500);
        Require(edge.StartX > oldX, "Resizing an expanded group did not move its external output endpoint.");
        child.SetSelection([input]);
        Require(graph.SelectedNodes.All(node => node.Id != inputId),
            "Child selection leaked into the parent workspace.");
        child.DeleteNode(child.GetNode(outputId));
        Require(graph.Connections.Count == 1 && child.Connections.Count == 0,
            "Deleting an inline boundary did not remove the invalid parent and child edges.");
        Require(graph.History.Undo() && graph.Connections.Count == 2 && child.Connections.Count == 1 &&
                graph.GetNode(groupId).OutputPorts.Single().Id == GroupBoundarySchema.OutputPortId(outputId),
            "Boundary deletion and external edges were not restored in one undo.");
        child.DisconnectConnection(child.Connections.Single());
        Require(child.Connections.Count == 0 && graph.History.Undo() && child.Connections.Count == 1,
            "Inline connection deletion did not use document history.");
        Require(VfxProgram.Compile(graph.CreateSnapshot()).TypedInstructions.Any(instruction => instruction.NodeId == inputId),
            "Persisted inline layout metadata broke the existing group compiler route.");
        graph.SetInlinePresentation(graph.GetNode(groupId), false, 880, 500);
        Require(!graph.GetNode(groupId).IsInlineExpanded && graph.History.Undo() &&
                graph.GetNode(groupId).IsInlineExpanded,
            "Expand/collapse was not persisted in document history.");
    }

    private static void VerifyLinkedLocks()
    {
        var directory = Path.Combine(Path.GetTempPath(), "liberafx-inline-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = new GraphWorkspaceViewModel(new UndoRedoManager());
            var original = source.CreateNode(GraphNodeKind.GraphGroup);
            var sourceChild = source.OpenInlineGraph(original);
            var scalarId = sourceChild.CreateNode(GraphNodeKind.Float).Id;
            var itemId = sourceChild.PublishGroupParameter(sourceChild.GetNode(scalarId), "value");
            var profiles = new TeamProfilesService(Path.Combine(directory, "profiles.json"));
            var libraryDirectory = Path.Combine(directory, "groups");
            Require(profiles.Save(null, "Inline fixture", libraryDirectory), "Cannot create isolated library fixture.");
            var library = new GroupAssetLibrary(libraryDirectory);
            var asset = library.Create("Inline linked fixture", source.GetNode(original.Id).ChildGraph!);
            using var catalog = new GroupAssetCatalog(profiles);
            var graph = new GraphWorkspaceViewModel(new UndoRedoManager(), catalog);
            var group = graph.CreateGroupAssetNode(asset.Id, 0, 0);
            var child = graph.OpenInlineGraph(group);
            var scalar = child.Nodes.Single();
            child.CreateNode(GraphNodeKind.Float, 300, 20);
            Require(library.Load(asset.Id).Graph.Nodes.Count == 1 && library.Load(asset.Id).Revision == 1,
                "An inline edit silently overwrote a shared library asset.");
            library.SetLocked(asset.Id, true);
            catalog.Refresh();
            Require(child.IsReadOnly, "A late library lock was not applied to an already open inline view.");
            var before = graph.CreateSnapshot();
            var historyId = graph.History.CurrentNodeId;
            Reject(() => child.CreateNode(GraphNodeKind.Float));
            Reject(() => child.MoveNode(scalar, 40, 50));
            Reject(() => child.RenameNode(scalar, "Forbidden"));
            Reject(() => child.SetParameter(scalar, "value", JsonSerializer.SerializeToElement(9d)));
            Reject(() => child.DeleteNode(scalar));
            Reject(() => child.TryConnectNodes(scalar, child.Nodes.Last(), out _));
            Require(historyId == graph.History.CurrentNodeId && GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()),
                "A rejected locked edit changed the document/history.");
            graph.SetGroupInstanceValue(graph.GetNode(group.Id), new(itemId, [new(JsonSerializer.SerializeToElement(7d))]));
            Require(graph.GetNode(group.Id).GroupOverrides!.Single().Components.Single().Value!.Value.GetDouble() == 7 &&
                    child.IsReadOnly,
                "A structural lock incorrectly blocked the exposed instance value.");
            Require(graph.MaximizeInlineGraph(graph.GetNode(group.Id)) && graph.IsReadOnly,
                "A locked group could not be maximized for read-only inspection.");
            graph.NavigateToGraph(0);
            library.SetLocked(asset.Id, false);
            catalog.Refresh();
            child.CreateNode(GraphNodeKind.Float);
            Require(!child.IsReadOnly && child.Nodes.Count == 3, "Unlocking did not restore inline authoring.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("A stale or locked inline edit was accepted.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
