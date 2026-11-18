using LiberaFx.Services;

internal static class LocalProjectCatalogTests
{
    public static void Run()
    {
        var root = Path.Combine(AppDataDirectory.Root, "catalog-fixture");
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        File.WriteAllText(Path.Combine(root, "Mars.lfxproj"), "{}");
        File.WriteAllText(Path.Combine(root, "unrelated.txt"), "");
        File.WriteAllText(Path.Combine(root, "nested", "Earth.lfxproj"), "{}");
        var top = LocalProjectCatalog.Read(root, false);
        if (top.Assets.Count != 1 || top.Assets[0].Name != "Mars" || top.Assets[0].Bytes != 2)
            throw new InvalidOperationException("Top-level project catalog did not isolate project files.");
        var recursive = LocalProjectCatalog.Read(root, true);
        if (recursive.Assets.Count != 2 || recursive.Truncated || recursive.Assets[0].Name != "Earth")
            throw new InvalidOperationException("Recursive project catalog lost nested projects.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            LocalProjectCatalog.Read(root, true, cancellation.Token);
            throw new InvalidOperationException("Canceled project scan continued.");
        }
        catch (OperationCanceledException) { }
        var empty = Path.Combine(root, "empty");
        Directory.CreateDirectory(empty);
        try
        {
            LocalProjectCatalog.Read(empty, true, cancellation.Token);
            throw new InvalidOperationException("Canceled empty-folder scan continued.");
        }
        catch (OperationCanceledException) { }
        try
        {
            LocalProjectCatalog.Read("relative-folder", true);
            throw new InvalidOperationException("Relative project root was accepted.");
        }
        catch (ArgumentException) { }
        var window = new LiberaFx.Views.LocalProjectsWindow();
    }
}
