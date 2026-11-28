using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;
using LiberaFx.ViewModels.Panels;
using LiberaFx.Views.Assets;
using LiberaFx.Views.Panels;
using LiberaFx.Views.Windows;
using Avalonia.Controls;

namespace LiberaFx.ViewModels.Main;

public sealed partial class DockFactory : Factory
{
    private readonly ActiveGraphProvider _activeGraph;
    private readonly MainViewModel _main;
    private readonly List<(IDockable Tool, string Key)> _localizedTools = [];

    public GraphDocumentViewModel InitialDocument { get; }

    public DocumentDock DocumentsDock { get; private set; } = null!;

    public DockFactory(ActiveGraphProvider activeGraph, GraphDocumentViewModel initialDocument, MainViewModel main)
    {
        _activeGraph = activeGraph;
        InitialDocument = initialDocument;
        _main = main;
        MessageLocalization.Observe(this, static factory => factory.RefreshLocalizedTitles());
    }

    public override IRootDock CreateLayout()
    {
        var graphDocument = InitialDocument;

        var outliner = new OutlinerViewModel(_activeGraph)
        {
            Id = "Outliner",
            Title = LocalizationService.Shared.Get("Messages.Tool.Outliner")
        };
        var nodeList = new NodeCatalogViewModel(_activeGraph, _main.GroupAssets)
        {
            Id = "NodeList",
            Title = LocalizationService.Shared.Get("Messages.Tool.NodeList")
        };
        var properties = new NodePropertiesPaneViewModel(_activeGraph, _main.Playback, _main.AssetBrowser, _main.ExecutionDebugger)
        {
            Id = "Properties",
            Title = LocalizationService.Shared.Get("Messages.Tool.Properties")
        };
        var engineConnection = new PreviewPaneViewModel(_main)
        {
            Id = "EngineConnection",
            Title = LocalizationService.Shared.Get("Messages.Tool.EngineConnection")
        };
        var history = new HistoryPaneViewModel(_activeGraph)
        {
            Id = "History",
            Title = LocalizationService.Shared.Get("Messages.Tool.History")
        };
        var timeline = new TimelineViewModel(_activeGraph, _main.Playback)
        {
            Id = "Timeline",
            Title = LocalizationService.Shared.Get("Messages.Tool.Timeline")
        };
        var curves = new CurveEditorViewModel(_activeGraph, _main.Playback)
        {
            Id = "Curves",
            Title = LocalizationService.Shared.Get("Messages.Tool.Curves")
        };
        _localizedTools.Clear();
        _localizedTools.AddRange([
            (outliner, "Messages.Tool.Outliner"),
            (nodeList, "Messages.Tool.NodeList"),
            (properties, "Messages.Tool.Properties"),
            (engineConnection, "Messages.Tool.EngineConnection"),
            (history, "Messages.Tool.History"),
            (timeline, "Messages.Tool.Timeline"),
            (curves, "Messages.Tool.Curves")]);
        for (var slot = 2; slot <= 4; slot++)
        {
            var id = $"Properties{slot}";
            var key = $"Messages.Tool.{id}";
            var inspector = new NodePropertiesPaneViewModel(_activeGraph, _main.Playback, _main.AssetBrowser, _main.ExecutionDebugger)
            {
                Id = id,
                Title = LocalizationService.Shared.Get(key)
            };
            _localizedTools.Add((inspector, key));
        }
        var spriteEditor = new SpriteEditorPaneViewModel(_main)
        {
            Id = "SpriteEditor",
            Title = LocalizationService.Shared.Get("Sprite.Title")
        };
        _localizedTools.Add((spriteEditor, "Sprite.Title"));
        AddWorkPane("Log", "Panels.LogTitle", () => new LogWindow { DataContext = _main });
        AddWorkPane("Mcp", "Panels.McpConnectionTitle", () => new McpWindow { DataContext = _main });
        AddWorkPane("PreviewProfiler", "Profiler.Title", () => new PreviewProfilerWindow { DataContext = _main });
        AddWorkPane("PreviewConnections", "Connections.Title", () => new PreviewConnectionsWindow { DataContext = _main });
        AddWorkPane("LocalProjects", "Library.Title", () => new LocalProjectsWindow(_main));
        AddWorkPane("Problems", "Messages.GraphProblemsTitle", () => new ProblemsWindow(_main));
        var resources = AddWorkPane("Resources", "Messages.Tool.Resources",
            () => new ProjectResourcesView { DataContext = _main.AssetBrowser.ViewModel.ProjectResources });
        AddWorkPane("Content", "Asset.Title", () => new EngineAssetsWindow(_main.AssetBrowser));
        AddWorkPane("Chat", "Chat.Title", () => new ChatWindow(_main), retainView: true);
        AddWorkPane("LiveGame", "LiveGame.Title", _main.CreateLiveGameView, retainView: true);
        AddWorkPane("RevisionHistory", "Vcs.Title", () => new RevisionHistoryWindow(_main));
        AddWorkPane("LayoutPresets", "Layouts.Title", () => new WindowLayoutsWindow(_main.WindowLayouts));

        var rightDock = new ProportionalDock
        {
            Proportion = 0.32,
            Orientation = Orientation.Vertical,
            VisibleDockables = CreateList<IDockable>(
                new ToolDock
                {
                    Proportion = 0.56,
                    Alignment = Alignment.Right,
                    ActiveDockable = properties,
                    VisibleDockables = CreateList<IDockable>(properties, outliner, nodeList)
                },
                new ProportionalDockSplitter(),
                new ToolDock
                {
                    Proportion = 0.44,
                    Alignment = Alignment.Right,
                    ActiveDockable = resources,
                    VisibleDockables = CreateList<IDockable>(resources, history, engineConnection)
                })
        };

        DocumentsDock = new DocumentDock
        {
            Id = "Documents",
            Proportion = 0.66,
            IsCollapsable = false,
            ActiveDockable = graphDocument,
            VisibleDockables = CreateList<IDockable>(graphDocument),
            CanCreateDocument = false,
        };

        var bottomTimelineAndCurves = new ToolDock
        {
            Proportion = 0.34,
            Alignment = Alignment.Bottom,
            ActiveDockable = curves,
            VisibleDockables = CreateList<IDockable>(curves, timeline)
        };

        var graphAndTimeline = new ProportionalDock
        {
            Proportion = 0.68,
            Orientation = Orientation.Vertical,
            ActiveDockable = DocumentsDock,
            VisibleDockables = CreateList<IDockable>(
                DocumentsDock,
                new ProportionalDockSplitter(),
                bottomTimelineAndCurves)
        };

        var mainLayout = new ProportionalDock
        {
            Orientation = Orientation.Horizontal,
            ActiveDockable = graphAndTimeline,
            VisibleDockables = CreateList<IDockable>(
                graphAndTimeline,
                new ProportionalDockSplitter(),
                rightDock)
        };

        var rootDock = CreateRootDock();
        rootDock.Id = "Root";
        rootDock.IsCollapsable = false;
        rootDock.ActiveDockable = mainLayout;
        rootDock.VisibleDockables = CreateList<IDockable>(mainLayout);
        RememberLayout(rootDock);
        _paneHomes[spriteEditor.Id] = _paneHomes[timeline.Id];
        foreach (var pane in Tools.OfType<EditorWorkPaneViewModel>())
        {
            _paneHomes.TryAdd(pane.Id, _paneHomes[timeline.Id]);
        }
        foreach (var inspector in _localizedTools.Select(entry => entry.Tool).OfType<NodePropertiesPaneViewModel>())
        {
            _paneHomes.TryAdd(inspector.Id, _paneHomes[properties.Id]);
        }
        return rootDock;
    }

    private void RefreshLocalizedTitles()
    {
        foreach (var (tool, key) in _localizedTools)
            tool.Title = LocalizationService.Shared.Get(key);
    }

    private EditorWorkPaneViewModel AddWorkPane(string id, string key, Func<Control> createView, bool retainView = false)
    {
        var pane = new EditorWorkPaneViewModel(createView, retainView)
        {
            Id = id,
            Title = LocalizationService.Shared.Get(key)
        };
        _localizedTools.Add((pane, key));
        return pane;
    }
}
