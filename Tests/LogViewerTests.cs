using Avalonia.Controls;
using Avalonia.Threading;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using LiberaFx.Views;

internal static class LogViewerTests
{
    public static void Run()
    {
        var log = new EditorLog();
        using var viewer = new LogViewerViewModel(log);
        log.Add("Edit", "First test message");
        log.Add("File", "Saved test project");
        log.Add("Edit", "Second TEST message");
        viewer.SelectedCategory = viewer.Categories.Single(option => option.Value == "Edit");
        viewer.FilterText = "message";
        viewer.SearchText = "test";
        if (viewer.VisibleText.Contains("Saved", StringComparison.Ordinal) ||
            viewer.MatchStart < 0 || !viewer.SearchStatus.Contains("2"))
            throw new InvalidOperationException("Log category filtering or case-insensitive search failed.");
        var firstMatch = viewer.MatchStart;
        viewer.NextMatchCommand.Execute(null);
        if (viewer.MatchStart <= firstMatch) throw new InvalidOperationException("Next log search result did not advance.");
        viewer.NextMatchCommand.Execute(null);
        if (viewer.MatchStart != firstMatch) throw new InvalidOperationException("Log search did not wrap.");
        viewer.PreviousMatchCommand.Execute(null);
        if (viewer.MatchStart <= firstMatch) throw new InvalidOperationException("Previous log search result did not wrap.");
        viewer.FilterText = "not present";
        if (viewer.VisibleText.Length != 0 || viewer.MatchStart != -1 || viewer.NextMatchCommand.CanExecute(null))
            throw new InvalidOperationException("An empty log filter retained stale search matches.");
        viewer.ResetFiltersCommand.Execute(null);
        if (!viewer.VisibleText.Contains("Saved", StringComparison.Ordinal) || log.Entries.Count != 3)
            throw new InvalidOperationException("Resetting log filters changed the actual log.");
        var directory = Path.Combine(AppDataDirectory.Root, "log-tests");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "export.log");
        File.WriteAllText(path, new string('x', 10000));
        LogTextExporter.SaveAsync(path, viewer.AllText).GetAwaiter().GetResult();
        if (File.ReadAllText(path) != viewer.AllText || Directory.EnumerateFiles(directory, ".log-*.tmp").Any())
            throw new InvalidOperationException("Log export retained trailing bytes or a temporary file.");
        var blocked = Path.Combine(directory, "blocked.log");
        Directory.CreateDirectory(blocked);
        try
        {
            LogTextExporter.SaveAsync(blocked, viewer.AllText).GetAwaiter().GetResult();
            throw new InvalidOperationException("A log export unexpectedly overwrote a directory.");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        if (log.Entries.Count != 3 || Directory.EnumerateFiles(directory, ".log-*.tmp").Any())
            throw new InvalidOperationException("A failed log export modified the log or left a temporary file.");
        viewer.ClearCommand.Execute(null);
        if (log.Entries.Count != 0 || viewer.VisibleText.Length != 0 || viewer.MatchStart != -1 ||
            viewer.Categories.Count != 1)
            throw new InvalidOperationException("Clearing the log retained stale text, categories or search results.");

        var model = new MainViewModel();
        model.Log.Add("Test", "Selectable plain log text");
        var view = new LogWindow { DataContext = model };
        var window = new Window { Content = view, Width = 980, Height = 600 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var text = view.FindControl<TextBox>("LogText")!;
            if (!text.IsReadOnly || text.Text?.Contains("Selectable plain log text") != true ||
                view.FindControl<Grid>("LogContent")!.DataContext is not LogViewerViewModel state)
                throw new InvalidOperationException("The log window does not expose a selectable plain text log.");
            view.FindControl<TextBox>("SearchBox")!.Text = "plain";
            Dispatcher.UIThread.RunJobs();
            if (state.SearchText != "plain" || text.SelectedText != "plain")
                throw new InvalidOperationException("Log search did not select its match in the actual text area.");
        }
        finally
        {
            window.Close();
            model.DisposeStorageFiles();
        }
    }
}
