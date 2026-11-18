using LiberaFx.Services;

internal static class AnythingSearchTests
{
    public static void Run()
    {
        var shortcuts = new ShortcutService(Path.Combine(
            Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT")!, "search-shortcuts.json"));
        if (!shortcuts.Matches("search.anything", new Avalonia.Input.KeyEventArgs
            { Key = Avalonia.Input.Key.T, KeyModifiers = Avalonia.Input.KeyModifiers.Control }) ||
            shortcuts.Matches("search.anything", new Avalonia.Input.KeyEventArgs
            { Key = Avalonia.Input.Key.P, KeyModifiers = Avalonia.Input.KeyModifiers.Control }))
            throw new InvalidOperationException("Search must default to Ctrl+T, leaving Ctrl+P unclaimed.");
        static AnythingSearchItem Item(string id, string name, string detail = "", string aliases = "") =>
            new(id, "Node", name, detail, aliases, () => Task.CompletedTask);
        var items = new[]
        {
            Item("fuzzy", "Super Layer System"),
            Item("exact", "Solar System", "Sample graph"),
            Item("ja", "加算", aliases: "Add"),
            Item("nested", "Moon", "Solar System / Earth")
        };
        if (AnythingSearch.Find(items, "sls").FirstOrDefault()?.Id != "fuzzy")
            throw new InvalidOperationException("Subsequence search failed.");
        if (AnythingSearch.Find(items, "solar system").FirstOrDefault()?.Id != "exact")
            throw new InvalidOperationException("Exact names must rank before detail-only matches.");
        if (AnythingSearch.Find(items, "ADD").Single().Id != "ja")
            throw new InvalidOperationException("English aliases must remain searchable.");
        if (AnythingSearch.Find(items, "moon earth").Single().Id != "nested" ||
            AnythingSearch.Find(items, "moon missing").Count != 0 ||
            AnythingSearch.Find(items, "", 2).Count != 2)
            throw new InvalidOperationException("Multi-term filtering or result limits failed.");
    }
}
