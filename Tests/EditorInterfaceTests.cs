using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using LiberaFx.Models.Appearance;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using LiberaFx.Views;

internal static class EditorInterfaceTests
{
    public static void Run()
    {
        VerifyCatalogs();
        var text = LocalizationService.Shared;
        var originalLanguage = text.SelectedLanguage;
        var root = Path.Combine(AppDataDirectory.Root, "inspector-tests");
        var directory = Path.Combine(root, "groups");
        var profilesPath = Path.Combine(root, "profiles.json");
        var profiles = new TeamProfilesService(profilesPath);
        var originalProfile = profiles.Active.Id;
        if (!profiles.Save(null, "Inspector", directory)) throw new InvalidOperationException(profiles.Status);
        using var catalog = new GroupAssetCatalog(profiles);
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager(), catalog);
        var provider = new ActiveGraphProvider(graph);
        var pane = new NodePropertiesPaneViewModel(provider);
        var nodeList = new NodeCatalogViewModel(provider, catalog);
        var parameterGraph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var parameterNode = parameterGraph.CreateNode(GraphNodeKind.Float);
        var parameter = new NodeParameterEditorViewModel(parameterGraph, parameterNode, parameterNode.Definition.Parameters.Single());
        var parameterNameRefreshed = false;
        parameter.PropertyChanged += (_, e) => parameterNameRefreshed |= e.PropertyName == nameof(parameter.Name);
        var view = new NodePropertiesView { DataContext = pane };
        var window = new Window { Content = view };
        Guid assetId = Guid.Empty;
        window.Show();
        try
        {
            var group = graph.CreateNode(GraphNodeKind.GraphGroup);
            if (!pane.IsGroup || !pane.SaveGroupCommand.CanExecute(null) || pane.ToggleGroupLockCommand.CanExecute(null))
                throw new InvalidOperationException("Inspector group actions have incorrect availability.");
            pane.SaveGroupCommand.Execute(null);
            assetId = group.GroupAsset?.Id ?? throw new InvalidOperationException("Inspector save did not create an asset.");
            if (!File.Exists(Path.Combine(directory, assetId.ToString("N") + ".lfxgroup")) ||
                !pane.ToggleGroupLockCommand.CanExecute(null))
                throw new InvalidOperationException("Saved group is not reusable or lockable.");

            var save = view.FindControl<IconButton>("SaveGroupButton")!;
            var toggle = view.FindControl<IconButton>("GroupLockButton")!;
            pane.ToggleGroupLockCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            if (!pane.IsGroupLocked || pane.SaveGroupCommand.CanExecute(null) ||
                toggle.Icon != EditorIcon.Lock || graph.EnterGraph(group))
                throw new InvalidOperationException("Inspector did not reflect the locked asset.");

            var before = graph.CreateSnapshot();
            text.SelectedLanguage = text.Languages.Single(language => language.Id == "ja");
            Dispatcher.UIThread.RunJobs();
            if (AutomationProperties.GetName(save) != text.Get("Editor.SaveGroup") ||
                AutomationProperties.GetName(toggle) != text.Get("Editor.UnlockGroup") ||
                ToolTip.GetTip(save)?.ToString() != text.Get("Editor.SaveGroup") ||
                pane.GroupRevision != text.Get("Editor.GroupRevision", 1) ||
                !GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()))
                throw new InvalidOperationException("Live translation changed graph data or missed inspector accessibility text.");
            nodeList.SearchText = "Scalar";
            if (!nodeList.Groups.SelectMany(category => category.Nodes)
                    .Any(entry => entry.KindName == "Float" && entry.Name == text.Get("Editor.Node.Scalar")) ||
                parameter.Name != text.Get("Editor.Parameter.Value") || !parameterNameRefreshed ||
                parameterNode.Name != parameterNode.Definition.Name)
                throw new InvalidOperationException("Translated node catalog or inspector labels changed saved names or lost English search.");
            using (var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppDataDirectory.Root, "LiberaFx", "language.json"))))
                if (settings.RootElement.GetProperty("Language").GetString() != "ja")
                    throw new InvalidOperationException("The selected language was not saved.");

            pane.ToggleGroupLockCommand.Execute(null);
            if (pane.IsGroupLocked || !pane.SaveGroupCommand.CanExecute(null) || !graph.EnterGraph(group))
                throw new InvalidOperationException("Inspector unlock did not restore editing.");
            graph.CreateNode(GraphNodeKind.Time);
            graph.NavigateToGraph(0);
            graph.SelectNode(graph.Nodes.Single());
            pane.SaveGroupCommand.Execute(null);
            if (new GroupAssetLibrary(directory).Load(assetId).Revision != 2)
                throw new InvalidOperationException("Inspector save did not publish group edits.");

            profiles.Select(originalProfile);
            if (pane.SaveGroupCommand.CanExecute(null) || pane.ToggleGroupLockCommand.CanExecute(null) ||
                graph.Nodes.Single().GroupAsset?.Id != assetId)
                throw new InvalidOperationException("Project switching changed embedded data or allowed missing-asset actions.");
            graph.CreateNode(GraphNodeKind.Float);
            if (pane.IsGroup || pane.SaveGroupCommand.CanExecute(null))
                throw new InvalidOperationException("Non-group nodes expose group save actions.");
            foreach (var icon in Enum.GetValues<EditorIcon>())
                if (new IconButton { Icon = icon }.Content is not Avalonia.Controls.Shapes.Path { Data: not null })
                    throw new InvalidOperationException($"Icon {icon} has no vector geometry.");
        }
        finally
        {
            text.SelectedLanguage = originalLanguage;
            window.Close();
            if (File.Exists(profilesPath)) File.Delete(profilesPath);
            if (assetId != Guid.Empty)
            {
                var name = assetId.ToString("N");
                foreach (var path in new[] {
                    Path.Combine(directory, name + ".lfxgroup"), Path.Combine(directory, "." + name + ".lock"),
                    Path.Combine(directory, ".versions", name, "1.lfxgroup") })
                    if (File.Exists(path)) File.Delete(path);
                var archived = Path.Combine(directory, ".versions", name);
                if (Directory.Exists(archived)) Directory.Delete(archived);
                var versions = Path.Combine(directory, ".versions");
                if (Directory.Exists(versions)) Directory.Delete(versions);
            }
            if (Directory.Exists(directory)) Directory.Delete(directory);
            if (Directory.Exists(root)) Directory.Delete(root);
        }
        VerifySettingsBindingLifetime();
    }

    private static void VerifySettingsBindingLifetime()
    {
        var model = new MainViewModel();
        var settings = new SettingsWindow { DataContext = model };
        var text = LocalizationService.Shared;
        var original = text.SelectedLanguage;
        try
        {
            settings.Show();
            settings.FindControl<ComboBox>("LanguageList")!.SelectedItem = text.Languages.Single(language => language.Id == "ja");
            Dispatcher.UIThread.RunJobs();
            if (text.SelectedLanguage.Id != "ja" || settings.Title != text.Get("Editor.SettingsTitle"))
                throw new InvalidOperationException("The language setting did not update the actual settings window.");
            settings.Close();
            if (!model.TeamProfiles.Save(null, "Profile after closing settings", string.Empty) ||
                model.TeamProfiles.Active.Name != "Profile after closing settings")
                throw new InvalidOperationException("A closed settings window reverted the active project.");
        }
        finally
        {
            if (settings.IsVisible) settings.Close();
            text.SelectedLanguage = original;
            model.DisposeStorageFiles();
        }
    }

    private static void VerifyCatalogs()
    {
        var catalogs = new Dictionary<string, Dictionary<string, string>>();
        foreach (var language in new[] { "en", "ja" })
        {
            var catalog = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var uri in AssetLoader.GetAssets(new Uri("avares://LiberaFx/Assets/Translations"), null)
                         .Where(uri => uri.AbsolutePath.EndsWith("." + language + ".json", StringComparison.Ordinal)))
            {
                using var stream = AssetLoader.Open(uri);
                foreach (var entry in JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!)
                    if (!catalog.TryAdd(entry.Key, entry.Value) || string.IsNullOrWhiteSpace(entry.Value))
                        throw new InvalidOperationException($"Duplicate or empty translation: {entry.Key}");
            }
            catalogs.Add(language, catalog);
        }
        if (!catalogs["en"].Keys.ToHashSet().SetEquals(catalogs["ja"].Keys))
            throw new InvalidOperationException("English and Japanese catalogs have different keys.");
        foreach (var (key, english) in catalogs["en"])
        {
            var japanese = catalogs["ja"][key];
            var expected = Regex.Matches(english, @"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}").Select(match => match.Groups[1].Value).Order();
            var actual = Regex.Matches(japanese, @"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}").Select(match => match.Groups[1].Value).Order();
            if (!expected.SequenceEqual(actual))
                throw new InvalidOperationException($"Translation format arguments differ: {key}");
        }
        if (LocalizationService.Shared.Get("Missing.Test.Key") != "Missing.Test.Key")
            throw new InvalidOperationException("Missing translations do not fall back predictably.");
    }
}
