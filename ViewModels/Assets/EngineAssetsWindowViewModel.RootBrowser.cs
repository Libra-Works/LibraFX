using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Project;
using LiberaFx.Services.Settings;

namespace LiberaFx.ViewModels.Assets;

public sealed partial class EngineAssetsWindowViewModel
{
    public void RefreshPhysicalRoot()
    {
        PhysicalLoadTask = RefreshPhysicalRootAsync();
    }

    private async Task RefreshPhysicalRootAsync()
    {
        if (_disposed) return;
        _physicalLoad?.Cancel();
        _physicalLoad?.Dispose();
        var request = _physicalLoad = new CancellationTokenSource();
        var cancellation = request.Token;
        var root = ProjectAssetRoot.FromProjectFileKey(_main.ActiveDocument?.FileKey);
        var oldRoot = _physicalRoot;
        _physicalRoot = root;
        var sameRoot = oldRoot is not null && root is not null &&
            string.Equals(oldRoot, root, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (!sameRoot) { _physicalFolder = string.Empty; _physicalOffset = 0; }
        _physicalHasMore = false;
        NextPhysicalPageCommand?.NotifyCanExecuteChanged();
        PreviousPhysicalPageCommand?.NotifyCanExecuteChanged();
        var physicalSelection = _selectedRootEntry?.Root == AssetBrowserRootKind.PhysicalProject;
        var previousPath = sameRoot && physicalSelection
            ? _selectedRootEntry?.Path : null;
        PhysicalRootEntries.Clear();
        OnPropertyChanged(nameof(PhysicalRootLabel));
        OnPropertyChanged(nameof(PhysicalFolderLabel));
        OnPropertyChanged(nameof(HasPhysicalRoot));
        OnPropertyChanged(nameof(CanNavigatePhysicalUp));
        OnPropertyChanged(nameof(CanOpenSelectedPhysicalFolder));
        CreatePhysicalFolderCommand?.NotifyCanExecuteChanged();
        RefreshPhysicalRootCommand?.NotifyCanExecuteChanged();
        OpenPhysicalFolderCommand?.NotifyCanExecuteChanged();
        NavigatePhysicalUpCommand?.NotifyCanExecuteChanged();
        if (root is null)
        {
            if (_selectedRootEntry?.Root == AssetBrowserRootKind.PhysicalProject)
            {
                _selectedRootEntry = null;
                OnPropertyChanged(nameof(SelectedRootEntry));
                OnPropertyChanged(nameof(SelectedPhysicalRootEntry));
                OnPropertyChanged(nameof(SelectedAssetReference));
                OnPropertyChanged(nameof(HasSelectedAssetReference));
                OnPropertyChanged(nameof(CanOpenSelectedPhysicalFolder));
                OpenPhysicalFolderCommand?.NotifyCanExecuteChanged();
            }
            PhysicalRootStatus = LocalizationService.Shared.Get("Resources.ProjectRootUnavailable");
            return;
        }
        try
        {
            PhysicalRootStatus = LocalizationService.Shared.Get("Resources.PhysicalLoading");
            var folder = _physicalFolder;
            var offset = _physicalOffset;
            var page = await Task.Run(() => ProjectAssetRoot.ListPage(root, folder, offset, 128, cancellation), cancellation);
            if (_disposed || cancellation.IsCancellationRequested || !ReferenceEquals(_physicalLoad, request)) return;
            foreach (var entry in page.Entries)
                PhysicalRootEntries.Add(new(AssetBrowserRootKind.PhysicalProject, entry.Name,
                    entry.RelativePath, entry.IsDirectory, Length: entry.Length, ProjectRoot: root));
            if (physicalSelection)
            {
                _selectedRootEntry = previousPath is null ? null :
                    PhysicalRootEntries.FirstOrDefault(entry => entry.Path == previousPath);
                OnPropertyChanged(nameof(SelectedRootEntry));
                OnPropertyChanged(nameof(SelectedPhysicalRootEntry));
                OnPropertyChanged(nameof(SelectedAssetReference));
                OnPropertyChanged(nameof(HasSelectedAssetReference));
                OnPropertyChanged(nameof(CanOpenSelectedPhysicalFolder));
                OpenPhysicalFolderCommand?.NotifyCanExecuteChanged();
            }
            _physicalHasMore = page.HasMore;
            NextPhysicalPageCommand?.NotifyCanExecuteChanged();
            PreviousPhysicalPageCommand?.NotifyCanExecuteChanged();
            PhysicalRootStatus = LocalizationService.Shared.Get("Resources.PhysicalPage",
                PhysicalRootEntries.Count, offset / 128 + 1);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            if (_disposed || cancellation.IsCancellationRequested) return;
            PhysicalRootStatus = LocalizationService.Shared.Get("Resources.PhysicalRootReadFailed",
                exception.Message);
        }
    }

    private void CreatePhysicalFolder()
    {
        if (_physicalRoot is not { } root) return;
        try
        {
            _ = ProjectAssetRoot.CreateFolder(root, AssetBrowserRootKind.PhysicalProject, PhysicalFolderName,
                _physicalFolder);
            PhysicalFolderName = string.Empty;
            RefreshPhysicalRoot();
            PhysicalRootStatus = LocalizationService.Shared.Get("Resources.PhysicalFolderCreated");
        }
        catch (ProjectFolderExistsException)
        {
            PhysicalRootStatus = LocalizationService.Shared.Get("Resources.PhysicalFolderExists");
        }
        catch (UnauthorizedAccessException)
        {
            PhysicalRootStatus = LocalizationService.Shared.Get("Resources.PhysicalFolderAccessDenied");
        }
        catch (ArgumentException)
        {
            PhysicalRootStatus = LocalizationService.Shared.Get("Resources.InvalidFolderName");
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or
                                           System.Security.SecurityException)
        {
            PhysicalRootStatus = LocalizationService.Shared.Get("Resources.PhysicalFolderFailed",
                exception.Message);
        }
    }

    private void OpenPhysicalFolder()
    {
        if (SelectedRootEntry is not { Root: AssetBrowserRootKind.PhysicalProject, IsDirectory: true } entry)
            return;
        _physicalFolder = entry.Path;
        _physicalOffset = 0;
        SelectedRootEntry = null;
        OnPropertyChanged(nameof(PhysicalFolderLabel));
        RefreshPhysicalRoot();
    }

    private void NavigatePhysicalUp()
    {
        if (!CanNavigatePhysicalUp) return;
        _physicalOffset = 0;
        _physicalFolder = Path.GetDirectoryName(_physicalFolder) ?? string.Empty;
        SelectedRootEntry = null;
        OnPropertyChanged(nameof(PhysicalFolderLabel));
        RefreshPhysicalRoot();
    }

    private void SyncVirtualRootSelection()
    {
        var current = SelectedAsset is { } asset
            ? VirtualRootEntries.FirstOrDefault(entry => entry.EngineAsset?.Path == asset.Path &&
                entry.PeerId == _selectedAssetPeerId)
            : _selectedRootEntry?.Root == AssetBrowserRootKind.VirtualEngine ? null : _selectedRootEntry;
        if (!ReferenceEquals(_selectedRootEntry, current))
        {
            _selectedRootEntry = current;
            OnPropertyChanged(nameof(SelectedRootEntry));
            OnPropertyChanged(nameof(SelectedVirtualRootEntry));
            OnPropertyChanged(nameof(SelectedPhysicalRootEntry));
            OnPropertyChanged(nameof(CanOpenSelectedPhysicalFolder));
            OpenPhysicalFolderCommand?.NotifyCanExecuteChanged();
        }
        OnPropertyChanged(nameof(SelectedAssetReference));
        OnPropertyChanged(nameof(HasSelectedAssetReference));
    }

    private void RefreshVirtualRoot()
    {
        VirtualRootEntries.Clear();
        if (SelectedPeer is { } peer)
            foreach (var asset in Assets)
                VirtualRootEntries.Add(new(AssetBrowserRootKind.VirtualEngine, asset.Name, asset.Path,
                    false, asset, peer.ClientId));
        SyncVirtualRootSelection();
    }

    public async Task RevealExistingAsync(AssetSelectionRequest request, string path, Guid peerId)
    {
        if (_disposed || _coordinator is null || !_coordinator.IsCurrent(request, peerId)) return;
        var definition = request.Node.Definition.Parameters.FirstOrDefault(parameter =>
            parameter.Id == request.ParameterId);
        if (definition is null || request.Node.GetParameterValue(definition).ValueKind !=
            System.Text.Json.JsonValueKind.String ||
            request.Node.GetParameterValue(definition).GetString() != path)
        {
            request.IsStale = true;
            OnCallerChanged();
            return;
        }

        try
        {
            var detail = await _main.PreviewAssets.DetailsAsync(path, peerId);
            if (!_coordinator.IsCurrent(request, peerId) || !ReferenceEquals(_coordinator.Request, request) ||
                request.Node.GetParameterValue(definition).GetString() != path) return;
            if (request.AssetClass is { } assetClass && detail.AssetClass != assetClass)
                throw new InvalidDataException(LocalizationService.Shared.Get("Resources.AssetClassMismatch"));

            var existing = Assets.FirstOrDefault(asset => asset.Path == detail.Path);
            if (existing is null) Assets.Insert(0, detail);
            else Assets[Assets.IndexOf(existing)] = detail;
            _updatingAssetSelection = true;
            try
            {
                _detailsReady = true;
                SetSelectedAsset(detail, loadDetails: false);
            }
            finally { _updatingAssetSelection = false; }
            RefreshVirtualRoot();
            RefreshAssignmentState();
            var thumbnail = await _main.PreviewAssets.ThumbnailAsync(path, 256, peerId);
            if (!_coordinator.IsCurrent(request, peerId) || SelectedAsset?.Path != path) return;
            _selectedThumbnailData = thumbnail;
            _visibleThumbnails?.Store(thumbnail);
            using var stream = new MemoryStream(thumbnail.Image, writable: false);
            Thumbnail = new Bitmap(stream);
            Status = LocalizationService.Shared.Get("Resources.AssetRevealed");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                           InvalidDataException or IOException or TimeoutException or FormatException)
        {
            if (!_disposed && _coordinator.IsCurrent(request, peerId))
                Status = LocalizationService.Shared.Get("Resources.AssetRevealFailed", exception.Message);
        }
    }

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(AssetDetails));
        OnPropertyChanged(nameof(PhysicalRootLabel));
        OnPropertyChanged(nameof(CallerHint));
        OnPropertyChanged(nameof(AssignmentHint));
        RefreshPhysicalRoot();
        RefreshVirtualRoot();
        RefreshAssignmentState();
    }

}
