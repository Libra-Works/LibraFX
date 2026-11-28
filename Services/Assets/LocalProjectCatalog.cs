namespace LiberaFx.Services;

public sealed record LocalProjectAsset(string Path, string Name, DateTime Modified, long Bytes);
public sealed record LocalProjectPage(IReadOnlyList<LocalProjectAsset> Assets, bool Truncated);

public static class LocalProjectCatalog
{
    public const int Limit = 5000;
    public static LocalProjectPage Read(string folder, bool recursive, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
            throw new ArgumentException("Select an existing local folder.");
        var assets = new List<LocalProjectAsset>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
            IgnoreInaccessible = true
        };
        foreach (var path in Directory.EnumerateFiles(folder, "*.lfxproj", options))
        {
            cancellation.ThrowIfCancellationRequested();
            if (assets.Count == Limit) return new(assets.OrderBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase).ToArray(), true);
            try
            {
                var file = new FileInfo(path);
                assets.Add(new(file.FullName, System.IO.Path.GetFileNameWithoutExtension(path), file.LastWriteTime, file.Length));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return new(assets.OrderBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase).ToArray(), false);
    }
}
