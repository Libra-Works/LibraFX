using System.Diagnostics;
using System.Text.Json;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Extensions.Abstractions;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Crash;
using LiberaFx.Services.Extensions;
using LiberaFx.Services.Project;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;
using LiberaFx.ViewModels.Main;
using LiberaFx.Views.Assets;
using LiberaFx.Views.Panels;
using LiberaFx.Views.Windows;

internal static class ExtensionHostTests
{
    public static void VerifyBundledVcs()
    {
        var task = VerifyBundledVcsAsync();
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
        using var timeout = DispatcherTimer.RunOnce(() => frame.Continue = false, TimeSpan.FromSeconds(45));
        if (!task.IsCompleted)
        {
            Dispatcher.UIThread.PushFrame(frame);
        }
        if (!task.IsCompleted)
        {
            throw new TimeoutException("Version control UI regression timed out.");
        }
        task.GetAwaiter().GetResult();
    }

    private static async Task VerifyBundledVcsAsync()
    {
        const string gitId = "liberafx.git";
        const string name = "effect #1.lfxproj";
        var root = Path.Combine(AppDataDirectory.Root, "vcs-fixture");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, name);
        Git("init", "--initial-branch=fixture");
        Git("config", "user.name", "LibraFX fixture");
        Git("config", "user.email", "fixture@example.invalid");
        Git("config", "commit.gpgsign", "false");
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        await SaveAsync();
        Git("add", "--", name);
        Git("commit", "-m", "Initial effect");
        graph.CreateNode(GraphNodeKind.Comment);
        await SaveAsync();
        Git("add", "--", name);
        graph.CreateNode(GraphNodeKind.Float);
        await SaveAsync();
        File.WriteAllText(Path.Combine(root, "untracked + name.txt"), "fixture");
        var workingBytes = File.ReadAllBytes(file);
        var index = Path.Combine(root, ".git", "index");
        var indexBytes = File.ReadAllBytes(index);

        var first = new MainViewModel();
        try
        {
            var entry = first.Extensions.Extensions.Single(item => item.Id == gitId);
            if (entry.Enabled || first.Extensions.VersionControlProviders.Count != 0 ||
                first.Extensions.SetEnabled(gitId, true, explicitlyTrusted: false))
            {
                throw new InvalidOperationException("The bundled VCS plugin did not require explicit trust.");
            }
            if (first.InspectorPlugins.Plugins.Single(item => item.Id == "liberafx.inspector.color.rgb") is not
                { IsEnabled: false, IsTrusted: false })
            {
                throw new InvalidOperationException("The bundled Inspector must be present but not run by default.");
            }
            var settings = new SettingsWindow { DataContext = first };
            settings.Show();
            try
            {
                var text = LocalizationService.Shared;
                var tabs = settings.FindControl<TabControl>("SettingsTabs")!;
                tabs.SelectedItem = tabs.Items.OfType<TabItem>()
                    .Single(item => Equals(item.Header, text.Get("Extensions.Plugins")));
                var plugins = settings.FindControl<TabControl>("PluginSettingsTabs")!;
                plugins.SelectedItem = plugins.Items.OfType<TabItem>()
                    .Single(item => Equals(item.Header, text.Get("Extensions.Title")));
                Dispatcher.UIThread.RunJobs();
                var toggle = settings.GetVisualDescendants().OfType<CheckBox>().Single(item => Equals(item.Tag, gitId));
                toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                var trust = settings.OwnedWindows.Single();
                Invoke(trust.GetVisualDescendants().OfType<Button>().Single(item => Equals(item.Content, text.Get("Extensions.Enable"))));
                await WaitAsync(() => entry.Enabled && toggle.IsChecked == true);
                if (first.Extensions.VersionControlProviders.Count != 0 || first.Extensions.Status != text.Get("Extensions.Restart"))
                {
                    throw new InvalidOperationException("Plugin approval must prompt for restart, not hot-load the DLL.");
                }
            }
            finally { settings.Close(); }
        }
        finally { first.DisposeStorageFiles(); }

        var main = new MainViewModel();
        try
        {
            var provider = (IVersionControlWorkspaceProvider)main.Extensions.VersionControlProviders.Single();
            if (provider.GetType().Assembly == typeof(MainViewModel).Assembly || provider is IVersionControlEditProvider)
            {
                throw new InvalidOperationException("Bundled Git must be a separate read-only DLL.");
            }
            var status = await provider.GetWorkspaceStatusAsync(file, CancellationToken.None);
            if (status.Branch != "fixture" || status.Changes.Count != 2 ||
                !status.Changes.Contains(new(name, VcsChangeKind.Modified, VcsChangeKind.Modified)) ||
                !status.Changes.Contains(new("untracked + name.txt", VcsChangeKind.None, VcsChangeKind.Untracked)))
            {
                throw new InvalidOperationException("Workspace status lost a branch, staged/unstaged changes, or an unusual path.");
            }
            var model = main.AssetBrowser.ViewModel.ProjectResources;
            model.SourceIndex = 1;
            model.Folder = root;
            await model.RefreshCommand.ExecuteAsync(null);
            model.SelectedEntry = model.Entries.Single(entry => entry.Path == file);
            await model.PreviewTask;
            var resources = new ProjectResourcesView { DataContext = model };
            var owner = new Window { Content = resources, Width = 900, Height = 680 };
            owner.Show();
            try
            {
                var button = resources.FindControl<Button>("ResourceVersionControlButton")!;
                if (!button.IsEffectivelyVisible)
                {
                    throw new InvalidOperationException("The selected resource did not expose version control.");
                }
                Invoke(button);
                var dialog = owner.OwnedWindows.Single();
                var view = (RevisionHistoryWindow)dialog.Content!;
                var text = LocalizationService.Shared;
                var expander = view.GetVisualDescendants().OfType<Expander>().Single();
                expander.IsExpanded = true;
                Dispatcher.UIThread.RunJobs();
                var refreshStatus = view.GetVisualDescendants().OfType<Button>()
                    .Single(item => Equals(item.Content, text.Get("Vcs.WorkspaceRefresh")));
                Invoke(refreshStatus);
                await WaitAsync(() => view.GetVisualDescendants().OfType<ListBox>().Any(list => list.ItemCount == 2));
                var history = view.GetVisualDescendants().OfType<Button>()
                    .Single(item => Equals(Avalonia.Automation.AutomationProperties.GetName(item), text.Get("Vcs.History")));
                Invoke(history);
                await WaitAsync(() => view.GetVisualDescendants().OfType<ComboBox>().Any(combo => combo.ItemCount == 2));
                var compare = view.GetVisualDescendants().OfType<Button>()
                    .Single(item => Equals(Avalonia.Automation.AutomationProperties.GetName(item), text.Get("Vcs.Compare")));
                Invoke(compare);
                await WaitAsync(() => view.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == text.Get("Vcs.Legend")));
                if (view.GetVisualDescendants().OfType<Button>()
                    .Any(item => item.IsEffectivelyVisible && Equals(item.Content, text.Get("Vcs.OpenForEdit"))))
                {
                    throw new InvalidOperationException("The read-only provider exposed an edit action.");
                }
                var editProvider = new EditFixture();
                main.Extensions.RegisterBuiltIn(editProvider);
                view.GetVisualDescendants().OfType<ComboBox>().Single(combo =>
                    Equals(Avalonia.Automation.AutomationProperties.GetName(combo), text.Get("Vcs.Provider")))
                    .SelectedItem = editProvider;
                Dispatcher.UIThread.RunJobs();
                var edit = view.GetVisualDescendants().OfType<Button>()
                    .Single(item => item.IsEffectivelyVisible && Equals(item.Content, text.Get("Vcs.OpenForEdit")));
                Invoke(edit);
                var confirm = dialog.OwnedWindows.Single();
                Invoke(confirm.GetVisualDescendants().OfType<Button>().Single(item => item.IsCancel));
                await WaitAsync(() => dialog.OwnedWindows.Count == 0 && edit.IsEffectivelyEnabled);
                if (editProvider.EditedFile is not null)
                {
                    throw new InvalidOperationException("Cancelling checkout still called the provider.");
                }
                Invoke(edit);
                confirm = dialog.OwnedWindows.Single();
                Invoke(confirm.GetVisualDescendants().OfType<Button>().Single(item => Equals(item.Content, text.Get("Vcs.OpenForEdit"))));
                await WaitAsync(() => editProvider.EditedFile == file);
            }
            finally { owner.Close(); }
            if (!File.ReadAllBytes(file).SequenceEqual(workingBytes) || !File.ReadAllBytes(index).SequenceEqual(indexBytes))
            {
                throw new InvalidOperationException("Read-only status or revision comparison modified the workspace or index.");
            }
            if (!main.Extensions.SetEnabled(gitId, false, explicitlyTrusted: false) ||
                main.Extensions.VersionControlProviders.Any(item => item.Id == gitId))
            {
                throw new InvalidOperationException("Disabling Git did not remove the provider.");
            }

        }
        finally { main.DisposeStorageFiles(); }
        Console.WriteLine("Bundled plugin trust, Git status, Resource Viewer history and graphical comparison passed.");

        async Task SaveAsync()
        {
            using var stream = File.Create(file);
            await new ProjectFileService().SaveAsync(stream, graph);
        }

        void Git(params string[] args)
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var arg in new[] { "-c", "core.hooksPath=" + Path.Combine(root, "no-hooks") }.Concat(args))
            {
                start.ArgumentList.Add(arg);
            }
            using var process = Process.Start(start)!;
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("Git fixture command timed out.");
            }
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(process.StandardError.ReadToEnd());
            }
        }

        static void Invoke(Button button) =>
            new ButtonAutomationPeer(button).GetProvider<IInvokeProvider>()!.Invoke();

        static async Task WaitAsync(Func<bool> ready)
        {
            for (var attempt = 0; attempt < 160; attempt++)
            {
                if (ready())
                {
                    return;
                }
                await Task.Delay(25);
            }
            throw new TimeoutException("The version control UI did not reach the expected state.");
        }
    }

    private sealed class EditFixture : IVersionControlEditProvider
    {
        public string Id => "liberafx.fixture.workspace";
        public string Name => "Edit fixture";
        public string? EditedFile { get; private set; }
        public Task<IReadOnlyList<VcsRevision>> GetHistoryAsync(string localFile, CancellationToken cancellation) =>
            Task.FromResult<IReadOnlyList<VcsRevision>>([]);
        public Task<byte[]> ReadRevisionAsync(string localFile, string revision, CancellationToken cancellation) =>
            throw new NotSupportedException();
        public Task OpenForEditAsync(string localFile, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            EditedFile = localFile;
            return Task.CompletedTask;
        }
    }

    public static void VerifyCrashProvider(string sampleDirectory)
    {
        const string id = "liberafx.sample.redmine-crash";
        const string redmineId = "liberafx.sample.redmine.crash-reports";
        const string assembly = "LiberaFx.Sample.RedmineCrash.dll";
        const string manifest = "liberafx-extension.json";
        var root = Path.Combine(AppDataDirectory.Root, "crash-provider-host-tests", Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "Extensions", "redmine");
        Directory.CreateDirectory(package);
        File.Copy(Path.Combine(sampleDirectory, manifest), Path.Combine(package, manifest));
        File.Copy(Path.Combine(sampleDirectory, assembly), Path.Combine(package, assembly));
        var config = Path.Combine(package, "redmine.json");
        const string validConfiguration =
            """{"BaseUrl":"https://example.invalid/redmine/","ProjectId":42,"CredentialTarget":"LiberaFx.ContractTest"}""";
        var graph = new ActiveGraphProvider(new GraphWorkspaceViewModel(new UndoRedoManager()));
        var github = new GitHubCrashReportProvider("fixture/reports", "LiberaFx.ContractTest");

        File.WriteAllText(config, "{broken");
        using (var untrusted = CreateHost())
        {
            RequireBuiltInOnly(untrusted);
            if (untrusted.Extensions.Single(entry => entry.Id == id).Enabled || untrusted.SetEnabled(id, true, explicitlyTrusted: false) ||
                !untrusted.SetEnabled(id, true, explicitlyTrusted: true))
            {
                throw new InvalidOperationException("The external provider must require explicit trust.");
            }
        }
        using (var broken = CreateHost())
        {
            RequireBuiltInOnly(broken);
            if (!broken.Extensions.Single(entry => entry.Id == id).Status.StartsWith("Load failed:", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("An approved provider with broken configuration was not diagnosed.");
            }
        }
        File.WriteAllText(config, validConfiguration);
        using (var changed = CreateHost())
        {
            RequireBuiltInOnly(changed);
            if (changed.Extensions.Single(entry => entry.Id == id).Enabled || !changed.SetEnabled(id, true, explicitlyTrusted: true))
            {
                throw new InvalidOperationException("Changing provider configuration must require reapproval.");
            }
        }
        using (var enabled = CreateHost())
        {
            if (!enabled.CrashReportProviders.Any(provider => provider.Id == redmineId) ||
                !enabled.SetEnabled(id, false, explicitlyTrusted: false))
            {
                throw new InvalidOperationException("The independently built Redmine DLL did not load or disable.");
            }
            RequireBuiltInOnly(enabled);
        }
        using (var restarted = CreateHost())
        {
            RequireBuiltInOnly(restarted);
        }
        var manifestPath = Path.Combine(package, manifest);
        var manifestContent = File.ReadAllText(manifestPath);
        File.WriteAllText(manifestPath, manifestContent.Replace("\"apiVersion\": 1", "\"apiVersion\": 999",
            StringComparison.Ordinal));
        using (var incompatible = CreateHost())
        {
            RequireBuiltInOnly(incompatible);
            if (incompatible.SetEnabled(id, true, explicitlyTrusted: true))
            {
                throw new InvalidOperationException("The crash provider can be enabled with an incompatible SDK version.");
            }
        }
        File.WriteAllText(manifestPath, manifestContent.Replace(assembly, "Missing.Redmine.dll",
            StringComparison.Ordinal));
        using var missing = CreateHost();
        RequireBuiltInOnly(missing);
        if (missing.Extensions.Any(entry => entry.Id == id && entry.Enabled))
        {
            throw new InvalidOperationException("A provider whose DLL is missing must not be enabled.");
        }

        EditorExtensionHost CreateHost() => new(graph, root, [github]);

        void RequireBuiltInOnly(EditorExtensionHost host)
        {
            if (host.CrashReportProviders.Count != 1 || !ReferenceEquals(host.CrashReportProviders[0], github))
            {
                throw new InvalidOperationException("The external provider unexpectedly registered or replaced GitHub.");
            }
        }
    }

    public static void Run(string sampleDirectory)
    {
        var root = Path.Combine(AppDataDirectory.Root, "extension-host-tests", Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "Extensions", "sample");
        Directory.CreateDirectory(package);
        foreach (var file in Directory.GetFiles(sampleDirectory))
            File.Copy(file, Path.Combine(package, Path.GetFileName(file)));
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var provider = new ActiveGraphProvider(graph);
        const string id = "liberafx.sample.add-comment";
        using (var untrusted = new EditorExtensionHost(provider, root))
        {
            if (untrusted.Commands.Count != 0 || untrusted.Extensions.Single(entry => entry.Id == id).Enabled ||
                untrusted.SetEnabled(id, true, explicitlyTrusted: false) ||
                !untrusted.SetEnabled(id, true, explicitlyTrusted: true) || untrusted.Commands.Count != 0)
                throw new InvalidOperationException("Discovery must not execute untrusted packages or load newly approved code.");
        }
        using (var trusted = new EditorExtensionHost(provider, root))
        {
            if (trusted.Commands.Count != 1)
                throw new InvalidOperationException("Trusted external sample did not load: " + trusted.Extensions.Single(entry => entry.Id == id).Status);
            trusted.ExecuteAsync(id + "/add-comment").GetAwaiter().GetResult();
            if (graph.Nodes.Count != 1 || !graph.History.CanUndo)
                throw new InvalidOperationException("Extension edits must use document history.");
            graph.History.Undo();
            if (graph.Nodes.Count != 0) throw new InvalidOperationException("Extension edit was not undoable.");
            trusted.SetEnabled(id, false, explicitlyTrusted: false);
            try
            {
                trusted.ExecuteAsync(id + "/add-comment").GetAwaiter().GetResult();
                throw new Exception("A disabled extension command was executable.");
            }
            catch (InvalidOperationException) { }
        }
        using (var approve = new EditorExtensionHost(provider, root))
            if (!approve.SetEnabled(id, true, explicitlyTrusted: true)) throw new InvalidOperationException(approve.Status);
        File.AppendAllText(Path.Combine(package, "liberafx-extension.json"), " ");
        using (var changed = new EditorExtensionHost(provider, root))
            if (changed.Commands.Count != 0 || changed.Extensions.Single(entry => entry.Id == id).Enabled)
                throw new InvalidOperationException("A modified package retained approval.");
        var preferences = Path.Combine(root, "extensions.json");
        using (var stale = new EditorExtensionHost(provider, root))
        {
            File.WriteAllText(preferences, "{}");
            if (stale.SetEnabled(id, true, explicitlyTrusted: true) || File.ReadAllText(preferences) != "{}")
                throw new InvalidOperationException("Concurrent preference changes must not be overwritten.");
        }
        File.WriteAllText(preferences, "{invalid");
        using (var corrupt = new EditorExtensionHost(provider, root))
            if (corrupt.SetEnabled(id, true, explicitlyTrusted: true) || File.ReadAllText(preferences) != "{invalid")
                throw new InvalidOperationException("Corrupt preferences must be preserved without executing plugins.");
    }
}
