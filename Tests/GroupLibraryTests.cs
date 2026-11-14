using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;

internal static class GroupLibraryTests
{
    public static void Run()
    {
        var root = Path.Combine(AppDataDirectory.Root, "library-tests");
        var profilesPath = Path.Combine(root, "profiles.json");
        var libraryPath = Path.Combine(root, "groups");
        Guid assetId = Guid.Empty;
        Guid parentId = Guid.Empty;
        try
        {
            var profiles = new TeamProfilesService(profilesPath);
            var original = profiles.Active.Id;
            if (!profiles.Save(null, "Team A", libraryPath)) throw new InvalidOperationException(profiles.Status);
            var teamId = profiles.Active.Id;
            if (!profiles.Select(original)) throw new InvalidOperationException(profiles.Status);
            var restored = new TeamProfilesService(profilesPath);
            if (restored.Active.Id != original ||
                restored.Profiles.Single(item => item.Id == teamId).GroupDirectory != libraryPath ||
                restored.Save(null, "Team A", libraryPath) || restored.Save(null, "Team B", "relative"))
                throw new InvalidOperationException("Project profile validation or persistence failed.");

            var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
            workspace.CreateNode(GraphNodeKind.Float);
            var library = new GroupAssetLibrary(libraryPath);
            var asset = library.Create("Example group", workspace.CreateSnapshot());
            assetId = asset.Id;
            if (library.LoadAll(out var warnings).Single().Id != asset.Id || warnings.Count != 0)
                throw new InvalidOperationException("Group asset was not listed.");
            library.SetLocked(asset.Id, true);
            try
            {
                library.Save(asset.Id, asset.Revision, asset.Name, asset.Graph);
                throw new Exception("A locked group was overwritten.");
            }
            catch (InvalidOperationException) { }
            library.SetLocked(asset.Id, false);
            var updated = library.Save(asset.Id, asset.Revision, "Updated group", asset.Graph);
            if (updated.Revision != 2 || library.LoadRevision(asset.Id, 1).Name != asset.Name)
                throw new InvalidOperationException("Group revision was not preserved.");
            try
            {
                library.Save(asset.Id, 1, asset.Name, asset.Graph);
                throw new Exception("A stale group revision was overwritten.");
            }
            catch (InvalidOperationException) { }
            if (!restored.Select(teamId)) throw new InvalidOperationException(restored.Status);
            using var catalog = new GroupAssetCatalog(restored);
            var graph = new GraphWorkspaceViewModel(new UndoRedoManager(), catalog);
            var nodeList = new NodeCatalogViewModel(new ActiveGraphProvider(graph), catalog);
            if (!nodeList.Groups.SelectMany(group => group.Nodes).Any(node => node.GroupAssetId == asset.Id))
                throw new InvalidOperationException("Project group is missing from the node list.");
            var placed = graph.CreateGroupAssetNode(asset.Id, 80, 90);
            var placedId = placed.Id;
            if (placed.GroupAsset?.Revision != 2 || placed.ChildGraph!.Nodes[0].Id == asset.Graph.Nodes[0].Id)
                throw new InvalidOperationException("Group placement did not preserve the asset link or clone node IDs.");
            if (!graph.History.Undo() || graph.Nodes.Any(node => node.Id == placedId) ||
                !graph.History.Redo() || graph.GetNode(placedId).GroupAsset?.Id != asset.Id)
                throw new InvalidOperationException("Group placement was not undoable.");
            graph.SelectNode(graph.GetNode(placedId));
            graph.ToggleSelectedGroupAssetLock();
            if (graph.EnterGraph(graph.GetNode(placedId)))
                throw new InvalidOperationException("A locked group was editable.");
            graph.ToggleSelectedGroupAssetLock();
            if (!graph.EnterGraph(graph.GetNode(placedId)))
                throw new InvalidOperationException("An unlocked group was not editable.");
            graph.NavigateToGraph(0);
            using var project = new MemoryStream();
            new ProjectFileService().SaveAsync(project, graph).GetAwaiter().GetResult();
            project.Position = 0;
            var reloaded = ProjectDocumentReader.LoadAsync(project).GetAwaiter().GetResult();
            if (reloaded.Graph.Nodes.Single().GroupAsset?.Id != asset.Id)
                throw new InvalidOperationException("The saved project lost its group link.");
            var edited = graph.CreateGroupAssetNode(asset.Id, 180, 90);
            var editedId = edited.Id;
            if (!graph.EnterGraph(edited)) throw new InvalidOperationException("Could not edit group instance.");
            graph.CreateNode(GraphNodeKind.Float);
            graph.NavigateToGraph(0);
            workspace.CreateNode(GraphNodeKind.Time);
            library.Save(asset.Id, updated.Revision, updated.Name, workspace.CreateSnapshot());
            catalog.Refresh();
            var pending = graph.FindGroupUpdates();
            if (pending.Count != 2 || pending.Single(item => item.NodeId == placedId).HasLocalChanges ||
                !pending.Single(item => item.NodeId == editedId).HasLocalChanges)
                throw new InvalidOperationException("Group updates did not detect local changes.");
            if (graph.ApplyGroupUpdates(pending) != 1 || graph.GetNode(placedId).GroupAsset?.Revision != 3 ||
                graph.GetNode(editedId).GroupAsset?.Revision != 2)
                throw new InvalidOperationException("Safe group updates did not preserve the edited instance.");
            if (!graph.History.Undo() || graph.GetNode(placedId).GroupAsset?.Revision != 2 ||
                !graph.History.Redo() || graph.GetNode(placedId).GroupAsset?.Revision != 3)
                throw new InvalidOperationException("Batch group updates were not undoable.");

            var parentSource = new GraphWorkspaceViewModel(new UndoRedoManager(), catalog);
            parentSource.CreateGroupAssetNode(asset.Id, 0, 0);
            var parent = library.Create("Parent", parentSource.CreateSnapshot());
            parentId = parent.Id;
            var nested = new GraphWorkspaceViewModel(new UndoRedoManager(), catalog);
            var parentNode = nested.CreateGroupAssetNode(parent.Id, 0, 0);
            var parentNodeId = parentNode.Id;
            workspace.CreateNode(GraphNodeKind.Float);
            library.Save(asset.Id, 3, asset.Name, workspace.CreateSnapshot());
            library.Save(parent.Id, 1, parent.Name, parent.Graph);
            catalog.Refresh();
            var nestedUpdates = nested.FindGroupUpdates();
            if (nestedUpdates.Count != 2 || nestedUpdates.Any(item => item.HasLocalChanges))
                throw new InvalidOperationException("Nested group updates were not detected.");
            var baselinePath = Path.Combine(libraryPath, ".versions", asset.Id.ToString("N"), "3.lfxgroup");
            var baselineBytes = File.ReadAllBytes(baselinePath);
            try
            {
                File.WriteAllText(baselinePath, "{invalid");
                if (nested.FindGroupUpdates().Any(item => !item.HasLocalChanges))
                    throw new InvalidOperationException("A damaged baseline was not protected.");
            }
            finally { File.WriteAllBytes(baselinePath, baselineBytes); }
            var otherDocument = new GraphWorkspaceViewModel(new UndoRedoManager(), catalog);
            otherDocument.CreateGroupAssetNode(parent.Id, 0, 0);
            var otherUpdates = otherDocument.FindGroupUpdates();
            var otherPrepared = otherDocument.PrepareGroupUpdates(otherUpdates)
                ?? throw new InvalidOperationException("Second document updates were not prepared.");
            library.Save(parent.Id, 2, parent.Name, parent.Graph);
            var rejected = false;
            try { nested.PrepareGroupUpdates(nestedUpdates); }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected || otherDocument.Nodes.Single().GroupAsset?.Revision != 2 ||
                nested.GetNode(parentNodeId).GroupAsset?.Revision != 1)
                throw new InvalidOperationException("A stale batch modified a document or was not rejected.");
            catalog.Refresh();
            nestedUpdates = nested.FindGroupUpdates();
            var prepared = nested.PrepareGroupUpdates(nestedUpdates)
                ?? throw new InvalidOperationException("Nested group updates were not prepared.");
            nested.CreateNode(GraphNodeKind.Time);
            rejected = false;
            try { nested.CommitGroupUpdates(prepared); }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected || !nested.History.Undo())
                throw new InvalidOperationException("A prepared update overwrote a changed graph.");
            prepared = nested.PrepareGroupUpdates(nestedUpdates)
                ?? throw new InvalidOperationException("Nested group updates were not prepared.");
            if (nested.GetNode(parentNodeId).GroupAsset?.Revision != 1 || nested.CommitGroupUpdates(prepared) != 2 ||
                nested.GetNode(parentNodeId).ChildGraph!.Nodes.Single().GroupAsset?.Revision != 4 ||
                !nested.History.Undo() ||
                nested.GetNode(parentNodeId).ChildGraph!.Nodes.Single().GroupAsset?.Revision != 3)
                throw new InvalidOperationException("Nested updates were not applied and undone together.");

            if (!nested.EnterGraph(nested.GetNode(parentNodeId)))
                throw new InvalidOperationException("Could not edit parent group.");
            var childNode = nested.Nodes.Single();
            if (!nested.EnterGraph(childNode)) throw new InvalidOperationException("Could not edit nested group.");
            nested.CreateNode(GraphNodeKind.Time);
            nested.NavigateToGraph(0);
            nestedUpdates = nested.FindGroupUpdates();
            if (nestedUpdates.Count != 2 || nestedUpdates.Any(item => !item.HasLocalChanges) ||
                nested.ApplyGroupUpdates(nestedUpdates) != 0 ||
                nested.GetNode(parentNodeId).ChildGraph!.Nodes.Single().ChildGraph!.Nodes.Count !=
                    parent.Graph.Nodes.Single().ChildGraph!.Nodes.Count + 1)
                throw new InvalidOperationException("A nested local change was not protected.");
            if (!restored.Select(original) ||
                nodeList.Groups.SelectMany(group => group.Nodes).Any(node => node.GroupAssetId == asset.Id) ||
                !nodeList.Groups.SelectMany(group => group.Nodes).Any(node => node.KindName == "Float") ||
                graph.GetNode(placedId).GroupAsset?.Id != asset.Id)
                throw new InvalidOperationException("Changing projects did not isolate the catalog or preserve the open graph.");
            File.WriteAllText(profilesPath, "{invalid");
            var invalid = new TeamProfilesService(profilesPath);
            if (invalid.Save(null, "Team B", libraryPath) || File.ReadAllText(profilesPath) != "{invalid")
                throw new InvalidOperationException("Corrupted settings were overwritten.");
        }
        finally
        {
            if (File.Exists(profilesPath)) File.Delete(profilesPath);
            if (assetId != Guid.Empty)
            {
                var name = assetId.ToString("N");
                var archiveDirectory = Path.Combine(libraryPath, ".versions", name);
                var archive = Path.Combine(archiveDirectory, "1.lfxgroup");
                var asset = Path.Combine(libraryPath, name + ".lfxgroup");
                var lockFile = Path.Combine(libraryPath, "." + name + ".lock");
                if (File.Exists(archive)) File.Delete(archive);
                foreach (var revision in new[] { 2, 3 })
                {
                    var archived = Path.Combine(archiveDirectory, $"{revision}.lfxgroup");
                    if (File.Exists(archived)) File.Delete(archived);
                }
                if (File.Exists(asset)) File.Delete(asset);
                if (File.Exists(lockFile)) File.Delete(lockFile);
                if (Directory.Exists(archiveDirectory)) Directory.Delete(archiveDirectory);
            }
            if (parentId != Guid.Empty)
            {
                var name = parentId.ToString("N");
                var archived = Path.Combine(libraryPath, ".versions", name, "1.lfxgroup");
                var archived2 = Path.Combine(libraryPath, ".versions", name, "2.lfxgroup");
                var archiveDirectory = Path.GetDirectoryName(archived)!;
                var parent = Path.Combine(libraryPath, name + ".lfxgroup");
                var lockFile = Path.Combine(libraryPath, "." + name + ".lock");
                if (File.Exists(archived)) File.Delete(archived);
                if (File.Exists(archived2)) File.Delete(archived2);
                if (File.Exists(parent)) File.Delete(parent);
                if (File.Exists(lockFile)) File.Delete(lockFile);
                if (Directory.Exists(archiveDirectory)) Directory.Delete(archiveDirectory);
            }
            var versions = Path.Combine(libraryPath, ".versions");
            if (Directory.Exists(versions)) Directory.Delete(versions);
            if (Directory.Exists(libraryPath)) Directory.Delete(libraryPath);
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }
}
