using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using LiberaFx.Models.Resources;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed partial class EngineAssetsWindowViewModel
{
    private CancellationTokenSource? _folderRequests;
    private CancellationTokenSource? _projectScopeRequest;
    private EngineProjectIdentity? _engineProject;
    private string _browseFolder = "", _folderStatus = "";
    private EngineFolderEntry? _selectedEngineFolder;
    private int _folderOffset, _folderTotal;
    public ObservableCollection<EngineFolderEntry> EngineFolders { get; } = [];
    public string BrowseEngineFolder { get => _browseFolder; private set => SetProperty(ref _browseFolder, value); }
    public string EngineFolderStatus { get => _folderStatus; private set => SetProperty(ref _folderStatus, value); }
    public EngineFolderEntry? SelectedEngineFolder
    {
        get => _selectedEngineFolder;
        set => SetProperty(ref _selectedEngineFolder, value);
    }
    public IAsyncRelayCommand RefreshEngineFoldersCommand { get; private set; } = null!;
    public IAsyncRelayCommand OpenEngineFolderCommand { get; private set; } = null!;
    public IAsyncRelayCommand UpEngineFolderCommand { get; private set; } = null!;
    public IAsyncRelayCommand NextEngineFolderPageCommand { get; private set; } = null!;
    public IAsyncRelayCommand PreviousEngineFolderPageCommand { get; private set; } = null!;
    public IAsyncRelayCommand UseEngineFolderCommand { get; private set; } = null!;
    public IAsyncRelayCommand AllEngineFoldersCommand { get; private set; } = null!;
    public Task ChooseEngineFolderAsync(string folder) => ApplyEngineFolderAsync(folder);

    private void InitializeEngineFolders()
    {
        RefreshEngineFoldersCommand = new AsyncRelayCommand(() => LoadEngineFoldersAsync(BrowseEngineFolder, 0));
        OpenEngineFolderCommand = new AsyncRelayCommand(() => SelectedEngineFolder is { } selected
            ? LoadEngineFoldersAsync(selected.Path, 0) : Task.CompletedTask);
        UpEngineFolderCommand = new AsyncRelayCommand(() => LoadEngineFoldersAsync(
            BrowseEngineFolder.Length == 0 ? "" : EngineAssetFolder.Parent(BrowseEngineFolder), 0));
        NextEngineFolderPageCommand = new AsyncRelayCommand(() => _folderOffset + EngineFolders.Count < _folderTotal
            ? LoadEngineFoldersAsync(BrowseEngineFolder, _folderOffset + 64) : Task.CompletedTask);
        PreviousEngineFolderPageCommand = new AsyncRelayCommand(() => LoadEngineFoldersAsync(BrowseEngineFolder, Math.Max(0, _folderOffset - 64)));
        UseEngineFolderCommand = new AsyncRelayCommand(() => ApplyEngineFolderAsync(SelectedEngineFolder?.Path ?? BrowseEngineFolder));
        AllEngineFoldersCommand = new AsyncRelayCommand(() => ApplyEngineFolderAsync(""));
    }

    private void ResetEngineFolders(Guid? peer)
    {
        _folderRequests?.Cancel(); _folderRequests?.Dispose(); _folderRequests = null;
        _projectScopeRequest?.Cancel(); _projectScopeRequest = null;
        _engineProject = null;
        EngineFolder = BrowseEngineFolder = EngineFolderStatus = "";
        SelectedEngineFolder = null;
        EngineFolders.Clear(); _folderOffset = _folderTotal = 0;
        if (peer is { } id && _main.PreviewAssets.Supports(id, "resourceIdentity"))
            _ = RestoreEngineFolderAsync(id);
    }

    private async Task RestoreEngineFolderAsync(Guid peer)
    {
        var request = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        _projectScopeRequest = request;
        try
        {
            var state = await _main.PreviewAssets.EngineProjectAsync(peer, cancellation: request.Token);
            if (_disposed || SelectedPeer?.ClientId != peer || request.IsCancellationRequested) return;
            _engineProject = state.Project;
            if (state.Project is { } project && EngineFolder.Length == 0)
                EngineFolder = EngineAssetScopeSettings.Load(project);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ArgumentException or
            InvalidOperationException or System.Text.Json.JsonException)
        { if (!_disposed && SelectedPeer?.ClientId == peer && !request.IsCancellationRequested) EngineFolderStatus = error.Message; }
        finally { if (ReferenceEquals(_projectScopeRequest, request)) _projectScopeRequest = null; request.Dispose(); }
    }

    private async Task LoadEngineFoldersAsync(string folder, int offset)
    {
        if (_disposed || SelectedPeer is not { } peer) return;
        _folderRequests?.Cancel();
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        _folderRequests = request;
        try
        {
            var page = await _main.PreviewAssets.FoldersAsync(folder, offset, peer.ClientId, request.Token);
            if (_disposed || SelectedPeer?.ClientId != peer.ClientId || request.IsCancellationRequested) return;
            BrowseEngineFolder = folder;
            SelectedEngineFolder = null;
            EngineFolders.Clear();
            foreach (var entry in page.Folders) EngineFolders.Add(entry);
            _folderOffset = page.Offset; _folderTotal = page.Total;
            EngineFolderStatus = LocalizationService.Shared.Get("Asset.FolderCount", page.Total);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ArgumentException or InvalidOperationException)
        { if (!_disposed && !request.IsCancellationRequested) EngineFolderStatus = error.Message; }
        finally { if (ReferenceEquals(_folderRequests, request)) _folderRequests = null; }
    }

    private async Task ApplyEngineFolderAsync(string folder)
    {
        if (_disposed) return;
        try
        {
            if (SelectedPeer is { } peer && _main.PreviewAssets.Supports(peer.ClientId, "resourceIdentity"))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var state = await _main.PreviewAssets.EngineProjectAsync(peer.ClientId, cancellation: timeout.Token);
                if (_disposed || SelectedPeer?.ClientId != peer.ClientId) return;
                _engineProject = state.Project;
            }
            EngineFolder = EngineAssetFolder.Normalize(folder);
            if (_engineProject is { } project) EngineAssetScopeSettings.Save(project, EngineFolder);
            EngineFolderStatus = LocalizationService.Shared.Get(_engineProject is null ? "Asset.ScopeSession" : "Asset.ScopeSaved");
            await SearchCommand.ExecuteAsync(null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException)
        { EngineFolderStatus = error.Message; }
    }
}
