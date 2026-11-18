namespace LiberaFx.Services;

public enum AssetBrowserRootKind
{
    VirtualEngine,
    PhysicalProject
}

public sealed record ProjectAssetEntry(string Name, string RelativePath, bool IsDirectory, long? Length);
public sealed class ProjectFolderExistsException(string message) : IOException(message) { }

public sealed record AssetBrowserRootEntry(AssetBrowserRootKind Root, string Name, string Path,
    bool IsDirectory, EngineAsset? EngineAsset = null, Guid? PeerId = null, long? Length = null,
    string? ProjectRoot = null)
{
    public string KindLabel => LocalizationService.Shared.Get(IsDirectory
        ? "Resources.Folder" : Root == AssetBrowserRootKind.VirtualEngine
            ? "Resources.EngineAsset"
            : System.IO.Path.GetExtension(Name).Equals(ProjectFileService.FileExtension,
                StringComparison.OrdinalIgnoreCase)
                ? "Resources.ProjectFile" : "Resources.PhysicalFile");
    public AssetBrowserAssetReference? Reference => Root == AssetBrowserRootKind.VirtualEngine &&
        EngineAsset is { } asset && PeerId is { } peer
        ? new(Root, asset.Engine, asset.Path, peer)
        : Root == AssetBrowserRootKind.PhysicalProject && !IsDirectory
            ? new(Root, "Physical file", Path, null, ProjectRoot)
            : null;
}

public sealed record AssetBrowserAssetReference(AssetBrowserRootKind Root, string Type,
    string Identity, Guid? SessionPeerId, string? ProjectRoot = null);

public static class ProjectAssetRoot
{
    public static string? FromProjectFileKey(string? fileKey)
    {
        if (string.IsNullOrWhiteSpace(fileKey)) return null;
        try
        {
            var path = Uri.TryCreate(fileKey, UriKind.Absolute, out var uri) && uri.IsFile
                ? uri.LocalPath
                : Path.IsPathFullyQualified(fileKey) ? fileKey : null;
            if (path is null || !Path.GetExtension(path).Equals(ProjectFileService.FileExtension,
                    StringComparison.OrdinalIgnoreCase)) return null;
            var root = Path.GetDirectoryName(Path.GetFullPath(path));
            return root is not null && Directory.Exists(root) ? root : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
                                           UnauthorizedAccessException or NotSupportedException or
                                           System.Security.SecurityException)
        {
            return null;
        }
    }

    public static IReadOnlyList<ProjectAssetEntry> List(string root, string? relativeFolder = null)
    {
        var fullRoot = NormalizeRoot(root);
        var folder = ResolveDirectory(fullRoot, relativeFolder);
        var entries = new List<ProjectAssetEntry>();
        foreach (var path in Directory.EnumerateFileSystemEntries(folder))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            var relativePath = Path.GetRelativePath(fullRoot, path);
            if (!IsSafeRelativePath(relativePath)) continue;
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            entries.Add(new(Path.GetFileName(path), relativePath, isDirectory,
                isDirectory ? null : new FileInfo(path).Length));
        }
        return entries.OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string CreateFolder(string root, AssetBrowserRootKind rootKind, string name,
        string? relativeParent = null)
    {
        if (rootKind != AssetBrowserRootKind.PhysicalProject)
            throw new InvalidOperationException("Folders can only be created under the physical project root.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 ||
            name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.IndexOfAny(['/', '\\']) >= 0 || name != name.Trim())
            throw new ArgumentException("Enter a valid folder name.", nameof(name));

        var fullRoot = NormalizeRoot(root);
        var parent = ResolveDirectory(fullRoot, relativeParent);
        var target = Path.GetFullPath(Path.Combine(parent, name));
        var relative = Path.GetRelativePath(fullRoot, target);
        if (!IsSafeRelativePath(relative) || Path.GetDirectoryName(target) != parent)
            throw new UnauthorizedAccessException("The folder must remain under the physical project root.");
        if (File.Exists(target) || Directory.Exists(target))
            throw new ProjectFolderExistsException(
                "An item with that name already exists in the current physical folder.");

        Directory.CreateDirectory(target);
        return target;
    }

    private static string NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new ArgumentException("A physical project root is required.", nameof(root));
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
            throw new DirectoryNotFoundException("The physical project root is unavailable.");
        return Path.TrimEndingDirectorySeparator(fullRoot);
    }

    private static string ResolveDirectory(string root, string? relativeFolder)
    {
        if (string.IsNullOrEmpty(relativeFolder)) return root;
        if (!IsSafeRelativePath(relativeFolder))
            throw new UnauthorizedAccessException("The selected folder is outside the physical project root.");
        var current = root;
        foreach (var segment in relativeFolder.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.None))
        {
            if (string.IsNullOrWhiteSpace(segment) || segment is "." or "..")
                throw new UnauthorizedAccessException("The selected folder is outside the physical project root.");
            current = Path.GetFullPath(Path.Combine(current, segment));
            if (!IsSafeRelativePath(Path.GetRelativePath(root, current)))
                throw new UnauthorizedAccessException("The selected folder is outside the physical project root.");
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("The selected folder is not a physical project directory.");
        }
        return current;
    }

    private static bool IsSafeRelativePath(string relativePath)
    {
        if (Path.IsPathFullyQualified(relativePath) || relativePath is "." or "..") return false;
        var segments = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.None);
        return segments.Length > 0 && segments.All(segment =>
            !string.IsNullOrWhiteSpace(segment) && segment is not ("." or ".."));
    }
}
