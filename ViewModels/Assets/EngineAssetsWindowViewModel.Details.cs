using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using LiberaFx.Models.Graph;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed partial class EngineAssetsWindowViewModel
{
    private async Task LoadPageAsync(int offset)
    {
        if (_disposed) return;
        RefreshPeers();
        if (SelectedPeer is null)
        {
            Status = _explicitPeerId.HasValue
                ? LocalizationService.Shared.Get("Asset.PeerDisconnected")
                : Peers.Count > 1
                ? LocalizationService.Shared.Get("Asset.SelectPeer")
                : LocalizationService.Shared.Get("Asset.NoPeer");
            return;
        }
        CancellationTokenSource? request = null;
        var peerId = SelectedPeer.ClientId;
        try
        {
            request = BeginRequest();
            var cancellation = request.Token;
            Status = LocalizationService.Shared.Get("Asset.Loading");
            var page = await _main.PreviewAssets.CatalogAsync(Query.Trim(), AssetClass,
                offset, 32, peerId, cancellation, EngineFolder);
            cancellation.ThrowIfCancellationRequested();
            if (SelectedPeer?.ClientId != peerId) return;
            SetSelectedAsset(null, loadDetails: false);
            _detailsReady = false;
            Assets.Clear();
            foreach (var asset in page.Assets) Assets.Add(asset);
            RefreshVirtualRoot();
            Offset = page.Offset;
            Total = page.Total;
            OnPropertyChanged(nameof(CanGoPrevious));
            OnPropertyChanged(nameof(CanGoNext));
            PreviousCommand.NotifyCanExecuteChanged();
            NextCommand.NotifyCanExecuteChanged();
            SelectedAsset = Assets.FirstOrDefault();
            Status = LocalizationService.Shared.Get("Asset.Results", Total, Offset + 1,
                Math.Min(Offset + Assets.Count, Total));
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_requests, request) && !_disposed)
                Status = LocalizationService.Shared.Get("Asset.RequestTimedOut");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or
                                           IOException or InvalidDataException or TimeoutException)
        {
            if (ReferenceEquals(_requests, request) && !_disposed)
                Status = LocalizationService.Shared.Get("Asset.RequestFailed", exception.Message);
        }
    }

    private async Task LoadDetailsAsync(EngineAsset selected)
    {
        if (_disposed) return;
        ClearThumbnail();
        LoadMetadata(selected);
        if (SelectedPeer is null) return;
        var peerId = SelectedPeer.ClientId;
        CancellationTokenSource? request = null;
        try
        {
            request = BeginRequest();
            var cancellation = request.Token;
            var detail = await _main.PreviewAssets.DetailsAsync(selected.Path, peerId, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (SelectedAsset?.Path != selected.Path || SelectedPeer?.ClientId != peerId) return;
            _updatingAssetSelection = true;
            try
            {
                var index = Assets.IndexOf(selected);
                if (index >= 0) Assets[index] = detail;
                _detailsReady = true;
                SetSelectedAsset(detail, loadDetails: false);
                RefreshAssignmentState();
            }
            finally { _updatingAssetSelection = false; }
            OnPropertyChanged(nameof(SelectedAsset));
            var thumbnail = _visibleThumbnails?.TryGet(selected.Path, out var cached) == true ? cached! :
                await _main.PreviewAssets.ThumbnailAsync(selected.Path, 256, peerId, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (SelectedAsset?.Path != selected.Path || SelectedPeer?.ClientId != peerId) return;
            _selectedThumbnailData = thumbnail;
            _visibleThumbnails?.Store(thumbnail);
            using var stream = new MemoryStream(thumbnail.Image, writable: false);
            Thumbnail = new Bitmap(stream);
            Status = detail.Materials?.Count > 0
                ? LocalizationService.Shared.Get("Asset.MaterialCount", detail.Materials.Count)
                : LocalizationService.Shared.Get("Asset.Ready");
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_requests, request) && !_disposed)
                Status = LocalizationService.Shared.Get("Asset.RequestTimedOut");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or
                                           IOException or InvalidDataException or TimeoutException or
                                           FormatException)
        {
            if (ReferenceEquals(_requests, request) && !_disposed)
                Status = LocalizationService.Shared.Get("Asset.RequestFailed", exception.Message);
        }
    }

    private void LoadMetadata(EngineAsset asset)
    {
        try
        {
            var metadata = _main.AssetMetadata.Get(asset.Engine, asset.Path);
            Description = metadata.Description;
            SemanticUsage = metadata.SemanticUsage;
            Tags = string.Join(", ", metadata.NormalizedTags);
            Columns = metadata.AtlasColumns.ToString(CultureInfo.InvariantCulture);
            Rows = metadata.AtlasRows.ToString(CultureInfo.InvariantCulture);
            Fps = metadata.FramesPerSecond.ToString("G", CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status = LocalizationService.Shared.Get("Asset.MetadataReadFailed", exception.Message);
        }
    }

    private void SaveMetadata()
    {
        if (SelectedAsset is not { } asset) return;
        if (!int.TryParse(Columns, NumberStyles.Integer, CultureInfo.InvariantCulture, out var columns) ||
            !int.TryParse(Rows, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rows) ||
            !double.TryParse(Fps, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps))
        {
            Status = LocalizationService.Shared.Get("Asset.InvalidAtlas");
            return;
        }
        try
        {
            _main.AssetMetadata.Set(asset.Engine, asset.Path, new AssetMetadata(asset.Engine, asset.Path,
                Description, Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                SemanticUsage, columns, rows, fps));
            Status = LocalizationService.Shared.Get("Asset.MetadataSaved");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
                                           UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Status = LocalizationService.Shared.Get("Asset.MetadataSaveFailed", exception.Message);
        }
    }

    private void AddModel()
    {
        if (SelectedAsset is not { } asset) return;
        try { RenderNodeAuthoring.CreateModel(_main.Graph, asset); Status = LocalizationService.Shared.Get("Asset.ModelAdded"); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or InvalidDataException)
        { Status = LocalizationService.Shared.Get("Asset.AuthoringFailed", exception.Message); }
    }

    private void AddSprite()
    {
        if (SelectedAsset is not { } asset) return;
        try
        {
            if (!TryAtlasValues(out var columns, out var rows, out var fps))
            {
                Status = LocalizationService.Shared.Get("Asset.InvalidAtlas");
                return;
            }
            RenderNodeAuthoring.CreateSprite(_main.Graph, asset, columns, rows, fps);
            Status = LocalizationService.Shared.Get("Asset.SpriteAdded");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                           InvalidDataException or IOException)
        { Status = LocalizationService.Shared.Get("Asset.AuthoringFailed", exception.Message); }
    }

    private bool TryAtlasValues(out int columns, out int rows, out double fps)
    {
        columns = 0;
        rows = 0;
        fps = 0;
        return int.TryParse(Columns, NumberStyles.Integer, CultureInfo.InvariantCulture, out columns) &&
               int.TryParse(Rows, NumberStyles.Integer, CultureInfo.InvariantCulture, out rows) &&
               double.TryParse(Fps, NumberStyles.Float, CultureInfo.InvariantCulture, out fps) &&
               columns is >= 1 and <= 4096 && rows is >= 1 and <= 4096 &&
               double.IsFinite(fps) && fps is >= 0 and <= 10000;
    }

    private void Assign()
    {
        if (!CanAssign) return;
        try
        {
            if (_coordinator?.Request is not null)
            {
                if (!_coordinator.TrySelect(SelectedAsset, SelectedAssetReference, SelectedPeer?.ClientId,
                        SelectedAssignmentTarget, out var error))
                {
                    Status = error;
                    return;
                }
            }
            else if (SelectedAsset is { } asset && SelectedAssignmentTarget is { } target &&
                     _observedGraph?.SelectedNode is { } node)
                ResourceAssignment.Assign(_observedGraph, node, asset, target);
            else return;
            Status = LocalizationService.Shared.Get("Asset.Assigned");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or InvalidDataException)
        { Status = LocalizationService.Shared.Get("Asset.AuthoringFailed", exception.Message); }
    }

    private CancellationTokenSource BeginRequest()
    {
        CancelRequest();
        _requests = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        return _requests;
    }

    private void CancelRequest()
    {
        var request = _requests;
        _requests = null;
        request?.Cancel();
        request?.Dispose();
    }

    private void ClearThumbnail() { _selectedThumbnailData = null; Thumbnail = null; }

    private static string FormatMaterials(IReadOnlyList<EngineMaterial>? materials)
    {
        if (materials is not { Count: > 0 })
            return $"{Environment.NewLine}{Environment.NewLine}{LocalizationService.Shared.Get("Asset.NoMaterials")}";
        var lines = new List<string>();
        foreach (var material in materials.Take(32))
        {
            lines.Add($"{Environment.NewLine}{Environment.NewLine}{LocalizationService.Shared.Get("Asset.Slot", material.Slot)}: {material.Name} — {material.Path}");
            foreach (var parameter in material.Parameters.Take(24))
                lines.Add($"  {parameter.Name} [{parameter.Kind}] = {parameter.Value.GetRawText()}");
            if (material.Parameters.Count > 24) lines.Add("  …");
        }
        if (materials.Count > 32) lines.Add("…");
        return string.Join(Environment.NewLine, lines);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _folderRequests?.Cancel(); _folderRequests?.Dispose();
        _projectScopeRequest?.Cancel();
        _visibleThumbnails?.Dispose();
        _visibleThumbnails = null;
        _physicalLoad?.Cancel(); _physicalLoad?.Dispose(); _physicalLoad = null;
        CancelRequest();
        _main.PropertyChanged -= OnMainPropertyChanged;
        if (_observedDocument is not null) _observedDocument.PropertyChanged -= OnDocumentPropertyChanged;
        if (_observedGraph is not null)
        {
            _observedGraph.PropertyChanged -= OnGraphPropertyChanged;
            _observedGraph.History.StateChanged -= OnGraphHistoryChanged;
        }
        ProjectResources.Dispose();
        References.Dispose();
        ClearThumbnail();
    }
}
