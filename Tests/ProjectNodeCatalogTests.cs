using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using LiberaFx.Views;

internal static class ProjectNodeCatalogTests
{
    public static void Run()
    {
        var root = Path.Combine(AppDataDirectory.Root, "project-node-tests");
        var firstDirectory = Path.Combine(root, "first");
        var secondDirectory = Path.Combine(root, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var profilesPath = Path.Combine(root, "profiles.json");
        var legacyId = Guid.NewGuid();
        File.WriteAllText(profilesPath, JsonSerializer.Serialize(new
        {
            Version = 1, ActiveId = legacyId,
            Profiles = new[] { new { Id = legacyId, Name = "Legacy", GroupDirectory = "" } }
        }));
        var profiles = new TeamProfilesService(profilesPath);
        if (profiles.Active.NodeDirectory != "" || profiles.Active.Id != legacyId ||
            profiles.Save(null, "Invalid", "", "relative"))
            throw new InvalidOperationException("Legacy profiles or project node directory validation regressed.");

        var firstType = Guid.NewGuid();
        var secondType = Guid.NewGuid();
        var firstFile = Path.Combine(firstDirectory, "first.json");
        var secondFile = Path.Combine(secondDirectory, "second.json");
        File.WriteAllText(firstFile, Definition(firstType, "First", "Shared"));
        var secondJson = Definition(secondType, "Second", "Shared");
        File.WriteAllText(secondFile, secondJson);
        if (!profiles.Save(null, "First project", "", firstDirectory))
            throw new InvalidOperationException(profiles.Status);
        var firstProfile = profiles.Active.Id;
        if (!profiles.Save(null, "Second project", "", secondDirectory))
            throw new InvalidOperationException(profiles.Status);
        var secondProfile = profiles.Active.Id;
        var reloaded = new TeamProfilesService(profilesPath);
        if (reloaded.Active.NodeDirectory != secondDirectory ||
            reloaded.Profiles.Single(item => item.Id == firstProfile).NodeDirectory != firstDirectory)
            throw new InvalidOperationException("Project node directories did not persist.");

        using var catalog = new NodeDefinitionCatalog(reloaded);
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager(), nodeDefinitions: catalog);
        var provider = new ActiveGraphProvider(graph);
        var nodeList = new NodeCatalogViewModel(provider);
        var firstKind = new GraphNodeKind(firstType.ToString("D"));
        var secondKind = new GraphNodeKind(secondType.ToString("D"));
        if (catalog.AvailableDefinitions.Any(item => item.Kind == firstKind) ||
            !catalog.AvailableDefinitions.Any(item => item.Kind == secondKind) ||
            !GraphNodeDefinition.All.Any(item => item.Kind == firstKind) ||
            !GraphNodeDefinition.AuthoringCore.All(catalog.AvailableDefinitions.Contains))
            throw new InvalidOperationException("The catalog did not separate Core, active and retained project definitions.");
        var originalNode = graph.CreateNode(secondKind);
        var original = graph.CreateSnapshot();
        reloaded.Select(firstProfile);
        if (nodeList.ProjectName != "First project" ||
            nodeList.Groups.SelectMany(group => group.Nodes).Any(entry => entry.DragIdentifier == secondKind.Value) ||
            !GraphSnapshot.ContentEquals(original, graph.CreateSnapshot()) ||
            !GraphNodeDefinition.TryFind("Shared", out var selected, graph.AvailableNodeDefinitions) ||
            selected!.Kind != firstKind)
            throw new InvalidOperationException("Project switching changed an open graph or retained another project's search entries.");
        try
        {
            graph.CreateNode(secondKind);
            throw new InvalidOperationException("A stale drag or API request added an inactive project type.");
        }
        catch (ArgumentException) { }

        var view = new GraphEditorView { DataContext = graph };
        var window = new Window { Content = view, Width = 900, Height = 650 };
        window.Show();
        try
        {
            typeof(GraphEditorView).GetMethod("ShowNodeSearch", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(view, [new Point(120, 120)]);
            var search = view.FindControl<TextBox>("NodeSearchBox")!;
            var results = view.FindControl<ListBox>("NodeSearchResults")!;
            search.Text = "Shared";
            Dispatcher.UIThread.RunJobs();
            if (results.ItemsSource!.Cast<NodeCatalogEntryViewModel>().Single().DragIdentifier != firstKind.Value)
                throw new InvalidOperationException("Quick node search is not scoped to the active project.");
            typeof(GraphEditorView).GetMethod("AddSearchSelection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
            var bothProjects = graph.CreateSnapshot();
            reloaded.Select(secondProfile);
            if (!graph.History.Undo() || graph.Nodes.Count != 1 || graph.Nodes.Single().Definition.Kind != secondKind ||
                !graph.History.Redo() || !GraphSnapshot.ContentEquals(bothProjects, graph.CreateSnapshot()))
                throw new InvalidOperationException("Project switching invalidated cross-project graph history.");
            using (var project = new MemoryStream())
            {
                new ProjectFileService().SaveAsync(project, graph).GetAwaiter().GetResult();
                project.Position = 0;
                var loaded = ProjectDocumentReader.LoadAsync(project).GetAwaiter().GetResult();
                if (!GraphSnapshot.ContentEquals(bothProjects, loaded.Graph))
                    throw new InvalidOperationException("A graph containing retained project types could not round-trip.");
            }
            var listTypes = LiberaFxMcpTools.ListNodeTypes(provider);
            WaitFor(() => listTypes.IsCompleted);
            using (var types = JsonDocument.Parse(listTypes.GetAwaiter().GetResult()))
            {
                var ids = types.RootElement.EnumerateArray().Select(item => item.GetProperty("typeId").GetGuid()).ToArray();
                if (ids.Contains(firstType) || !ids.Contains(secondType))
                    throw new InvalidOperationException("MCP exposed a hidden project type.");
            }

            var watchedType = Guid.NewGuid();
            var watchedKind = new GraphNodeKind(watchedType.ToString("D"));
            var watchedFile = Path.Combine(secondDirectory, "watched.json");
            typeof(GraphEditorView).GetMethod("ShowNodeSearch", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(view, [new Point(120, 120)]);
            search.Text = "Shared";
            File.WriteAllText(watchedFile, Definition(watchedType, "Watched", "Shared"));
            WaitFor(() => catalog.AvailableDefinitions.Any(item => item.Kind == watchedKind));
            if (results.ItemsSource!.Cast<NodeCatalogEntryViewModel>().Count() != 2 ||
                !nodeList.Groups.SelectMany(group => group.Nodes).Any(entry => entry.DragIdentifier == watchedKind.Value))
                throw new InvalidOperationException("File watcher updates missed the node list or open quick search.");
            graph.CreateNode(watchedKind);
            var beforeRemoval = graph.CreateSnapshot();
            File.Delete(watchedFile);
            WaitFor(() => catalog.AvailableDefinitions.All(item => item.Kind != watchedKind));
            if (!GraphSnapshot.ContentEquals(beforeRemoval, graph.CreateSnapshot()) ||
                !graph.History.Undo() || !graph.History.Redo() ||
                !GraphSnapshot.ContentEquals(beforeRemoval, graph.CreateSnapshot()))
                throw new InvalidOperationException("Removing a library definition damaged open graph data or history.");

            File.WriteAllText(secondFile, Definition(secondType, "Second", "Changed"));
            File.WriteAllText(Path.Combine(secondDirectory, "core-collision.json"),
                Definition(GraphNodeDefinition.Core.Single(item => item.Kind == GraphNodeKind.Float).TypeId, "Spoof", "Spoof"));
            File.WriteAllText(Path.Combine(secondDirectory, "name-collision.json"), Definition(Guid.NewGuid(), "First", "Collision"));
            File.WriteAllText(Path.Combine(secondDirectory, "broken.json"), "{invalid");
            catalog.Refresh();
            if (catalog.AvailableDefinitions.Any(item => item.Kind == secondKind) ||
                !catalog.Warnings.Contains("Core") || !catalog.Warnings.Contains("different registered definition") ||
                !catalog.Warnings.Contains("broken.json") || originalNode.Definition.Name != "Shared" ||
                !GraphSnapshot.ContentEquals(beforeRemoval, graph.CreateSnapshot()))
                throw new InvalidOperationException("Invalid or conflicting definitions replaced types used by open graphs.");
            File.WriteAllText(secondFile, secondJson);
            catalog.Refresh();
            if (!catalog.AvailableDefinitions.Any(item => item.Kind == secondKind))
                throw new InvalidOperationException("Restoring a valid definition did not restore its catalog entry.");
            if (!reloaded.Save(secondProfile, "Renamed project", "") ||
                reloaded.Active.NodeDirectory != secondDirectory || nodeList.ProjectName != "Renamed project")
                throw new InvalidOperationException("Editing legacy profile fields lost the node directory or project label.");
        }
        finally
        {
            window.Close();
        }
        VerifySettingsDirectory(firstDirectory, secondDirectory, firstKind, secondKind);
    }

    private static void VerifySettingsDirectory(string firstDirectory, string secondDirectory,
        GraphNodeKind firstKind, GraphNodeKind secondKind)
    {
        var model = new MainViewModel();
        if (!model.TeamProfiles.Save(null, "UI project", "", firstDirectory))
            throw new InvalidOperationException(model.TeamProfiles.Status);
        var settings = new SettingsWindow { DataContext = model };
        try
        {
            settings.Show();
            settings.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 3;
            Dispatcher.UIThread.RunJobs();
            var directory = settings.FindControl<TextBox>("NodeDirectoryBox")!;
            if (directory.Text != firstDirectory)
                throw new InvalidOperationException("The selected project's node directory is not shown in Settings.");
            directory.Text = secondDirectory;
            settings.GetVisualDescendants().OfType<IconButton>().Single(button =>
                AutomationProperties.GetName(button) == LocalizationService.Shared.Get("Editor.SaveProject"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (model.TeamProfiles.Active.NodeDirectory != secondDirectory ||
                !model.NodeDefinitions.AvailableDefinitions.Any(item => item.Kind == secondKind) ||
                model.NodeDefinitions.AvailableDefinitions.Any(item => item.Kind == firstKind))
                throw new InvalidOperationException("The Settings save button did not update the project's visible node definitions.");
        }
        finally
        {
            settings.Close();
            model.DisposeStorageFiles();
        }
    }

    private static void WaitFor(Func<bool> ready)
    {
        if (ready()) return;
        var frame = new DispatcherFrame();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var registration = timeout.Token.Register(() => Dispatcher.UIThread.Post(() => frame.Continue = false));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            if (ready()) frame.Continue = false;
        };
        timer.Start();
        try { Dispatcher.UIThread.PushFrame(frame); }
        finally { timer.Stop(); }
        if (!ready()) throw new TimeoutException("The project node file watcher did not refresh the catalog.");
    }

    private static string Definition(Guid id, string type, string name) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, typeId = id, @namespace = "CatalogTest", kind = type, name,
        group = "Project examples", portType = "Float", portCategory = "Parameter",
        inputPorts = Array.Empty<object>(),
        parameters = new[] { new { id = "value", name = "Value", type = "float", defaultValue = 1 } }
    });
}
