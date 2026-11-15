using System.ComponentModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed class SpriteEditorViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private string _frame = "0";
    private bool _playing;
    private int _previewFrame;
    private string _status = "";
    private Bitmap? _thumbnail;
    private CancellationTokenSource? _thumbnailRequest;
    private bool _disposed;
    private Guid? _peerId;
    private bool _localPreview;
    private string? _previewAssetPath;

    public EngineAssetsWindowViewModel Catalog { get; }
    public string Frame { get => _frame; set { if (SetProperty(ref _frame, value)) RefreshPreview(); } }
    public bool Playing { get => _playing; set { if (SetProperty(ref _playing, value)) RefreshPreview(); } }
    public int PreviewFrame { get => _previewFrame; private set => SetProperty(ref _previewFrame, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string CatalogStatus => Thumbnail is null ? Catalog.Status : string.Empty;
    public string PreviewSource => Thumbnail is null ? string.Empty :
        LocalizationService.Shared.Get(_localPreview ? "Sprite.LocalPreview" : "Sprite.EnginePreview");
    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            if (ReferenceEquals(_thumbnail, value)) return;
            var old = _thumbnail;
            SetProperty(ref _thumbnail, value);
            old?.Dispose();
            OnPropertyChanged(nameof(CatalogStatus));
            OnPropertyChanged(nameof(PreviewSource));
        }
    }

    public SpriteEditorViewModel(MainViewModel main)
    {
        _main = main;
        Catalog = new EngineAssetsWindowViewModel(main) { AssetClass = "texture" };
        _peerId = Catalog.SelectedPeer?.ClientId;
        Catalog.PropertyChanged += CatalogChanged;
        LoadSelected();
    }

    private void CatalogChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Catalog.SelectedPeer) && _peerId != Catalog.SelectedPeer?.ClientId)
        {
            _peerId = Catalog.SelectedPeer?.ClientId;
            Catalog.SelectedAsset = null;
            Catalog.Assets.Clear();
        }
        if (e.PropertyName == nameof(Catalog.SelectedAsset))
        {
            var path = Catalog.SelectedAsset?.Path;
            if (path != _previewAssetPath)
            {
                _previewAssetPath = path;
                _localPreview = false;
            }
            if (!_localPreview) _ = LoadThumbnailAsync();
        }
        if (e.PropertyName == nameof(Catalog.Status)) OnPropertyChanged(nameof(CatalogStatus));
        if (e.PropertyName is nameof(Catalog.Columns) or nameof(Catalog.Rows) or nameof(Catalog.Fps))
            RefreshPreview();
    }

    public void SetLocalPreview(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (_disposed) { bitmap.Dispose(); return; }
        _thumbnailRequest?.Cancel();
        _localPreview = true;
        Thumbnail = bitmap;
        Status = LocalizationService.Shared.Get("Sprite.LocalPreview");
    }

    public void SetError(string message) => Status = LocalizationService.Shared.Get("Asset.RequestFailed", message);

    private async Task LoadThumbnailAsync()
    {
        _thumbnailRequest?.Cancel();
        _thumbnailRequest?.Dispose();
        _thumbnailRequest = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var cancellation = _thumbnailRequest.Token;
        Thumbnail = null;
        if (Catalog.SelectedAsset is not { } asset || Catalog.SelectedPeer is not { } peer) return;
        try
        {
            var image = await _main.PreviewAssets.ThumbnailAsync(asset.Path, 128, peer.ClientId).WaitAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (_disposed || Catalog.SelectedAsset?.Path != asset.Path || Catalog.SelectedPeer?.ClientId != peer.ClientId) return;
            using var stream = new MemoryStream(image.Image, writable: false);
            Thumbnail = new Bitmap(stream);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException or
                                           InvalidDataException or TimeoutException or FormatException)
        {
            if (!_disposed) Status = LocalizationService.Shared.Get("Asset.RequestFailed", exception.Message);
        }
    }

    public bool TrySettings(out SpriteEditorSettings settings)
    {
        settings = new SpriteEditorSettings(0, 0, 0, 0, Playing);
        if (!int.TryParse(Catalog.Columns, NumberStyles.Integer, CultureInfo.InvariantCulture, out var columns) ||
            !int.TryParse(Catalog.Rows, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rows) ||
            !double.TryParse(Catalog.Fps, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) ||
            !int.TryParse(Frame, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame)) return false;
        settings = new SpriteEditorSettings(columns, rows, fps, frame, Playing);
        try { settings.Validate(); return true; }
        catch (ArgumentException) { return false; }
    }

    public void Tick(double seconds)
    {
        if (TrySettings(out var settings)) PreviewFrame = settings.FrameAt(seconds);
    }

    public void SelectFrame(int frame)
    {
        Frame = frame.ToString(CultureInfo.InvariantCulture);
        Playing = false;
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (TrySettings(out var settings)) PreviewFrame = settings.Frame;
    }

    public void LoadSelected()
    {
        var node = _main.ActiveDocument?.Workspace.SelectedNode;
        if (node?.Definition.TypeName is not ("SpriteDraw" or "SpriteAnimation")) return;
        string Read(string id) => node.GetParameterValue(node.Definition.Parameters.Single(p => p.Id == id)).ToString();
        Catalog.Columns = Read("columns");
        Catalog.Rows = Read("rows");
        Catalog.Fps = Read("fps");
        var frame = node.GetParameterValue(node.Definition.Parameters.Single(p => p.Id == "frame")).GetDouble();
        Frame = Math.Max(0, (int)frame).ToString(CultureInfo.InvariantCulture);
        Playing = frame < 0 && node.GetParameterValue(node.Definition.Parameters.Single(p => p.Id == "playing")).GetBoolean();
        Status = LocalizationService.Shared.Get("Sprite.Loaded", Read("assetPath"));
    }

    public void Apply(bool selected)
    {
        if (_main.ActiveDocument is not { } document)
        {
            Status = LocalizationService.Shared.Get("Sprite.NoDocument");
            return;
        }
        if (Catalog.SelectedAsset is not { } asset)
        {
            Status = LocalizationService.Shared.Get("Sprite.SelectTexture");
            return;
        }
        if (!TrySettings(out var settings))
        {
            Status = LocalizationService.Shared.Get("Sprite.InvalidSettings");
            return;
        }
        try
        {
            var target = selected ? document.Workspace.SelectedNode ??
                throw new InvalidOperationException(LocalizationService.Shared.Get("Sprite.SelectNode")) : null;
            SpriteEditorAuthoring.Apply(document.Workspace, target, asset, settings);
            Status = LocalizationService.Shared.Get("Sprite.Applied");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            Status = LocalizationService.Shared.Get("Asset.AuthoringFailed", exception.Message);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _thumbnailRequest?.Cancel();
        _thumbnailRequest?.Dispose();
        Catalog.PropertyChanged -= CatalogChanged;
        Catalog.Dispose();
        Thumbnail = null;
    }
}
