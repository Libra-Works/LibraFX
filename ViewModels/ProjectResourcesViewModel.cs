using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed record WorkspaceResourceEntry(string Name, string Origin, string? Path = null,
    LocalResourceKind? LocalKind = null, GroupAsset? Group = null)
{
    public string KindLabel => LocalizationService.Shared.Get(Group is not null ? "Resources.Group" :
        LocalKind == LocalResourceKind.Image ? "Resources.Image" : "Resources.Effect");
    public bool PreviewOnly => LocalKind == LocalResourceKind.Image;
}

public sealed class ProjectResourcesViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private IReadOnlyList<WorkspaceResourceEntry> _localEntries = [];
    private CancellationTokenSource? _load;
    private CancellationTokenSource? _preview;
    private WorkspaceResourceEntry? _selectedEntry;
    private Bitmap? _thumbnail;
    private GraphWorkspaceViewModel? _previewGraph;
    private int _sourceIndex;
    private string _folder = Path.Combine(AppContext.BaseDirectory, "Assets", "Samples");
    private string _query = "";
    private string _status = "";
    private bool _recursive;
    private bool _disposed;

    public ObservableCollection<WorkspaceResourceEntry> Entries { get; } = [];
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand UseCommand { get; }
    public Task PreviewTask { get; private set; } = Task.CompletedTask;
    public int SourceIndex
    {
        get => _sourceIndex;
        set
        {
            if (!SetProperty(ref _sourceIndex, value)) return;
            CancelLoad();
            SelectedEntry = null;
            OnPropertyChanged(nameof(IsLocal));
            Filter();
            Status = IsLocal ? "" : _main.GroupAssets.Warnings;
        }
    }
    public bool IsLocal => SourceIndex == 1;
    public string Folder { get => _folder; set { if (SetProperty(ref _folder, value)) { CancelLoad(); _localEntries = []; Filter(); } } }
    public bool Recursive { get => _recursive; set => SetProperty(ref _recursive, value); }
    public string Query { get => _query; set { if (SetProperty(ref _query, value)) Filter(); } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public WorkspaceResourceEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (!SetProperty(ref _selectedEntry, value)) return;
            OnPropertyChanged(nameof(Details));
            OnPropertyChanged(nameof(PreviewOnly));
            OnPropertyChanged(nameof(CanUse));
            OnPropertyChanged(nameof(UseLabel));
            UseCommand.NotifyCanExecuteChanged();
            PreviewTask = LoadPreviewAsync(value);
        }
    }
    public bool PreviewOnly => SelectedEntry?.PreviewOnly == true;
    public bool CanUse => !_disposed && SelectedEntry is { } entry && !entry.PreviewOnly &&
        (entry.Group is null || _main.ActiveDocument is not null);
    public string UseLabel => LocalizationService.Shared.Get(SelectedEntry?.Group is not null
        ? "Resources.InsertGroup" : "Resources.OpenEffect");
    public string Details => SelectedEntry is not { } entry ? "" :
        $"{entry.KindLabel} · {entry.Origin}{Environment.NewLine}" +
        (entry.Group is { } group
            ? $"{group.Id:D} · {LocalizationService.Shared.Get("Resources.Revision", group.Revision)}" +
              $"{Environment.NewLine}{LocalizationService.Shared.Get("Resources.GraphCount", group.Graph.Nodes.Count, group.Graph.Connections.Count)}"
            : entry.Path);
    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        private set { _thumbnail?.Dispose(); SetProperty(ref _thumbnail, value); }
    }
    public GraphWorkspaceViewModel? PreviewGraph
    {
        get => _previewGraph;
        private set { SetProperty(ref _previewGraph, value); OnPropertyChanged(nameof(HasGraphPreview)); }
    }
    public bool HasGraphPreview => PreviewGraph is not null;

    public ProjectResourcesViewModel(MainViewModel main)
    {
        _main = main;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        UseCommand = new AsyncRelayCommand(UseAsync, () => CanUse);
        main.GroupAssets.Changed += OnCatalogChanged;
        main.TeamProfiles.Changed += OnCatalogChanged;
        main.PropertyChanged += OnMainChanged;
        MessageLocalization.Observe(this, static model =>
        {
            if (model._disposed) return;
            model.Filter();
            model.OnPropertyChanged(nameof(Details));
            model.OnPropertyChanged(nameof(UseLabel));
        });
        Filter();
    }

    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        SelectedEntry = null;
        Filter();
    }

    private void OnMainChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.ActiveDocument)) return;
        OnPropertyChanged(nameof(CanUse));
        UseCommand.NotifyCanExecuteChanged();
    }

    private void Filter()
    {
        if (_disposed) return;
        var candidates = IsLocal ? _localEntries : _main.GroupAssets.Assets
            .Select(group => new WorkspaceResourceEntry(group.Name, _main.TeamProfiles.Active.Name, Group: group)).ToArray();
        var filtered = candidates.Where(entry =>
            entry.Name.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase) ||
            entry.KindLabel.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase) ||
            entry.Origin.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        var previous = SelectedEntry;
        Entries.Clear();
        foreach (var entry in filtered) Entries.Add(entry);
        SelectedEntry = filtered.FirstOrDefault(entry => entry == previous);
    }

    private async Task RefreshAsync()
    {
        if (_disposed) return;
        if (!IsLocal)
        {
            _main.GroupAssets.Refresh();
            Filter();
            Status = _main.GroupAssets.Warnings;
            return;
        }
        CancelLoad();
        using var request = new CancellationTokenSource();
        _load = request;
        var cancellation = request.Token;
        var folder = Folder;
        var recursive = Recursive;
        try
        {
            var page = await Task.Run(() => LocalResourceCatalog.Read(folder, recursive, cancellation), cancellation);
            if (_disposed || cancellation.IsCancellationRequested || !IsLocal || Folder != folder) return;
            _localEntries = page.Assets.Select(asset =>
                new WorkspaceResourceEntry(asset.Name, folder, asset.Path, asset.Kind)).ToArray();
            Filter();
            Status = LocalizationService.Shared.Get(page.Truncated ? "Library.Truncated" : "Library.Count", page.Assets.Count);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { if (!cancellation.IsCancellationRequested && !_disposed) Status = ex.Message; }
        finally { if (ReferenceEquals(_load, request)) _load = null; }
    }

    private async Task LoadPreviewAsync(WorkspaceResourceEntry? entry)
    {
        _preview?.Cancel();
        Thumbnail = null;
        PreviewGraph = null;
        if (entry is null || _disposed) return;
        using var request = new CancellationTokenSource();
        _preview = request;
        var cancellation = request.Token;
        try
        {
            var graph = entry.Group?.Graph;
            if (entry.Path is { } path)
            {
                var bytes = await ReadBoundedAsync(path, cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (_disposed || SelectedEntry != entry) return;
                using var stream = new MemoryStream(bytes, writable: false);
                if (entry.LocalKind == LocalResourceKind.Image)
                {
                    if (!CommentImage.IsValidSource(bytes, out _, out _))
                        throw new InvalidDataException(LocalizationService.Shared.Get("Resources.InvalidImage"));
                    Thumbnail = Bitmap.DecodeToWidth(stream, 512);
                    Status = LocalizationService.Shared.Get("Resources.PreviewOnly");
                    return;
                }
                graph = (await ProjectDocumentReader.LoadAsync(stream)).Graph;
            }
            cancellation.ThrowIfCancellationRequested();
            if (graph is not null && !_disposed && SelectedEntry == entry)
            {
                var preview = new GraphWorkspaceViewModel(new UndoRedoManager());
                preview.InitializeFromSnapshot(graph);
                PreviewGraph = preview;
                Status = LocalizationService.Shared.Get("Resources.GraphCount", graph.Nodes.Count, graph.Connections.Count);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                       JsonException or ArgumentException or NotSupportedException)
        { if (!_disposed && !cancellation.IsCancellationRequested && SelectedEntry == entry) Status = ex.Message; }
        finally { if (ReferenceEquals(_preview, request)) _preview = null; }
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken cancellation)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > CommentImage.MaximumSourceBytes)
            throw new InvalidDataException(LocalizationService.Shared.Get("Resources.FileTooLarge"));
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellation);
        if (stream.ReadByte() != -1) throw new IOException(LocalizationService.Shared.Get("Resources.FileChanged"));
        return bytes;
    }

    private async Task UseAsync()
    {
        if (!CanUse || SelectedEntry is not { } entry) return;
        try
        {
            if (entry.Group is { } group)
            {
                if (IsLocal || !_main.GroupAssets.Assets.Contains(group))
                    throw new InvalidOperationException(LocalizationService.Shared.Get("Resources.GroupChanged"));
                _main.Graph.CreateGroupAssetNode(group.Id, 80, 80);
            }
            else if (entry.Path is { } path) await _main.OpenProjectPathAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                       InvalidOperationException or ArgumentException)
        { Status = ex.Message; }
    }

    private void CancelLoad() => _load?.Cancel();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelLoad();
        _preview?.Cancel();
        _main.GroupAssets.Changed -= OnCatalogChanged;
        _main.TeamProfiles.Changed -= OnCatalogChanged;
        _main.PropertyChanged -= OnMainChanged;
        Thumbnail = null;
        PreviewGraph = null;
    }
}
