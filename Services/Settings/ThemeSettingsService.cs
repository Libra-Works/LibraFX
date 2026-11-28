using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Styling;
using LiberaFx.Models.Appearance;

namespace LiberaFx.Services.Settings;

public sealed class ThemeSettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true
    };

    private static readonly string[] EditorColorKeys =
    [
        "GraphMinorGrid", "GraphMajorGrid", "GraphParameter", "GraphSignal",
        "NodeBorder", "NodeSelectedBorder", "SelectionFill", "SelectionBorder",
        "CurveBackground", "CurveGrid", "CurveAxis", "CurveLine", "CurveHandle",
        "CurveKey", "CurveSelected", "CurveLabel"
    ];

    private static readonly Dictionary<string, SolidColorBrush> EditorBrushes =
        EditorColorKeys.ToDictionary(key => key, _ => new SolidColorBrush());
    private static readonly Dictionary<string, ImmutableSolidColorBrush> EditorRenderBrushes =
        EditorColorKeys.ToDictionary(key => key, _ => new ImmutableSolidColorBrush(Colors.Transparent));

    private readonly Dictionary<string, SchemeFile> _schemes = new(StringComparer.Ordinal);
    private readonly string _settingsPath = Path.Combine(
        AppDataDirectory.Root, "LiberaFx", "settings.json");
    private readonly string _legacySettingsPath = Path.Combine(
        AppDataDirectory.Root, "EffectTool", "settings.json");

    public ThemeSettings Current { get; private set; }
    public IReadOnlyList<string> ColorSchemes { get; }
    private readonly List<string> _warnings = [];
    public string LoadWarnings => string.Join(Environment.NewLine, _warnings);
    public event EventHandler? SettingsChanged;
    public static event EventHandler? EditorColorsChanged;

    public static IBrush EditorBrush(string name) => EditorBrushes[name];
    internal static IBrush EditorRenderBrush(string name) => EditorRenderBrushes[name];

    public ThemeSettingsService()
    {
        LoadSchemes();
        ColorSchemes = _schemes.Keys
            .OrderBy(name => name switch
            {
                "LibraDark" => 0,
                "LibraLight" => 1,
                "System" => 2,
                _ => 3
            })
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Current = LoadSettings();
        ApplyToApplication(Current);
    }

    public void Apply(ThemeSettings settings)
    {
        ValidateSettings(settings);
        Save(settings);
        ApplyToApplication(settings);
        Current = settings;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LoadSchemes()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "ColorSchemes");
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderBy(path => path, StringComparer.Ordinal))
            {
                try
                {
                    var scheme = JsonSerializer.Deserialize<SchemeFile>(File.ReadAllText(path), SerializerOptions);
                    ValidateScheme(scheme);
                    if (!_schemes.TryAdd(scheme!.Name!, scheme))
                    {
                        Trace.TraceWarning("Duplicate color scheme: {0}", path);
                        _warnings.Add($"{Path.GetFileName(path)}: duplicate scheme name.");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or FormatException or ArgumentException)
                {
                    Trace.TraceWarning("Ignoring invalid color scheme {0}: {1}", path, ex.Message);
                    _warnings.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }
        }

        foreach (var builtin in new[] { "LibraDark", "LibraLight", "System" })
        {
            if (_schemes.ContainsKey(builtin)) continue;
            using var stream = AssetLoader.Open(new Uri($"avares://LiberaFx/Assets/ColorSchemes/{builtin}.json"));
            var fallback = JsonSerializer.Deserialize<SchemeFile>(stream, SerializerOptions);
            ValidateScheme(fallback);
            _schemes.Add(builtin, fallback!);
        }
    }

    private static void ValidateScheme(SchemeFile? scheme)
    {
        if (scheme is null || scheme.SchemaVersion != 1 || string.IsNullOrWhiteSpace(scheme.Name) ||
            scheme.Variant is not ("System" or "Light" or "Dark") ||
            scheme.EditorColors is null || scheme.AccentColors is null)
            throw new InvalidDataException("Invalid color scheme header.");

        if (scheme.Variant != "System")
        {
            if (scheme.Palette is null) throw new InvalidDataException("A light or dark scheme requires a palette.");
            foreach (var color in new[] { scheme.Palette.Region, scheme.Palette.Surface, scheme.Palette.Foreground,
                         scheme.Palette.MutedForeground, scheme.Palette.Border, scheme.Palette.Accent })
                ParseColor(color);
        }

        foreach (var key in EditorColorKeys)
            ParseColor(scheme.EditorColors.GetValueOrDefault(key));
        foreach (var accent in Enum.GetNames<AccentPalette>().Where(name => name != nameof(AccentPalette.SchemeDefault)))
            ParseColor(scheme.AccentColors.GetValueOrDefault(accent));
    }

    private static Color ParseColor(string? value) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException("Missing color.") : Color.Parse(value);

    private ThemeSettings LoadSettings()
    {
        try
        {
            var path = File.Exists(_settingsPath) ? _settingsPath : _legacySettingsPath;
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<ThemeSettings>(File.ReadAllText(path), SerializerOptions);
                if (settings is not null && _schemes.ContainsKey(settings.ColorScheme) &&
                    Enum.IsDefined(settings.AccentPalette)) return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Trace.TraceWarning("Could not load appearance settings: {0}", ex.Message);
        }
        var defaultScheme = _schemes.ContainsKey("LibraDark") ? "LibraDark" : "System";
        return new ThemeSettings(defaultScheme, AccentPalette.SchemeDefault);
    }

    private void Save(ThemeSettings settings)
    {
        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("The settings directory could not be resolved.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, SerializerOptions));
    }

    private void ValidateSettings(ThemeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.ColorScheme is null || !_schemes.ContainsKey(settings.ColorScheme) ||
            !Enum.IsDefined(settings.AccentPalette))
            throw new InvalidDataException("Theme settings contain an unknown option.");
    }

    private void ApplyToApplication(ThemeSettings settings)
    {
        var application = Application.Current
            ?? throw new InvalidOperationException("The Avalonia application is not initialized.");
        var scheme = _schemes[settings.ColorScheme];
        application.RequestedThemeVariant = scheme.Variant switch
        {
            "System" => ThemeVariant.Default,
            "Light" => ThemeVariant.Light,
            _ => ThemeVariant.Dark
        };
        var resources = application.Resources;
        var keys = new[] { "SystemRegionColor", "SystemBaseHighColor", "SystemBaseMediumColor",
            "SystemBaseMediumLowColor", "SystemAltHighColor" };
        foreach (var key in keys) resources.Remove(key);
        if (scheme.Variant != "System")
        {
            var palette = scheme.Palette!;
            var values = new[] { palette.Region, palette.Foreground, palette.MutedForeground, palette.Border, palette.Surface };
            for (var i = 0; i < keys.Length; i++) resources[keys[i]] = ParseColor(values[i]);
        }
        var accent = settings.AccentPalette == AccentPalette.SchemeDefault
            ? scheme.Variant == "System" ? null : scheme.Palette!.Accent
            : scheme.AccentColors![settings.AccentPalette.ToString()];
        if (accent is null) resources.Remove("SystemAccentColor");
        else resources["SystemAccentColor"] = ParseColor(accent);

        foreach (var key in EditorColorKeys)
        {
            var color = ParseColor(scheme.EditorColors![key]);
            EditorBrushes[key].Color = color;
            EditorRenderBrushes[key] = new ImmutableSolidColorBrush(color);
            resources["Effect" + key + "Brush"] = EditorBrushes[key];
        }
        EditorColorsChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class SchemeFile
    {
        public int SchemaVersion { get; set; }
        public string? Name { get; set; }
        public string? Variant { get; set; }
        public Palette? Palette { get; set; }
        public Dictionary<string, string>? EditorColors { get; set; }
        public Dictionary<string, string>? AccentColors { get; set; }
    }

    private sealed class Palette
    {
        public string? Region { get; set; }
        public string? Surface { get; set; }
        public string? Foreground { get; set; }
        public string? MutedForeground { get; set; }
        public string? Border { get; set; }
        public string? Accent { get; set; }
    }
}
