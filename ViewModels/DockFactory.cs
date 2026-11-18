using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

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
        var resources = new ToolPaneViewModel
        {
            Id = "Resources",
            Title = LocalizationService.Shared.Get("Messages.Tool.Resources"),
            DescriptionKey = "Messages.ResourcesUnavailable"
        };
        var properties = new NodePropertiesPaneViewModel(_activeGraph, _main.Playback, _main.AssetBrowser)
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
        for (var slot = 2; slot <= 4; slot++)
        {
            var id = $"Properties{slot}";
            var key = $"Messages.Tool.{id}";
            var inspector = new NodePropertiesPaneViewModel(_activeGraph, _main.Playback, _main.AssetBrowser)
            {
                Id = id,
                Title = LocalizationService.Shared.Get(key)
            };
            _localizedTools.Add((inspector, key));
        }

        var leftDock = new ProportionalDock
        {
            Proportion = 0.19,
            Orientation = Orientation.Vertical,
            VisibleDockables = CreateList<IDockable>(
                new ToolDock
                {
                    Proportion = 0.35,
                    Alignment = Alignment.Left,
                    ActiveDockable = outliner,
                    VisibleDockables = CreateList<IDockable>(outliner)
                },
                new ProportionalDockSplitter(),
                new ToolDock
                {
                    Proportion = 0.65,
                    Alignment = Alignment.Left,
                    ActiveDockable = nodeList,
                    VisibleDockables = CreateList<IDockable>(nodeList, resources)
                })
        };

        var rightDock = new ProportionalDock
        {
            Proportion = 0.27,
            Orientation = Orientation.Vertical,
            VisibleDockables = CreateList<IDockable>(
                new ToolDock
                {
                    Proportion = 0.62,
                    Alignment = Alignment.Right,
                    ActiveDockable = properties,
                    VisibleDockables = CreateList<IDockable>(properties, history)
                },
                new ProportionalDockSplitter(),
                new ToolDock
                {
                    Proportion = 0.38,
                    Alignment = Alignment.Right,
                    ActiveDockable = engineConnection,
                    VisibleDockables = CreateList<IDockable>(engineConnection)
                })
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
            Proportion = 0.54,
            Orientation = Orientation.Vertical,
            ActiveDockable = DocumentsDock,
            VisibleDockables = CreateList<IDockable>(
                DocumentsDock,
                new ProportionalDockSplitter(),
                new ToolDock
                {
                    Proportion = 0.30,
                    Alignment = Alignment.Bottom,
                    ActiveDockable = timeline,
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
        RememberLayout(rootDock);
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
}
