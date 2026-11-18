using System.Text.Json;
using LiberaFx.Models;

namespace LiberaFx.Services;

public sealed class WindowLayoutPresetStore
{
    public const int MaximumPresets = 32;
    private static readonly string[] RequiredPaneIds =
        ["Outliner", "NodeList", "Resources", "Properties", "EngineConnection", "History", "Timeline", "Curves"];
    public static readonly string[] PaneIds =
        [.. RequiredPaneIds, "Properties2", "Properties3", "Properties4"];
    private static readonly HashSet<string> AllowedPanes = new(PaneIds, StringComparer.Ordinal);
    private static readonly HashSet<string> Sides = new(["Left", "Right", "Top", "Bottom"], StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 48 };
    public string Path { get; }

    public WindowLayoutPresetStore(string? path = null) =>
        Path = path ?? System.IO.Path.Combine(AppDataDirectory.Root, "LiberaFx", "window-layouts.json");

    public WindowLayoutSettings Load()
    {
        if (!File.Exists(Path)) return new();
        if (new FileInfo(Path).Length > 1024 * 1024)
            throw new InvalidDataException("Window layout file exceeds 1 MiB.");
        var settings = JsonSerializer.Deserialize<WindowLayoutSettings>(File.ReadAllText(Path), JsonOptions)
            ?? throw new InvalidDataException("Window layout file is empty.");
        Validate(settings);
        return settings;
    }

    public void Save(WindowLayoutSettings settings)
    {
        Validate(settings);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 1024 * 1024)
            throw new InvalidDataException("Window layout file exceeds 1 MiB.");
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var staging = System.IO.Path.Combine(directory, $".window-layouts-{Guid.NewGuid():N}.pending");
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(staging, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    public static string ValidateName(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 ||
            name.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
            name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)) || name is "." or "..")
            throw new InvalidDataException("Use 1–64 characters without path separators; Default is reserved.");
        return name;
    }

    public static void Validate(WindowLayoutSettings settings)
    {
        if (settings.Version != 1) throw new InvalidDataException("Unsupported window layout version.");
        if (settings.Presets is null || settings.Presets.Count > MaximumPresets)
            throw new InvalidDataException("Too many window layout presets (maximum 32).");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in settings.Presets)
        {
            if (preset is null || ValidateName(preset.Name) != preset.Name || !names.Add(preset.Name))
                throw new InvalidDataException("Invalid or duplicate window layout name.");
            ValidateSnapshot(preset.Layout);
        }
        if (settings.Selected != "Default" && !settings.Presets.Any(p => p.Name == settings.Selected))
            throw new InvalidDataException("Selected window layout does not exist.");
        if (settings.Workspace is not null) ValidateSnapshot(settings.Workspace);
    }

    public static void ValidateSnapshot(WindowLayoutSnapshot snapshot)
    {
        if (snapshot is null || snapshot.Main is null || snapshot.Hidden is null ||
            snapshot.Pinned is null || snapshot.Windows is null || snapshot.Windows.Count > 16)
            throw new InvalidDataException("Invalid window layout structure.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var nodes = 0;
        var documentAnchors = 0;
        var hiddenHomes = new HashSet<string>(StringComparer.Ordinal);
        var floatingDocuments = 0;
        void Pane(string? id)
        {
            if (id is null || !AllowedPanes.Contains(id) || !seen.Add(id))
                throw new InvalidDataException($"Unknown or repeated pane ID: {id}.");
        }
        void Node(LayoutNode node, int depth, bool floating)
        {
            if (node is null || depth > 20 || ++nodes > 160 || node.Children is null || node.HiddenPanes is null ||
                node.Proportion is { } proportion && (!double.IsFinite(proportion) || proportion < 0 || proportion > 1))
                throw new InvalidDataException("Invalid dock topology or split ratio.");
            if (node.HiddenPanes.Count > 0 && node.Kind != "tool")
                throw new InvalidDataException("Hidden panes must reference a tool dock.");
            switch (node.Kind)
            {
                case "pane":
                    Pane(node.Pane);
                    if (node.Children.Count != 0) throw new InvalidDataException("Pane cannot contain docks.");
                    break;
                case "documents":
                    if ((floating ? ++floatingDocuments : ++documentAnchors) > 1 || node.Children.Count != 0)
                        throw new InvalidDataException("Invalid document area.");
                    break;
                case "splitter":
                    if (node.Children.Count != 0) throw new InvalidDataException("Invalid splitter.");
                    break;
                case "proportional":
                case "tool":
                    if (node.Kind == "tool" && node.Alignment is not ("Left" or "Right" or "Top" or "Bottom" or "Unset"))
                        throw new InvalidDataException("Invalid tool alignment.");
                    foreach (var child in node.Children)
                    {
                        if (node.Kind == "tool" && child.Kind != "pane")
                            throw new InvalidDataException("Tool dock contains a non-tool.");
                        Node(child, depth + 1, floating);
                    }
                    if (node.ActivePane is not null && !node.Children.Any(c => c.Kind == "pane" && c.Pane == node.ActivePane))
                        throw new InvalidDataException("Active pane is not in its dock.");
                    foreach (var id in node.HiddenPanes)
                        if (node.Kind != "tool" || !AllowedPanes.Contains(id) || !hiddenHomes.Add(id))
                            throw new InvalidDataException("Invalid or repeated hidden pane home.");
                    break;
                default:
                    throw new InvalidDataException("Unknown dock node kind.");
            }
        }
        void Pinned(Dictionary<string, List<string>> pinned)
        {
            if (pinned is null) throw new InvalidDataException("Missing pinned pane list.");
            foreach (var (side, ids) in pinned)
            {
                if (!Sides.Contains(side) || ids is null) throw new InvalidDataException("Invalid pinned pane side.");
                foreach (var id in ids) Pane(id);
            }
        }
        Node(snapshot.Main, 0, false);
        Pinned(snapshot.Pinned);
        foreach (var id in snapshot.Hidden) Pane(id);
        foreach (var window in snapshot.Windows)
        {
            if (window is null) throw new InvalidDataException("Invalid floating window.");
            floatingDocuments = 0;
            Node(window.Content, 0, true);
            if (window.ContainsDocuments != (floatingDocuments == 1))
                throw new InvalidDataException("Floating document area does not match its window.");
            Pinned(window.Pinned);
            ValidateBounds(window.Bounds);
        }
        if (snapshot.MainWindow is not null) ValidateBounds(snapshot.MainWindow);
        if (documentAnchors != 1 || !RequiredPaneIds.All(seen.Contains))
            throw new InvalidDataException("Layout must contain the document area and every required pane exactly once.");
        if (hiddenHomes.Any(id => !snapshot.Hidden.Contains(id)))
            throw new InvalidDataException("A visible pane has a hidden pane home.");
    }

    private static void ValidateBounds(LayoutWindowBounds bounds)
    {
        if (bounds is null || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
            !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) ||
            Math.Abs(bounds.X) > 1000000 || Math.Abs(bounds.Y) > 1000000 ||
            bounds.Width < 100 || bounds.Width > 50000 || bounds.Height < 100 || bounds.Height > 50000)
            throw new InvalidDataException("Invalid window bounds.");
    }
}
