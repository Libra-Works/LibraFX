using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using LiberaFx.Views;

internal static class AnythingSearchTests
{
    public static void Run()
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT")!,
            $"search-shortcuts-{Guid.NewGuid():N}.json");
        const string search = "search.anything";
        static KeyEventArgs Ctrl(Key key) => new() { Key = key, KeyModifiers = KeyModifiers.Control };
        try
        {
            var shortcuts = new ShortcutService(path);
            if (!shortcuts.Matches(search, Ctrl(Key.P)) || shortcuts.Matches(search, Ctrl(Key.T)))
                throw new InvalidOperationException("Search must default to Ctrl+P.");
            shortcuts.Assign(search, Key.T, KeyModifiers.Control);
            var reopened = new ShortcutService(path);
            if (!reopened.Matches(search, Ctrl(Key.T)) || reopened.Matches(search, Ctrl(Key.P)))
                throw new InvalidOperationException("An explicit search shortcut must survive reopening settings.");
        }
        finally
        {
            File.Delete(path);
        }
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
        var favorites = items.Select(item => item.Id == "nested" ? item with { IsFavorite = true } : item).ToArray();
        if (AnythingSearch.Find(favorites, "solar").First().Id != "nested" ||
            AnythingSearch.Find(favorites, "missing").Count != 0)
            throw new InvalidOperationException("Favorites must rank matching results first without bypassing the query.");

        VerifyKeyboardWorkflow();
    }

    private static void VerifyKeyboardWorkflow()
    {
        var model = new MainViewModel();
        var window = new MainWindow { DataContext = model };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var count = model.Documents.Count;
            Press(window, Key.P, KeyModifiers.Control);
            var palette = window.OwnedWindows.OfType<AnythingSearchWindow>().Single();
            var query = palette.GetVisualDescendants().OfType<TextBox>().Single();
            query.Text = "new";
            Dispatcher.UIThread.RunJobs();
            var results = palette.GetVisualDescendants().OfType<ListBox>().Single();
            if (results.SelectedItem is not AnythingSearchItem { Id: "command/new" })
                throw new InvalidOperationException("The palette did not select the new document result.");
            Press(query, Key.Down);
            Press(query, Key.Up);
            Press(query, Key.Enter);
            Dispatcher.UIThread.RunJobs();
            if (model.Documents.Count != count + 1 || palette.IsVisible)
                throw new InvalidOperationException("Keyboard activation did not execute the command and close the palette.");
        }
        finally
        {
            foreach (var child in window.OwnedWindows.ToArray())
                child.Close();
            window.Close();
            model.DisposeStorageFiles();
        }

        static void Press(InputElement element, Key key, KeyModifiers modifiers = KeyModifiers.None)
        {
            var input = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };
            element.RaiseEvent(input);
            if (!input.Handled)
                throw new InvalidOperationException($"The search UI did not handle {key}.");
        }
    }
}
