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
                if (File.Exists(asset)) File.Delete(asset);
                if (File.Exists(lockFile)) File.Delete(lockFile);
                if (Directory.Exists(archiveDirectory)) Directory.Delete(archiveDirectory);
                var versions = Path.Combine(libraryPath, ".versions");
                if (Directory.Exists(versions)) Directory.Delete(versions);
            }
            if (Directory.Exists(libraryPath)) Directory.Delete(libraryPath);
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }
}
