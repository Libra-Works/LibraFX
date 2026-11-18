using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Dock.Avalonia.Controls;
using LiberaFx.Models;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed partial class DockFactory
{
    private IRootDock _layoutRoot = null!;
    private WindowLayoutSnapshot _defaultLayout = null!;
    private readonly Dictionary<IDockable, (IDock Parent, int Index)> _homeOwners = new();
    private readonly Dictionary<string, IDock> _paneHomes = new(StringComparer.Ordinal);
    private readonly Dictionary<IDockable, IDock> _hiddenHomes = new();
    private readonly Dictionary<IDockable, double> _homeProportions = new();
    private readonly Dictionary<IDockWindow, IHostWindow> _preservedHosts = new();
    private readonly HashSet<string> _openedTools = new(StringComparer.Ordinal);

    public IReadOnlyList<IDockable> Tools => _localizedTools.Select(t => t.Tool).ToArray();
    public WindowLayoutSnapshot DefaultLayout => _defaultLayout;
    public void EnableFloatingWindows() => DefaultHostWindowLocator ??= static () => new HostWindow();

    public override void InitDockWindow(IDockWindow window, IDockable? owner)
    {
        if (_preservedHosts.TryGetValue(window, out var host)) base.InitDockWindow(window, owner, host);
        else base.InitDockWindow(window, owner);
    }

    private void RememberLayout(IRootDock root)
    {
        _layoutRoot = root;
        RememberOwners(root);
        _defaultLayout = CaptureLayout();
    }

    private void RememberOwners(IDock dock)
    {
        if (dock.VisibleDockables is null) return;
        for (var index = 0; index < dock.VisibleDockables.Count; index++)
        {
            var child = dock.VisibleDockables[index];
            _homeOwners[child] = (dock, index);
            if (double.IsFinite(child.Proportion) && child.Proportion > 0) _homeProportions[child] = child.Proportion;
            if (Tools.Contains(child)) _paneHomes[child.Id] = dock;
            if (child is IDock nested) RememberOwners(nested);
        }
    }

    public override void CloseDockable(IDockable? dockable)
    {
        if (dockable is null || !Tools.Contains(dockable))
        {
            base.CloseDockable(dockable);
            return;
        }
        HideDockable(dockable);
    }

    public override void HideDockable(IDockable dockable)
    {
        if (Tools.Contains(dockable))
        {
            foreach (var root in Roots()) RememberOwners(root);
            if ((dockable.Owner is IToolDock ? dockable.Owner : dockable.OriginalOwner ?? dockable.Owner) is IDock owner)
                _hiddenHomes[dockable] = owner;
        }
        base.HideDockable(dockable);
        if (dockable is EditorWorkPaneViewModel pane)
        {
            pane.OnHidden();
        }
    }

    public bool ShowTool(string id)
    {
        var tool = Tools.FirstOrDefault(t => t.Id == id);
        if (tool is null) return false;
        var firstOpen = _openedTools.Add(id) && FindRoot(tool) is null && !_hiddenHomes.ContainsKey(tool);
        var roots = Roots().ToArray();
        var pinnedRoot = roots.FirstOrDefault(r => PinnedLists(r).Any(p => p.Items?.Contains(tool) == true));
        if (pinnedRoot is not null)
        {
            PreviewPinnedDockable(tool);
            SetActiveDockable(tool);
            SetFocusedDockable(pinnedRoot, tool);
            pinnedRoot.Window?.Host?.SetActive();
            return true;
        }
        var hiddenRoot = roots.FirstOrDefault(r => r.HiddenDockables?.Contains(tool) == true);
        if (hiddenRoot is not null)
        {
            RestoreDockable(tool);
            hiddenRoot.HiddenDockables?.Remove(tool);
        }
        if (tool.Owner is not IDock owner || owner.VisibleDockables?.Contains(tool) != true ||
            !IsReachable(owner))
        {
            owner = _hiddenHomes.GetValueOrDefault(tool) ?? _paneHomes.GetValueOrDefault(id) ??
                new ToolDock { Alignment = Alignment.Left, VisibleDockables = CreateList<IDockable>() };
            // Native collapse can detach the home dock as well as its ancestors.
            if (!IsReachable(owner)) EnsureHomeAttached(owner);
            DetachTool(tool);
            owner.VisibleDockables ??= CreateList<IDockable>();
            if (!owner.VisibleDockables.Contains(tool)) owner.VisibleDockables.Add(tool);
            InitDockable(tool, owner);
        }
        owner.ActiveDockable = tool;
        for (IDockable? current = owner; current is not null; current = current.Owner)
        {
            current.IsEmpty = false;
            if (current.Proportion == 0)
                current.Proportion = _homeProportions.GetValueOrDefault(current,
                    double.IsFinite(current.CollapsedProportion) && current.CollapsedProportion > 0
                        ? current.CollapsedProportion : 0.22);
        }
        SetActiveDockable(tool);
        SetFocusedDockable(_layoutRoot, tool);
        if (firstOpen && DefaultHostWindowLocator is not null)
        {
            FloatSpaciousTool(tool);
        }
        FindRoot(tool)?.Window?.Host?.SetActive();
        return true;
    }

    private void FloatSpaciousTool(IDockable tool)
    {
        var (width, height) = tool.Id switch
        {
            "SpriteEditor" => (1280, 800),
            "Content" or "PreviewProfiler" or "RevisionHistory" => (1160, 740),
            "Chat" or "LiveGame" => (840, 720),
            _ => (0, 0)
        };
        if (width == 0 || _main.WindowLayouts.CaptureMainWindow?.Invoke() is not { } mainBounds)
        {
            return;
        }

        var bounds = new LayoutWindowBounds
        {
            Width = width, Height = height, X = mainBounds.X + 32, Y = mainBounds.Y + 48
        };
        bounds = _main.WindowLayouts.ClampFloatingWindow?.Invoke(bounds) ?? bounds;
        FloatDockable(tool, new DockWindowOptions { ShowInTaskbar = false });
        if (FindRoot(tool)?.Window is not { } window)
        {
            return;
        }
        window.X = bounds.X;
        window.Y = bounds.Y;
        window.Width = bounds.Width;
        window.Height = bounds.Height;
        window.Host?.SetPosition(bounds.X, bounds.Y);
        window.Host?.SetSize(bounds.Width, bounds.Height);
    }

    public override bool OnWindowClosing(IDockWindow? window)
    {
        if (!base.OnWindowClosing(window))
        {
            return false;
        }
        if (window is null)
        {
            return true;
        }

        foreach (var pane in Tools.OfType<EditorWorkPaneViewModel>().Where(pane =>
                     ReferenceEquals(FindRoot(pane)?.Window, window)))
        {
            pane.OnHidden();
        }
        return true;
    }

    private void EnsureHomeAttached(IDock dock)
    {
        if (IsReachable(dock)) return;
        if (_homeOwners.TryGetValue(dock, out var home))
        {
            EnsureHomeAttached(home.Parent);
            home.Parent.VisibleDockables ??= CreateList<IDockable>();
            if (!home.Parent.VisibleDockables.Contains(dock))
            {
                var index = Math.Min(home.Index, home.Parent.VisibleDockables.Count);
                home.Parent.VisibleDockables.Insert(index, dock);
                if (home.Parent is IProportionalDock)
                {
                    var items = home.Parent.VisibleDockables.Where(item => item is not IProportionalDockSplitter).ToArray();
                    home.Parent.VisibleDockables = CreateList<IDockable>(items.SelectMany((item, i) =>
                        i == 0 ? new[] { item } : new IDockable[] { new ProportionalDockSplitter(), item }).ToArray());
                    foreach (var splitter in home.Parent.VisibleDockables.OfType<IProportionalDockSplitter>())
                        InitDockable(splitter, home.Parent);
                }
            }
            home.Parent.ActiveDockable ??= dock;
            InitDockable(dock, home.Parent);
        }
        else
        {
            var existing = _layoutRoot.VisibleDockables?.FirstOrDefault();
            var container = new ProportionalDock
            {
                Orientation = Orientation.Horizontal,
                VisibleDockables = existing is null
                    ? CreateList<IDockable>(dock)
                    : CreateList<IDockable>(dock, new ProportionalDockSplitter(), existing),
                ActiveDockable = existing ?? dock
            };
            _layoutRoot.VisibleDockables = CreateList<IDockable>(container);
            _layoutRoot.ActiveDockable = container;
            InitDockable(container, _layoutRoot);
        }
    }

    private bool IsReachable(IDockable dockable) =>
        Roots().Any(r => ReferenceEquals(r, dockable) || Traverse(r).Contains(dockable));

    private IEnumerable<IRootDock> Roots()
    {
        yield return _layoutRoot;
        foreach (var window in _layoutRoot.Windows?.ToArray() ?? [])
            if (window.Layout is not null) yield return window.Layout;
    }

    private static IEnumerable<IDockable> Traverse(IDock dock)
    {
        foreach (var child in dock.VisibleDockables?.ToArray() ?? [])
        {
            yield return child;
            if (child is IDock nested)
                foreach (var descendant in Traverse(nested)) yield return descendant;
        }
    }

    internal IRootDock? FindRoot(IDockable tool) =>
        Roots().FirstOrDefault(r => Traverse(r).Contains(tool));

    private static IEnumerable<(string Side, IList<IDockable>? Items)> PinnedLists(IRootDock root)
    {
        yield return ("Left", root.LeftPinnedDockables);
        yield return ("Right", root.RightPinnedDockables);
        yield return ("Top", root.TopPinnedDockables);
        yield return ("Bottom", root.BottomPinnedDockables);
    }

    public WindowLayoutSnapshot CaptureLayout()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var documents = false;
        var hidden = Roots().SelectMany(r => r.HiddenDockables ?? []).Where(Tools.Contains).ToHashSet();
        bool HasHiddenHome(IDockable ancestor)
        {
            foreach (var (tool, home) in _hiddenHomes)
            {
                if (!hidden.Contains(tool)) continue;
                IDockable? current = home;
                var visited = new HashSet<IDockable>();
                while (current is not null && visited.Add(current))
                {
                    if (ReferenceEquals(current, ancestor)) return true;
                    current = _homeOwners.TryGetValue(current, out var parent) ? parent.Parent : null;
                }
            }
            return false;
        }
        LayoutNode? Capture(IDockable item, bool floating = false)
        {
            if (Tools.Contains(item))
            {
                if (!seen.Add(item.Id)) return null;
                return new() { Kind = "pane", Pane = item.Id };
            }
            if (item is IDocumentDock)
            {
                if (!floating && documents) return null;
                if (!floating) documents = true;
                return new() { Kind = "documents", Proportion = SavedRatio(item) };
            }
            if (item is IProportionalDockSplitter) return new() { Kind = "splitter" };
            if (item is not IDock dock) return null;
            var source = (dock.VisibleDockables ?? []).ToList();
            foreach (var (child, home) in _homeOwners.Where(pair => ReferenceEquals(pair.Value.Parent, dock))
                         .OrderBy(pair => pair.Value.Index))
                if (!source.Contains(child) && child is IDock && HasHiddenHome(child))
                    source.Insert(Math.Min(home.Index, source.Count), child);
            var children = source.Select(c => Capture(c, floating))
                .OfType<LayoutNode>().ToList();
            CleanSplitters(children);
            var hiddenPanes = _hiddenHomes.Where(pair => hidden.Contains(pair.Key) && ReferenceEquals(pair.Value, dock))
                .Select(pair => pair.Key.Id).ToList();
            if (children.Count == 0 && hiddenPanes.Count == 0) return null;
            if (item is IRootDock && children.Count == 1) return children[0];
            return new()
            {
                Kind = item is IToolDock ? "tool" : "proportional",
                Proportion = SavedRatio(item),
                Vertical = item is IProportionalDock proportional && proportional.Orientation == Orientation.Vertical,
                Alignment = item is IToolDock tool ? tool.Alignment.ToString() : "Unset",
                ActivePane = children.Any(c => c.Pane == dock.ActiveDockable?.Id) ? dock.ActiveDockable?.Id : null,
                HiddenPanes = hiddenPanes,
                Children = children
            };
        }
        Dictionary<string, List<string>> Pinned(IRootDock root)
        {
            var result = new Dictionary<string, List<string>>();
            foreach (var (side, items) in PinnedLists(root))
            {
                var ids = (items ?? []).Where(Tools.Contains).Select(t => t.Id).Where(seen.Add).ToList();
                if (ids.Count > 0) result.Add(side, ids);
            }
            return result;
        }
        var snapshot = new WindowLayoutSnapshot();
        snapshot.Main = Capture(_layoutRoot) ?? new() { Kind = "proportional" };
        snapshot.Pinned = Pinned(_layoutRoot);
        foreach (var window in _layoutRoot.Windows ?? [])
        {
            if (window.Layout is null) continue;
            var documentSeen = false;
            LayoutNode CollapseDocuments(LayoutNode node)
            {
                if (node.Kind == "documents")
                {
                    if (documentSeen) return new() { Kind = "splitter" };
                    documentSeen = true;
                }
                node.Children = node.Children.Select(CollapseDocuments).ToList();
                CleanSplitters(node.Children);
                return node;
            }
            var content = Capture(window.Layout, floating: true);
            if (content is not null) content = CollapseDocuments(content);
            var pinned = Pinned(window.Layout);
            if (content is null && pinned.Count == 0) continue;
            var bounds = new LayoutWindowBounds
            {
                X = double.IsFinite(window.X) ? window.X : 0,
                Y = double.IsFinite(window.Y) ? window.Y : 0,
                Width = double.IsFinite(window.Width) && window.Width >= 100 ? window.Width : 600,
                Height = double.IsFinite(window.Height) && window.Height >= 100 ? window.Height : 500,
                Maximized = window.WindowState == DockWindowState.Maximized
            };
            if (window.Host is { } host)
            {
                host.GetPosition(out var x, out var y);
                host.GetSize(out var width, out var height);
                if (double.IsFinite(x) && double.IsFinite(y)) { bounds.X = x; bounds.Y = y; }
                if (width >= 100 && height >= 100) { bounds.Width = width; bounds.Height = height; }
                bounds.Maximized = host.GetWindowState() == DockWindowState.Maximized;
            }
            snapshot.Windows.Add(new()
            {
                Content = content ?? new() { Kind = "tool" }, Pinned = pinned, Bounds = bounds,
                ContainsDocuments = documentSeen
            });
        }
        if (!documents)
        {
            snapshot.Main = snapshot.Main.Children.Count == 0 ? new() { Kind = "documents" } : new()
                {
                    Kind = "proportional",
                    Children = [snapshot.Main, new() { Kind = "splitter" }, new() { Kind = "documents" }]
                };
        }
        snapshot.Hidden = Tools.Where(t => seen.Add(t.Id)).Select(t => t.Id).ToList();
        WindowLayoutPresetStore.ValidateSnapshot(snapshot);
        return snapshot;
    }

    private static double? Ratio(double ratio) => double.IsFinite(ratio) && ratio >= 0 && ratio <= 1 ? ratio : null;
    private double? SavedRatio(IDockable item) => Ratio(item.Proportion == 0
        ? _homeProportions.GetValueOrDefault(item, item.CollapsedProportion) : item.Proportion);

    private static void CleanSplitters(List<LayoutNode> children)
    {
        for (var i = children.Count - 1; i >= 0; i--)
            if (children[i].Kind == "splitter" && (i == 0 || i == children.Count - 1 || children[i - 1].Kind == "splitter"))
                children.RemoveAt(i);
    }

    private void DetachTool(IDockable tool)
    {
        foreach (var root in Roots().ToArray())
        {
            foreach (var (_, list) in PinnedLists(root)) list?.Remove(tool);
            root.HiddenDockables?.Remove(tool);
            foreach (var dock in Traverse(root).OfType<IDock>().Prepend(root).ToArray())
            {
                if (dock.VisibleDockables?.Remove(tool) == true && ReferenceEquals(dock.ActiveDockable, tool))
                    dock.ActiveDockable = dock.VisibleDockables.FirstOrDefault();
            }
        }
        if (tool.Owner is IDock oldOwner) oldOwner.VisibleDockables?.Remove(tool);
    }

    public void ApplyLayout(WindowLayoutSnapshot snapshot,
        Func<LayoutWindowBounds, LayoutWindowBounds>? clamp = null, bool showWindows = true)
    {
        WindowLayoutPresetStore.ValidateSnapshot(snapshot);
        var activeDocument = _main.ActiveDocument;
        var dockActives = Roots().SelectMany(r => Traverse(r).OfType<IDocumentDock>())
            .ToDictionary(d => d, d => d.ActiveDockable);
        // Extract the current native document island, retaining every split and tab VM.
        // Document floating windows stay alive; presets never own their lifetime.
        IDockable? DocumentIsland(IDockable item)
        {
            if (item is IDocumentDock) return item;
            if (item is not IDock dock) return null;
            var children = (dock.VisibleDockables ?? []).Where(c => c is not IProportionalDockSplitter)
                .Select(DocumentIsland).OfType<IDockable>().ToArray();
            if (children.Length == 0) return null;
            if (children.Length == 1) return children[0];
            var arranged = new List<IDockable>();
            foreach (var child in children)
            {
                if (arranged.Count > 0) arranged.Add(new ProportionalDockSplitter());
                arranged.Add(child);
            }
            dock.VisibleDockables = CreateList<IDockable>(arranged.ToArray());
            return dock;
        }
        var mainIslands = (_layoutRoot.VisibleDockables ?? []).Select(DocumentIsland).OfType<IDockable>().ToArray();
        var mainIsland = mainIslands.Length switch
        {
            0 => null,
            1 => mainIslands[0],
            _ => new ProportionalDock
            {
                Orientation = Orientation.Horizontal,
                VisibleDockables = CreateList<IDockable>(mainIslands.SelectMany((island, index) =>
                    index == 0 ? new[] { island } : new IDockable[] { new ProportionalDockSplitter(), island }).ToArray())
            }
        };
        var retainedWindows = (_layoutRoot.Windows ?? []).Where(w => w.Layout is not null &&
            Traverse(w.Layout).Any(d => d is IDocumentDock)).ToArray();
        var floatingIslands = retainedWindows.ToDictionary(w => w, w =>
            (w.Layout!.VisibleDockables ?? []).Select(DocumentIsland).OfType<IDockable>().FirstOrDefault());
        foreach (var tool in Tools) DetachTool(tool);
        foreach (var window in (_layoutRoot.Windows ?? []).Except(retainedWindows).ToArray())
        {
            // Clear contents first so host shutdown cannot close any pane VM.
            if (window.Layout is { } layout)
            {
                layout.VisibleDockables?.Clear();
                layout.HiddenDockables?.Clear();
            }
            window.Host?.Exit();
            _layoutRoot.Windows?.Remove(window);
        }
        _homeOwners.Clear();
        _paneHomes.Clear();
        _hiddenHomes.Clear();
        _homeProportions.Clear();
        IDockable Build(LayoutNode node, IDockable? documentIsland)
        {
            IDockable result;
            switch (node.Kind)
            {
                case "pane":
                    result = Tools.Single(t => t.Id == node.Pane);
                    break;
                case "documents":
                    result = documentIsland ?? new DocumentDock
                    {
                        IsCollapsable = false, CanCreateDocument = false, VisibleDockables = CreateList<IDockable>()
                    };
                    break;
                case "splitter":
                    result = new ProportionalDockSplitter();
                    break;
                default:
                    var dock = node.Kind == "tool"
                        ? (IDock)new ToolDock { Alignment = Enum.Parse<Alignment>(node.Alignment) }
                        : new ProportionalDock { Orientation = node.Vertical ? Orientation.Vertical : Orientation.Horizontal };
                    var children = node.Children.Select(child => Build(child, documentIsland)).ToArray();
                    for (var i = 0; i < children.Length; i++) _homeOwners[children[i]] = (dock, i);
                    var visible = children.Where(child => child is not IDock childDock ||
                        child is IDocumentDock || childDock.VisibleDockables?.Count > 0).ToList();
                    for (var i = visible.Count - 1; i >= 0; i--)
                        if (visible[i] is IProportionalDockSplitter && (i == 0 || i == visible.Count - 1 ||
                            visible[i - 1] is IProportionalDockSplitter)) visible.RemoveAt(i);
                    dock.VisibleDockables = CreateList<IDockable>(visible.ToArray());
                    foreach (var id in node.HiddenPanes)
                    {
                        _paneHomes[id] = dock;
                        _hiddenHomes[Tools.Single(t => t.Id == id)] = dock;
                    }
                    dock.ActiveDockable = dock.VisibleDockables.FirstOrDefault(t => t.Id == node.ActivePane) ??
                        dock.VisibleDockables.FirstOrDefault(t => t is not IProportionalDockSplitter);
                    result = dock;
                    break;
            }
            if (node.Proportion is { } proportion)
            {
                result.Proportion = proportion;
                if (proportion > 0) _homeProportions[result] = proportion;
            }
            return result;
        }
        void SetPinned(IRootDock root, Dictionary<string, List<string>> pinned)
        {
            IList<IDockable> List(string side) => CreateList<IDockable>(
                pinned.GetValueOrDefault(side, []).Select(id => Tools.Single(t => t.Id == id)).ToArray());
            root.LeftPinnedDockables = List("Left");
            root.RightPinnedDockables = List("Right");
            root.TopPinnedDockables = List("Top");
            root.BottomPinnedDockables = List("Bottom");
        }
        var content = Build(snapshot.Main, mainIsland);
        _layoutRoot.VisibleDockables = CreateList<IDockable>(content);
        _layoutRoot.ActiveDockable = content;
        _layoutRoot.HiddenDockables = CreateList<IDockable>(
            snapshot.Hidden.Select(id => Tools.Single(t => t.Id == id)).ToArray());
        SetPinned(_layoutRoot, snapshot.Pinned);
        var documentWindowIndex = 0;
        foreach (var floating in snapshot.Windows)
        {
            var existingDocumentWindow = floating.ContainsDocuments && documentWindowIndex < retainedWindows.Length
                ? retainedWindows[documentWindowIndex++] : null;
            LayoutNode? WithoutDocuments(LayoutNode node)
            {
                if (node.Kind == "documents") return null;
                var children = node.Children.Select(WithoutDocuments).OfType<LayoutNode>().ToList();
                CleanSplitters(children);
                if (node.Kind is "proportional" or "tool" && children.Count == 0 && node.HiddenPanes.Count == 0) return null;
                return new()
                {
                    Kind = node.Kind, Pane = node.Pane, ActivePane = node.ActivePane, Proportion = node.Proportion,
                    Vertical = node.Vertical, Alignment = node.Alignment, HiddenPanes = node.HiddenPanes, Children = children
                };
            }
            var floatingNode = floating.ContainsDocuments && existingDocumentWindow is null
                ? WithoutDocuments(floating.Content) : floating.Content;
            if (floatingNode is null && floating.Pinned.Count == 0) continue;
            var root = existingDocumentWindow?.Layout ?? CreateRootDock();
            root.IsCollapsable = false;
            var floatingContent = Build(floatingNode ?? new() { Kind = "tool" },
                existingDocumentWindow is null ? null : floatingIslands[existingDocumentWindow]);
            root.VisibleDockables = CreateList<IDockable>(floatingContent);
            root.ActiveDockable = floatingContent;
            SetPinned(root, floating.Pinned);
            var bounds = clamp?.Invoke(floating.Bounds) ?? floating.Bounds;
            var window = existingDocumentWindow ?? CreateDockWindow();
            if (existingDocumentWindow is null) window.Id = $"LayoutWindow-{Guid.NewGuid():N}";
            window.Layout = root;
            window.X = bounds.X; window.Y = bounds.Y;
            window.Width = bounds.Width; window.Height = bounds.Height;
            window.WindowState = bounds.Maximized ? DockWindowState.Maximized : DockWindowState.Normal;
            root.Window = window;
            if (existingDocumentWindow is null) AddWindow(_layoutRoot, window);
        }
        foreach (var window in retainedWindows)
            if (window.Host is { } host) _preservedHosts[window] = host;
        try { InitLayout(_layoutRoot); }
        finally { _preservedHosts.Clear(); }
        RememberOwners(_layoutRoot);
        foreach (var root in Roots().Skip(1)) RememberOwners(root);
        // Hidden and pinned panes need a usable home even after the native tree changed.
        var fallback = Traverse(_layoutRoot).OfType<IToolDock>().FirstOrDefault();
        if (fallback is null)
        {
            fallback = new ToolDock { Alignment = Alignment.Left, VisibleDockables = CreateList<IDockable>() };
            _homeOwners[fallback] = (_layoutRoot, 0);
        }
        foreach (var tool in Tools)
        {
            var home = tool switch
            {
                NodePropertiesPaneViewModel => _paneHomes.GetValueOrDefault("Properties"),
                SpriteEditorPaneViewModel or EditorWorkPaneViewModel => _paneHomes.GetValueOrDefault("Timeline"),
                _ => null
            };
            _paneHomes.TryAdd(tool.Id, home ?? fallback);
            if (_layoutRoot.HiddenDockables?.Contains(tool) == true) tool.OriginalOwner = _paneHomes[tool.Id];
        }
        foreach (var (dock, active) in dockActives)
            if (active is not null && dock.VisibleDockables?.Contains(active) == true) dock.ActiveDockable = active;
        if (activeDocument is not null) _main.RestoreLayoutActiveDocument(activeDocument);
        if (showWindows) _layoutRoot.ShowWindows?.Execute(null);
    }
}
