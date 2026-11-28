using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using LiberaFx.Models.Appearance;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;

namespace LiberaFx.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly DockFactory _dockFactory;
    private readonly ThemeSettingsService _themeSettings = new();
    private readonly ProjectFileService _projectFiles = new();
    private readonly Dictionary<string, IStorageFile> _storageFiles = new(StringComparer.Ordinal);
    private IStorageProvider? _storageProvider;
    private GraphDocumentViewModel? _activeDocument;
    private string _mcpStatus = LocalizationService.Shared.Get("Messages.Starting");
    private string _fileStatus = LocalizationService.Shared.Get("Messages.Ready");
    private string _appearanceStatus = string.Empty;
    private string _previewStatus = LocalizationService.Shared.Get("Messages.Starting");
    private int _previewClientCount;
    private string? _mcpStatusKey = "Messages.Starting";
    private object?[] _mcpStatusArgs = [];
    private string? _previewStatusKey = "Messages.Starting";
    private object?[] _previewStatusArgs = [];
    private string? _fileStatusKey = "Messages.Ready";
    private object?[] _fileStatusArgs = [];
    private string? _appearanceStatusKey;
    private object?[] _appearanceStatusArgs = [];

    public EditorLog Log { get; } = new();
    public UndoHistorySettingsService HistorySettings { get; } = UndoHistorySettingsService.Shared;
    public PreviewTelemetryStore PreviewTelemetry { get; } = new();
    public PreviewAssetStore PreviewAssets { get; }
    public AssetMetadataStore AssetMetadata { get; } = new();
    public string PreviewConnectionSummary
    {
        get
        {
            var peers = PreviewTelemetry.Clients();
            if (peers.Count == 0) return LocalizationService.Shared.Get("Connections.Disconnected");
            var syncing = LocalizationService.Shared.Get("Connections.Syncing");
            return string.Join(" | ", peers.Select(peer =>
            {
                var label = $"{peer.Engine ?? "?"} / {peer.Role ?? "?"} / {peer.ViewerName ?? peer.ClientId.ToString("D")[..8]}";
                return peer.AwaitingProgramAcknowledgement ? $"{label} ({syncing})" : label;
            }));
        }
    }
    public void RefreshPreviewConnections() => OnPropertyChanged(nameof(PreviewConnectionSummary));
    public string McpEndpoint { get; }
    public string PreviewEndpoint { get; }
    public string PreviewAccessToken { get; }
    public string PreviewFingerprint { get; }
    public string PreviewPublicCertificatePem { get; init; } = "";
    public string PreviewStatus
    {
        get => _previewStatus;
        set
        {
            _previewStatusKey = null;
            _previewStatusArgs = [];
            if (SetProperty(ref _previewStatus, value)) Log.Add("Preview", value);
        }
    }
    public int PreviewClientCount
    {
        get => _previewClientCount;
        set
        {
            if (SetProperty(ref _previewClientCount, value)) Log.Add("Preview", LocalizationService.Shared.Get("Messages.ConnectedClients", value));
        }
    }

    public ShortcutService Shortcuts => ShortcutService.Shared;
    public ServerSettingsService ServerSettings { get; }
    public TeamProfilesService TeamProfiles { get; } = new();
    public GroupAssetCatalog GroupAssets { get; }
    public NodeDefinitionCatalog NodeDefinitions { get; }
    public LocalizationService Localization => LocalizationService.Shared;
    public InspectorPluginService InspectorPlugins => InspectorPluginService.Shared;
    public EditorExtensionHost Extensions { get; }
    public EditorPlayback Playback { get; } = new();

    public string McpAccessToken { get; }

    public string McpStatus
    {
        get => _mcpStatus;
        set
        {
            _mcpStatusKey = null;
            _mcpStatusArgs = [];
            if (SetProperty(ref _mcpStatus, value)) Log.Add("MCP", value);
        }
    }

    public void SetMcpStatus(string key, params object?[] args)
    {
        McpStatus = LocalizationService.Shared.Get(key, args);
        _mcpStatusKey = key;
        _mcpStatusArgs = args;
    }

    public void SetPreviewStatus(string key, params object?[] args)
    {
        PreviewStatus = LocalizationService.Shared.Get(key, args);
        _previewStatusKey = key;
        _previewStatusArgs = args;
    }

    private void RefreshLocalizedStatuses()
    {
        if (_mcpStatusKey is { } mcpKey)
            SetProperty(ref _mcpStatus, LocalizationService.Shared.Get(mcpKey, _mcpStatusArgs), nameof(McpStatus));
        if (_previewStatusKey is { } previewKey)
            SetProperty(ref _previewStatus, LocalizationService.Shared.Get(previewKey, _previewStatusArgs), nameof(PreviewStatus));
        if (_fileStatusKey is { } fileKey)
            SetProperty(ref _fileStatus, LocalizationService.Shared.Get(fileKey, _fileStatusArgs), nameof(FileStatus));
        if (_appearanceStatusKey is { } appearanceKey)
            SetProperty(ref _appearanceStatus, LocalizationService.Shared.Get(appearanceKey, _appearanceStatusArgs), nameof(AppearanceStatus));
    }

    private void SetFileStatus(string key, params object?[] args)
    {
        FileStatus = LocalizationService.Shared.Get(key, args);
        _fileStatusKey = key;
        _fileStatusArgs = args;
    }

    private void SetAppearanceStatus(string key, params object?[] args)
    {
        AppearanceStatus = LocalizationService.Shared.Get(key, args);
        _appearanceStatusKey = key;
        _appearanceStatusArgs = args;
    }

    public string FileStatus
    {
        get => _fileStatus;
        private set
        {
            _fileStatusKey = null;
            _fileStatusArgs = [];
            if (SetProperty(ref _fileStatus, value)) Log.Add("File", value);
        }
    }

    public IRootDock Layout { get; }

    public DocumentDock DocumentsDock => _dockFactory.DocumentsDock;

    public ObservableCollection<GraphDocumentViewModel> Documents { get; } = [];

    public GraphDocumentViewModel? ActiveDocument
    {
        get => _activeDocument;
        private set
        {
            if (!SetProperty(ref _activeDocument, value))
            {
                return;
            }

            OnPropertyChanged(nameof(Graph));
            UndoCommand?.NotifyCanExecuteChanged();
            RedoCommand?.NotifyCanExecuteChanged();
        }
    }

    public GraphWorkspaceViewModel Graph => ActiveDocument is not null ? ActiveGraphProvider.Graph
        : throw new InvalidOperationException("There is no active document.");

    public ActiveGraphProvider ActiveGraphProvider { get; }

    public IRelayCommand NewDocumentCommand { get; }
    public IRelayCommand ForceRefreshPreviewCommand { get; }

    public IRelayCommand UndoCommand { get; }

    public IRelayCommand RedoCommand { get; }

    public IAsyncRelayCommand OpenProjectCommand { get; }

    public IAsyncRelayCommand SaveProjectCommand { get; }

    public IAsyncRelayCommand SaveProjectAsCommand { get; }

    public IAsyncRelayCommand ExportVfxCommand { get; }

    public IReadOnlyList<string> ColorSchemes => _themeSettings.ColorSchemes;
    public string AppearanceWarnings => _themeSettings.LoadWarnings;
    public string AppearanceStatus
    {
        get => _appearanceStatus;
        private set
        {
            _appearanceStatusKey = null;
            _appearanceStatusArgs = [];
            SetProperty(ref _appearanceStatus, value);
        }
    }

    public IReadOnlyList<AccentPalette> AccentPalettes { get; } =
        Enum.GetValues<AccentPalette>();

    public string SelectedColorScheme
    {
        get => _themeSettings.Current.ColorScheme;
        set => ChangeThemeSettings(_themeSettings.Current with { ColorScheme = value });
    }

    public AccentPalette SelectedAccentPalette
    {
        get => _themeSettings.Current.AccentPalette;
        set => ChangeThemeSettings(_themeSettings.Current with { AccentPalette = value });
    }

    public MainViewModel(string mcpAccessToken = "", string previewAccessToken = "", string previewFingerprint = "",
        string mcpEndpoint = McpHost.Endpoint, string previewEndpoint = PreviewHost.Endpoint,
        ServerSettingsService? serverSettings = null)
    {
        ServerSettings = serverSettings ?? new ServerSettingsService();
        _ = InspectorPluginService.Shared;
        PreviewAssets = new PreviewAssetStore(PreviewTelemetry);
        GroupAssets = new GroupAssetCatalog(TeamProfiles);
        NodeDefinitions = new NodeDefinitionCatalog(TeamProfiles);
        Log.Add("Application", LocalizationService.Shared.Get("Messages.EditorStarted"));
        McpAccessToken = mcpAccessToken;
        PreviewAccessToken = previewAccessToken;
        PreviewFingerprint = previewFingerprint;
        McpEndpoint = mcpEndpoint;
        PreviewEndpoint = previewEndpoint;
        var initialWorkspace = CreateWorkspace(initializeNew: true);
        var initialDocument = new GraphDocumentViewModel(initialWorkspace)
        {
            Id = Guid.NewGuid().ToString("N"),
            CanClose = false
        };
        ActiveGraphProvider = new ActiveGraphProvider(initialWorkspace);
        ActiveGraphProvider.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActiveGraphProvider.Graph)) OnPropertyChanged(nameof(Graph));
        };
        Extensions = new EditorExtensionHost(ActiveGraphProvider);
        _dockFactory = new DockFactory(ActiveGraphProvider, initialDocument, this);
        Layout = _dockFactory.CreateLayout();
        _dockFactory.InitLayout(Layout);
        Documents.Add(initialDocument);
        _dockFactory.DocumentsDock.PropertyChanged += OnDocumentsDockPropertyChanged;
        SetActiveDocument(initialDocument);

        NewDocumentCommand = new RelayCommand(CreateDocument);
        ForceRefreshPreviewCommand = new RelayCommand(RequestForcePreviewRefresh, () => ActiveDocument is not null);
        UndoCommand = new RelayCommand(
            () => ActiveDocument?.Workspace.History.Undo(),
            () => ActiveDocument?.Workspace.History.CanUndo == true);
        RedoCommand = new RelayCommand(
            () => ActiveDocument?.Workspace.History.Redo(),
            () => ActiveDocument?.Workspace.History.CanRedo == true);
        OpenProjectCommand = new AsyncRelayCommand(OpenProjectsAsync);
        SaveProjectCommand = new AsyncRelayCommand(
            () => SaveActiveProjectAsync(saveAs: false),
            () => ActiveDocument is not null);
        SaveProjectAsCommand = new AsyncRelayCommand(
            () => SaveActiveProjectAsync(saveAs: true),
            () => ActiveDocument is not null);
        ExportVfxCommand = new AsyncRelayCommand(ExportVfxAsync, () => ActiveDocument is not null);
        _themeSettings.SettingsChanged += OnThemeSettingsChanged;
        MessageLocalization.Observe(this, static main => main.RefreshLocalizedStatuses());
    }

    public void SetStorageProvider(IStorageProvider storageProvider)
    {
        _storageProvider = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));
    }

    public void ActivateDocument(GraphDocumentViewModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!Documents.Contains(document))
        {
            throw new InvalidOperationException("The document is not open.");
        }

        _dockFactory.DocumentsDock.ActiveDockable = document;
        SetActiveDocument(document);
    }

    public void ExecuteEdit(IUndoableCommand command)
    {
        Graph.History.Execute(command);
    }

    private void OnDocumentsDockPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentDock.ActiveDockable) &&
            _dockFactory.DocumentsDock.ActiveDockable is GraphDocumentViewModel document)
        {
            SetActiveDocument(document);
        }
    }

    private void SetActiveDocument(GraphDocumentViewModel document)
    {
        if (ActiveDocument is not null)
        {
            ActiveDocument.Workspace.History.StateChanged -= OnHistoryStateChanged;
        }

        ActiveDocument = document;
        ActiveGraphProvider.SetGraph(document.Workspace.ActiveEditingWorkspace);
        document.Workspace.History.StateChanged += OnHistoryStateChanged;
        UndoCommand?.NotifyCanExecuteChanged();
        RedoCommand?.NotifyCanExecuteChanged();
        SaveProjectCommand?.NotifyCanExecuteChanged();
        SaveProjectAsCommand?.NotifyCanExecuteChanged();
        ExportVfxCommand?.NotifyCanExecuteChanged();
        ForceRefreshPreviewCommand?.NotifyCanExecuteChanged();
    }

    public void RequestForcePreviewRefresh()
    {
        if (ActiveDocument is not { } document) return;
        document.Workspace.RequestPreviewRefresh();
        PreviewStatus = LocalizationService.Shared.Get("Procedural.RefreshPreviewRequested");
    }

    private GraphWorkspaceViewModel CreateWorkspace(bool initializeNew = false)
    {
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager(), GroupAssets, NodeDefinitions);
        if (initializeNew)
        {
            workspace.InitializeFromSnapshot(GraphDefaults.CreateEffect());
        }
        workspace.History.ActionPerformed += message => Log.Add("Edit", message);
        workspace.EditingWorkspaceActivated += (_, editing) =>
        {
            if (ReferenceEquals(ActiveDocument?.Workspace, workspace))
                ActiveGraphProvider.SetGraph(editing);
        };
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GraphWorkspaceViewModel.ConnectionStatus) && !workspace.IsRefreshingLocalizedText)
                Log.Add("Graph", workspace.ConnectionStatus);
        };
        return workspace;
    }

    private static string GetFileKey(IStorageFile file) => file.Path.AbsoluteUri;

    public void DisposeStorageFiles()
    {
        foreach (var inspector in _dockFactory.Tools.OfType<NodePropertiesPaneViewModel>())
        {
            inspector.Dispose();
        }
        foreach (var spriteEditor in _dockFactory.Tools.OfType<SpriteEditorPaneViewModel>())
        {
            spriteEditor.Dispose();
        }
        _executionDebugger?.Dispose();
        foreach (var pane in _dockFactory.Tools.OfType<EditorWorkPaneViewModel>())
        {
            pane.ReleaseView();
        }
        DisposeAssetBrowser();
        Extensions.Dispose();
        Playback.Dispose();
        GroupAssets.Dispose();
        NodeDefinitions.Dispose();
        var disposed = new HashSet<IStorageFile>(ReferenceEqualityComparer.Instance);
        foreach (var file in _storageFiles.Values)
        {
            if (disposed.Add(file))
            {
                file.Dispose();
            }
        }

        _storageFiles.Clear();
    }

    private void StoreStorageFile(string fileKey, IStorageFile file)
    {
        if (_storageFiles.TryGetValue(fileKey, out var previous) &&
            !ReferenceEquals(previous, file))
        {
            previous.Dispose();
        }

        _storageFiles[fileKey] = file;
    }

    private void ReleaseStorageFile(string fileKey)
    {
        if (_storageFiles.Remove(fileKey, out var file))
        {
            file.Dispose();
        }
    }

    private static bool IsFileOperationException(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            JsonException;

    private void ChangeThemeSettings(ThemeSettings settings)
    {
        if (settings == _themeSettings.Current)
        {
            return;
        }

        try
        {
            _themeSettings.Apply(settings);
            AppearanceStatus = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetAppearanceStatus("Messages.SaveAppearanceFailed", ex.Message);
            OnPropertyChanged(nameof(SelectedColorScheme));
            OnPropertyChanged(nameof(SelectedAccentPalette));
        }
    }

    private void OnHistoryStateChanged(object? sender, EventArgs e)
    {
        UndoCommand?.NotifyCanExecuteChanged();
        RedoCommand?.NotifyCanExecuteChanged();
    }

    private void OnThemeSettingsChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SelectedColorScheme));
        OnPropertyChanged(nameof(SelectedAccentPalette));
    }

    private static FilePickerFileType ProjectFileType => new(LocalizationService.Shared.Get("Messages.ProjectFileType"))
    {
        Patterns = [$"*{ProjectFileService.FileExtension}"],
        MimeTypes = ["application/json"]
    };

    private static FilePickerFileType VfxBinaryFileType => new(LocalizationService.Shared.Get("Messages.BinaryFileType"))
    {
        Patterns = ["*.lfxp"]
    };

    /// <summary>
    /// Stages writes through a sibling temporary file when a local path is available so failed exports never expose partial output.
    /// </summary>
    internal static async Task WriteStorageFileAtomicallyAsync(IStorageFile file, ReadOnlyMemory<byte> bytes)
    {
        var localPath = file.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(localPath) && Path.IsPathFullyQualified(localPath))
        {
            await WriteLocalFileAtomicallyAsync(localPath, bytes);
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        if (stream.CanSeek)
        {
            stream.SetLength(0);
            stream.Position = 0;
        }
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    internal static async Task WriteLocalFileAtomicallyAsync(string localPath, ReadOnlyMemory<byte> bytes)
    {
        var directory = Path.GetDirectoryName(localPath)
            ?? throw new IOException("Destination directory could not be determined.");
        var temporary = Path.Combine(directory, $".liberafx-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var tempStream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await tempStream.WriteAsync(bytes);
                await tempStream.FlushAsync();
            }

            File.Move(temporary, localPath, overwrite: true);
            temporary = null;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
