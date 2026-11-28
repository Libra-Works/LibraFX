using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Inspector.Abstractions;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Extensions;
using LiberaFx.Services.Project;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;
using LiberaFx.ViewModels.Main;
using LiberaFx.Views.Controls;
using LiberaFx.Views.Windows;

namespace LiberaFx.Tests;

internal static class InspectorPluginTests
{
    private const string PluginId = "test.color.fixture";
    private const string InspectorId = "fixture-editor";
    private const string FailurePluginId = "test.color.failure";
    private const string FailureInspectorId = "factory-throws";

    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("LIBERAFX_INSPECTOR_SAMPLE_STAGE") is { } stage)
        {
            RunSampleStage(stage);
            return;
        }
        var originalRoot = Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(originalRoot) || !Path.IsPathFullyQualified(originalRoot))
            throw new ArgumentException("Set an absolute isolated LIBERAFX_DATA_ROOT for inspector SDK regressions.");
        var root = Path.Combine(Path.GetFullPath(originalRoot), "inspector-sdk-" + Guid.NewGuid().ToString("N"));
        var marker = Path.Combine(root, "fixture-execution.log");
        Environment.SetEnvironmentVariable("LIBERAFX_DATA_ROOT", root);
        Environment.SetEnvironmentVariable("LIBERAFX_INSPECTOR_TEST_MARKER", marker);
        InspectorPluginService? service = null;
        try
        {
            PrepareFixture(root);
            service = InspectorPluginService.Shared;
            var incompatible = service.Plugins.SingleOrDefault(plugin => plugin.Id == "test.incompatible");
            var missing = service.Plugins.SingleOrDefault(plugin => plugin.Id == "test.missing");
            var legacyTrusted = service.Plugins.SingleOrDefault(plugin => plugin.Id == FailurePluginId);
            if (!service.Diagnostics.Any(message => message.Contains("Duplicate plugin ID", StringComparison.Ordinal)) ||
                incompatible?.IsCompatible != false ||
                missing is null || !missing.IsEnabled || missing.IsTrusted ||
                legacyTrusted?.IsTrusted != false ||
                !service.Diagnostics.Any(message => message.Contains("duplicated", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Duplicate IDs, legacy trust migration, API incompatibility or duplicate inspector IDs were not diagnosed.");
            if (File.Exists(marker))
                throw new InvalidOperationException("Manifest discovery executed a disabled plugin DLL.");
            if (service.Plugins.Any(plugin => plugin.Id == PluginId))
                throw new InvalidOperationException("The fixture should not be installed before the add-folder action.");
            if (!service.TrySetEnabled("test.missing", true, userConfirmed: true))
                throw new InvalidOperationException($"Could not explicitly trust the missing-DLL fixture: {service.Status}");
            RunChild("--inspector-plugins-missing-check");
            if (File.Exists(marker))
                throw new InvalidOperationException("Trusting a missing-DLL plugin executed unrelated plugin code.");
            if (!service.TryInstallPluginFolder(Path.Combine(root, "fixture-package"), out var installMessage) ||
                service.Plugins.All(plugin => plugin.Id != PluginId))
                throw new InvalidOperationException($"Adding a manifest-based plugin folder failed: {installMessage}");
            if (File.Exists(marker))
                throw new InvalidOperationException("Adding a disabled plugin folder executed its DLL.");
            if (service.TrySetEnabled(PluginId, true, userConfirmed: false))
                throw new InvalidOperationException("A plugin was enabled without an explicit trust confirmation.");
            if (!service.TrySetEnabled(PluginId, true, userConfirmed: true) ||
                !service.TrySetDefault("color", $"{PluginId}/{InspectorId}"))
                throw new InvalidOperationException($"Could not persist explicit trust and per-type inspector choice: {service.Status}");
            if (!service.TrySetEnabled(FailurePluginId, true, userConfirmed: true))
                throw new InvalidOperationException($"Could not explicitly trust the factory-failure fixture: {service.Status}");
            if (File.Exists(marker))
                throw new InvalidOperationException("Enabling a plugin hot-loaded DLL code instead of requiring restart.");

            var preferencesPath = Path.Combine(root, "LiberaFx", "inspector-plugins.json");
            using (var preferences = JsonDocument.Parse(File.ReadAllText(preferencesPath)))
            {
                if (preferences.RootElement.GetProperty("SchemaVersion").GetInt32() != 2 ||
                    !preferences.RootElement.GetProperty("Enabled").GetProperty(PluginId).GetBoolean() ||
                    !preferences.RootElement.GetProperty("Trusted").GetProperty(PluginId).GetBoolean() ||
                    preferences.RootElement.GetProperty("Defaults").GetProperty("color").GetString() !=
                    $"{PluginId}/{InspectorId}" ||
                    preferences.RootElement.GetProperty("Fingerprints").GetProperty(PluginId).GetString()?.Length != 64)
                    throw new InvalidOperationException("Inspector trust fingerprints, enablement and default selection did not persist.");
            }

            var pluginDirectory = Path.Combine(root, "LiberaFx", "InspectorPlugins", PluginId);
            var tamperedFile = Path.Combine(pluginDirectory, "untrusted-extra-file.txt");
            File.WriteAllText(tamperedFile, "The package changed after explicit trust.");
            try { RunChild("--inspector-plugins-tampered-check"); }
            finally { File.Delete(tamperedFile); }
            RunChild("--inspector-plugins-reload");
            if (!File.Exists(marker) || File.ReadAllLines(marker).Count(line => line == "loaded") != 1)
                throw new InvalidOperationException("The explicitly enabled plugin did not execute exactly once on restart.");
            RunIndependentSamples(Path.Combine(root, "independent-samples"));
            Console.WriteLine("Inspector SDK, disabled-DLL discovery, trust, restart, per-type selection, UI edit, undo/redo and persistence checks passed.");
        }
        finally
        {
            service?.Dispose();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Environment.SetEnvironmentVariable("LIBERAFX_DATA_ROOT", originalRoot);
            Environment.SetEnvironmentVariable("LIBERAFX_INSPECTOR_TEST_MARKER", null);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    public static void RunReloadChild()
    {
        var marker = Environment.GetEnvironmentVariable("LIBERAFX_INSPECTOR_TEST_MARKER")
            ?? throw new ArgumentException("Inspector execution marker is not configured.");
        var service = InspectorPluginService.Shared;
        if (!File.Exists(marker) || service.Plugins.SingleOrDefault(plugin => plugin.Id == PluginId) is not
            { IsEnabled: true, IsTrusted: true } entry ||
            entry.Status != LocalizationService.Shared.Get("Editor.InspectorStatusLoaded"))
            throw new InvalidOperationException("The trusted plugin did not load at startup.");

        var model = new MainViewModel();
        var settings = new SettingsWindow { DataContext = model };
        settings.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            if (settings.FindControl<TabControl>("PluginSettingsTabs") is not { } tabs ||
                !tabs.Items.OfType<TabItem>().Any(item =>
                    item.Header?.ToString() == LocalizationService.Shared.Get("Editor.Inspectors")))
                throw new InvalidOperationException("The Inspector settings tab is not visible.");
            var search = settings.FindControl<TextBox>("InspectorSearchBox")
                ?? throw new InvalidOperationException("Inspector search control is missing.");
            search.Text = PluginId;
            Dispatcher.UIThread.RunJobs();
            if (service.VisiblePlugins.Count != 1 || service.VisiblePlugins[0].Id != PluginId)
                throw new InvalidOperationException("The Inspector settings search did not filter by stable plugin ID.");
            search.Text = string.Empty;
            Dispatcher.UIThread.RunJobs();
        }
        finally { settings.Close(); }

        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var definition = GraphNodeDefinition.All.FirstOrDefault(item =>
            item.TypeName == "Color" && item.Parameters.Any(parameter => parameter.Type == "color"))
            ?? throw new InvalidOperationException("No built-in Color parameter was available for the inspector regression.");
        var node = graph.CreateNode(definition.Kind);
        graph.SelectNode(node);
        var color = definition.Parameters.First(parameter => parameter.Type == "color");
        var parameter = new NodeParameterEditorViewModel(graph, node, color);
        var host = new ParameterEditorHost { DataContext = parameter };
        var window = new Window { Content = host };
        window.Show();
        Button edit;
        try
        {
            Dispatcher.UIThread.RunJobs();
            edit = host.GetVisualDescendants().OfType<Button>()
                .SingleOrDefault(button => button.Name == "InspectorFixtureEdit")
                ?? throw new InvalidOperationException("The selected SDK inspector was not hosted in the parameter editor.");
            Invoke(edit);
            Dispatcher.UIThread.RunJobs();
            if (!NodeParameter.ValuesEqual(node.GetParameterValue(color), JsonSerializer.SerializeToElement(new[] { 0.21, 0.32, 0.43, 0.54 })))
                throw new InvalidOperationException("The SDK inspector edit did not update the graph through the host command path.");
            if (!graph.History.Undo() || !graph.History.Redo() ||
                !NodeParameter.ValuesEqual(node.GetParameterValue(color),
                    JsonSerializer.SerializeToElement(new[] { 0.21, 0.32, 0.43, 0.54 })))
                throw new InvalidOperationException("Inspector edits were not undoable and redoable.");
            using var project = new MemoryStream();
            new ProjectFileService().SaveAsync(project, graph).GetAwaiter().GetResult();
            project.Position = 0;
            var reloaded = ProjectDocumentReader.LoadAsync(project).GetAwaiter().GetResult();
            if (!NodeParameter.ValuesEqual(reloaded.Graph.Nodes.Single().Parameters!.Single(item => item.Id == color.Id).Value,
                    JsonSerializer.SerializeToElement(new[] { 0.21, 0.32, 0.43, 0.54 })))
                throw new InvalidOperationException("Inspector-edited values did not survive project persistence.");
            graph.PasteSnapshot(graph.CreateSelectionSnapshot(), 500, 500);
            var copiedNode = graph.Nodes.Single(item => item.Id != node.Id);
            if (!NodeParameter.ValuesEqual(copiedNode.GetParameterValue(color),
                    JsonSerializer.SerializeToElement(new[] { 0.21, 0.32, 0.43, 0.54 })) ||
                !graph.History.Undo() || graph.Nodes.Count != 1 ||
                !graph.History.Redo() || graph.Nodes.Count != 2 ||
                !NodeParameter.ValuesEqual(copiedNode.GetParameterValue(color),
                    JsonSerializer.SerializeToElement(new[] { 0.21, 0.32, 0.43, 0.54 })))
                throw new InvalidOperationException("Inspector-edited values did not survive copy/paste and its undo/redo.");
            graph.SelectNode(node);
        }
        finally { window.Close(); }

        if (!service.TrySetDefault("color", $"{FailurePluginId}/{FailureInspectorId}"))
            throw new InvalidOperationException($"Could not select the factory-failure fixture: {service.Status}");
        var fallbackHost = new ParameterEditorHost { DataContext = parameter };
        var fallbackWindow = new Window { Content = fallbackHost };
        var beforeFailure = node.GetParameterValue(color).Clone();
        fallbackWindow.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            if (fallbackHost.GetVisualDescendants().OfType<Button>()
                    .Any(button => button.Name == "InspectorFixtureEdit") ||
                !service.Diagnostics.Any(message => message.Contains("factory failed", StringComparison.OrdinalIgnoreCase)) ||
                !NodeParameter.ValuesEqual(beforeFailure, node.GetParameterValue(color)))
                throw new InvalidOperationException("A throwing factory did not visibly fall back without changing the graph value.");
        }
        finally { fallbackWindow.Close(); }
        if (!service.TrySetDefault("color", $"{PluginId}/{InspectorId}"))
            throw new InvalidOperationException("Could not restore the working Color inspector after factory-fallback checks.");

        Dispatcher.UIThread.RunJobs();
        graph.SelectNode(null);
        Invoke(edit);
        Dispatcher.UIThread.RunJobs();
        if (!File.ReadAllText(marker).Contains("rejected", StringComparison.Ordinal))
            throw new InvalidOperationException("A stale inspector context edited after selection change or disposal.");

        if (!service.TrySetEnabled(PluginId, false, userConfirmed: false))
            throw new InvalidOperationException($"The plugin could not be disabled: {service.Status}");
        RunChild("--inspector-plugins-disabled-check");
        model.DisposeStorageFiles();
    }

    public static void RunDisabledChildCheck()
    {
        var marker = Environment.GetEnvironmentVariable("LIBERAFX_INSPECTOR_TEST_MARKER")
            ?? throw new ArgumentException("Inspector execution marker is not configured.");
        var previous = File.ReadAllText(marker);
        var service = InspectorPluginService.Shared;
        if (service.Plugins.SingleOrDefault(plugin => plugin.Id == PluginId) is not { IsEnabled: false } ||
            File.ReadAllText(marker) != previous)
            throw new InvalidOperationException("A disabled DLL was executed on restart.");
    }

    public static void RunTamperedChildCheck()
    {
        var marker = Environment.GetEnvironmentVariable("LIBERAFX_INSPECTOR_TEST_MARKER")
            ?? throw new ArgumentException("Inspector execution marker is not configured.");
        var markerBefore = File.Exists(marker) ? File.ReadAllText(marker) : null;
        var service = InspectorPluginService.Shared;
        if (service.Plugins.SingleOrDefault(plugin => plugin.Id == PluginId) is not
                { IsEnabled: true, IsTrusted: false } ||
            (File.Exists(marker) ? File.ReadAllText(marker) : null) != markerBefore)
            throw new InvalidOperationException("Changed plugin files ran without renewed explicit trust.");
    }

    public static void RunMissingChildCheck()
    {
        var service = InspectorPluginService.Shared;
        if (service.Plugins.SingleOrDefault(plugin => plugin.Id == "test.missing") is not
                { IsEnabled: true, IsTrusted: true } entry ||
            !entry.Status.Contains("Load failed", StringComparison.Ordinal))
            throw new InvalidOperationException("An explicitly trusted plugin with a missing DLL was not diagnosed.");
    }

    private static void PrepareFixture(string root)
    {
        var packageDirectory = Path.Combine(root, "fixture-package");
        Directory.CreateDirectory(packageDirectory);
        foreach (var source in Directory.EnumerateFiles(AppContext.BaseDirectory))
            File.Copy(source, Path.Combine(packageDirectory, Path.GetFileName(source)), overwrite: true);
        var assemblyName = Path.GetFileName(Assembly.GetExecutingAssembly().Location);
        WriteManifest(packageDirectory, PluginId, "Smoke Color Inspector", InspectorId,
            "Fixture Editor", InspectorSdk.ApiVersion, assemblyName, typeof(InspectorFixturePlugin).FullName!,
            description: "Regression fixture for host-mediated edits.");

        var plugins = Path.Combine(root, "LiberaFx", "InspectorPlugins");
        WriteManifest(Path.Combine(plugins, "missing"), "test.missing", "Missing DLL",
            "missing-editor", "Missing", InspectorSdk.ApiVersion, "Missing.Dependency.dll", "Missing.Plugin");
        WriteManifest(Path.Combine(plugins, "incompatible"), "test.incompatible", "Incompatible API",
            "old-editor", "Old editor", InspectorSdk.ApiVersion + 1, assemblyName, "Missing.Plugin");
        WriteManifest(Path.Combine(plugins, "duplicate-a"), "test.duplicate", "Duplicate A",
            "same-editor", "Same", InspectorSdk.ApiVersion, "Unused.dll", "Missing.Plugin");
        WriteManifest(Path.Combine(plugins, "duplicate-b"), "test.duplicate", "Duplicate B",
            "same-editor", "Same", InspectorSdk.ApiVersion, "Unused.dll", "Missing.Plugin");
        WriteManifest(Path.Combine(plugins, "duplicate-inspector"), "test.bad-inspectors", "Bad inspector list",
            "duplicate", "First", InspectorSdk.ApiVersion, "Unused.dll", "Missing.Plugin",
            duplicateInspector: true);

        var failureDirectory = Path.Combine(plugins, "factory-failure");
        Directory.CreateDirectory(failureDirectory);
        foreach (var source in Directory.EnumerateFiles(packageDirectory).Where(path =>
                     Path.GetExtension(path) is ".dll" or ".json"))
            File.Copy(source, Path.Combine(failureDirectory, Path.GetFileName(source)), overwrite: true);
        WriteManifest(failureDirectory, FailurePluginId, "Factory Failure Inspector",
            FailureInspectorId, "Factory throws", InspectorSdk.ApiVersion, assemblyName,
            typeof(InspectorFailurePlugin).FullName!, description: "Regression fixture for factory fallback.");
        var enabled = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["test.missing"] = true,
            ["test.incompatible"] = true,
            [FailurePluginId] = true
        };
        var preferences = new
        {
            schemaVersion = 1,
            Enabled = enabled,
            Trusted = new Dictionary<string, bool>(enabled, StringComparer.Ordinal),
            Defaults = new Dictionary<string, string>(StringComparer.Ordinal)
        };
        var preferencePath = Path.Combine(root, "LiberaFx", "inspector-plugins.json");
        Directory.CreateDirectory(Path.GetDirectoryName(preferencePath)!);
        File.WriteAllText(preferencePath, JsonSerializer.Serialize(preferences));
    }

    private static void WriteManifest(string directory, string pluginId, string name, string inspectorId,
        string inspectorName, int apiVersion, string assembly, string entryType, bool duplicateInspector = false,
        string description = "Smoke fixture.")
    {
        Directory.CreateDirectory(directory);
        var inspectors = new List<object>
        {
            new { id = inspectorId, displayName = inspectorName, description, types = new[] { "color" } }
        };
        if (duplicateInspector)
            inspectors.Add(new { id = inspectorId, displayName = inspectorName, description = "Duplicate.", types = new[] { "color" } });
        var manifest = new
        {
            manifestVersion = 1,
            pluginId,
            displayName = name,
            version = "1.0.0",
            apiVersion,
            targetFramework = "net10.0",
            avaloniaVersion = "12.1.6",
            assembly,
            entryType,
            inspectors
        };
        File.WriteAllText(Path.Combine(directory, "liberafx-inspector.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Invoke(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        peer.GetProvider<IInvokeProvider>()!.Invoke();
        Dispatcher.UIThread.RunJobs();
    }

    private static void CaptureWindow(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("LIBERAFX_INSPECTOR_SCREENSHOTS");
        if (string.IsNullOrEmpty(directory)) return;
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("Inspector screenshots require an absolute isolated output directory.");
        Directory.CreateDirectory(directory);
        foreach (var visual in window.GetVisualDescendants()) visual.InvalidateVisual();
        window.InvalidateVisual();
        for (var frame = 0; frame < 10; frame++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Thread.Sleep(40);
        }
        using var bitmap = new RenderTargetBitmap(new PixelSize(
            (int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height)));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
    }

    private static void RunIndependentSamples(string root)
    {
        var rgb = Environment.GetEnvironmentVariable("LIBERAFX_INSPECTOR_RGB_PACKAGE");
        var hsv = Environment.GetEnvironmentVariable("LIBERAFX_INSPECTOR_HSV_PACKAGE");
        if (string.IsNullOrEmpty(rgb) || string.IsNullOrEmpty(hsv))
        {
            Console.WriteLine("Independent RGB/HSV workflow NOT RUN: set absolute LIBERAFX_INSPECTOR_RGB_PACKAGE and LIBERAFX_INSPECTOR_HSV_PACKAGE.");
            return;
        }
        foreach (var package in new[] { rgb, hsv })
            if (!Path.IsPathFullyQualified(package) || !File.Exists(Path.Combine(package, "liberafx-inspector.json")))
                throw new ArgumentException($"An absolute independently built sample package is required: {package}");
        Directory.CreateDirectory(root);
        void Stage(string name) => RunChild("--inspector-plugins", root, name);
        Stage("introduce");
        Stage("rgb");
        Stage("hsv");
        Directory.Delete(Path.Combine(root, "LiberaFx", "InspectorPlugins", "sample.color.hsv"), recursive: true);
        Stage("missing");
        var manifestPath = Path.Combine(root, "LiberaFx", "InspectorPlugins", "sample.color.rgb", "liberafx-inspector.json");
        var manifest = File.ReadAllText(manifestPath);
        var incompatible = System.Text.Json.Nodes.JsonNode.Parse(manifest)!;
        incompatible["apiVersion"] = InspectorSdk.ApiVersion + 1;
        File.WriteAllText(manifestPath, incompatible.ToJsonString());
        Stage("incompatible");
        File.WriteAllText(manifestPath, manifest);
        var preferencesPath = Path.Combine(root, "LiberaFx", "inspector-plugins.json");
        var preferences = File.ReadAllText(preferencesPath);
        File.WriteAllText(preferencesPath, "{broken");
        Stage("corrupt");
        if (File.ReadAllText(preferencesPath) != "{broken")
            throw new InvalidOperationException("Corrupt preferences were overwritten.");
        File.WriteAllText(preferencesPath, preferences);
        Stage("recovered");
        Console.WriteLine("Independent RGB/HSV: import service, Settings trust/selection automation, slider automation, undo/redo, process restarts, disabled/missing/incompatible fallback and manual corrupt-settings recovery PASS.");
    }

    private static void RunSampleStage(string stage)
    {
        const string rgbId = "sample.color.rgb";
        const string hsvId = "sample.color.hsv";
        var root = Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT")!;
        var service = InspectorPluginService.Shared;
        var model = new MainViewModel();
        var settings = new SettingsWindow { DataContext = model };
        var capture = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LIBERAFX_INSPECTOR_SCREENSHOTS"));
        if (capture)
        {
            settings.Width = 1100;
            settings.Height = 1000;
        }
        settings.Show();
        Dispatcher.UIThread.RunJobs();
        var tabs = settings.FindControl<TabControl>("SettingsTabs")!;
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(item =>
            item.Header?.ToString() == LocalizationService.Shared.Get("Extensions.Plugins"));
        var plugins = settings.FindControl<TabControl>("PluginSettingsTabs")!;
        plugins.SelectedItem = plugins.Items.OfType<TabItem>().Single(item =>
            item.Header?.ToString() == LocalizationService.Shared.Get("Editor.Inspectors"));
        Dispatcher.UIThread.RunJobs();

        void Toggle(string id, bool confirm)
        {
            var checkbox = settings.GetVisualDescendants().OfType<CheckBox>().Single(item => item.Tag as string == id);
            checkbox.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            if (confirm)
            {
                var dialog = settings.OwnedWindows.Single();
                if (stage == "introduce") CaptureWindow(dialog, $"{id}-trust-en.png");
                Invoke(dialog.GetVisualDescendants().OfType<Button>().Single(item =>
                    item.Content?.ToString() == LocalizationService.Shared.Get("Editor.InspectorTrustConfirm")));
            }
        }
        void Select(string key)
        {
            var combo = settings.GetVisualDescendants().OfType<ComboBox>()
                .Single(item => item.DataContext is InspectorDefaultChoice { Type: "color" });
            var option = ((InspectorDefaultChoice)combo.DataContext!).Options.Single(item => item.Id == key);
            combo.IsDropDownOpen = true;
            Dispatcher.UIThread.RunJobs();
            var container = combo.ContainerFromIndex(combo.Items.IndexOf(option))
                ?? throw new InvalidOperationException("The Color option was not realized.");
            var peer = ControlAutomationPeer.CreatePeerForElement(container)!;
            peer.GetProvider<ISelectionItemProvider>()!.Select();
            combo.IsDropDownOpen = false;
            Dispatcher.UIThread.RunJobs();
            if (((InspectorDefaultChoice)combo.DataContext!).Selected.Id != key)
                throw new InvalidOperationException("Settings Color selection did not persist.");
        }
        try
        {
            if (stage == "introduce")
            {
                foreach (var package in new[] { "LIBERAFX_INSPECTOR_RGB_PACKAGE", "LIBERAFX_INSPECTOR_HSV_PACKAGE" })
                    if (!service.TryInstallPluginFolder(Environment.GetEnvironmentVariable(package)!, out var message))
                        throw new InvalidOperationException(message);
                Dispatcher.UIThread.RunJobs();
                foreach (var id in new[] { rgbId, hsvId })
                {
                    if (service.Plugins.Single(item => item.Id == id).IsTrusted)
                        throw new InvalidOperationException("A new sample was trusted without confirmation.");
                    Toggle(id, confirm: true);
                    if (!service.Plugins.Single(item => item.Id == id).IsEnabled)
                        throw new InvalidOperationException("Settings trust confirmation did not enable the sample.");
                }
                Select($"{rgbId}/rgb-sliders");
                return;
            }

            if (capture && stage is "rgb" or "hsv")
            {
                var text = LocalizationService.Shared;
                var original = text.SelectedLanguage;
                foreach (var language in new[] { "en", "ja" })
                {
                    text.SelectedLanguage = text.Languages.Single(item => item.Id == language);
                    if (service.Plugins.Single(item => item.Id == (stage == "hsv" ? hsvId : rgbId)).Status !=
                        text.Get("Editor.InspectorStatusLoaded"))
                        throw new InvalidOperationException($"Inspector status was not localized for {language}.");
                    CaptureWindow(settings, $"settings-{stage}-{language}.png");
                }
                text.SelectedLanguage = original;
            }

            var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
            var projectPath = Path.Combine(root, "sample.lfxproj");
            if (File.Exists(projectPath))
            {
                using var input = new MemoryStream(File.ReadAllBytes(projectPath));
                var project = ProjectDocumentReader.LoadAsync(input).GetAwaiter().GetResult();
                graph.InitializeFromSnapshot(project.Graph);
            }
            var definition = GraphNodeDefinition.All.First(item =>
                item.TypeName == "Color" && item.Parameters.Any(parameter => parameter.Type == "color"));
            var node = graph.Nodes.SingleOrDefault() ?? graph.CreateNode(definition.Kind);
            graph.SelectNode(node);
            var color = definition.Parameters.First(item => item.Type == "color");
            var parameter = new NodeParameterEditorViewModel(graph, node, color);
            var before = node.GetParameterValue(color).Clone();
            var host = new ParameterEditorHost { DataContext = parameter };
            var window = new Window { Content = host };
            if (capture)
            {
                window.Width = 760;
                window.Height = 260;
            }
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var sliders = host.GetVisualDescendants().OfType<Slider>().ToArray();
                if (stage is "rgb" or "hsv" or "recovered")
                {
                    var expectedId = stage == "hsv" ? $"{hsvId}/hsv-sliders" : $"{rgbId}/rgb-sliders";
                    if (service.Defaults.Single(item => item.Type == "color").Selected.Id != expectedId ||
                        sliders.Length != 4 || (sliders[0].Maximum == 360) != (stage == "hsv"))
                        throw new InvalidOperationException($"Restart did not restore the expected {stage} editor.");
                    if (stage != "recovered")
                    {
                        var index = stage == "rgb" ? 0 : 3;
                        var value = stage == "rgb" ? 0.25 : 0.4;
                        var peer = ControlAutomationPeer.CreatePeerForElement(sliders[index])!;
                        peer.GetProvider<IRangeValueProvider>()!.SetValue(value);
                        Dispatcher.UIThread.RunJobs();
                        var edited = node.GetParameterValue(color).Clone();
                        if (Math.Abs(edited[index].GetDouble() - value) > 0.00001 || NodeParameter.ValuesEqual(before, edited))
                            throw new InvalidOperationException($"{stage} slider edit failed: {before} -> {edited}; status={parameter.Status}.");
                        CaptureWindow(window, $"editor-{stage}-edited.png");
                        if (!graph.History.Undo() || !NodeParameter.ValuesEqual(before, graph.Nodes.Single().GetParameterValue(color)))
                            throw new InvalidOperationException($"{stage} Undo failed: expected {before}, actual {graph.Nodes.Single().GetParameterValue(color)}.");
                        if (!graph.History.Redo() || !NodeParameter.ValuesEqual(edited, graph.Nodes.Single().GetParameterValue(color)))
                            throw new InvalidOperationException($"{stage} Redo failed.");
                        using (var output = new MemoryStream())
                        {
                            new ProjectFileService().SaveAsync(output, graph).GetAwaiter().GetResult();
                            File.WriteAllBytes(projectPath, output.ToArray());
                        }
                        File.WriteAllText(Path.Combine(root, "expected-color.json"), edited.GetRawText());
                    }
                    if (stage == "rgb") Select($"{hsvId}/hsv-sliders");
                    if (stage == "hsv") Toggle(rgbId, confirm: false);
                }
                else
                {
                    if (sliders.Length != 0 || !NodeParameter.ValuesEqual(before, node.GetParameterValue(color)))
                        throw new InvalidOperationException($"{stage} fallback changed the graph or kept a plugin editor.");
                    if (stage == "missing")
                    {
                        if (service.Plugins.Single(item => item.Id == rgbId).IsEnabled ||
                            !service.Diagnostics.Any(item => item.Contains("missing or not loaded", StringComparison.Ordinal)))
                            throw new InvalidOperationException("Disabled/missing DLL fallback was not diagnosed.");
                        Toggle(rgbId, confirm: true);
                        Select($"{rgbId}/rgb-sliders");
                    }
                    if (stage == "incompatible" && service.Plugins.Single(item => item.Id == rgbId).IsCompatible)
                        throw new InvalidOperationException("An incompatible replacement was accepted.");
                    if (stage == "corrupt")
                    {
                        if (service.PreferencesValid || service.TrySetDefault("color", $"{rgbId}/rgb-sliders") ||
                            service.TrySetEnabled(rgbId, true, userConfirmed: true) ||
                            settings.GetVisualDescendants().OfType<ComboBox>().Single(item =>
                                item.DataContext is InspectorDefaultChoice { Type: "color" }).IsEnabled)
                            throw new InvalidOperationException("Corrupt settings were not preserved as read-only.");
                    }
                }
                if (stage is not ("rgb" or "hsv") &&
                    !NodeParameter.ValuesEqual(JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "expected-color.json"))).RootElement,
                        node.GetParameterValue(color)))
                    throw new InvalidOperationException($"{stage} lost the persisted Color value.");
                if (stage == "hsv" && Math.Abs(before[0].GetDouble() - 0.25) > 0.00001)
                    throw new InvalidOperationException("RGB value was lost when switching to HSV after restart.");
            }
            finally { window.Close(); }
        }
        finally
        {
            settings.Close();
            model.DisposeStorageFiles();
            service.Dispose();
        }
        Console.WriteLine($"Independent sample stage {stage}: PASS");
    }

    private static void RunChild(string mode, string? root = null, string? sampleStage = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                Assembly.GetExecutingAssembly().Location,
                mode
            }
        };
        if (root is not null) startInfo.Environment["LIBERAFX_DATA_ROOT"] = root;
        if (sampleStage is not null) startInfo.Environment["LIBERAFX_INSPECTOR_SAMPLE_STAGE"] = sampleStage;
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the isolated inspector restart check.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException($"The isolated inspector restart check '{sampleStage ?? mode}' timed out.{Environment.NewLine}" +
                standardOutput.GetAwaiter().GetResult() + standardError.GetAwaiter().GetResult());
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Inspector child process failed with exit code {process.ExitCode}.{Environment.NewLine}" +
                standardOutput.GetAwaiter().GetResult() + standardError.GetAwaiter().GetResult());
        Console.Write(standardOutput.GetAwaiter().GetResult());
    }
}

public sealed class InspectorFixturePlugin : IInspectorPlugin
{
    static InspectorFixturePlugin()
    {
        if (Environment.GetEnvironmentVariable("LIBERAFX_INSPECTOR_TEST_MARKER") is { } marker)
            File.AppendAllText(marker, "loaded\n");
    }

    public string Id => "test.color.fixture";
    public string Name => "Smoke Color Inspector";
    public Version Version => new(1, 0, 0);
    public int ApiVersion => InspectorSdk.ApiVersion;
    public IReadOnlyList<IInspectorDefinition> Inspectors { get; } = [new InspectorFixtureDefinition()];
}

public sealed class InspectorFixtureDefinition : IInspectorDefinition
{
    public string Id => "fixture-editor";
    public string Name => "Fixture Editor";
    public string Description => "Regression fixture for host-mediated edits.";
    public IReadOnlyList<string> SupportedTypes { get; } = ["color"];

    public Control CreateEditor(IInspectorContext context)
    {
        var button = new Button { Name = "InspectorFixtureEdit", Content = "Set Color" };
        button.Click += (_, _) =>
        {
            var accepted = context.TrySetValue(JsonSerializer.SerializeToElement(new[] { 0.21, 0.32, 0.43, 0.54 }), out _);
            if (Environment.GetEnvironmentVariable("LIBERAFX_INSPECTOR_TEST_MARKER") is { } marker)
                File.AppendAllText(marker, accepted ? "edited\n" : "rejected\n");
        };
        return button;
    }

}

public sealed class InspectorFailurePlugin : IInspectorPlugin
{
    public string Id => "test.color.failure";
    public string Name => "Factory Failure Inspector";
    public Version Version => new(1, 0, 0);
    public int ApiVersion => InspectorSdk.ApiVersion;
    public IReadOnlyList<IInspectorDefinition> Inspectors { get; } = [new InspectorFailureDefinition()];
}

public sealed class InspectorFailureDefinition : IInspectorDefinition
{
    public string Id => "factory-throws";
    public string Name => "Factory throws";
    public string Description => "Regression fixture for factory fallback.";
    public IReadOnlyList<string> SupportedTypes { get; } = ["color"];

    public Control CreateEditor(IInspectorContext context) =>
        throw new InvalidOperationException("Intentional smoke-test factory failure.");
}
