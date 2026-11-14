using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using LiberaFx.Models.Appearance;
using LiberaFx.Models.Commands;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly DockFactory _dockFactory;
    private readonly ThemeSettingsService _themeSettings = new();
    private readonly ProjectFileService _projectFiles = new();
    private readonly Dictionary<string, IStorageFile> _storageFiles = new(StringComparer.Ordinal);
    private IStorageProvider? _storageProvider;
    private GraphDocumentViewModel? _activeDocument;
    private string _mcpStatus = "Starting";
    private string _fileStatus = "Ready";
    private string _appearanceStatus = string.Empty;
    private string _previewStatus = "Starting";
    private int _previewClientCount;

    public EditorLog Log { get; } = new();
    public string McpEndpoint => McpHost.Endpoint;
    public string PreviewEndpoint => PreviewHost.Endpoint;
    public string PreviewAccessToken { get; }
    public string PreviewFingerprint { get; }
    public string PreviewStatus
    {
        get => _previewStatus;
        set
        {
            if (SetProperty(ref _previewStatus, value)) Log.Add("Preview", value);
        }
    }
    public int PreviewClientCount
    {
        get => _previewClientCount;
        set
        {
            if (SetProperty(ref _previewClientCount, value)) Log.Add("Preview", $"Connected clients: {value}");
        }
    }

    public ShortcutService Shortcuts => ShortcutService.Shared;

    public string McpAccessToken { get; }

    public string McpStatus
    {
        get => _mcpStatus;
        set
        {
            if (SetProperty(ref _mcpStatus, value)) Log.Add("MCP", value);
        }
    }

    public string FileStatus
    {
        get => _fileStatus;
        private set
        {
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

    public GraphWorkspaceViewModel Graph => ActiveDocument?.Workspace
        ?? throw new InvalidOperationException("There is no active document.");

    public ActiveGraphProvider ActiveGraphProvider { get; }

    public IRelayCommand NewDocumentCommand { get; }

    public IRelayCommand UndoCommand { get; }

    public IRelayCommand RedoCommand { get; }

    public IAsyncRelayCommand OpenProjectCommand { get; }

    public IAsyncRelayCommand SaveProjectCommand { get; }

    public IAsyncRelayCommand SaveProjectAsCommand { get; }

    public IReadOnlyList<string> ColorSchemes => _themeSettings.ColorSchemes;
    public string AppearanceWarnings => _themeSettings.LoadWarnings;
    public string AppearanceStatus { get => _appearanceStatus; private set => SetProperty(ref _appearanceStatus, value); }

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

    public MainViewModel(string mcpAccessToken = "", string previewAccessToken = "", string previewFingerprint = "")
    {
        Log.Add("Application", "Editor started.");
        McpAccessToken = mcpAccessToken;
        PreviewAccessToken = previewAccessToken;
        PreviewFingerprint = previewFingerprint;
        var initialWorkspace = CreateWorkspace();
        var initialDocument = new GraphDocumentViewModel(initialWorkspace)
        {
            Id = Guid.NewGuid().ToString("N"),
            CanClose = false
        };
        ActiveGraphProvider = new ActiveGraphProvider(initialWorkspace);
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
        _themeSettings.SettingsChanged += OnThemeSettingsChanged;
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
        var document = new GraphDocumentViewModel(CreateWorkspace())
        {
            Id = Guid.NewGuid().ToString("N"),
            CanClose = false
        };
        Documents.Add(document);
        _dockFactory.DocumentsDock.AddDocument(document);
        ActivateDocument(document);
        FileStatus = "Created a new untitled document.";
    }

    private async Task OpenProjectsAsync()
    {
        if (_storageProvider is null)
        {
            FileStatus = "The platform file picker is not available.";
            return;
        }

        try
        {
            var files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open LIBERA FX project",
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
            FileStatus = $"Could not open project: {ex.Message}";
        }
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
                FileStatus = $"Already open: {file.Name}";
                return;
            }

            await using var stream = await file.OpenReadAsync();
            var project = await _projectFiles.LoadAsync(stream);
            var workspace = CreateWorkspace();
            workspace.InitializeFromSnapshot(project.Graph);
            if (project.History is not null)
            {
                workspace.History.LoadSnapshot(project.History);
            }
            var document = new GraphDocumentViewModel(workspace)
            {
                Id = Guid.NewGuid().ToString("N"),
                CanClose = false
            };
            document.MarkSaved(file.Name, fileKey, project.Graph, project.History);
            Documents.Add(document);
            _dockFactory.DocumentsDock.AddDocument(document);
            ActivateDocument(document);
            StoreStorageFile(fileKey, file);
            retained = true;
            FileStatus = $"Opened {file.Name}.";
        }
        catch (Exception ex) when (IsFileOperationException(ex))
        {
            FileStatus = $"Could not open '{file.Name}': {ex.Message}";
        }
        finally
        {
            if (!retained)
            {
                file.Dispose();
            }
        }
    }

    public async Task<bool> SaveActiveProjectAsync(bool saveAs)
    {
        var document = ActiveDocument;
        if (document is null)
        {
            FileStatus = "There is no active document to save.";
            return false;
        }

        if (_storageProvider is null)
        {
            FileStatus = "The platform file picker is not available.";
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
                Title = "Save LIBERA FX project",
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

            FileStatus = $"Saved {file.Name}.";
            return true;
        }
        catch (Exception ex) when (IsFileOperationException(ex))
        {
            FileStatus = $"Could not save project: {ex.Message}";
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
            FileStatus = "Some documents changed while saving. Save them again before closing.";
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
        ActiveGraphProvider.SetGraph(document.Workspace);
        document.Workspace.History.StateChanged += OnHistoryStateChanged;
        UndoCommand?.NotifyCanExecuteChanged();
        RedoCommand?.NotifyCanExecuteChanged();
        SaveProjectCommand?.NotifyCanExecuteChanged();
        SaveProjectAsCommand?.NotifyCanExecuteChanged();
    }

    private GraphWorkspaceViewModel CreateWorkspace()
    {
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        workspace.History.ActionPerformed += message => Log.Add("Edit", message);
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GraphWorkspaceViewModel.ConnectionStatus))
                Log.Add("Graph", workspace.ConnectionStatus);
        };
        return workspace;
    }

    private static string GetFileKey(IStorageFile file) => file.Path.AbsoluteUri;

    public void DisposeStorageFiles()
    {
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
            AppearanceStatus = $"Could not save appearance settings: {ex.Message}";
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

    private static readonly FilePickerFileType ProjectFileType = new("LIBERA FX Project")
    {
        Patterns = [$"*{ProjectFileService.FileExtension}"],
        MimeTypes = ["application/json"]
    };
}
