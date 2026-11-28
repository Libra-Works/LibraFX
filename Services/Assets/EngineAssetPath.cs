namespace LiberaFx.Services;

public static class EngineAssetPath
{
    public static void Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 ||
            path.Contains("..", StringComparison.Ordinal) || path.IndexOf('\\') >= 0 || path.Any(char.IsControl))
            throw new ArgumentException("Use an engine asset path (/Game/, /Engine/, Assets/, Packages/, or res://).");
        var isUnreal = path.StartsWith("/Game/", StringComparison.Ordinal) || path.StartsWith("/Engine/", StringComparison.Ordinal);
        var isUnity = path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal);
        var isGodot = path.StartsWith("res://", StringComparison.Ordinal) && path.Length > "res://".Length;
        var rest = isGodot ? path["res://".Length..] : path;
        if (!(isUnreal || isUnity || isGodot) || rest.IndexOf(':') >= 0)
            throw new ArgumentException("Use an engine asset path (/Game/, /Engine/, Assets/, Packages/, or res://).");
    }
}
