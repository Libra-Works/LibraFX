namespace LiberaFx.Services;

public static class EngineAssetFolder
{
    public static string Normalize(string folder)
    {
        folder = folder.Trim().TrimEnd('/');
        if (folder.Length > 512 || folder.Any(c => char.IsControl(c) || "\\:*?\"<>|.".Contains(c)) ||
            folder.Contains("//", StringComparison.Ordinal) || folder.Length > 0 &&
            folder is not ("/Game" or "/Engine") && !folder.StartsWith("/Game/", StringComparison.Ordinal) &&
            !folder.StartsWith("/Engine/", StringComparison.Ordinal))
            throw new ArgumentException("Use an engine folder such as /Game/VFX or /Engine, or leave it empty.", nameof(folder));
        return folder;
    }

    public static bool Contains(string folder, string assetPath) => folder.Length == 0 ||
        assetPath.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    public static string Parent(string assetPath) => assetPath[..assetPath.LastIndexOf('/')];
}
