namespace LiberaFx.Models;

// Only dock topology and window geometry belong here, never graph/document data.
public sealed class WindowLayoutSnapshot
{
    public LayoutNode Main { get; set; } = new() { Kind = "documents" };
    public List<string> Hidden { get; set; } = [];
    public Dictionary<string, List<string>> Pinned { get; set; } = [];
    public List<LayoutFloatingWindow> Windows { get; set; } = [];
    public LayoutWindowBounds? MainWindow { get; set; }
}

public sealed class LayoutNode
{
    public string Kind { get; set; } = "";
    public string? Pane { get; set; }
    public string? ActivePane { get; set; }
    public double? Proportion { get; set; }
    public bool Vertical { get; set; }
    public string Alignment { get; set; } = "Unset";
    public List<string> HiddenPanes { get; set; } = [];
    public List<LayoutNode> Children { get; set; } = [];
}

public sealed class LayoutFloatingWindow
{
    public LayoutNode Content { get; set; } = new();
    public LayoutWindowBounds Bounds { get; set; } = new();
    public Dictionary<string, List<string>> Pinned { get; set; } = [];
    public bool ContainsDocuments { get; set; }
}

public sealed class LayoutWindowBounds
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 1100;
    public double Height { get; set; } = 750;
    public bool Maximized { get; set; }
}

public sealed class WindowLayoutPreset
{
    public string Name { get; set; } = "";
    public WindowLayoutSnapshot Layout { get; set; } = new();
    public override string ToString() => Name;
}

public sealed class WindowLayoutSettings
{
    public int Version { get; set; } = 1;
    public string Selected { get; set; } = "Default";
    public List<WindowLayoutPreset> Presets { get; set; } = [];
    public WindowLayoutSnapshot? Workspace { get; set; }
}
