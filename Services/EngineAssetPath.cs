namespace LiberaFx.Services;

public static class EngineAssetPath
{
    public static void Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 ||
            !(path.StartsWith("/Game/", StringComparison.Ordinal) || path.StartsWith("/Engine/", StringComparison.Ordinal)) ||
            path.Contains("..", StringComparison.Ordinal) || path.IndexOfAny(['\\', ':']) >= 0 || path.Any(char.IsControl))
            throw new ArgumentException("Use an Unreal /Game/ or /Engine/ object path.");
    }
}
