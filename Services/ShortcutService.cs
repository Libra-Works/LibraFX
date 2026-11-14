using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LiberaFx.Services;

public sealed class ShortcutEntry : ObservableObject
{
    internal ShortcutEntry(string id, string label, string scope, Key key, KeyModifiers modifiers)
    {
        Id = id;
        Label = label;
        Scope = scope;
        Key = key;
        Modifiers = modifiers;
        DefaultKey = key;
        DefaultModifiers = modifiers;
    }

    public string Id { get; }
    public string Label { get; }
    public string Scope { get; }
    public string Gesture => new KeyGesture(Key, Modifiers).ToString();
    internal Key Key { get; private set; }
    internal KeyModifiers Modifiers { get; private set; }
    internal Key DefaultKey { get; }
    internal KeyModifiers DefaultModifiers { get; }
    internal void Set(Key key, KeyModifiers modifiers)
    {
        Key = key;
        Modifiers = modifiers;
        OnPropertyChanged(nameof(Gesture));
    }
}

public sealed class ShortcutService : ObservableObject
{
    private readonly string _path;
    private string _curvePanButton = "Middle";
    private string _curveZoomModifier = "None";
    private string _status = "Click a shortcut and press a key combination.";

    public static ShortcutService Shared { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiberaFx", "shortcuts.json"));

    public ObservableCollection<ShortcutEntry> Entries { get; } =
    [
        new("undo", "Undo", "Global", Key.Z, KeyModifiers.Control),
        new("redo", "Redo", "Global", Key.Y, KeyModifiers.Control),
        new("graph.copy", "Copy nodes", "Graph", Key.C, KeyModifiers.Control),
        new("graph.cut", "Cut nodes", "Graph", Key.X, KeyModifiers.Control),
        new("graph.paste", "Paste nodes", "Graph", Key.V, KeyModifiers.Control),
        new("graph.all", "Select all nodes", "Graph", Key.A, KeyModifiers.Control),
        new("graph.delete", "Delete nodes", "Graph", Key.Delete, KeyModifiers.None),
        new("graph.search", "Search and add node", "Graph", Key.Space, KeyModifiers.None),
        new("graph.frame", "Frame selected nodes", "Graph", Key.F, KeyModifiers.None),
        new("graph.group", "Group selected nodes", "Graph", Key.G, KeyModifiers.Control),
        new("graph.zoomIn", "Zoom in", "Graph", Key.OemPlus, KeyModifiers.None),
        new("graph.zoomOut", "Zoom out", "Graph", Key.OemMinus, KeyModifiers.None),
        new("curve.delete", "Delete key", "F-Curve", Key.Delete, KeyModifiers.None),
        new("curve.frame", "Frame all keys", "F-Curve", Key.Home, KeyModifiers.None),
        new("curve.add", "Add key", "F-Curve", Key.Insert, KeyModifiers.None)
    ];

    public IReadOnlyList<string> PanOptions { get; } = ["Middle", "Right"];
    public IReadOnlyList<string> ZoomOptions { get; } = ["None", "Control"];
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string CurvePanButton
    {
        get => _curvePanButton;
        set => SetMouseOption(ref _curvePanButton, value, PanOptions, nameof(CurvePanButton));
    }
    public string CurveZoomModifier
    {
        get => _curveZoomModifier;
        set => SetMouseOption(ref _curveZoomModifier, value, ZoomOptions, nameof(CurveZoomModifier));
    }

    public ShortcutService(string path)
    {
        _path = path;
        Load();
    }

    public bool Matches(string id, KeyEventArgs e)
    {
        var entry = Entries.First(item => item.Id == id);
        return e.Key == entry.Key && (e.KeyModifiers & ModifierMask) == entry.Modifiers;
    }

    public void Assign(string id, Key key, KeyModifiers modifiers)
    {
        var entry = Entries.First(item => item.Id == id);
        modifiers &= ModifierMask;
        if (!Enum.IsDefined(key) || key == Key.None || key == Key.Escape || key == Key.Tab || IsModifier(key))
        {
            Status = "Choose a non-modifier key other than Tab or Escape.";
            return;
        }

        var conflict = Entries.FirstOrDefault(other => other != entry &&
            (entry.Scope == other.Scope || entry.Scope == "Global" || other.Scope == "Global") &&
            other.Key == key && other.Modifiers == modifiers);
        if (conflict is not null)
        {
            Status = $"Already assigned to {conflict.Label} ({conflict.Scope}).";
            return;
        }

        var oldKey = entry.Key;
        var oldModifiers = entry.Modifiers;
        entry.Set(key, modifiers);
        if (!TrySave())
        {
            entry.Set(oldKey, oldModifiers);
            return;
        }
        Status = $"{entry.Label}: {entry.Gesture}";
    }

    public void Reset()
    {
        var old = Entries.Select(entry => (entry.Key, entry.Modifiers)).ToArray();
        var oldPan = _curvePanButton;
        var oldZoom = _curveZoomModifier;
        foreach (var entry in Entries) entry.Set(entry.DefaultKey, entry.DefaultModifiers);
        _curvePanButton = "Middle";
        _curveZoomModifier = "None";
        if (!TrySave())
        {
            for (var i = 0; i < Entries.Count; i++) Entries[i].Set(old[i].Key, old[i].Modifiers);
            _curvePanButton = oldPan;
            _curveZoomModifier = oldZoom;
        }
        else Status = "Default shortcuts restored.";
        OnPropertyChanged(nameof(CurvePanButton));
        OnPropertyChanged(nameof(CurveZoomModifier));
    }

    private void SetMouseOption(ref string field, string value, IReadOnlyList<string> options, string property)
    {
        if (!options.Contains(value) || field == value) return;
        var old = field;
        field = value;
        if (!TrySave()) field = old;
        else Status = "Mouse shortcut saved.";
        OnPropertyChanged(property);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var file = JsonSerializer.Deserialize<ShortcutFile>(File.ReadAllText(_path));
            if (file?.Bindings is null) return;
            var desired = Entries.Select(entry => (entry.Key, entry.Modifiers)).ToArray();
            var loaded = new bool[Entries.Count];
            for (var i = 0; i < Entries.Count; i++)
            {
                var entry = Entries[i];
                if (!file.Bindings.TryGetValue(entry.Id, out var setting) || setting is null ||
                    !Enum.TryParse<Key>(setting.Key, out var key) || !Enum.IsDefined(key) || key == Key.None ||
                    key == Key.Escape || key == Key.Tab || IsModifier(key) ||
                    (setting.Modifiers & ~ModifierMask) != 0) continue;
                desired[i] = (key, setting.Modifiers);
                loaded[i] = true;
            }
            var conflict = Enumerable.Range(0, Entries.Count).Any(i =>
                Enumerable.Range(i + 1, Entries.Count - i - 1).Any(j =>
                    loaded[i] && loaded[j] && ScopesConflict(Entries[i], Entries[j]) &&
                    desired[i] == desired[j]));
            if (conflict) Status = "Conflicting saved shortcuts; defaults are in use.";
            else
            {
                for (var i = 0; i < Entries.Count; i++)
                {
                    if (loaded[i]) continue;
                    if (Enumerable.Range(0, Entries.Count).Any(j => loaded[j] &&
                            ScopesConflict(Entries[i], Entries[j]) && desired[i] == desired[j]))
                    {
                        var candidates = Enumerable.Range((int)Key.F1, 12).Select(key =>
                            ((Key)key, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift));
                        desired[i] = candidates.First(candidate => !Enumerable.Range(0, Entries.Count).Any(j =>
                            loaded[j] && ScopesConflict(Entries[i], Entries[j]) && desired[j] == candidate));
                        Status = "Some new shortcuts were reassigned to preserve your existing settings.";
                    }
                    loaded[i] = true;
                }
                for (var i = 0; i < Entries.Count; i++) Entries[i].Set(desired[i].Key, desired[i].Modifiers);
            }
            if (PanOptions.Contains(file.CurvePanButton)) _curvePanButton = file.CurvePanButton!;
            if (ZoomOptions.Contains(file.CurveZoomModifier)) _curveZoomModifier = file.CurveZoomModifier!;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Status = "Could not load shortcuts; defaults are in use.";
        }
    }

    private bool TrySave()
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var file = new ShortcutFile
            {
                Bindings = Entries.ToDictionary(entry => entry.Id, entry => new ShortcutSetting(entry.Key.ToString(), entry.Modifiers)),
                CurvePanButton = _curvePanButton,
                CurveZoomModifier = _curveZoomModifier
            };
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Status = $"Could not save shortcuts: {ex.Message}";
            return false;
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary))
            {
                try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static bool IsModifier(Key key) => key.ToString() is
        "LeftCtrl" or "RightCtrl" or "LeftShift" or "RightShift" or "LeftAlt" or "RightAlt" or "LWin" or "RWin";
    private static bool ScopesConflict(ShortcutEntry first, ShortcutEntry second) =>
        first.Scope == second.Scope || first.Scope == "Global" || second.Scope == "Global";
    private const KeyModifiers ModifierMask = KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta;
    public sealed record ShortcutSetting(string Key, KeyModifiers Modifiers);
    public sealed class ShortcutFile
    {
        public Dictionary<string, ShortcutSetting>? Bindings { get; set; }
        public string? CurvePanButton { get; set; }
        public string? CurveZoomModifier { get; set; }
    }
}
