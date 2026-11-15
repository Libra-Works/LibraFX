using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed class DockFactory : Factory
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
        var resources = new ToolPaneViewModel
        {
            Id = "Resources",
            Title = LocalizationService.Shared.Get("Messages.Tool.Resources"),
            DescriptionKey = "Messages.ResourcesUnavailable"
        };
        var properties = new NodePropertiesPaneViewModel(_activeGraph)
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
            (resources, "Messages.Tool.Resources"),
            (properties, "Messages.Tool.Properties"),
            (engineConnection, "Messages.Tool.EngineConnection"),
            (history, "Messages.Tool.History"),
            (timeline, "Messages.Tool.Timeline"),
            (curves, "Messages.Tool.Curves")]);

        var leftDock = new ProportionalDock
        {
            Proportion = 0.22,
            Orientation = Orientation.Vertical,
            VisibleDockables = CreateList<IDockable>(
                new ToolDock
                {
                    Alignment = Alignment.Left,
                    ActiveDockable = outliner,
                    VisibleDockables = CreateList<IDockable>(outliner)
                },
                new ProportionalDockSplitter(),
                new ToolDock
                {
                    Alignment = Alignment.Left,
                    ActiveDockable = nodeList,
                    VisibleDockables = CreateList<IDockable>(nodeList, resources)
                })
        };

        var rightDock = new ToolDock
        {
            Alignment = Alignment.Right,
            ActiveDockable = properties,
            VisibleDockables = CreateList<IDockable>(properties, history, engineConnection)
        };

        DocumentsDock = new DocumentDock
        {
            Id = "Documents",
            IsCollapsable = false,
            ActiveDockable = graphDocument,
            VisibleDockables = CreateList<IDockable>(graphDocument),
            CanCreateDocument = false,
        };

        var graphAndTimeline = new ProportionalDock
        {
            Orientation = Orientation.Vertical,
            ActiveDockable = DocumentsDock,
            VisibleDockables = CreateList<IDockable>(
                DocumentsDock,
                new ProportionalDockSplitter(),
                new ToolDock
                {
                    Proportion = 0.25,
                    Alignment = Alignment.Bottom,
                    ActiveDockable = curves,
                    VisibleDockables = CreateList<IDockable>(timeline, curves)
                })
        };

        var mainLayout = new ProportionalDock
        {
            Orientation = Orientation.Horizontal,
            ActiveDockable = graphAndTimeline,
            VisibleDockables = CreateList<IDockable>(
                leftDock,
                new ProportionalDockSplitter(),
                graphAndTimeline,
                new ProportionalDockSplitter(),
                rightDock)
        };

        var rootDock = CreateRootDock();
        rootDock.Id = "Root";
        rootDock.IsCollapsable = false;
        rootDock.ActiveDockable = mainLayout;
        rootDock.VisibleDockables = CreateList<IDockable>(mainLayout);
        return rootDock;
    }

    private void RefreshLocalizedTitles()
    {
        foreach (var (tool, key) in _localizedTools)
            tool.Title = LocalizationService.Shared.Get(key);
    }
}
