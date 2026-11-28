using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Compilation;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;
using LiberaFx.ViewModels.Main;

namespace LiberaFx.ViewModels.Assets;

public sealed record AssetReferenceRow(ReferenceResource Resource, int Count, string Resolution)
{
    public string Name => Resource.Name;
    public string Summary => $"{LocalizationService.Shared.Get("References.Kind." + Resource.AssetClass)} · " +
        $"{LocalizationService.Shared.Get("References.UseCount", Count)} · {Resolution}";
}

public sealed record AssetReferenceLocation(AssetReferenceUse Use)
{
    public string Label => $"{Use.Location} · {Use.Parameter}" +
        (Use.Revision is { } revision ? $" · r{revision}" : "") +
        $" · {LocalizationService.Shared.Get(Use.Inferred ? "References.Inferred" : "References.Direct")}";
}

public sealed class AssetReferencesViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly HashSet<GraphDocumentViewModel> _documents = [];
    private ReferenceFolderScan _scan = new([], [], [], false);
    private AssetReferenceIndex _index = new([]);
    private CancellationTokenSource? _request;
    private AssetReferenceRow? _selectedResource;
    private AssetReferenceLocation? _selectedLocation;
    private AssetReferenceLocation? _selectedOutgoing;
    private GraphWorkspaceViewModel? _previewGraph;
    private string? _activeKey;
    private string _query = "";
    private string _folder = "";
    private string _status = "";
    private int _mode;
    private bool _disposed;
    private bool _scheduled;
    private bool _filtering;
    private bool _assetConnected;
    private HashSet<Guid> _groupIds = [];
    private HashSet<string> _fileKeys = [];
    private HashSet<string> _sourceKeys = [];

    public ObservableCollection<string> Folders { get; } = [];
    public ObservableCollection<AssetReferenceRow> Resources { get; } = [];
    public ObservableCollection<AssetReferenceLocation> Incoming { get; } = [];
    public ObservableCollection<AssetReferenceLocation> Outgoing { get; } = [];
    public IRelayCommand AddFolderCommand { get; }
    public IRelayCommand ClearFoldersCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand GoToCommand { get; }
    public IRelayCommand FollowOutgoingCommand { get; }
    public string Folder { get => _folder; set => SetProperty(ref _folder, value); }
    public string Query { get => _query; set { if (SetProperty(ref _query, value)) Filter(); } }
    public int Mode { get => _mode; set { if (SetProperty(ref _mode, value)) Filter(); } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string RegisteredFolders => string.Join(Environment.NewLine, Folders);
    public bool HasFolders => Folders.Count > 0;
    public string Coverage => LocalizationService.Shared.Get("References.Coverage", Folders.Count) +
        Environment.NewLine + LocalizationService.Shared.Get("References.EngineCoverage",
            _main.PreviewTelemetry.Clients().Count(peer => peer.Engine == "Unreal" && peer.Role == "editor")) +
        (_scan.Truncated || _index.Truncated ? Environment.NewLine + LocalizationService.Shared.Get("References.Truncated") : "") +
        (HasDiagnostics ? Environment.NewLine + LocalizationService.Shared.Get("References.PartialErrors") : "");
    public bool HasDiagnostics => Diagnostics.Length > 0;
    public string Diagnostics => string.Join(Environment.NewLine, _scan.Errors.Concat(_index.Errors).Append(_main.GroupAssets.Warnings)
        .Where(message => !string.IsNullOrEmpty(message)).Take(20));
    public string EmptyMessage => LocalizationService.Shared.Get(
        Mode == 0 && _main.ActiveDocument is null ? "References.NoDocument" : "References.NoMatches");
    public bool IsEmpty => Resources.Count == 0;
    public string Details => SelectedResource is not { } row ? "" :
        $"{row.Name}{Environment.NewLine}{row.Summary}{Environment.NewLine}" +
        (row.Resource.GroupId is { } id
            ? LocalizationService.Shared.Get("References.GroupId", id)
            : LocalizationService.Shared.Get("References.IndexKey", row.Resource.Key)) +
        $"{Environment.NewLine}{row.Resource.Binding}{Environment.NewLine}{row.Resource.Scope}" +
        Environment.NewLine + LocalizationService.Shared.Get("References.OutgoingScope");
    public AssetReferenceRow? SelectedResource
    {
        get => _selectedResource;
        set
        {
            if (_filtering || !SetProperty(ref _selectedResource, value)) return;
            UpdateLinks();
        }
    }
    public AssetReferenceLocation? SelectedLocation
    {
        get => _selectedLocation;
        set { SetProperty(ref _selectedLocation, value); GoToCommand.NotifyCanExecuteChanged(); }
    }
    public AssetReferenceLocation? SelectedOutgoing
    {
        get => _selectedOutgoing;
        set { SetProperty(ref _selectedOutgoing, value); FollowOutgoingCommand.NotifyCanExecuteChanged(); }
    }
    public GraphWorkspaceViewModel? PreviewGraph
    {
        get => _previewGraph;
        private set { SetProperty(ref _previewGraph, value); OnPropertyChanged(nameof(HasPreview)); }
    }
    public bool HasPreview => PreviewGraph is not null;

    public void InspectBinding(string binding)
    {
        Mode = 1;
        Query = binding;
        SelectedResource = Resources.FirstOrDefault();
    }

    public AssetReferencesViewModel(MainViewModel main)
    {
        _main = main;
        AddFolderCommand = new RelayCommand(AddFolder);
        ClearFoldersCommand = new RelayCommand(() =>
        {
            CancelScan(); Folders.Clear(); _scan = new([], [], [], false); Rebuild();
        });
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        GoToCommand = new AsyncRelayCommand(GoToAsync, () => SelectedLocation is not null && !_disposed);
        FollowOutgoingCommand = new RelayCommand(() =>
        {
            if (SelectedOutgoing is not { } selected) return;
            var key = selected.Use.Target.Key;
            Mode = 1;
            Query = "";
            SelectedResource = Resources.FirstOrDefault(row => row.Resource.Key == key);
        }, () => SelectedOutgoing is not null);
        main.PropertyChanged += OnMainChanged;
        main.Documents.CollectionChanged += OnDocumentsChanged;
        main.GroupAssets.Changed += OnCatalogChanged;
        main.TeamProfiles.Changed += OnProfileChanged;
        SynchronizeDocuments();
        MessageLocalization.Observe(this, static model => { if (!model._disposed) model.Filter(); });
        Rebuild();
    }

    private void SynchronizeDocuments()
    {
        foreach (var document in _documents.Except(_main.Documents).ToArray())
        {
            document.Workspace.History.StateChanged -= OnHistoryChanged;
            document.PropertyChanged -= OnDocumentChanged;
            _documents.Remove(document);
        }
        foreach (var document in _main.Documents.Except(_documents).ToArray())
        {
            _documents.Add(document);
            document.Workspace.History.StateChanged += OnHistoryChanged;
            document.PropertyChanged += OnDocumentChanged;
        }
    }
    private void OnDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e) { SynchronizeDocuments(); Schedule(); }
    private void OnHistoryChanged(object? sender, EventArgs e) => Schedule();
    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is "Title" or nameof(GraphDocumentViewModel.FileName) or nameof(GraphDocumentViewModel.FileKey)) Schedule(); }
    private void OnCatalogChanged(object? sender, EventArgs e) => Schedule();
    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(MainViewModel.ActiveDocument) or nameof(MainViewModel.Graph)) Schedule(); }
    private void OnProfileChanged(object? sender, EventArgs e)
    {
        CancelScan();
        Folders.Clear();
        _scan = new([], [], [], false);
        Schedule();
    }
    private void Schedule()
    {
        if (_disposed || _scheduled) return;
        _scheduled = true;
        Dispatcher.UIThread.Post(() => { _scheduled = false; if (!_disposed) Rebuild(); });
    }
    private void Rebuild()
    {
        if (_disposed) return;
        var sources = _scan.Sources.ToDictionary(source => source.Resource.Key);
        foreach (var document in _main.Documents)
        {
            var path = LocalPath(document.FileKey);
            var resource = path is not null ? AssetReferenceIndex.File(path, project: true) :
                new ReferenceResource("document:" + document.Id, ReferenceResourceKind.Project, document.FileName, "project");
            sources[resource.Key] = new(resource, document.Workspace.CreateSnapshot(), document.Id, path);
            if (ReferenceEquals(document, _main.ActiveDocument)) _activeKey = resource.Key;
        }
        if (_main.ActiveDocument is null) _activeKey = null;
        foreach (var group in _main.GroupAssets.Assets)
        {
            var resource = AssetReferenceIndex.Group(group.Id, group.Name);
            sources[resource.Key] = new(resource, group.Graph);
        }
        _index = new(sources.Values.OrderBy(source =>
            source.Resource.Key == _activeKey ? 0 : source.DocumentId is not null ? 1 :
            source.Resource.Kind == ReferenceResourceKind.Group ? 2 : 3).ToArray(), _scan.Files);
        _groupIds = _main.GroupAssets.Assets.Select(group => group.Id).ToHashSet();
        _fileKeys = _scan.Files.Select(file => file.Key).ToHashSet();
        _sourceKeys = _index.Sources.Select(source => source.Resource.Key).ToHashSet();
        Filter();
    }
    private void Filter()
    {
        if (_disposed) return;
        var key = SelectedResource?.Resource.Key;
        var activeUses = Mode == 0 ? _index.Uses.Where(use => use.Source.Resource.Key == _activeKey).ToArray() : _index.Uses;
        var counts = activeUses.GroupBy(use => use.Target.Key).ToDictionary(group => group.Key, group => group.Count());
        _assetConnected = _main.PreviewTelemetry.Clients().Any(peer =>
            peer.Engine == "Unreal" && peer.Role == "editor" && peer.Capabilities.Contains("assets"));
        var query = Query.Trim();
        var rows = _index.Resources.Where(resource => (Mode != 0 || counts.ContainsKey(resource.Key)) &&
                $"{resource.Name} {resource.AssetClass} {LocalizationService.Shared.Get("References.Kind." + resource.AssetClass)} {resource.Binding} {resource.Key} {resource.Scope}"
                    .Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(resource => new AssetReferenceRow(resource, counts.GetValueOrDefault(resource.Key), Resolution(resource)))
            .OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        _filtering = true;
        try { Resources.Clear(); foreach (var row in rows) Resources.Add(row); }
        finally { _filtering = false; }
        _selectedResource = rows.FirstOrDefault(row => row.Resource.Key == key);
        OnPropertyChanged(nameof(SelectedResource));
        UpdateLinks();
        OnPropertyChanged(nameof(Coverage));
        OnPropertyChanged(nameof(RegisteredFolders));
        OnPropertyChanged(nameof(HasFolders));
        OnPropertyChanged(nameof(Diagnostics));
        OnPropertyChanged(nameof(HasDiagnostics));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyMessage));
    }
    private string Resolution(ReferenceResource resource) => LocalizationService.Shared.Get(resource.Kind switch
    {
        ReferenceResourceKind.Engine => _assetConnected
            ? "References.Legacy" : "References.Disconnected",
        ReferenceResourceKind.Unknown => "References.Unknown",
        ReferenceResourceKind.Unassigned => "References.Unassigned",
        ReferenceResourceKind.Builtin => "References.Builtin",
        ReferenceResourceKind.Group => resource.GroupId is { } id && _groupIds.Contains(id)
            ? "References.GroupAvailable" : _main.GroupAssets.HasLibraryDirectory && _main.GroupAssets.Warnings.Length == 0
                ? "References.GroupMissing" : "References.GroupUnverified",
        ReferenceResourceKind.LocalFile => _fileKeys.Contains(resource.Key)
            ? "References.IndexedFile" : "References.UnverifiedFile",
        _ => _sourceKeys.Contains(resource.Key)
            ? "References.Project" : "References.UnreadableProject"
    });
    private void UpdateLinks()
    {
        var previous = SelectedLocation?.Use.Identity;
        Incoming.Clear(); Outgoing.Clear();
        SelectedLocation = SelectedOutgoing = null;
        PreviewGraph = null;
        if (SelectedResource is { } row)
        {
            foreach (var use in _index.Incoming(row.Resource).Concat(_index.Candidates(row.Resource)))
                Incoming.Add(new(use));
            foreach (var use in _index.Outgoing(row.Resource)) Outgoing.Add(new(use));
            SelectedLocation = Incoming.FirstOrDefault(location => location.Use.Identity == previous);
        }
        OnPropertyChanged(nameof(Details));
    }
    private void AddFolder()
    {
        try
        {
            if (Folders.Count >= 16) { Status = LocalizationService.Shared.Get("References.FolderLimit"); return; }
            var path = Path.GetFullPath(Folder);
            if (!Path.IsPathFullyQualified(Folder) || !Directory.Exists(path))
                throw new ArgumentException(LocalizationService.Shared.Get("References.ChooseFolder"));
            if (!Folders.Contains(path, StringComparer.OrdinalIgnoreCase)) Folders.Add(path);
            Status = LocalizationService.Shared.Get("References.RefreshRequired");
            OnPropertyChanged(nameof(Coverage));
            OnPropertyChanged(nameof(RegisteredFolders));
            OnPropertyChanged(nameof(HasFolders));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { Status = ex.Message; }
    }
    private async Task RefreshAsync()
    {
        if (_disposed) return;
        CancelScan();
        using var request = new CancellationTokenSource();
        _request = request;
        var roots = Folders.ToArray();
        try
        {
            var scan = await Task.Run(() => AssetReferenceFolderScanner.ReadAsync(roots, request.Token), request.Token);
            if (_disposed || request.IsCancellationRequested) return;
            _scan = scan;
            _main.GroupAssets.Refresh();
            Rebuild();
            Status = LocalizationService.Shared.Get("References.Refreshed");
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_request, request)) _request = null; }
    }
    private async Task GoToAsync()
    {
        if (SelectedLocation?.Use is not { } use) return;
        if (!_index.Uses.Any(current => current.Identity == use.Identity)) { Rebuild(); return; }
        var document = _main.Documents.FirstOrDefault(candidate => candidate.Id == use.Source.DocumentId);
        if (document is null && use.Source.LocalPath is { } path)
        {
            await _main.OpenProjectPathAsync(path);
            document = _main.Documents.FirstOrDefault(candidate => LocalPath(candidate.FileKey) is { } opened &&
                AssetReferenceIndex.FileKey(opened) == AssetReferenceIndex.FileKey(path));
        }
        if (_disposed) return;
        if (document is not null)
        {
            _main.ActivateDocument(document);
            document.Workspace.NavigateToNode(use.ParentPath, use.NodeId);
            if (document.Workspace.SelectedNode?.Id != use.NodeId)
                Status = LocalizationService.Shared.Get("References.SourceUnavailable");
        }
        else if (use.Source.Resource.GroupId is not null)
        {
            var preview = new GraphWorkspaceViewModel(new UndoRedoManager());
            preview.InitializeFromSnapshot(use.Source.Graph);
            preview.NavigateToNode(use.ParentPath, use.NodeId);
            PreviewGraph = preview;
        }
        else Status = LocalizationService.Shared.Get("References.SourceUnavailable");
    }
    private static string? LocalPath(string? fileKey) =>
        Uri.TryCreate(fileKey, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null;
    private void CancelScan() { _request?.Cancel(); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelScan();
        _main.PropertyChanged -= OnMainChanged;
        _main.Documents.CollectionChanged -= OnDocumentsChanged;
        _main.GroupAssets.Changed -= OnCatalogChanged;
        _main.TeamProfiles.Changed -= OnProfileChanged;
        foreach (var document in _documents)
        {
            document.Workspace.History.StateChanged -= OnHistoryChanged;
            document.PropertyChanged -= OnDocumentChanged;
        }
        _documents.Clear();
        PreviewGraph = null;
    }
}
