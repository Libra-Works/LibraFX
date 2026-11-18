using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using LiberaFx.Models;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using LiberaFx.Views;

namespace LiberaFx.Tests;

internal static class DockLayoutTests
{
    public static void Run()
    {
        VerifyAuthoringDefault();
        VerifyCloseReopenAndDocuments();
        VerifyTopologyRoundtrip();
        VerifyPresetsAndFailures();
        VerifyGeometryAndMenu();
        VerifyHostedWindowReopening();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static IEnumerable<IDockable> Walk(IDock dock)
    {
        foreach (var item in dock.VisibleDockables ?? [])
        {
            yield return item;
            if (item is IDock nested)
                foreach (var child in Walk(nested)) yield return child;
        }
    }

    private static DockFactory Factory(MainViewModel main) => (DockFactory)main.Layout.Factory!;

    private static void VerifyAuthoringDefault()
    {
        var main = new MainViewModel();
        try
        {
            var factory = Factory(main);
            var timeline = factory.Tools.Single(t => t.Id == "Timeline");
            var properties = factory.Tools.Single(t => t.Id == "Properties");
            var preview = factory.Tools.Single(t => t.Id == "EngineConnection");

            void CheckDefault()
            {
                var columns = ((IDock)main.Layout.VisibleDockables!.Single()).VisibleDockables!.OfType<IDock>().ToArray();
                var canvas = (IDock)main.DocumentsDock.Owner!;
                Check(columns.Length == 3 && columns.Contains(canvas) &&
                      columns.Where(dock => dock != canvas).All(dock => dock.Proportion < canvas.Proportion),
                    "The initial graph column must have more room than either sidebar.");
                Check(timeline.Owner is IToolDock { Alignment: Alignment.Bottom } timelineDock &&
                      ReferenceEquals(timelineDock.ActiveDockable, timeline) &&
                      ReferenceEquals(timelineDock.Owner, canvas),
                    "The timeline must be selected directly below the graph.");
                Check(properties.Owner is IToolDock propertiesDock && preview.Owner is IToolDock previewDock &&
                      !ReferenceEquals(propertiesDock, previewDock) && ReferenceEquals(propertiesDock.Owner, previewDock.Owner) &&
                      ReferenceEquals(propertiesDock.ActiveDockable, properties) &&
                      ReferenceEquals(previewDock.ActiveDockable, preview),
                    "Properties and preview connection must be visible together.");
            }

            CheckDefault();
            factory.ShowTool("Curves");
            factory.CloseDockable(preview);
            factory.ApplyLayout(factory.DefaultLayout, showWindows: false);
            CheckDefault();
        }
        finally
        {
            main.DisposeStorageFiles();
        }
    }

    private static void VerifyCloseReopenAndDocuments()
    {
        var main = new MainViewModel();
        try
        {
            var factory = Factory(main);
            var tools = factory.Tools.ToArray();
            var document = main.ActiveDocument!;
            var history = document.Workspace.History;
            var initialStart = document.Workspace.Nodes.Single();
            Check(initialStart.Definition.Kind.Value == "OnStart" && !history.CanUndo && !document.IsDirty,
                "A new effect needs one initial OnStart, not an undoable edit or unsaved migration.");
            var spawnable = document.Workspace.CreateNode(GraphNodeKind.SpawnableGroup);
            Check(spawnable.ChildGraph!.Nodes.Single().Kind.Value == "OnStart" &&
                  spawnable.ChildGraph.Nodes.Single().Id != initialStart.Id,
                "A new Spawnable template must start with its own local OnStart.");
            var oldEmpty = new GraphWorkspaceViewModel(new UndoRedoManager());
            oldEmpty.InitializeFromSnapshot(new GraphSnapshot([], []));
            Check(oldEmpty.Nodes.Count == 0, "Opening an old empty document must not inject OnStart.");
            var savedTemplate = document.Workspace.CreateSnapshot().Nodes.Single(item => item.Id == spawnable.Id)
                with { ChildGraph = new GraphSnapshot([], []) };
            var oldTemplate = new GraphWorkspaceViewModel(new UndoRedoManager());
            oldTemplate.InitializeFromSnapshot(new GraphSnapshot([savedTemplate], []));
            Check(oldTemplate.Nodes.Single().ChildGraph!.Nodes.Count == 0,
                "Restoring a saved empty Spawnable must not silently add OnStart.");
            Check(history.Undo() && document.Workspace.Nodes.Count == 1 && history.Redo(),
                "Creating a Spawnable and its initial OnStart must be one undoable operation.");
            Check(history.Undo(), "Cannot restore the initial graph after checking Spawnable creation.");
            var node = document.Workspace.CreateNode(GraphNodeKind.Comment);
            var count = document.Workspace.Nodes.Count;
            foreach (var tool in tools) ((IFactory)factory).CloseDockable((object)tool);
            Check(main.Layout.HiddenDockables?.Count == 8, "Native tool close must hide all eight existing VMs.");
            foreach (var tool in tools)
            {
                Check(factory.ShowTool(tool.Id) && factory.ShowTool(tool.Id), "Closed tool could not be restored twice.");
                Check(Walk(main.Layout).Count(t => ReferenceEquals(t, tool)) == 1, "Reopen duplicated or lost a tool VM.");
            }
            Check(ReferenceEquals(main.ActiveDocument, document) && ReferenceEquals(main.ActiveGraphProvider.Graph, document.Workspace) &&
                  ReferenceEquals(document.Workspace.History, history) && document.Workspace.Nodes.Contains(node) && document.IsDirty,
                "Tool closing/reopening disturbed the active graph, history or dirty document.");
            // Also recover tools removed by the native remove API, not just hidden ones.
            var catalog = tools.Single(t => t.Id == "NodeList");
            factory.RemoveDockable(catalog, collapse: true);
            Check(factory.ShowTool("NodeList") && Walk(main.Layout).Count(t => ReferenceEquals(t, catalog)) == 1,
                "Removed tool did not recover its collapsed home.");
            main.NewDocumentCommand.Execute(null);
            var second = main.ActiveDocument!;
            Check(second.Workspace.Nodes.Single().Definition.Kind.Value == "OnStart" &&
                  second.Workspace.Nodes.Single().Id != initialStart.Id && !second.Workspace.History.CanUndo,
                "Each new document must own an independent initial OnStart.");
            var documentsDock = main.DocumentsDock;
            var extraDock = new DocumentDock { IsCollapsable = false, VisibleDockables = factory.CreateList<IDockable>(second), ActiveDockable = second };
            documentsDock.VisibleDockables!.Remove(second);
            factory.InitDockable(second, extraDock);
            var documentParent = (IDock)documentsDock.Owner!;
            var split = new ProportionalDock
            {
                Orientation = Orientation.Horizontal,
                VisibleDockables = factory.CreateList<IDockable>(documentsDock, new ProportionalDockSplitter(), extraDock),
                ActiveDockable = extraDock
            };
            var index = documentParent.VisibleDockables!.IndexOf(documentsDock);
            documentParent.VisibleDockables[index] = split;
            factory.InitDockable(split, documentParent);
            factory.ApplyLayout(factory.DefaultLayout, showWindows: false);
            Check(ReferenceEquals(main.DocumentsDock, documentsDock) && Walk(main.Layout).Contains(extraDock) &&
                  ReferenceEquals(main.ActiveDocument, second) && main.Documents.SequenceEqual(new[] { document, second }) &&
                  ReferenceEquals(second.Owner, extraDock), "Default restoration replaced or flattened open split documents.");
            history.Undo();
            Check(document.Workspace.Nodes.Count == count - 1, "Undo history was lost after layout restoration.");
        }
        finally { main.DisposeStorageFiles(); }
    }

    private static void VerifyTopologyRoundtrip()
    {
        var main = new MainViewModel();
        try
        {
            var factory = Factory(main);
            var tools = factory.Tools.ToArray();
            factory.CloseDockable(tools.Single(t => t.Id == "Resources"));
            factory.PinDockable(tools.Single(t => t.Id == "History"));
            var floating = tools.Single(t => t.Id == "EngineConnection");
            factory.FloatDockable(floating);
            var snapshot = factory.CaptureLayout();
            Check(snapshot.Windows.Count == 1 && snapshot.Hidden.Contains("Resources") &&
                  snapshot.Pinned.Values.SelectMany(ids => ids).Contains("History"), "Floating/pinned/hidden topology was not captured.");
            var json = JsonSerializer.Serialize(snapshot);
            Check(!json.Contains("Workspace") && !json.Contains("Nodes") && !json.Contains("AccessToken"),
                "Preset serialized graph contents or credentials.");
            var roundtrip = JsonSerializer.Deserialize<WindowLayoutSnapshot>(json)!;
            factory.ApplyLayout(factory.DefaultLayout, showWindows: false);
            factory.ApplyLayout(roundtrip, showWindows: false);
            var after = factory.CaptureLayout();
            Check(after.Windows.Count == 1 && after.Hidden.SequenceEqual(snapshot.Hidden) &&
                  after.Pinned.Values.SelectMany(ids => ids).SequenceEqual(snapshot.Pinned.Values.SelectMany(ids => ids)),
                "Floating/pinned/hidden topology did not survive roundtrip.");
            Check(factory.ShowTool("EngineConnection") && factory.ShowTool("History") && factory.ShowTool("Resources"),
                "Restored floating/pinned/hidden tool could not be focused.");
            Check(factory.Tools.SequenceEqual(tools), "Roundtrip recreated tools.");
            factory.CloseDockable(tools.Single(t => t.Id == "Resources"));
            var hiddenLayout = factory.CaptureLayout();
            factory.ApplyLayout(hiddenLayout, showWindows: false);
            factory.ShowTool("Resources");
            Check(tools.Single(t => t.Id == "Resources").Owner is IToolDock restoredHome &&
                  restoredHome.VisibleDockables!.Any(t => t.Id == "NodeList"), "Hidden pane lost its original tab group on roundtrip.");
            for (var i = 0; i < 30; i++)
                factory.ApplyLayout(factory.CaptureLayout(), showWindows: false);
            WindowLayoutPresetStore.ValidateSnapshot(factory.CaptureLayout());
            // A floating document and its native window must outlive preset changes.
            var document = main.ActiveDocument!;
            factory.FloatDockable(document);
            var documentWindow = main.Layout.Windows!.Single(w => w.Layout is not null && Walk(w.Layout).Contains(document));
            var documentOwner = document.Owner;
            var floatingSnapshot = factory.CaptureLayout();
            factory.ApplyLayout(floatingSnapshot, showWindows: false);
            Check(main.Layout.Windows!.Contains(documentWindow) && ReferenceEquals(document.Owner, documentOwner),
                "Floating document roundtrip replaced its native window or owner.");
            factory.ApplyLayout(factory.DefaultLayout, showWindows: false);
            Check(main.Layout.Windows!.Contains(documentWindow) && ReferenceEquals(document.Owner, documentOwner) &&
                  ReferenceEquals(main.ActiveDocument, document), "Applying a preset closed or redocked a floating document.");
        }
        finally { main.DisposeStorageFiles(); }
    }

    private static void VerifyPresetsAndFailures()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "build", "window-layout-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var main = new MainViewModel();
        try
        {
            var factory = Factory(main);
            var store = new WindowLayoutPresetStore(Path.Combine(directory, "layouts.json"));
            var vm = new WindowLayoutsViewModel(factory, main, store);
            factory.CloseDockable(factory.Tools.Single(t => t.Id == "NodeList"));
            Check(vm.Create("Editing") && store.Load().Selected == "Editing", "Named preset creation failed.");
            Check(!vm.Create("editing") && !vm.Create("../unsafe") && !vm.Create("Default"), "Unsafe/duplicate/protected names were accepted.");
            Check(vm.Apply("Default") && Walk(main.Layout).Any(t => t.Id == "NodeList"), "Default did not reopen panes.");
            Check(vm.Apply("Editing") && main.Layout.HiddenDockables!.Any(t => t.Id == "NodeList"), "Preset selection did not restore hidden pane.");
            factory.ShowTool("NodeList");
            Check(vm.SaveCurrent("Editing") && !store.Load().Presets[0].Layout.Hidden.Contains("NodeList"), "Preset update did not replace topology.");
            factory.CloseDockable(factory.Tools.Single(t => t.Id == "Curves"));
            Check(vm.SaveWorkspace(), "Last workspace save failed.");
            var startup = new MainViewModel();
            try
            {
                var startupVm = new WindowLayoutsViewModel(Factory(startup), startup, store);
                startupVm.RestoreWorkspace();
                Check(startupVm.ActivePreset == "Editing" && startup.Layout.HiddenDockables!.Any(t => t.Id == "Curves"),
                    "Startup did not restore selected preset and last workspace.");
            }
            finally { startup.DisposeStorageFiles(); }
            Check(vm.Delete("Editing") && store.Load().Presets.Count == 0 && !vm.Delete("Default"), "Preset deletion or Default protection failed.");
            File.WriteAllText(store.Path, "{bad json");
            var corrupt = new WindowLayoutsViewModel(factory, main, store);
            corrupt.RestoreWorkspace();
            Check(!string.IsNullOrWhiteSpace(corrupt.Status) && !corrupt.SaveWorkspace() &&
                  File.ReadAllText(store.Path) == "{bad json", "Corrupt settings crashed startup or were silently overwritten.");
            var failurePath = Path.Combine(directory, "blocked.json");
            Directory.CreateDirectory(failurePath);
            var failureVm = new WindowLayoutsViewModel(factory, main, new WindowLayoutPresetStore(failurePath));
            Check(!failureVm.Create("Blocked") && failureVm.ActivePreset == "Default" &&
                  failureVm.Choices.Count == 1 && !string.IsNullOrWhiteSpace(failureVm.Status) &&
                  !Directory.EnumerateFiles(directory, "*.pending").Any(), "I/O failure changed state or left staging files.");
            var invalid = new WindowLayoutSettings { Version = 2 };
            ExpectInvalid(() => store.Save(invalid));
            var excessive = new WindowLayoutSettings
            {
                Presets = Enumerable.Range(0, 33).Select(i => new WindowLayoutPreset
                    { Name = $"Preset {i}", Layout = factory.DefaultLayout }).ToList()
            };
            ExpectInvalid(() => store.Save(excessive));
            var duplicate = factory.CaptureLayout();
            duplicate.Hidden.Add("NodeList");
            ExpectInvalid(() => WindowLayoutPresetStore.ValidateSnapshot(duplicate));
            var unknown = factory.CaptureLayout();
            unknown.Hidden.Add("UnknownPane");
            ExpectInvalid(() => WindowLayoutPresetStore.ValidateSnapshot(unknown));
        }
        finally
        {
            main.DisposeStorageFiles();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyHostedWindowReopening()
    {
        var main = new MainViewModel();
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 750 };
        var factory = Factory(main);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var document = main.ActiveDocument;
            var tools = factory.Tools.ToArray();
            foreach (var tool in tools) factory.CloseDockable(tool);
            factory.ApplyLayout(factory.CaptureLayout());
            var nodeListItem = window.FindControl<MenuItem>("WindowMenu")!.Items.OfType<MenuItem>()
                .Single(item => item.Tag is "NodeList");
            nodeListItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Check(window.GetVisualDescendants().OfType<NodeCatalogView>().Any(view => view.IsVisible && view.Bounds.Width > 20),
                "WINDOW > Node List did not show a usable pane after all tool docks collapsed.");
            factory.ApplyLayout(factory.DefaultLayout);
            var floatingTool = tools.Single(t => t.Id == "EngineConnection");
            factory.FloatDockable(floatingTool);
            Dispatcher.UIThread.RunJobs();
            var floatingWindow = main.Layout.Windows!.Single(w => Walk(w.Layout!).Contains(floatingTool));
            Check(floatingWindow.Host is not null, "Native floating tool window did not get a host.");
            var snapshot = factory.CaptureLayout();
            factory.ApplyLayout(factory.DefaultLayout);
            factory.ApplyLayout(snapshot);
            Dispatcher.UIThread.RunJobs();
            Check(main.Layout.Windows!.Single(w => Walk(w.Layout!).Contains(floatingTool)).Host is not null,
                "Restored floating tool window did not get a host.");
            factory.CloseDockable(floatingTool);
            Check(factory.ShowTool(floatingTool.Id), "Closing a floating tool made it impossible to reopen.");
            factory.ApplyLayout(factory.DefaultLayout);
            var historyTool = tools.Single(t => t.Id == "History");
            factory.PinDockable(historyTool);
            factory.ShowTool(historyTool.Id);
            factory.CloseDockable(historyTool);
            Check(factory.ShowTool(historyTool.Id), "Closing a pinned tool made it impossible to reopen.");
            Check(ReferenceEquals(main.ActiveDocument, document), "Hosted layout operations changed the active graph.");
            factory.ApplyLayout(factory.DefaultLayout);
            factory.FloatDockable(document!);
            Dispatcher.UIThread.RunJobs();
            var documentWindow = main.Layout.Windows!.Single(w => Walk(w.Layout!).Contains(document!));
            var documentHost = documentWindow.Host;
            var documentLayout = factory.CaptureLayout();
            factory.ApplyLayout(documentLayout);
            factory.ApplyLayout(factory.DefaultLayout);
            Dispatcher.UIThread.RunJobs();
            Check(main.Layout.Windows!.Contains(documentWindow) && ReferenceEquals(documentWindow.Host, documentHost) &&
                  documentHost is Window { IsVisible: true }, "Preset switching recreated or closed the floating document host.");
        }
        finally
        {
            factory.ApplyLayout(factory.DefaultLayout);
            foreach (var floating in main.Layout.Windows?.ToArray() ?? []) floating.Host?.Exit();
            window.Close();
            main.DisposeStorageFiles();
        }
    }

    private static void ExpectInvalid(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid preset data was accepted.");
    }

    private static void VerifyGeometryAndMenu()
    {
        var bounds = WindowLayoutGeometry.Clamp(new() { X = 15000, Y = -9000, Width = 3000, Height = 2000, Maximized = true },
            [new PixelRect(-1920, 0, 1920, 1080), new PixelRect(0, 0, 1280, 720)], 1.5);
        Check(bounds.X >= -1920 && bounds.X + bounds.Width * 1.5 <= 1280 &&
              bounds.Y >= 0 && bounds.Y + bounds.Height * 1.5 <= 1080 && bounds.Maximized,
            "Offscreen geometry did not clamp to current monitor work areas.");
        var main = new MainViewModel();
        try
        {
            var window = new MainWindow { DataContext = main };
            var menu = window.FindControl<MenuItem>("WindowMenu")!;
            Check(menu.Items.OfType<MenuItem>().Select(m => m.Tag).OfType<string>().Order().SequenceEqual(
                WindowLayoutPresetStore.PaneIds.Order()), "WINDOW menu does not expose every stable dock pane.");
            var manager = new WindowLayoutsWindow(main.WindowLayouts);
            Check(manager.FindControl<TextBox>("PresetName") is not null, "Preset manager did not load.");
        }
        finally { main.DisposeStorageFiles(); }
    }
}
