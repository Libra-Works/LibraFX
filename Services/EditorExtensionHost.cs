using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LiberaFx.Extensions.Abstractions;
using LiberaFx.Models.Graph;
using LiberaFx.ViewModels;

namespace LiberaFx.Services;

public sealed record ExtensionDependency(string Id, string MinimumVersion, string? MaximumVersion = null);
public sealed record ExtensionManifest(int ManifestVersion, string Id, string Name, string Version,
    int ApiVersion, string TargetFramework, string Assembly, string EntryType,
    string[] ExtensionPoints, ExtensionDependency[]? Dependencies = null,
    int? ApiMinimum = null, int? ApiMaximum = null, string[]? Targets = null);

public sealed class EditorExtensionEntry : ObservableObject
{
    internal EditorExtensionEntry(ExtensionManifest manifest, string directory, string fingerprint)
    {
        Manifest = manifest; Directory = directory; Fingerprint = fingerprint;
        MessageLocalization.Observe(this, static entry => entry.OnPropertyChanged(nameof(Status)));
    }
    public ExtensionManifest Manifest { get; }
    public string Id => Manifest.Id;
    public string Name => Manifest.Name;
    public string Version => Manifest.Version;
    public string Capabilities => string.Join(", ", Manifest.ExtensionPoints);
    internal string Directory { get; }
    internal string Fingerprint { get; }
    private string _status = "";
    private bool _enabled;
    public string Status
    {
        get => _status.StartsWith("Extensions.", StringComparison.Ordinal) ? LocalizationService.Shared.Get(_status) : _status;
        internal set => SetProperty(ref _status, value);
    }
    public bool Enabled { get => _enabled; internal set => SetProperty(ref _enabled, value); }
}

public sealed record RegisteredExtensionCommand(string PluginId, ExtensionCommand Definition)
{
    public string Id => PluginId + "/" + Definition.Id;
    public string Name => Definition.Name;
}

public sealed class EditorExtensionHost : ObservableObject, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true, WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly ActiveGraphProvider _graphs;
    private readonly List<(IEditorExtension Plugin, ExtensionLoadContext Context)> _loaded = [];
    private readonly Dictionary<string, string> _activeFingerprints = new(StringComparer.Ordinal);
    private readonly HashSet<string> _disabledThisSession = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<DocumentContext>> _activeContexts = new(StringComparer.Ordinal);
    private readonly string _preferencesPath;
    private Dictionary<string, string> _enabled = new(StringComparer.Ordinal);
    private string? _preferencesContent;
    private bool _preferencesValid = true;
    private bool _disposed;
    private string _status = "";

    public string InstallDirectory { get; }
    public ObservableCollection<EditorExtensionEntry> Extensions { get; } = [];
    public ObservableCollection<RegisteredExtensionCommand> Commands { get; } = [];
    public ObservableCollection<IVersionControlProvider> VersionControlProviders { get; } = [];
    public ObservableCollection<IChatAdapter> ChatAdapters { get; } = [];
    public ObservableCollection<ICrashReportProvider> CrashReportProviders { get; } = [];
    private readonly Dictionary<string, List<IExtensionProvider>> _providers = new(StringComparer.Ordinal);
    public string Status
    {
        get => _status.StartsWith("Extensions.", StringComparison.Ordinal) ? LocalizationService.Shared.Get(_status) : _status;
        private set => SetProperty(ref _status, value);
    }

    public void RegisterBuiltIn(IExtensionProvider provider)
    {
        var registration = new Registration([ExtensionSdk.VcsPoint, ExtensionSdk.ChatPoint, ExtensionSdk.CrashReportPoint]);
        switch (provider)
        {
            case IVersionControlProvider vcs: registration.AddVersionControlProvider(vcs); break;
            case IChatAdapter chat: registration.AddChatAdapter(chat); break;
            case ICrashReportProvider report: registration.AddCrashReportProvider(report); break;
            default: throw new ArgumentException("Unsupported provider.", nameof(provider));
        }
        if (AllProviders().Any(existing => existing.Id == provider.Id))
            throw new InvalidDataException("Duplicate provider ID.");
        AddProvider(provider);
    }

    public EditorExtensionHost(ActiveGraphProvider graphs, string? root = null,
        IEnumerable<IExtensionProvider>? builtIns = null)
    {
        _graphs = graphs;
        MessageLocalization.Observe(this, static host => host.OnPropertyChanged(nameof(Status)));
        root ??= Path.Combine(AppDataDirectory.Root, "LiberaFx");
        InstallDirectory = Path.Combine(root, "Extensions");
        _preferencesPath = Path.Combine(root, "extensions.json");
        try
        {
            if (File.Exists(_preferencesPath))
            {
                _preferencesContent = File.ReadAllText(_preferencesPath);
                _enabled = JsonSerializer.Deserialize<Dictionary<string, string>>(_preferencesContent)
                    ?? throw new InvalidDataException("Empty extension preferences.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            _preferencesValid = false;
            Status = ex.Message;
        }
        foreach (var provider in builtIns ?? []) RegisterBuiltIn(provider);
        Discover();
    }

    private void Discover()
    {
        if (!Directory.Exists(InstallDirectory)) return;
        try
        {
            foreach (var directory in Directory.GetDirectories(InstallDirectory).Order(StringComparer.Ordinal))
            {
                try
                {
                    var manifestPath = ExtensionPackagePolicy.PackageFile(directory, "liberafx-extension.json");
                    if (new FileInfo(manifestPath).Length > 65536)
                        throw new InvalidDataException("Extension manifest exceeds 64 KiB.");
                    var manifest = JsonSerializer.Deserialize<ExtensionManifest>(
                        File.ReadAllText(manifestPath), JsonOptions)
                        ?? throw new InvalidDataException("Empty manifest.");
                    ValidateManifest(manifest);
                    Extensions.Add(new(manifest, directory, ExtensionPackagePolicy.Fingerprint(directory)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                { Status += Path.GetFileName(directory) + ": " + ex.Message + Environment.NewLine; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Status += ex.Message; }
        var duplicateIds = Extensions.GroupBy(entry => entry.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var pending = new List<EditorExtensionEntry>();
        foreach (var entry in Extensions)
        {
            if (duplicateIds.Contains(entry.Id)) { entry.Status = "Duplicate extension ID."; continue; }
            if (!Compatible(entry.Manifest))
            { entry.Status = "Incompatible SDK or target framework."; continue; }
            entry.Enabled = _preferencesValid && _enabled.GetValueOrDefault(entry.Id) == entry.Fingerprint;
            if (!entry.Enabled)
            { entry.Status = "Extensions.Disabled"; continue; }
            pending.Add(entry);
        }
        while (pending.Count > 0)
        {
            var ready = pending.Where(entry => (entry.Manifest.Dependencies ?? []).All(dependency =>
                Extensions.Any(other => other.Id == dependency.Id && _activeFingerprints.ContainsKey(other.Id) &&
                    System.Version.Parse(other.Version) >= System.Version.Parse(dependency.MinimumVersion) &&
                    (dependency.MaximumVersion is null ||
                     System.Version.Parse(other.Version) <= System.Version.Parse(dependency.MaximumVersion))))).ToArray();
            if (ready.Length == 0)
            {
                foreach (var entry in pending) entry.Status = "Missing, incompatible, failed or cyclic dependency.";
                break;
            }
            foreach (var entry in ready) { pending.Remove(entry); Load(entry); }
        }
    }

    private static void ValidateManifest(ExtensionManifest manifest)
    {
        if (manifest.ManifestVersion != 1 || !ExtensionPackagePolicy.ValidId(manifest.Id) ||
            string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 160 ||
            !System.Version.TryParse(manifest.Version, out _) ||
            (manifest.ApiMinimum ?? manifest.ApiVersion) < 1 ||
            (manifest.ApiMaximum ?? manifest.ApiVersion) < (manifest.ApiMinimum ?? manifest.ApiVersion) ||
            manifest.ApiVersion < (manifest.ApiMinimum ?? manifest.ApiVersion) ||
            manifest.ApiVersion > (manifest.ApiMaximum ?? manifest.ApiVersion) ||
            manifest.Targets is { Length: 0 or > 16 } ||
            manifest.Targets?.Any(string.IsNullOrWhiteSpace) == true ||
            string.IsNullOrWhiteSpace(manifest.EntryType) ||
            string.IsNullOrWhiteSpace(manifest.Assembly) ||
            !manifest.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            manifest.ExtensionPoints is null || manifest.ExtensionPoints.Length is < 1 or > 32 ||
            manifest.ExtensionPoints.Any(point => point is not (ExtensionSdk.CommandPoint or ExtensionSdk.VcsPoint or
                ExtensionSdk.ChatPoint or ExtensionSdk.CrashReportPoint)) ||
            manifest.ExtensionPoints.Distinct(StringComparer.Ordinal).Count() != manifest.ExtensionPoints.Length ||
            (manifest.Dependencies ?? []).Any(dependency => dependency is null ||
                !ExtensionPackagePolicy.ValidId(dependency.Id) ||
                !System.Version.TryParse(dependency.MinimumVersion, out _) ||
                dependency.MaximumVersion is not null &&
                (!System.Version.TryParse(dependency.MaximumVersion, out _) ||
                 System.Version.Parse(dependency.MaximumVersion) < System.Version.Parse(dependency.MinimumVersion))))
            throw new InvalidDataException("Invalid extension manifest or unsupported extension point.");
    }

    private static bool Compatible(ExtensionManifest manifest) =>
        (manifest.ApiMinimum ?? manifest.ApiVersion) <= ExtensionSdk.ApiVersion &&
        (manifest.ApiMaximum ?? manifest.ApiVersion) >= ExtensionSdk.ApiVersion &&
        manifest.TargetFramework == "net10.0" &&
        (manifest.Targets ?? ["editor"]).Contains("editor", StringComparer.Ordinal);

    private void Load(EditorExtensionEntry entry)
    {
        ExtensionLoadContext? context = null;
        IEditorExtension? extension = null;
        try
        {
            if (ExtensionPackagePolicy.Fingerprint(entry.Directory) != entry.Fingerprint)
                throw new InvalidDataException("Package changed after discovery.");
            var assemblyPath = ExtensionPackagePolicy.PackageFile(entry.Directory, entry.Manifest.Assembly);
            context = new(assemblyPath, entry.Directory);
            var type = context.LoadFromAssemblyPath(assemblyPath).GetType(entry.Manifest.EntryType, throwOnError: true)!;
            if (!type.IsPublic || type.IsAbstract || !typeof(IEditorExtension).IsAssignableFrom(type) ||
                type.GetConstructor(Type.EmptyTypes) is null)
                throw new InvalidDataException("Entry type must be a public IEditorExtension with a public default constructor.");
            extension = (IEditorExtension)Activator.CreateInstance(type)!;
            var registration = new Registration(entry.Manifest.ExtensionPoints);
            extension.Register(registration);
            foreach (var provider in registration.Providers)
                if (AllProviders().Any(existing => existing.Id == provider.Id))
                    throw new InvalidDataException($"Duplicate provider ID '{provider.Id}'.");
            foreach (var command in registration.Commands) Commands.Add(new(entry.Id, command));
            foreach (var provider in registration.Providers) AddProvider(provider);
            _providers.Add(entry.Id, registration.Providers);
            _loaded.Add((extension, context));
            _activeFingerprints.Add(entry.Id, entry.Fingerprint);
            entry.Status = "Extensions.Loaded";
        }
        catch (Exception ex)
        {
            try { extension?.Dispose(); } catch { }
            context?.Unload();
            entry.Status = "Load failed: " + ex.GetBaseException().Message;
        }
    }

    public bool SetEnabled(string id, bool enabled, bool explicitlyTrusted)
    {
        var matches = Extensions.Where(entry => entry.Id == id).ToArray();
        if (_disposed || !_preferencesValid || matches.Length != 1 || enabled && !explicitlyTrusted)
            return false;
        var entry = matches[0];
        if (enabled && !Compatible(entry.Manifest)) return false;
        var next = new Dictionary<string, string>(_enabled, StringComparer.Ordinal);
        if (enabled) next[id] = entry.Fingerprint; else next.Remove(id);
        var temporary = _preferencesPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (enabled && ExtensionPackagePolicy.Fingerprint(entry.Directory) != entry.Fingerprint)
                throw new InvalidDataException("Package changed; restart and review it again.");
            Directory.CreateDirectory(Path.GetDirectoryName(_preferencesPath)!);
            using var preferenceLock = new FileStream(_preferencesPath + ".lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            var currentContent = File.Exists(_preferencesPath) ? File.ReadAllText(_preferencesPath) : null;
            if (!string.Equals(currentContent, _preferencesContent, StringComparison.Ordinal))
                throw new InvalidDataException("Extension preferences changed externally. Restart and review them before saving.");
            var content = JsonSerializer.Serialize(next, JsonOptions);
            File.WriteAllText(temporary, content);
            File.Move(temporary, _preferencesPath, overwrite: true);
            _preferencesContent = content;
            _enabled = next;
            entry.Enabled = enabled;
            if (!enabled)
            {
                _disabledThisSession.Add(id);
                if (_activeContexts.TryGetValue(id, out var contexts))
                    foreach (var context in contexts.ToArray()) context.Invalidate();
                if (_providers.TryGetValue(id, out var providers))
                    foreach (var provider in providers)
                {
                    if (provider is IVersionControlProvider vcs) VersionControlProviders.Remove(vcs);
                    if (provider is IChatAdapter chat) ChatAdapters.Remove(chat);
                    if (provider is ICrashReportProvider report) CrashReportProviders.Remove(report);
                }
            }
            entry.Status = "Extensions.Restart";
            Status = "Extensions.Restart";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { Status = ex.Message; return false; }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public async Task ExecuteAsync(string commandId, CancellationToken cancellation = default)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var command = Commands.Single(item => item.Id == commandId);
        if (!Extensions.Any(entry => entry.Id == command.PluginId && entry.Enabled) ||
            !_activeFingerprints.ContainsKey(command.PluginId) || _disabledThisSession.Contains(command.PluginId))
            throw new InvalidOperationException("The extension is not active.");
        using var context = new DocumentContext(_graphs);
        if (!_activeContexts.TryGetValue(command.PluginId, out var contexts))
            _activeContexts.Add(command.PluginId, contexts = []);
        contexts.Add(context);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, context.Lifetime);
        try { await command.Definition.ExecuteAsync(context, linked.Token); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception ex) { Status = "Command failed: " + ex.GetBaseException().Message; }
        finally
        {
            contexts.Remove(context);
            if (contexts.Count == 0) _activeContexts.Remove(command.PluginId);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var context in _activeContexts.Values.SelectMany(contexts => contexts).ToArray())
            context.Invalidate();
        Commands.Clear();
        VersionControlProviders.Clear();
        ChatAdapters.Clear();
        CrashReportProviders.Clear();
        _providers.Clear();
        foreach (var (plugin, context) in _loaded.AsEnumerable().Reverse())
        {
            try { plugin.Dispose(); } catch { }
            context.Unload();
        }
        _loaded.Clear();
        _activeFingerprints.Clear();
    }

    private IEnumerable<IExtensionProvider> AllProviders() =>
        VersionControlProviders.Cast<IExtensionProvider>().Concat(ChatAdapters).Concat(CrashReportProviders);

    private void AddProvider(IExtensionProvider provider)
    {
        if (provider is IVersionControlProvider vcs) VersionControlProviders.Add(vcs);
        if (provider is IChatAdapter chat) ChatAdapters.Add(chat);
        if (provider is ICrashReportProvider report) CrashReportProviders.Add(report);
    }

    private sealed class Registration(string[] points) : IExtensionRegistration
    {
        internal List<ExtensionCommand> Commands { get; } = [];
        internal List<IExtensionProvider> Providers { get; } = [];
        public void AddCommand(ExtensionCommand command)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (!points.Contains(ExtensionSdk.CommandPoint, StringComparer.Ordinal) ||
                !ExtensionPackagePolicy.ValidId(command.Id) || string.IsNullOrWhiteSpace(command.Name) ||
                command.Name.Length > 160 || command.Description is null || command.ExecuteAsync is null ||
                Commands.Any(existing => existing.Id == command.Id) || Commands.Count >= 128)
                throw new InvalidDataException("Invalid or duplicate command.");
            Commands.Add(command);
        }
        private void Add(IExtensionProvider provider, string point)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (!points.Contains(point, StringComparer.Ordinal) ||
                !ExtensionPackagePolicy.ValidId(provider.Id) || string.IsNullOrWhiteSpace(provider.Name) ||
                provider.Name.Length > 160 || Providers.Count >= 64 ||
                (provider is IVersionControlProvider ? 1 : 0) + (provider is IChatAdapter ? 1 : 0) +
                (provider is ICrashReportProvider ? 1 : 0) != 1 ||
                Providers.Any(existing => existing.Id == provider.Id))
                throw new InvalidDataException("Undeclared, invalid or duplicate provider.");
            Providers.Add(provider);
        }
        public void AddVersionControlProvider(IVersionControlProvider provider) => Add(provider, ExtensionSdk.VcsPoint);
        public void AddChatAdapter(IChatAdapter adapter) => Add(adapter, ExtensionSdk.ChatPoint);
        public void AddCrashReportProvider(ICrashReportProvider provider) => Add(provider, ExtensionSdk.CrashReportPoint);
    }

    private sealed class DocumentContext : IExtensionDocumentContext
    {
        private readonly ActiveGraphProvider _provider;
        private readonly GraphWorkspaceViewModel _graph;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly GraphSnapshot _snapshot;
        private bool _disposed;
        public DocumentContext(ActiveGraphProvider provider)
        {
            _provider = provider;
            _graph = provider.Graph;
            _snapshot = _graph.CreateCurrentSnapshot();
            Snapshot = JsonSerializer.SerializeToElement(_snapshot, JsonOptions);
            _provider.PropertyChanged += OnProviderChanged;
            _graph.PropertyChanged += OnGraphChanged;
            _graph.History.StateChanged += OnHistoryChanged;
        }
        public JsonElement Snapshot { get; }
        public CancellationToken Lifetime => _lifetime.Token;
        public bool IsCurrent => Dispatcher.UIThread.CheckAccess() && !_disposed && !_lifetime.IsCancellationRequested &&
            ReferenceEquals(_provider.Graph, _graph) &&
            GraphSnapshot.ContentEquals(_snapshot, _graph.CreateCurrentSnapshot());
        internal void Invalidate()
        {
            if (_disposed) return;
            try { _lifetime.Cancel(); }
            catch (AggregateException) { }
        }
        private void OnProviderChanged(object? sender, PropertyChangedEventArgs e) => Invalidate();
        private void OnHistoryChanged(object? sender, EventArgs e) => Invalidate();
        private void OnGraphChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(GraphWorkspaceViewModel.IsRootGraph) ||
                !GraphSnapshot.ContentEquals(_snapshot, _graph.CreateCurrentSnapshot())) Invalidate();
        }
        public bool TryApplyPlan(JsonElement plan, out string? error)
        {
            error = null;
            if (!Dispatcher.UIThread.CheckAccess()) { error = "Graph edits require the UI thread."; return false; }
            if (!IsCurrent) { error = "The document context is no longer current."; return false; }
            try
            {
                var edit = plan.Deserialize<LiberaGraphEditPlan>(JsonOptions)
                    ?? throw new InvalidDataException("An edit plan is required.");
                if (edit.Nodes is null || edit.Connections is null || edit.Nodes.Count > 1000 || edit.Connections.Count > 4000)
                    throw new InvalidDataException("Invalid or oversized edit plan.");
                _graph.ApplyPlan(edit);
                Invalidate();
                return true;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or ArgumentException)
            { error = ex.Message; return false; }
        }
        public void Dispose()
        {
            if (_disposed) return;
            Invalidate();
            _disposed = true;
            _provider.PropertyChanged -= OnProviderChanged;
            _graph.PropertyChanged -= OnGraphChanged;
            _graph.History.StateChanged -= OnHistoryChanged;
            _lifetime.Dispose();
        }
    }

    private sealed class ExtensionLoadContext(string assemblyPath, string directory)
        : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IEditorExtension).Assembly.GetName().Name)
                return typeof(IEditorExtension).Assembly;
            if (name.Name is "LiberaFx" or "LiberaFx.Inspector.Abstractions" ||
                name.Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true)
                throw new InvalidDataException("Editor extensions must use the public extension SDK.");
            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(
                ExtensionPackagePolicy.PackageFile(directory, Path.GetRelativePath(directory, path)));
        }
        protected override nint LoadUnmanagedDll(string name)
        {
            if (OperatingSystem.IsWindows() && string.Equals(name, "advapi32.dll", StringComparison.OrdinalIgnoreCase))
                return LoadUnmanagedDllFromPath(Path.Combine(Environment.SystemDirectory, "advapi32.dll"));
            throw new NotSupportedException("Native package dependencies are not supported by this host.");
        }
    }
}
