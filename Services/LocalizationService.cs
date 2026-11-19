using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LiberaFx.Services;

public sealed record EditorLanguage(string Id, string Name);

public sealed class LocalizationService : ObservableObject
{
    private static readonly Uri TranslationsUri = new("avares://LiberaFx/Assets/Translations");
    private readonly Dictionary<string, Dictionary<string, string>> _translations = new(StringComparer.Ordinal);
    private readonly string _settingsPath;
    private readonly ResourceDictionary _resources = new();
    private bool _canSave = true;
    private EditorLanguage _selectedLanguage;
    private EditorLanguage _selectedNodeLanguage;
    private string _status = string.Empty;

    public static LocalizationService Shared { get; private set; } = null!;
    public IReadOnlyList<EditorLanguage> Languages { get; } =
        [new("en", "English"), new("ja", "\u65e5\u672c\u8a9e")];
    public event EventHandler? LanguageChanged;
    public bool CanChangeLanguage => _canSave;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public EditorLanguage SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!Languages.Contains(value)) throw new ArgumentException("Unsupported editor language.", nameof(value));
            if (value == _selectedLanguage) return;
            if (!_canSave)
            {
                Status = Get("Editor.LanguageSettingsInvalid");
                OnPropertyChanged(nameof(SelectedLanguage));
                return;
            }
            try
            {
                Save(value.Id, _selectedNodeLanguage.Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Could not save editor language: {0}", ex.Message);
                Status = Get("Editor.LanguageSaveFailed");
                OnPropertyChanged(nameof(SelectedLanguage));
                return;
            }
            _selectedLanguage = value;
            ApplyResources();
            OnPropertyChanged();
            Status = string.Empty;
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public EditorLanguage SelectedNodeLanguage
    {
        get => _selectedNodeLanguage;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!Languages.Contains(value))
            {
                throw new ArgumentException("Unsupported node language.", nameof(value));
            }
            if (value == _selectedNodeLanguage)
            {
                return;
            }
            if (!_canSave)
            {
                Status = Get("Editor.LanguageSettingsInvalid");
                return;
            }
            try
            {
                Save(_selectedLanguage.Id, value.Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Could not save node language: {0}", ex.Message);
                Status = Get("Editor.LanguageSaveFailed");
                return;
            }
            _selectedNodeLanguage = value;
            OnPropertyChanged();
            Status = string.Empty;
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private LocalizationService(Application application)
    {
        _settingsPath = Path.Combine(AppDataDirectory.Root, "LiberaFx", "language.json");
        foreach (var language in Languages)
        {
            var strings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var uri in AssetLoader.GetAssets(TranslationsUri, null)
                         .Where(uri => uri.AbsolutePath.EndsWith("." + language.Id + ".json", StringComparison.Ordinal)))
            {
                using var stream = AssetLoader.Open(uri);
                var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                    ?? throw new InvalidDataException("Translation catalog is empty.");
                foreach (var entry in entries)
                    if (string.IsNullOrWhiteSpace(entry.Key) || entry.Value is null || !strings.TryAdd(entry.Key, entry.Value))
                        throw new InvalidDataException($"Invalid or duplicate translation key: {entry.Key}");
            }
            _translations.Add(language.Id, strings);
        }
        if (_translations["en"].Count == 0) throw new InvalidDataException("English translation catalog is missing.");
        var settings = LoadLanguage();
        _selectedLanguage = Languages.Single(language => language.Id == settings.Language);
        _selectedNodeLanguage = Languages.Single(language => language.Id == (settings.NodeLanguage ?? "en"));
        application.Resources.MergedDictionaries.Add(_resources);
        ApplyResources();
    }

    public static void Initialize(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        Shared = new LocalizationService(application);
    }

    public string Get(string key, params object?[] args)
    {
        var translated = _translations[_selectedLanguage.Id].GetValueOrDefault(key)
            ?? _translations["en"].GetValueOrDefault(key)
            ?? key;
        return args.Length == 0 ? translated : string.Format(CultureInfo.GetCultureInfo(_selectedLanguage.Id), translated, args);
    }

    public string GetOrDefault(string? key, string fallback) =>
        string.IsNullOrEmpty(key) ? fallback :
        _translations[_selectedLanguage.Id].GetValueOrDefault(key) ?? _translations["en"].GetValueOrDefault(key) ?? fallback;

    public string GetEnglishOrDefault(string? key, string fallback) =>
        string.IsNullOrEmpty(key) ? fallback : _translations["en"].GetValueOrDefault(key) ?? fallback;

    public string GetNodeLabel(string? key, string fallback) =>
        string.IsNullOrEmpty(key) ? fallback :
        _translations[_selectedNodeLanguage.Id].GetValueOrDefault(key) ?? GetEnglishOrDefault(key, fallback);

    public string GetNodeSearchTerms(string? key, string fallback) =>
        string.IsNullOrEmpty(key) ? fallback :
        $"{GetEnglishOrDefault(key, fallback)} {_translations["ja"].GetValueOrDefault(key)}";

    private LanguageSettings LoadLanguage()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return new("en");
            var settings = JsonSerializer.Deserialize<LanguageSettings>(File.ReadAllText(_settingsPath));
            if (settings is null || !Languages.Any(language => language.Id == settings.Language) ||
                (settings.NodeLanguage is not null && !Languages.Any(language => language.Id == settings.NodeLanguage)))
                throw new InvalidDataException("Unknown editor language.");
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            _canSave = false;
            Trace.TraceWarning("Could not read editor language: {0}", ex.Message);
            _status = _translations["en"].GetValueOrDefault("Editor.LanguageSettingsInvalid") ?? "Invalid language settings.";
            return new("en");
        }
    }

    private void Save(string language, string nodeLanguage)
    {
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".language-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new LanguageSettings(language, nodeLanguage)));
            File.Move(temporary, _settingsPath, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private void ApplyResources()
    {
        foreach (var key in _translations["en"].Keys)
            _resources["Text." + key] = Get(key);
    }

    private sealed record LanguageSettings(string Language, string? NodeLanguage = null);
}
