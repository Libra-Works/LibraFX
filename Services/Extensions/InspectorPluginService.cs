using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LiberaFx.Inspector.Abstractions;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;

namespace LiberaFx.Services.Extensions;

public sealed class InspectorPluginService : ObservableObject, IDisposable
{
    private const int ManifestVersion = 1;
    private const string RequiredAvaloniaVersion = "12.1.6";
    private static readonly string[] CommonTypes =
        ["bool", "color", "float", "int", "string", "vector2", "vector3", "vector4"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _pluginsDirectory = Path.Combine(AppDataDirectory.Root, "LiberaFx", "InspectorPlugins");
    private readonly string _preferencesPath = Path.Combine(AppDataDirectory.Root, "LiberaFx", "inspector-plugins.json");
    private readonly List<string> _diagnostics = [];
    private readonly Dictionary<string, LoadedInspector> _loaded = new(StringComparer.Ordinal);
    private readonly List<InspectorPluginLoadContext> _loadContexts = [];
    private InspectorPluginPreferences _preferences;
    private bool _preferencesValid = true;
    private string _status = string.Empty;
    private string _operationStatus = string.Empty;
    private string _pluginSearch = string.Empty;

    public static InspectorPluginService Shared { get; } = new();
    public ObservableCollection<InspectorPluginEntry> Plugins { get; } = [];
    public ObservableCollection<InspectorPluginEntry> VisiblePlugins { get; } = [];
    public ObservableCollection<InspectorDefaultChoice> Defaults { get; } = [];
    public IReadOnlyList<string> Diagnostics => _diagnostics;
    public event EventHandler? DefaultsChanged;
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }
    public string OperationStatus
    {
        get => _operationStatus;
        private set => SetProperty(ref _operationStatus, value);
    }
    public bool PreferencesValid => _preferencesValid;
    public string InstallDirectory => _pluginsDirectory;
    public void FilterPlugins(string? search)
    {
        _pluginSearch = search?.Trim() ?? string.Empty;
        UpdateVisiblePlugins();
    }

    private InspectorPluginService()
    {
        _preferences = LoadPreferences();
        DiscoverAndLoadStartupPlugins();
        RefreshDefaultChoices();
        RefreshStatus();
        LocalizationService.Shared.LanguageChanged += OnLanguageChanged;
    }

    public void Dispose()
    {
        LocalizationService.Shared.LanguageChanged -= OnLanguageChanged;
        _loaded.Clear();
        foreach (var context in _loadContexts) context.Unload();
        _loadContexts.Clear();
    }

    public bool HasConfiguredDefault(string type) =>
        !string.IsNullOrEmpty(_preferences.Defaults.GetValueOrDefault(type)) ||
        !string.IsNullOrEmpty(_preferences.Defaults.GetValueOrDefault("*"));

    public void RefreshLocalizedText()
    {
        RefreshDefaultChoices();
        RefreshStatus();
    }

    public bool TrySetEnabled(string pluginId, bool enabled, bool userConfirmed)
    {
        if (Plugins.FirstOrDefault(item => item.Id == pluginId) is not { } entry)
        {
            Status = $"Unknown inspector plugin: {pluginId}";
            return false;
        }
        if (!_preferencesValid)
        {
            Status = "Inspector preferences are invalid and were preserved. Repair inspector-plugins.json before changing settings.";
            return false;
        }
        if (enabled && (!userConfirmed || !entry.IsCompatible))
        {
            Status = entry.IsCompatible ? "Enabling a plugin requires explicit trust confirmation." : entry.Status;
            return false;
        }

        var previous = _preferences;
        var next = ClonePreferences(previous);
        next.Trusted[pluginId] = enabled || previous.Trusted.GetValueOrDefault(pluginId);
        if (enabled) next.Fingerprints[pluginId] = entry.Fingerprint;
        next.Enabled[pluginId] = enabled;
        if (!TrySave(next, out var error))
        {
            Status = $"Could not save inspector preferences; the current setting is unchanged. {error}";
            return false;
        }

        _preferences = next;
        entry.IsTrusted = next.Trusted.GetValueOrDefault(pluginId);
        entry.IsEnabled = enabled;
        entry.SetStatus(enabled
            ? "Enabled for next restart. DLL code runs with the editor user's permissions; no sandbox is provided."
            : "Disabled for next restart.");
        RefreshDefaultChoices();
        DefaultsChanged?.Invoke(this, EventArgs.Empty);
        RefreshStatus();
        return true;
    }

    public bool TrySetDefault(string type, string inspectorKey)
    {
        if (string.IsNullOrWhiteSpace(type) || type.Length > 128 || inspectorKey.Length > 260)
        {
            Status = "Invalid inspector type or selection.";
            return false;
        }
        if (!_preferencesValid)
        {
            Status = "Inspector preferences are invalid and were preserved. Repair inspector-plugins.json before changing settings.";
            return false;
        }
        if (inspectorKey.Length != 0 && !TrySplitInspectorKey(inspectorKey, out _, out _))
        {
            Status = "Inspector selection must use plugin-id/inspector-id.";
            return false;
        }

        var next = ClonePreferences(_preferences);
        next.Defaults[type] = inspectorKey;
        if (!TrySave(next, out var error))
        {
            Status = $"Could not save inspector preferences; the current selection is unchanged. {error}";
            return false;
        }
        _preferences = next;
        DefaultsChanged?.Invoke(this, EventArgs.Empty);
        RefreshStatus();
        return true;
    }

    public void SetOperationStatus(string message) => OperationStatus = message;

    public bool TryInstallPluginFolder(string sourceDirectory, out string message)
    {
        message = string.Empty;
        if (!Directory.Exists(sourceDirectory))
        {
            message = "The selected directory does not exist.";
            return false;
        }
        try
        {
            var manifestPath = Path.Combine(sourceDirectory, "liberafx-inspector.json");
            var manifest = ReadManifest(manifestPath);
            if (manifest.CompatibilityError is not null)
            {
                message = manifest.CompatibilityError;
                OperationStatus = message;
                return false;
            }
            if (Plugins.Any(item => item.Id == manifest.PluginId))
            {
                message = $"Plugin ID '{manifest.PluginId}' is already installed. Remove or update it manually before adding another copy.";
                OperationStatus = message;
                return false;
            }
            var target = Path.Combine(_pluginsDirectory, manifest.PluginId);
            Directory.CreateDirectory(_pluginsDirectory);
            if (Directory.Exists(target))
            {
                message = $"The destination already exists: {target}";
                OperationStatus = message;
                return false;
            }
            Directory.CreateDirectory(target);
            try
            {
                foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
                {
                    if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Symbolic links and reparse points are not supported in plugin folders.");
                    File.Copy(source, Path.Combine(target, Path.GetFileName(source)));
                }
                var installed = ReadManifest(Path.Combine(target, "liberafx-inspector.json"));
                if (installed.CompatibilityError is not null) throw new InvalidDataException(installed.CompatibilityError);
                var fingerprint = ComputePluginFingerprint(target);
                var entry = new InspectorPluginEntry(installed, target, fingerprint)
                {
                    IsTrusted = IsTrusted(installed.PluginId, fingerprint),
                    IsEnabled = _preferences.Enabled.GetValueOrDefault(installed.PluginId)
                };
                entry.SetStatus(entry.IsEnabled && entry.IsTrusted
                    ? "Enabled for next restart; this new DLL has not been executed."
                    : entry.IsTrusted ? "Trusted, disabled. DLL not executed." : "Not trusted or enabled. DLL not executed.");
                Plugins.Add(entry);
            }
            catch
            {
                Directory.Delete(target, recursive: true);
                throw;
            }
            UpdateVisiblePlugins();
            RefreshDefaultChoices();
            DefaultsChanged?.Invoke(this, EventArgs.Empty);
            RefreshStatus();
            message = $"Added '{manifest.DisplayName}'. Review its trust and enablement below; changes take effect after restart.";
            OperationStatus = message;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            message = $"Could not add inspector plugin: {ex.Message}";
            AddDiagnostic(message);
            OperationStatus = message;
            return false;
        }
    }

    public Control? CreateDefaultEditor(string type, NodeParameterEditorViewModel parameter)
    {
        var key = _preferences.Defaults.GetValueOrDefault(type);
        if (string.IsNullOrEmpty(key))
            key = _preferences.Defaults.GetValueOrDefault("*");
        if (string.IsNullOrEmpty(key)) return null;
        return CreateEditor(key, type, parameter);
    }

    public Control? CreateNamedEditor(string inspectorKey, string type, NodeParameterEditorViewModel parameter) =>
        TrySplitInspectorKey(inspectorKey, out _, out _)
            ? CreateEditor(inspectorKey, type, parameter) : null;

    private Control? CreateEditor(string key, string type, NodeParameterEditorViewModel parameter)
    {
        if (!_loaded.TryGetValue(key, out var inspector))
        {
            AddDiagnostic($"Selected inspector '{key}' is missing or not loaded; using the built-in editor.");
            return null;
        }
        if (inspector.Definition.SupportedTypes.Contains(type, StringComparer.Ordinal) is false)
        {
            AddDiagnostic($"Selected inspector '{key}' does not support type '{type}'; using the built-in editor.");
            return null;
        }
        if (!inspector.Entry.IsEnabled || !inspector.Entry.IsTrusted) return null;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            AddDiagnostic($"Inspector '{key}' was requested off the UI thread; using the built-in editor.");
            return null;
        }
        var isCurrent = parameter.IsCurrentInspectorTarget;
        var context = new InspectorContext(parameter, isCurrent);
        try
        {
            var control = inspector.Definition.CreateEditor(context);
            if (control is null) throw new InvalidOperationException("The inspector factory returned null.");
            return new InspectorEditorHost(control, context);
        }
        catch (Exception ex)
        {
            context.Dispose();
            AddDiagnostic($"Inspector '{key}' factory failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    private void DiscoverAndLoadStartupPlugins()
    {
        Plugins.Clear();
        VisiblePlugins.Clear();
        _loaded.Clear();
        var manifests = new List<InspectorPluginEntry>();
        string[] directories;
        try { directories = ExtensionPackagePolicy.DiscoverDirectories(_pluginsDirectory, "Inspectors"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddDiagnostic($"Inspector plugin directory could not be read: {ex.Message}");
            UpdateVisiblePlugins();
            return;
        }
        foreach (var directory in directories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var manifestPath = Path.Combine(directory, "liberafx-inspector.json");
                var manifest = ReadManifest(manifestPath);
                var fingerprint = ComputePluginFingerprint(directory);
                var entry = new InspectorPluginEntry(manifest, directory, fingerprint)
                {
                    IsTrusted = IsTrusted(manifest.PluginId, fingerprint),
                    IsEnabled = _preferences.Enabled.GetValueOrDefault(manifest.PluginId)
                };
                Plugins.Add(entry);
                manifests.Add(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
            {
                AddDiagnostic($"{Path.GetFileName(directory)}: {ex.GetBaseException().Message}");
            }
        }

        var duplicatePlugins = manifests.GroupBy(entry => entry.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var duplicate in duplicatePlugins)
        {
            foreach (var entry in manifests.Where(entry => entry.Id == duplicate))
            {
                entry.MarkIdentityConflict();
                entry.SetStatus($"Duplicate plugin ID '{duplicate}'; none of these copies will load.");
            }
            AddDiagnostic($"Duplicate plugin ID '{duplicate}'.");
        }

        var duplicateInspectors = manifests.SelectMany(entry => entry.Manifest.Inspectors.Select(inspector =>
            (Plugin: entry.Id, Inspector: inspector.Id, Key: InspectorKey(entry.Id, inspector.Id))))
            .GroupBy(item => item.Key, StringComparer.Ordinal).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var entry in manifests)
        {
            if (duplicatePlugins.Contains(entry.Id)) continue;
            if (entry.Manifest.CompatibilityError is { } compatibilityError)
            {
                entry.SetStatus(compatibilityError);
                AddDiagnostic($"{entry.Name}: {compatibilityError}");
                continue;
            }
            if (entry.Manifest.Inspectors.Any(inspector => duplicateInspectors.Contains(InspectorKey(entry.Id, inspector.Id))))
            {
                entry.MarkIdentityConflict();
                entry.SetStatus("Duplicate inspector ID; this plugin is not loaded.");
                AddDiagnostic($"{entry.Name}: duplicate inspector ID.");
                continue;
            }
            if (!entry.IsEnabled)
            {
                entry.SetStatus(entry.IsTrusted ? "Trusted, disabled. DLL not executed." : "Not trusted or enabled. DLL not executed.");
                continue;
            }
            if (!entry.IsTrusted)
            {
                entry.SetStatus("Enabled preference ignored because this DLL has not been explicitly trusted.");
                AddDiagnostic($"{entry.Name}: enabled but not trusted; DLL not executed.");
                continue;
            }
            TryLoad(entry);
        }
        UpdateVisiblePlugins();
    }

    private void TryLoad(InspectorPluginEntry entry)
    {
        InspectorPluginLoadContext? loadContext = null;
        try
        {
            var manifest = entry.Manifest;
            var assemblyPath = Path.GetFullPath(Path.Combine(entry.Directory, manifest.Assembly));
            var root = Path.GetFullPath(entry.Directory) + Path.DirectorySeparatorChar;
            if (!assemblyPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(assemblyPath))
                throw new FileNotFoundException("The plugin assembly declared by the manifest was not found.");
            loadContext = new InspectorPluginLoadContext(assemblyPath);
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var type = assembly.GetType(manifest.EntryType, throwOnError: true, ignoreCase: false)!;
            if (!type.IsPublic || type.IsAbstract || !typeof(IInspectorPlugin).IsAssignableFrom(type) ||
                type.GetConstructor(Type.EmptyTypes) is null)
                throw new InvalidDataException("The manifest entry type must be a public IInspectorPlugin with a parameterless constructor.");
            var plugin = (IInspectorPlugin)Activator.CreateInstance(type)!;
            if (plugin.Id != manifest.PluginId || plugin.Name != manifest.DisplayName ||
                plugin.Version.ToString() != manifest.Version || plugin.ApiVersion != InspectorSdk.ApiVersion)
                throw new InvalidDataException("Plugin code metadata does not match the manifest or supported SDK API.");
            var definitions = plugin.Inspectors ?? throw new InvalidDataException("The plugin returned a null inspector list.");
            if (definitions.Count != manifest.Inspectors.Count)
                throw new InvalidDataException("The inspector list does not match the manifest.");
            var definitionsById = definitions.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var pending = new List<LoadedInspector>();
            foreach (var declaration in manifest.Inspectors)
            {
                if (!definitionsById.TryGetValue(declaration.Id, out var definition) ||
                    definition.Name != declaration.DisplayName ||
                    definition.Description != declaration.Description ||
                    !definition.SupportedTypes.OrderBy(value => value, StringComparer.Ordinal)
                        .SequenceEqual(declaration.Types.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal))
                    throw new InvalidDataException($"Inspector '{declaration.Id}' does not match its manifest declaration.");
                if (_loaded.ContainsKey(InspectorKey(plugin.Id, definition.Id)))
                    throw new InvalidDataException($"Duplicate inspector ID '{definition.Id}'.");
                pending.Add(new(entry, definition));
            }
            foreach (var item in pending)
                _loaded.Add(InspectorKey(plugin.Id, item.Definition.Id), item);
            _loadContexts.Add(loadContext);
            entry.SetStatus("Trusted and loaded. DLL code runs with the editor user's permissions; no sandbox is provided.");
        }
        catch (Exception ex)
        {
            loadContext?.Unload();
            entry.SetStatus($"Load failed: {ex.GetBaseException().Message}");
            AddDiagnostic($"{entry.Name}: {entry.Status}");
        }
    }

    private InspectorPluginPreferences LoadPreferences()
    {
        if (!File.Exists(_preferencesPath)) return new();
        try
        {
            var preferences = JsonSerializer.Deserialize<InspectorPluginPreferences>(File.ReadAllText(_preferencesPath), JsonOptions)
                ?? throw new InvalidDataException("Inspector preferences are empty.");
            if (preferences.SchemaVersion is not (1 or 2) || preferences.Enabled is null ||
                preferences.Trusted is null || preferences.Defaults is null || preferences.Fingerprints is null)
                throw new InvalidDataException("Unsupported or incomplete inspector preferences.");
            if (preferences.SchemaVersion == 1)
            {
                preferences.SchemaVersion = 2;
                preferences.Fingerprints.Clear();
            }
            return preferences;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            _preferencesValid = false;
            AddDiagnostic($"Inspector preference file preserved but unusable: {ex.Message}");
            Status = "Inspector preferences could not be read; the original file is preserved and settings are read-only.";
            return new();
        }
    }

    private bool TrySave(InspectorPluginPreferences preferences, out string error)
    {
        error = string.Empty;
        var directory = Path.GetDirectoryName(_preferencesPath)!;
        var temporary = Path.Combine(directory, $".inspector-plugins-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences, JsonOptions));
            File.Move(temporary, _preferencesPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = ex.Message;
            return false;
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

    private void RefreshDefaultChoices()
    {
        Defaults.Clear();
        var types = CommonTypes.Concat(Plugins.SelectMany(plugin => plugin.Manifest.Inspectors)
            .SelectMany(inspector => inspector.Types)).Concat(_preferences.Defaults.Keys)
            .Distinct(StringComparer.Ordinal).OrderBy(type => type, StringComparer.Ordinal);
        foreach (var type in types)
        {
            var options = new List<InspectorDefaultOption> { new("", LocalizationService.Shared.Get("Editor.InspectorAutomatic")) };
            foreach (var plugin in Plugins)
            foreach (var inspector in plugin.Manifest.Inspectors.Where(item => item.Types.Contains(type, StringComparer.Ordinal)))
            {
                var identity = InspectorKey(plugin.Id, inspector.Id);
                var state = plugin.IsCompatible && plugin.IsEnabled && plugin.IsTrusted &&
                    _loaded.ContainsKey(identity) ? string.Empty :
                    $" ({LocalizationService.Shared.Get("Editor.InspectorUnavailable")})";
                options.Add(new(identity, $"{plugin.Name} — {inspector.DisplayName}{state}"));
            }
            Defaults.Add(new InspectorDefaultChoice(type, options,
                _preferences.Defaults.GetValueOrDefault(type) ?? string.Empty, _preferencesValid, TrySetDefault));
        }
    }

    private void RefreshStatus()
    {
        OnPropertyChanged(nameof(PreferencesValid));
        OnPropertyChanged(nameof(Diagnostics));
        Status = _diagnostics.Count == 0
            ? (_preferencesValid ? LocalizationService.Shared.Get("Editor.InspectorRestartHint") : Status)
            : string.Join(Environment.NewLine, _diagnostics);
    }

    private void AddDiagnostic(string diagnostic)
    {
        if (!_diagnostics.Contains(diagnostic, StringComparer.Ordinal)) _diagnostics.Add(diagnostic);
        OnPropertyChanged(nameof(Diagnostics));
        Status = string.Join(Environment.NewLine, _diagnostics);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var plugin in Plugins) plugin.RefreshLocalizedText();
        RefreshLocalizedText();
    }

    private void UpdateVisiblePlugins()
    {
        VisiblePlugins.Clear();
        foreach (var plugin in Plugins.Where(plugin => _pluginSearch.Length == 0 ||
                     plugin.Name.Contains(_pluginSearch, StringComparison.OrdinalIgnoreCase) ||
                     plugin.Id.Contains(_pluginSearch, StringComparison.OrdinalIgnoreCase) ||
                     plugin.Types.Contains(_pluginSearch, StringComparison.OrdinalIgnoreCase) ||
                     plugin.Inspectors.Contains(_pluginSearch, StringComparison.OrdinalIgnoreCase)))
            VisiblePlugins.Add(plugin);
    }

    private static InspectorPluginManifest ReadManifest(string path)
    {
        using var stream = File.OpenRead(path);
        var manifest = JsonSerializer.Deserialize<InspectorPluginManifest>(stream, JsonOptions)
            ?? throw new InvalidDataException("Inspector manifest is empty.");
        if (manifest.ManifestVersion != ManifestVersion || !ValidId(manifest.PluginId) ||
            string.IsNullOrWhiteSpace(manifest.DisplayName) || manifest.DisplayName.Length > 160 ||
            !Version.TryParse(manifest.Version, out _) || manifest.ApiVersion < 1 ||
            string.IsNullOrWhiteSpace(manifest.TargetFramework) || string.IsNullOrWhiteSpace(manifest.AvaloniaVersion) ||
            string.IsNullOrWhiteSpace(manifest.EntryType) || string.IsNullOrWhiteSpace(manifest.Assembly) ||
            Path.GetFileName(manifest.Assembly) != manifest.Assembly ||
            !manifest.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            manifest.Inspectors is null || manifest.Inspectors.Count is < 1 or > 64)
            throw new InvalidDataException("Inspector manifest fields are missing or invalid.");

        var inspectorIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var inspector in manifest.Inspectors)
        {
            if (!ValidId(inspector.Id) || !inspectorIds.Add(inspector.Id) ||
                string.IsNullOrWhiteSpace(inspector.DisplayName) || inspector.DisplayName.Length > 160 ||
                inspector.Description is null || inspector.Description.Length > 512 ||
                inspector.Types is null || inspector.Types.Count is < 1 or > 64 ||
                inspector.Types.Any(type => string.IsNullOrWhiteSpace(type) || type.Length > 128))
                throw new InvalidDataException("An inspector declaration is invalid or duplicated.");
        }
        manifest.CompatibilityError =
            manifest.ApiVersion != InspectorSdk.ApiVersion ? $"Unsupported Inspector SDK API {manifest.ApiVersion}." :
            manifest.TargetFramework != "net10.0" ? $"Unsupported target framework '{manifest.TargetFramework}' (expected net10.0)." :
            manifest.AvaloniaVersion != RequiredAvaloniaVersion ? $"Avalonia version mismatch (expected {RequiredAvaloniaVersion})." :
            null;
        return manifest;
    }

    private static bool ValidId(string id) => ExtensionPackagePolicy.ValidId(id);
    private bool IsTrusted(string pluginId, string fingerprint) =>
        _preferences.Trusted.GetValueOrDefault(pluginId) &&
        _preferences.Fingerprints.TryGetValue(pluginId, out var trustedFingerprint) &&
        string.Equals(trustedFingerprint, fingerprint, StringComparison.Ordinal);

    private static string ComputePluginFingerprint(string directory)
        => ExtensionPackagePolicy.Fingerprint(directory, recursive: false, legacy: true);

    private static string InspectorKey(string pluginId, string inspectorId) => $"{pluginId}/{inspectorId}";

    private static bool TrySplitInspectorKey(string key, out string pluginId, out string inspectorId)
    {
        var separator = key.IndexOf('/');
        pluginId = separator > 0 ? key[..separator] : string.Empty;
        inspectorId = separator >= 0 && separator < key.Length - 1 ? key[(separator + 1)..] : string.Empty;
        return ValidId(pluginId) && ValidId(inspectorId);
    }

    private static InspectorPluginPreferences ClonePreferences(InspectorPluginPreferences source) => new()
    {
        SchemaVersion = 2,
        Enabled = new(source.Enabled, StringComparer.Ordinal),
        Trusted = new(source.Trusted, StringComparer.Ordinal),
        Fingerprints = new(source.Fingerprints, StringComparer.Ordinal),
        Defaults = new(source.Defaults, StringComparer.Ordinal)
    };

    private sealed record LoadedInspector(InspectorPluginEntry Entry, IInspectorDefinition Definition);
}

public sealed class InspectorPluginManifest
{
    public int ManifestVersion { get; set; }
    public string PluginId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public int ApiVersion { get; set; }
    public string TargetFramework { get; set; } = string.Empty;
    public string AvaloniaVersion { get; set; } = string.Empty;
    public string Assembly { get; set; } = string.Empty;
    public string EntryType { get; set; } = string.Empty;
    public List<InspectorManifestEntry> Inspectors { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CompatibilityError { get; set; }
}

public sealed class InspectorManifestEntry
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Types { get; set; } = [];
}

