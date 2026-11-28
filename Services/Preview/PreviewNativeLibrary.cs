namespace LiberaFx.Services.Preview;

internal static class PreviewNativeLibrary
{
    public static string ResolveLibraryPath(string? explicitPath = null)
    {
        var path = explicitPath ?? Environment.GetEnvironmentVariable("LIBERAFX_PREVIEW_NATIVE_LIBRARY");
        var fileName = OperatingSystem.IsWindows() ? "liberafx_native.dll"
            : OperatingSystem.IsMacOS() ? "libliberafx_native.dylib" : "libliberafx_native.so";
        if (string.IsNullOrWhiteSpace(path))
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, fileName);
            if (File.Exists(bundled))
            {
                path = bundled;
            }
            else
            {
                for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                {
                    var candidate = OperatingSystem.IsWindows()
                        ? Path.Combine(dir.FullName, "Runtime", "build-native", "Release", fileName)
                        : Path.Combine(dir.FullName, "Runtime", "build-native", fileName);
                    if (File.Exists(candidate)) { path = candidate; break; }
                }
                path ??= bundled;
            }
        }
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new InvalidDataException($"Native preview requires a built native runtime. Set " +
                $"LIBERAFX_PREVIEW_NATIVE_LIBRARY to an existing absolute {fileName} path, or place it beside the editor; then resync preview.");
        return path;
    }

}
