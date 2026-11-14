using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

namespace LiberaFx.ViewModels;

public sealed class DockFactory : Factory
{
    private readonly ActiveGraphProvider _activeGraph;
    private readonly MainViewModel _main;

    public GraphDocumentViewModel InitialDocument { get; }

    public DocumentDock DocumentsDock { get; private set; } = null!;

    public DockFactory(ActiveGraphProvider activeGraph, GraphDocumentViewModel initialDocument, MainViewModel main)
    {
        _activeGraph = activeGraph;
        InitialDocument = initialDocument;
        _main = main;
    }

    public override IRootDock CreateLayout()
    {
        var graphDocument = InitialDocument;

        var outliner = new OutlinerViewModel(_activeGraph)
        {
            Id = "Outliner",
            Title = "Outliner"
        };
        var nodeList = new NodeCatalogViewModel(_activeGraph)
        {
            Id = "NodeList",
            Title = "Node List"
        };
        var resources = new ToolPaneViewModel
        {
            Id = "Resources",
            Title = "Resources",
            Description = "Asset browsing and engine asset integration are not implemented yet."
        };
        var properties = new NodePropertiesPaneViewModel(_activeGraph)
        {
            Id = "Properties",
            Title = "Properties"
        };
        var engineConnection = new PreviewPaneViewModel(_main)
        {
            Id = "EngineConnection",
            Title = "Engine Connection"
        };
        var history = new HistoryPaneViewModel(_activeGraph)
        {
            Id = "History",
            Title = "History"
        };
        var timeline = new ToolPaneViewModel
        {
            Id = "Timeline",
            Title = "Timeline",
            Description = "Timeline editing is not implemented yet. Timeline data will be represented by the underlying graph."
        };
        var curves = new CurveEditorViewModel(_activeGraph)
        {
            Id = "Curves",
            Title = "F-Curve"
        };

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
}
