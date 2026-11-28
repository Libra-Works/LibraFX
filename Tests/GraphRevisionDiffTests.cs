using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Project;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;
using LiberaFx.ViewModels.Main;
using LiberaFx.Views.Graph;
using LiberaFx.Views.Windows;

internal static class GraphRevisionDiffTests
{
    public static void Run()
    {
        var id = Guid.NewGuid();
        var source = new GraphNodeSnapshot(id, GraphNodeKind.Float, "Value", 0, 0, "Float", GraphPortCategory.Parameter,
            Parameters: [new("value", "float", JsonSerializer.SerializeToElement(1d))]);
        var target = source with { Id = Guid.NewGuid(), Name = "Target" };
        var before = new GraphSnapshot([source, target], [new(source.Id, target.Id)]);
        var after = before with { Nodes = [source with { Parameters = [new("value", "float", JsonSerializer.SerializeToElement(2d))] }, target] };
        if (GraphRevisionDiff.Compare(before, after).Single(change => change.After?.Id == id).Kind != GraphDiffKind.Modified)
            throw new InvalidOperationException("Parameter changes must be detected.");
        if (GraphRevisionDiff.Compare(before, before with { Connections = [] }).Any(change => change.Kind != GraphDiffKind.Modified))
            throw new InvalidOperationException("Connection removals must mark both endpoints.");
        var group = source with { Id = Guid.NewGuid(), Kind = GraphNodeKind.GraphGroup, ChildGraph = before };
        var nested = GraphRevisionDiff.Compare(new([group], []), new([group with { ChildGraph = after }], []));
        if (!nested.Any(change => change.Path.Contains('/') && change.Kind == GraphDiffKind.Modified))
            throw new InvalidOperationException("Nested changes must not be hidden.");
        if (!ReferenceEquals(GraphRevisionDiff.Scope(new([group], []), [group.Id]), before) ||
            GraphRevisionDiff.Scope(new([], []), [group.Id]) is not null)
            throw new InvalidOperationException("Nested comparison must follow stable group IDs and retain a missing side.");
        if (GraphRevisionDiff.Compare(before, before with { AutoDestroyWhenIdle = false })
                .Single(change => change.Kind == GraphDiffKind.Modified).Path != "$graph")
            throw new InvalidOperationException("Graph-level lifecycle metadata must not be hidden.");
        if (GraphRevisionDiff.Compare(new([source], []), new([target], [])).Select(change => change.Kind)
            .Order().SequenceEqual(new[] { GraphDiffKind.Added, GraphDiffKind.Removed }.Order()) is false)
            throw new InvalidOperationException("Added and removed identities must be distinct.");
    }

    public static void VerifyFileComparison()
    {
        var task = VerifyFileComparisonAsync();
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
        using var timeout = DispatcherTimer.RunOnce(() => frame.Continue = false, TimeSpan.FromSeconds(30));
        if (!task.IsCompleted)
        {
            Dispatcher.UIThread.PushFrame(frame);
        }
        if (!task.IsCompleted)
        {
            throw new TimeoutException("Graph file comparison timed out.");
        }
        task.GetAwaiter().GetResult();
    }

    private static async Task VerifyFileComparisonAsync()
    {
        var root = Path.Combine(AppDataDirectory.Root, "graph-comparison");
        Directory.CreateDirectory(root);
        var before = Path.Combine(root, "Before.lfxproj");
        var after = Path.Combine(root, "After.lfxproj");
        var invalid = Path.Combine(root, "Invalid.lfxproj");
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        var beforeGraph = workspace.CreateSnapshot();
        await SaveAsync(before);
        var added = workspace.CreateNode(GraphNodeKind.Comment);
        var afterGraph = workspace.CreateSnapshot();
        await SaveAsync(after);
        var beforeBytes = File.ReadAllBytes(before);
        var afterBytes = File.ReadAllBytes(after);
        File.WriteAllText(invalid, "{}");
        var library = new GroupAssetLibrary(root);
        var beforeGroup = library.PathFor(library.Create("Before group", beforeGraph).Id);
        var afterGroup = library.PathFor(library.Create("After group", afterGraph).Id);

        var main = new MainViewModel();
        var owner = new MainWindow { DataContext = main };
        var openDocument = main.ActiveDocument;
        var activeRevision = main.Graph.History.CurrentNodeId;
        owner.Show();
        try
        {
            owner.FindControl<MenuItem>("CompareGraphFilesMenu")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var dialog = owner.OwnedWindows.OfType<GraphFileComparisonWindow>().Single();
            var text = LocalizationService.Shared;
            var left = PathField("Vcs.LeftFile");
            var right = PathField("Vcs.RightFile");
            var compare = dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, text.Get("Vcs.Compare")));
            var diff = dialog.GetVisualDescendants().OfType<GraphComparisonView>().Single();
            var changes = diff.GetVisualDescendants().OfType<ListBox>().Single();

            await CompareAsync(before, after, () => changes.ItemCount == 1);
            if (changes.Items[0]?.ToString()?.Contains(added.Name, StringComparison.Ordinal) != true)
            {
                throw new InvalidOperationException("The standalone comparison did not identify the added node.");
            }
            await CompareAsync(beforeGroup, afterGroup, () => changes.ItemCount == 1);
            await CompareAsync(before, before, () => HasStatus(text.Get("Vcs.NoGraphChanges")));
            var errorPrefix = text.Get("Vcs.FileReadFailed", "").TrimEnd();
            await CompareAsync(before, invalid, () => dialog.GetVisualDescendants().OfType<TextBlock>()
                .Any(label => label.Text?.StartsWith(errorPrefix, StringComparison.Ordinal) == true));
            if (changes.ItemCount != 0 || !ReferenceEquals(main.ActiveDocument, openDocument) ||
                main.Graph.History.CurrentNodeId != activeRevision ||
                !File.ReadAllBytes(before).SequenceEqual(beforeBytes) || !File.ReadAllBytes(after).SequenceEqual(afterBytes))
            {
                throw new InvalidOperationException("Comparison modified the files or the open document, or left stale results.");
            }

            TextBox PathField(string label) => dialog.GetVisualDescendants().OfType<TextBox>()
                .Single(field => AutomationProperties.GetName(field) == text.Get(label));

            bool HasStatus(string value) => dialog.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == value);

            async Task CompareAsync(string leftPath, string rightPath, Func<bool> ready)
            {
                left.Text = leftPath;
                right.Text = rightPath;
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                new ButtonAutomationPeer(compare).GetProvider<IInvokeProvider>()!.Invoke();
                for (var attempt = 0; attempt < 160; attempt++)
                {
                    if (compare.IsEnabled && ready())
                    {
                        return;
                    }
                    await Task.Delay(25);
                }
                throw new TimeoutException("The standalone comparison did not show the expected result.");
            }
        }
        finally
        {
            owner.Close();
            main.DisposeStorageFiles();
        }
        Console.WriteLine("Standalone graph file comparison passed for projects, groups, identical and invalid files.");

        async Task SaveAsync(string path)
        {
            using var stream = File.Create(path);
            await new ProjectFileService().SaveAsync(stream, workspace);
        }
    }
}
