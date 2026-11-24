namespace LiberaFx.Services;

public static class EngineAssetFolder
{
    public static string Normalize(string folder)
    {
        folder = folder.Trim();
        if (folder.Equals("res://", StringComparison.Ordinal)) return "res://";
        folder = folder.TrimEnd('/');
        if (folder.Length > 512 || folder.Contains("..", StringComparison.Ordinal) ||
            folder.Any(c => char.IsControl(c) || "\\*?\"<>|".Contains(c)))
            throw new ArgumentException("Use an engine folder (/Game, /Engine, Assets, Packages, or res://), or leave it empty.", nameof(folder));
        if (folder.Length == 0) return folder;
        var isUnreal = (folder is "/Game" or "/Engine") ||
            folder.StartsWith("/Game/", StringComparison.Ordinal) || folder.StartsWith("/Engine/", StringComparison.Ordinal);
        var isUnity = (folder is "Assets" or "Packages") ||
            folder.StartsWith("Assets/", StringComparison.Ordinal) || folder.StartsWith("Packages/", StringComparison.Ordinal);
        var isGodot = folder.StartsWith("res://", StringComparison.Ordinal);
        var rest = isGodot ? folder["res://".Length..] : folder;
        if (!(isUnreal || isUnity || isGodot) || rest.Contains(':', StringComparison.Ordinal) ||
            rest.Contains("//", StringComparison.Ordinal))
            throw new ArgumentException("Use an engine folder (/Game, /Engine, Assets, Packages, or res://), or leave it empty.", nameof(folder));
        return folder;
    }

    public static bool Contains(string folder, string assetPath) => folder.Length == 0 ||
        (folder == "res://"
            ? assetPath.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
            : assetPath.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase));

    public static string Parent(string assetPath)
    {
        if (assetPath == "res://") return "";
        if (assetPath.StartsWith("res://", StringComparison.Ordinal))
        {
            var slash = assetPath.LastIndexOf('/');
            return slash <= "res://".Length - 1 ? "res://" : assetPath[..slash];
        }
        var last = assetPath.LastIndexOf('/');
        return last <= 0 ? "" : assetPath[..last];
    }
}
