using LiberaFx.Models.Graph;

namespace LiberaFx.Services;

internal static class GraphComparisonFile
{
    public const int MaxLocalFileMiB = 64;

    public static bool IsSupported(string path) =>
        HasExtension(path, ProjectFileService.FileExtension) || HasExtension(path, GroupAssetLibrary.FileExtension);

    public static async Task<GraphSnapshot> ReadAsync(string path, Stream stream) =>
        HasExtension(path, GroupAssetLibrary.FileExtension)
            ? GroupAssetLibrary.ReadAsset(stream).Graph
            : (await ProjectDocumentReader.LoadAsync(stream)).Graph;

    public static async Task<GraphSnapshot> ReadLocalAsync(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxLocalFileMiB * 1024L * 1024)
        {
            throw new InvalidDataException(LocalizationService.Shared.Get("Vcs.FileTooLarge", MaxLocalFileMiB));
        }
        return await ReadAsync(path, stream);
    }

    private static bool HasExtension(string path, string extension) =>
        string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase);
}
