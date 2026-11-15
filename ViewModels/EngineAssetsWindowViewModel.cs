using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiberaFx.Models.Graph;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed class AssetPeerOption(Guid clientId, string label)
{
    public Guid ClientId { get; } = clientId;
    public string Label { get; } = label;
    public override string ToString() => Label;
}

public sealed class EngineAssetsWindowViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _requests;
    private Bitmap? _thumbnail;
    private EngineAsset? _selectedAsset;
    private string _query = string.Empty;
    private string _assetClass = string.Empty;
    private string _status = string.Empty;
    private string _description = string.Empty;
    private string _semanticUsage = string.Empty;
    private string _tags = string.Empty;
    private string _columns = "1";
    private string _rows = "1";
    private string _fps = "12";
    private int _offset;
    private int _total;
    private AssetPeerOption? _selectedPeer;
    private Guid? _explicitPeerId;
    private GraphWorkspaceViewModel? _observedGraph;

    public ObservableCollection<AssetPeerOption> Peers { get; } = [];
    public ObservableCollection<EngineAsset> Assets { get; } = [];
    public IReadOnlyList<string> AssetClasses { get; } = ["", "model", "texture", "material"];

    public string Query { get => _query; set => SetProperty(ref _query, value); }
    public string AssetClass { get => _assetClass; set => SetProperty(ref _assetClass, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Description { get => _description; set => SetProperty(ref _description, value); }
    public string SemanticUsage { get => _semanticUsage; set => SetProperty(ref _semanticUsage, value); }
    public string Tags { get => _tags; set => SetProperty(ref _tags, value); }
    public string Columns { get => _columns; set => SetProperty(ref _columns, value); }
    public string Rows { get => _rows; set => SetProperty(ref _rows, value); }
    public string Fps { get => _fps; set => SetProperty(ref _fps, value); }
    public int Offset { get => _offset; private set => SetProperty(ref _offset, value); }
    public int Total { get => _total; private set => SetProperty(ref _total, value); }
    public EngineAsset? SelectedAsset
    {
        get => _selectedAsset;
        set => SetSelectedAsset(value, loadDetails: true);
    }

    public AssetPeerOption? SelectedPeer
    {
        get => _selectedPeer;
        set => SetSelectedPeer(value, userChanged: true);
    }

    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            if (ReferenceEquals(_thumbnail, value)) return;
            _thumbnail?.Dispose();
            SetProperty(ref _thumbnail, value);
        }
    }

    public bool HasSelectedAsset => SelectedAsset is not null;
    public string AssetDetails => SelectedAsset is not { } asset
        ? string.Empty
        : $"{LocalizationService.Shared.Get("Asset.Class")}: {asset.AssetClass}" +
          $"{Environment.NewLine}{LocalizationService.Shared.Get("Asset.EngineName")}: {asset.Engine}" +
          (asset.Width > 0 && asset.Height > 0
              ? $"{Environment.NewLine}{LocalizationService.Shared.Get("Asset.Size")}: {asset.Width} × {asset.Height}"
              : "") +
          FormatMaterials(asset.Materials);
    public bool CanAssign => SelectedAsset is not null && _main.ActiveDocument is not null &&
        _main.Graph.SelectedNode is { } node && RenderNodeSchema.IsRenderNode(node.Definition);
    public bool CanGoPrevious => Offset > 0;
    public bool CanGoNext => Offset + Assets.Count < Total;
    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand PreviousCommand { get; }
    public IAsyncRelayCommand NextCommand { get; }
    public IRelayCommand AddModelCommand { get; }
    public IRelayCommand AddSpriteCommand { get; }
    public IRelayCommand AssignCommand { get; }
    public IRelayCommand SaveMetadataCommand { get; }

    public EngineAssetsWindowViewModel(MainViewModel main)
    {
        _main = main;
        SearchCommand = new AsyncRelayCommand(() => LoadPageAsync(0));
        PreviousCommand = new AsyncRelayCommand(() => LoadPageAsync(Math.Max(0, Offset - 32)), () => CanGoPrevious);
        NextCommand = new AsyncRelayCommand(() => LoadPageAsync(Offset + 32), () => CanGoNext);
        AddModelCommand = new RelayCommand(AddModel, () => SelectedAsset is not null);
        AddSpriteCommand = new RelayCommand(AddSprite, () => SelectedAsset is not null);
        AssignCommand = new RelayCommand(Assign, () => CanAssign);
        SaveMetadataCommand = new RelayCommand(SaveMetadata, () => SelectedAsset is not null);
        _main.PropertyChanged += OnMainPropertyChanged;
        AttachGraph();
        RefreshPeers();
    }

    private void SetSelectedAsset(EngineAsset? value, bool loadDetails)
    {
        if (!SetProperty(ref _selectedAsset, value)) return;
        OnPropertyChanged(nameof(HasSelectedAsset));
        OnPropertyChanged(nameof(CanAssign));
        OnPropertyChanged(nameof(AssetDetails));
        SaveMetadataCommand.NotifyCanExecuteChanged();
        AssignCommand.NotifyCanExecuteChanged();
        AddModelCommand.NotifyCanExecuteChanged();
        AddSpriteCommand.NotifyCanExecuteChanged();
        if (loadDetails && value is not null) _ = LoadDetailsAsync(value);
    }

    private void SetSelectedPeer(AssetPeerOption? value, bool userChanged)
    {
        SetProperty(ref _selectedPeer, value);
        if (userChanged) _explicitPeerId = value?.ClientId;
    }

    private void OnMainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MainViewModel.ActiveDocument) or nameof(MainViewModel.Graph))) return;
        AttachGraph();
    }

    private void AttachGraph()
    {
        if (_observedGraph is not null) _observedGraph.PropertyChanged -= OnGraphPropertyChanged;
        _observedGraph = _main.ActiveDocument?.Workspace;
        if (_observedGraph is not null) _observedGraph.PropertyChanged += OnGraphPropertyChanged;
        RefreshAssignmentState();
    }

    private void OnGraphPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphWorkspaceViewModel.SelectedNode)) RefreshAssignmentState();
    }

    private void RefreshAssignmentState()
    {
        OnPropertyChanged(nameof(CanAssign));
        AssignCommand?.NotifyCanExecuteChanged();
    }

    public void RefreshPeers()
    {
        var clients = _main.PreviewTelemetry.Clients()
            .Where(peer => _main.PreviewAssets.Supports(peer.ClientId))
            .Select(peer => new AssetPeerOption(peer.ClientId, $"{peer.Engine ?? "Unreal"} · {peer.ClientId:D}"))
            .ToArray();
        Peers.Clear();
        foreach (var client in clients) Peers.Add(client);
        if (_explicitPeerId is { } explicitId)
            SetSelectedPeer(Peers.FirstOrDefault(peer => peer.ClientId == explicitId), userChanged: false);
        else
            SetSelectedPeer(Peers.Count == 1 ? Peers[0] : null, userChanged: false);
    }

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(AssetDetails));
    }

    private async Task LoadPageAsync(int offset)
    {
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
        try
        {
            using var request = BeginRequest();
            var page = await _main.PreviewAssets.CatalogAsync(Query.Trim(), AssetClass,
                offset, 32, SelectedPeer.ClientId).WaitAsync(request.Token);
            Assets.Clear();
            foreach (var asset in page.Assets) Assets.Add(asset);
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
            Status = LocalizationService.Shared.Get("Asset.RequestTimedOut");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or
                                           IOException or InvalidDataException or TimeoutException)
        {
            Status = LocalizationService.Shared.Get("Asset.RequestFailed", exception.Message);
        }
    }

    private async Task LoadDetailsAsync(EngineAsset selected)
    {
        ClearThumbnail();
        LoadMetadata(selected);
        if (SelectedPeer is null) return;
        try
        {
            using var request = BeginRequest();
            var detail = await _main.PreviewAssets.DetailsAsync(selected.Path, SelectedPeer.ClientId)
                .WaitAsync(request.Token);
            if (SelectedAsset?.Path != selected.Path) return;
            SetSelectedAsset(detail, loadDetails: false);
            var thumbnail = await _main.PreviewAssets.ThumbnailAsync(selected.Path, 256, SelectedPeer.ClientId)
                .WaitAsync(request.Token);
            if (SelectedAsset?.Path != selected.Path) return;
            using var stream = new MemoryStream(thumbnail.Image, writable: false);
            Thumbnail = new Bitmap(stream);
            Status = selected.Materials?.Count > 0
                ? LocalizationService.Shared.Get("Asset.MaterialCount", selected.Materials.Count)
                : LocalizationService.Shared.Get("Asset.Ready");
        }
        catch (OperationCanceledException)
        {
            Status = LocalizationService.Shared.Get("Asset.RequestTimedOut");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or
                                           IOException or InvalidDataException or TimeoutException or
                                           FormatException)
        {
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
        if (SelectedAsset is not { } asset || _main.Graph.SelectedNode is not { } node) return;
        try
        {
            RenderNodeAuthoring.AssignAsset(_main.Graph, node, asset);
            Status = LocalizationService.Shared.Get("Asset.Assigned");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or InvalidDataException)
        { Status = LocalizationService.Shared.Get("Asset.AuthoringFailed", exception.Message); }
    }

    private CancellationTokenSource BeginRequest()
    {
        _requests?.Cancel();
        _requests?.Dispose();
        _requests = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        return _requests;
    }

    private void ClearThumbnail() => Thumbnail = null;

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
        _requests?.Cancel();
        _requests?.Dispose();
        _main.PropertyChanged -= OnMainPropertyChanged;
        if (_observedGraph is not null) _observedGraph.PropertyChanged -= OnGraphPropertyChanged;
        ClearThumbnail();
    }
}
