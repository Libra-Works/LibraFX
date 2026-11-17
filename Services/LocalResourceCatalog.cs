namespace LiberaFx.Services;

public enum LocalResourceKind { Effect, Image }
public sealed record LocalResourceAsset(string Path, string Name, LocalResourceKind Kind, long Bytes);
public sealed record LocalResourcePage(IReadOnlyList<LocalResourceAsset> Assets, bool Truncated);

public static class LocalResourceCatalog
{
    public const int Limit = 5000;

    public static LocalResourcePage Read(string folder, bool recursive, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
            throw new ArgumentException("Select an existing local folder.");
        var assets = new List<LocalResourceAsset>();
        foreach (var path in Directory.EnumerateFiles(folder, "*", new EnumerationOptions
        {
            RecurseSubdirectories = recursive, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System
        }))
        {
            cancellation.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is not (".lfxproj" or ".png" or ".jpg" or ".jpeg")) continue;
            if (assets.Count == Limit) return new(Sort(assets), true);
            try
            {
                var file = new FileInfo(path);
                assets.Add(new(file.FullName, file.Name,
                    extension == ".lfxproj" ? LocalResourceKind.Effect : LocalResourceKind.Image, file.Length));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new(Sort(assets), false);
    }

    private static IReadOnlyList<LocalResourceAsset> Sort(List<LocalResourceAsset> assets) =>
        assets.OrderBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase).ToArray();
}
