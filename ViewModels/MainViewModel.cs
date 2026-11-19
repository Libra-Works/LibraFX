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
    public PreviewTelemetryStore PreviewTelemetry { get; } = new();
    public PreviewAssetStore PreviewAssets { get; }
    public AssetMetadataStore AssetMetadata { get; } = new();
    public string PreviewConnectionSummary
    {
        get
        {
            var peers = PreviewTelemetry.Clients();
            return peers.Count == 0 ? LocalizationService.Shared.Get("Connections.Disconnected") :
                string.Join(" | ", peers.Select(peer => $"{peer.Engine ?? "?"} / {peer.Role ?? "?"} / {peer.ViewerName ?? peer.ClientId.ToString("D")[..8]}"));
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
        Extensions = new EditorExtensionHost(ActiveGraphProvider, builtIns: [new GitVersionControlProvider()]);
        _dockFactory = new DockFactory(ActiveGraphProvider, initialDocument, this);
        Layout = _dockFactory.CreateLayout();
        _dockFactory.InitLayout(Layout);
        Documents.Add(initialDocument);
        _dockFactory.DocumentsDock.PropertyChanged += OnDocumentsDockPropertyChanged;
        SetActiveDocument(initialDocument);

        NewDocumentCommand = new RelayCommand(CreateDocument);
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

    private void CreateDocument()
    {
        var document = new GraphDocumentViewModel(CreateWorkspace(initializeNew: true))
        {
            Id = Guid.NewGuid().ToString("N"),
            CanClose = false
        };
        Documents.Add(document);
        _dockFactory.DocumentsDock.AddDocument(document);
        ActivateDocument(document);
        SetFileStatus("Messages.DocumentCreated");
    }

    private async Task OpenProjectsAsync()
    {
        if (_storageProvider is null)
        {
            SetFileStatus("Messages.FilePickerUnavailable");
            return;
        }

        try
        {
            var files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizationService.Shared.Get("Messages.OpenProjectTitle"),
                AllowMultiple = true,
                FileTypeFilter = [ProjectFileType]
            });

            foreach (var file in files)
            {
                await OpenProjectAsync(file);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (IsFileOperationException(ex))
        {
            SetFileStatus("Messages.OpenProjectFailed", ex.Message);
        }
    }

    public async Task OpenProjectPathAsync(string path)
    {
        if (_storageProvider is null) throw new InvalidOperationException("File picker is unavailable.");
        if (!Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".lfxproj", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select a local .lfxproj file.");
        var file = await _storageProvider.TryGetFileFromPathAsync(new Uri(Path.GetFullPath(path)))
            ?? throw new FileNotFoundException("Project file was not found.", path);
        await OpenProjectAsync(file);
    }

    private async Task OpenProjectAsync(IStorageFile file)
    {
        var retained = false;
        try
        {
            var fileKey = GetFileKey(file);
            var existing = Documents.FirstOrDefault(document => document.FileKey == fileKey);
            if (existing is not null)
            {
                ActivateDocument(existing);
                SetFileStatus("Messages.AlreadyOpen", file.Name);
                return;
            }

            await using var stream = await file.OpenReadAsync();
            var project = await _projectFiles.LoadAsync(stream);
            AddLoadedProject(project, file.Name, fileKey);
            StoreStorageFile(fileKey, file);
            retained = true;
            SetFileStatus("Messages.ProjectOpened", file.Name);
        }
        catch (Exception ex) when (IsFileOperationException(ex))
        {
            SetFileStatus("Messages.OpenFileFailed", file.Name, ex.Message);
        }
        finally
        {
            if (!retained)
            {
                file.Dispose();
            }
        }
    }

    public async Task ExportVfxAsync()
    {
        var document = ActiveDocument;
        if (document is null)
        {
            SetFileStatus("Messages.NoDocumentToExport");
            return;
        }
        if (_storageProvider is null)
        {
            SetFileStatus("Messages.FilePickerUnavailable");
            return;
        }

        IStorageFile? file = null;
        try
        {
            var bytes = VfxProgram.Compile(document.Workspace.CreateSnapshot()).ToBinary();
            file = await _storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LocalizationService.Shared.Get("Messages.ExportBinaryTitle"),
                DefaultExtension = "lfxb",
                SuggestedFileName = Path.GetFileNameWithoutExtension(document.FileName) + ".lfxb",
                FileTypeChoices = [VfxBinaryFileType]
            });
            if (file is null) return;
            if (!file.Name.EndsWith(".lfxb", StringComparison.OrdinalIgnoreCase) ||
                Documents.Any(open => open.FileKey is not null &&
                    string.Equals(open.FileKey, GetFileKey(file), StringComparison.OrdinalIgnoreCase)))
                throw new IOException(LocalizationService.Shared.Get("Messages.Error.ChooseBinaryFile"));

            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek)
            {
                stream.SetLength(0);
                stream.Position = 0;
            }
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
            SetFileStatus("Messages.BinaryExported", file.Name);
        }
        catch (Exception ex) when (IsFileOperationException(ex) ||
                                   ex is InvalidDataException or InvalidOperationException or ArgumentException)
        {
            SetFileStatus("Messages.ExportFailed", ex.Message);
        }
        catch (OperationCanceledException) { }
        finally { file?.Dispose(); }
    }

    public async Task<bool> SaveActiveProjectAsync(bool saveAs)
    {
        var document = ActiveDocument;
        if (document is null)
        {
            SetFileStatus("Messages.NoDocumentToSave");
            return false;
        }
        if (!saveAs && document.FileKey is { } key && !_storageFiles.ContainsKey(key) &&
            Uri.TryCreate(key, UriKind.Absolute, out var local) && local.IsFile && !local.IsUnc)
        {
            try
            {
                await SaveLocalProjectAsync(local.LocalPath, overwrite: true);
                return true;
            }
            catch (Exception ex) when (IsFileOperationException(ex) ||
                                       ex is ArgumentException or InvalidOperationException or InvalidDataException)
            {
                SetFileStatus("Messages.SaveProjectFailed", ex.Message);
                return false;
            }
        }
        if (_storageProvider is null)
        {
            SetFileStatus("Messages.FilePickerUnavailable");
            return false;
        }

        IStorageFile? file = null;
        var isCachedFile = false;
        var retainedFile = false;
        try
        {
            if (!saveAs && document.FileKey is not null)
            {
                _storageFiles.TryGetValue(document.FileKey, out file);
                isCachedFile = file is not null;
            }

            file ??= await _storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LocalizationService.Shared.Get("Messages.SaveProjectTitle"),
                DefaultExtension = ProjectFileService.FileExtension.TrimStart('.'),
                SuggestedFileName = document.FileName,
                FileTypeChoices = [ProjectFileType]
            });

            if (file is null)
            {
                return false;
            }

            await using var buffer = new MemoryStream();
            var savedProject = await _projectFiles.SaveAsync(buffer, document.Workspace);
            buffer.Position = 0;
            await using (var stream = await file.OpenWriteAsync())
            {
                if (stream.CanSeek)
                {
                    stream.SetLength(0);
                    stream.Position = 0;
                }

                await buffer.CopyToAsync(stream);
                await stream.FlushAsync();
            }

            var fileKey = GetFileKey(file);
            var oldFileKey = document.FileKey;
            StoreStorageFile(fileKey, file);
            retainedFile = true;
            document.MarkSaved(
                file.Name,
                fileKey,
                savedProject.Graph,
                savedProject.History);
            if (oldFileKey is not null &&
                oldFileKey != fileKey &&
                !Documents.Any(openDocument => openDocument != document && openDocument.FileKey == oldFileKey))
            {
                ReleaseStorageFile(oldFileKey);
            }

            SetFileStatus("Messages.ProjectSaved", file.Name);
            return true;
        }
        catch (Exception ex) when (IsFileOperationException(ex))
        {
            SetFileStatus("Messages.SaveProjectFailed", ex.Message);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (file is not null && !isCachedFile && !retainedFile)
            {
                file.Dispose();
            }
        }
    }

    public async Task<bool> SaveAllDocumentsAsync()
    {
        var previousDocument = ActiveDocument;
        foreach (var document in Documents.Where(document => document.IsDirty).ToArray())
        {
            ActivateDocument(document);
            if (!await SaveActiveProjectAsync(saveAs: false))
            {
                if (previousDocument is not null && Documents.Contains(previousDocument))
                {
                    ActivateDocument(previousDocument);
                }

                return false;
            }
        }

        if (previousDocument is not null && Documents.Contains(previousDocument))
        {
            ActivateDocument(previousDocument);
        }

        var allSaved = Documents.All(document => !document.IsDirty);
        if (!allSaved)
        {
            SetFileStatus("Messages.DocumentsChangedWhileSaving");
        }

        return allSaved;
    }

    public void CloseActiveDocument(bool discardChanges)
    {
        var document = ActiveDocument
            ?? throw new InvalidOperationException("There is no active document.");
        if (document.IsDirty && !discardChanges)
        {
            throw new InvalidOperationException("Save or discard the document before closing it.");
        }

        var index = Documents.IndexOf(document);
        _dockFactory.RemoveDockable(document, collapse: false);
        Documents.Remove(document);
        if (document.FileKey is not null &&
            !Documents.Any(openDocument => openDocument.FileKey == document.FileKey))
        {
            ReleaseStorageFile(document.FileKey);
        }

        if (Documents.Count == 0)
        {
            CreateDocument();
            return;
        }

        ActivateDocument(Documents[Math.Clamp(index, 0, Documents.Count - 1)]);
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
        Patterns = ["*.lfxb"]
    };
}
