using System.ComponentModel;
using System.Globalization;
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed partial class SpriteEditorViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private string _frame = "0";
    private bool _playing;
    private int _previewFrame;
    private string _status = "";
    private Bitmap? _thumbnail;
    private Bitmap? _sourceBitmap;
    private CancellationTokenSource? _thumbnailRequest;
    private bool _disposed;
    private Guid? _peerId;
    private bool _localPreview;
    private string? _previewAssetPath;
    private SpriteAnimationData? _loadedData;
    private SpriteAnimationCell? _selectedCell;
    private SpriteAnimationFrame? _selectedSequenceFrame;
    private string _cellName = string.Empty;
    private string _cellX = "0", _cellY = "0", _cellWidth = "1", _cellHeight = "1";
    private string _sequenceFps = "12", _rangeStart = "0", _rangeEnd = "0";
    private string _durationMilliseconds = "83.333333";
    private bool _loop = true;
    private double _elapsedSeconds;
    private bool _sourceSizeMismatch;
    private int _mismatchExpectedWidth, _mismatchExpectedHeight;
    private int _draftTextureWidth, _draftTextureHeight;
    private bool _drawingNewCell;
    private bool _cellFieldsValid = true;
    private GraphDocumentViewModel? _document;
    private GraphWorkspaceViewModel? _graph;
    private GraphNodeViewModel? _target;
    private bool _contextCurrent;
    private bool _snapToGrid;
    private string _gridWidth = "32", _gridHeight = "32";
    private SpriteCompositionData? _loadedComposition;
    private SpritePartViewModel? _selectedPart;
    private readonly ObservableCollection<SpriteAnimationFrame> _emptyFrames = [];
    private string _keyTime = "0", _positionX = "0", _positionY = "0", _rotation = "0", _scaleX = "1", _scaleY = "1";
    private double _playhead, _playbackStart;

    public EngineAssetsWindowViewModel Catalog { get; }
    public ObservableCollection<SpriteAnimationCell> Cells { get; } = [];
    public ObservableCollection<SpritePartViewModel> Parts { get; } = [];
    public ObservableCollection<SpriteAnimationFrame> Frames => SelectedPart?.Frames ?? _emptyFrames;
    public bool IsComposition => _loadedComposition is not null || Parts.Count > 1 || Parts.Any(part => part.Keys.Count > 0);
    public double Playhead => _playhead;
    public double TotalFrames => Math.Max(1, Parts.Select(part =>
        Math.Max(part.Frames.Sum(frame => frame.DurationFrames), part.Keys.LastOrDefault()?.Frame ?? 0)).DefaultIfEmpty(1).Max());
    public SpritePartViewModel? SelectedPart
    {
        get => _selectedPart;
        set
        {
            if (value is not null && !Parts.Contains(value)) return;
            if (!SetProperty(ref _selectedPart, value)) return;
            _selectedSequenceFrame = null;
            OnPropertyChanged(nameof(Frames));
            OnPropertyChanged(nameof(FrameMaximum));
            ScrubAt(_playhead);
        }
    }
    public string KeyTime
    {
        get => _keyTime;
        set
        {
            if (SetProperty(ref _keyTime, value) && Number(value, out var time) && time is >= 0 and <= 4_096_000_000)
                ScrubAt(time, false);
        }
    }
    public string PositionX { get => _positionX; set => SetProperty(ref _positionX, value); }
    public string PositionY { get => _positionY; set => SetProperty(ref _positionY, value); }
    public string Rotation { get => _rotation; set => SetProperty(ref _rotation, value); }
    public string ScaleX { get => _scaleX; set => SetProperty(ref _scaleX, value); }
    public string ScaleY { get => _scaleY; set => SetProperty(ref _scaleY, value); }
    public int FrameMaximum => Math.Max(0, Frames.Count - 1);
    public bool SnapToGrid { get => _snapToGrid; set => SetProperty(ref _snapToGrid, value); }
    public string GridWidth { get => _gridWidth; set => SetProperty(ref _gridWidth, value); }
    public string GridHeight { get => _gridHeight; set => SetProperty(ref _gridHeight, value); }
    public bool TryGridSize(out int width, out int height)
    {
        height = 0;
        return int.TryParse(GridWidth, NumberStyles.Integer, CultureInfo.InvariantCulture, out width) &&
            int.TryParse(GridHeight, NumberStyles.Integer, CultureInfo.InvariantCulture, out height) &&
            width is >= 1 and <= 8192 && height is >= 1 and <= 8192;
    }

    public double SnapCoordinate(double value, bool horizontal)
    {
        if (SnapToGrid && TryGridSize(out var width, out var height))
        {
            var step = horizontal ? width : height;
            value = Math.Round(value / step, MidpointRounding.AwayFromZero) * step;
        }
        return Math.Clamp(value, 0, horizontal ? TexturePixelWidth : TexturePixelHeight);
    }
    public string Frame
    {
        get => _frame;
        set
        {
            if (!SetProperty(ref _frame, value)) return;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
                index >= 0 && index < Frames.Count)
            {
                _selectedSequenceFrame = Frames[index];
                _playhead = Frames.Take(index).Sum(frame => frame.DurationFrames);
                _keyTime = _playhead.ToString("G8", CultureInfo.InvariantCulture);
                Playing = false;
                RefreshDurationField();
                OnPropertyChanged(nameof(SelectedSequenceFrame));
                OnPropertyChanged(nameof(SelectedFrameInfo));
                OnPropertyChanged(nameof(Playhead));
                OnPropertyChanged(nameof(KeyTime));
                RefreshPoseFields();
            }
            RefreshPreview();
        }
    }
    public bool Playing
    {
        get => _playing;
        set
        {
            if (!SetProperty(ref _playing, value)) return;
            if (value) _playbackStart = Playhead;
            RefreshPreview();
        }
    }
    public int PreviewFrame { get => _previewFrame; private set => SetProperty(ref _previewFrame, value); }
    public Bitmap? SourceBitmap
    {
        get => _sourceBitmap;
        private set
        {
            if (ReferenceEquals(_sourceBitmap, value)) return;
            var old = _sourceBitmap;
            SetProperty(ref _sourceBitmap, value);
            old?.Dispose();
            OnPropertyChanged(nameof(TextureBitmap));
            OnPropertyChanged(nameof(TextureDimensions));
            OnPropertyChanged(nameof(TexturePixelWidth));
            OnPropertyChanged(nameof(TexturePixelHeight));
            OnPropertyChanged(nameof(PreviewSource));
            OnPropertyChanged(nameof(CanDrawCells));
        }
    }
    public Bitmap? TextureBitmap => SourceBitmap ?? Thumbnail;
    public string TextureDimensions => SourceBitmap is { } source
        ? $"{source.PixelSize.Width} × {source.PixelSize.Height} px"
        : _draftTextureWidth > 0 && _draftTextureHeight > 0 ? $"{_draftTextureWidth} × {_draftTextureHeight} px" :
            LocalizationService.Shared.Get("Sprite.TextureSizeUnknown");
    public int TexturePixelWidth => _sourceSizeMismatch ? _mismatchExpectedWidth :
        SourceBitmap?.PixelSize.Width ?? _draftTextureWidth;
    public int TexturePixelHeight => _sourceSizeMismatch ? _mismatchExpectedHeight :
        SourceBitmap?.PixelSize.Height ?? _draftTextureHeight;
    public bool CanDrawCells => SourceBitmap is not null && !_sourceSizeMismatch;
    public bool CanApply => Catalog.SelectedAsset is not null && !_sourceSizeMismatch;
    public bool IsDrawingNewCell => _drawingNewCell;
    public SpriteAnimationCell? SelectedCell
    {
        get => _selectedCell;
        set
        {
            if (!SetProperty(ref _selectedCell, value)) return;
            RefreshCellFields();
            OnPropertyChanged(nameof(SelectedFrameInfo));
        }
    }
    public SpriteAnimationFrame? SelectedSequenceFrame
    {
        get => _selectedSequenceFrame;
        set
        {
            if (!SetProperty(ref _selectedSequenceFrame, value)) return;
            if (value is not null)
            {
                Playing = false;
                var index = Frames.IndexOf(value);
                if (index >= 0)
                {
                    _frame = index.ToString(CultureInfo.InvariantCulture);
                    OnPropertyChanged(nameof(Frame));
                }
                _durationMilliseconds = DurationToMilliseconds(value.DurationFrames).ToString("G8", CultureInfo.InvariantCulture);
                OnPropertyChanged(nameof(DurationMilliseconds));
            }
            RefreshPreview();
            OnPropertyChanged(nameof(SelectedFrameInfo));
        }
    }
    public string CellName { get => _cellName; set { if (SetProperty(ref _cellName, value)) UpdateSelectedCell(); } }
    public string CellX { get => _cellX; set { if (SetProperty(ref _cellX, value)) UpdateSelectedCell(); } }
    public string CellY { get => _cellY; set { if (SetProperty(ref _cellY, value)) UpdateSelectedCell(); } }
    public string CellWidth { get => _cellWidth; set { if (SetProperty(ref _cellWidth, value)) UpdateSelectedCell(); } }
    public string CellHeight { get => _cellHeight; set { if (SetProperty(ref _cellHeight, value)) UpdateSelectedCell(); } }
    public string SequenceFps
    {
        get => _sequenceFps;
        set
        {
            if (!SetProperty(ref _sequenceFps, value)) return;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) &&
                double.IsFinite(fps) && fps > 0)
            {
                RefreshDurationField();
                RefreshPreview();
            }
        }
    }
    public string RangeStart { get => _rangeStart; set { if (SetProperty(ref _rangeStart, value)) RefreshPreview(); } }
    public string RangeEnd { get => _rangeEnd; set { if (SetProperty(ref _rangeEnd, value)) RefreshPreview(); } }
    public string DurationMilliseconds
    {
        get => _durationMilliseconds;
        set
        {
            if (!SetProperty(ref _durationMilliseconds, value) || _selectedSequenceFrame is not { } frame ||
                !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds) ||
                !double.IsFinite(milliseconds) || milliseconds <= 0 ||
                !double.TryParse(SequenceFps, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) ||
                !double.IsFinite(fps) || fps <= 0 ||
                !double.IsFinite(milliseconds * fps / 1000) || milliseconds * fps / 1000 > 1_000_000)
                return;
            ReplaceSelectedFrame(frame with { DurationFrames = milliseconds * fps / 1000 });
        }
    }
    public bool Loop { get => _loop; set { if (SetProperty(ref _loop, value)) RefreshPreview(); } }
    public bool HasSpriteData => _sourceBitmap is not null || _loadedData is not null || Cells.Count > 0 || Frames.Count > 0;
    public string SelectedCellInfo => SelectedCell is { } cell
        ? $"{cell.Name} — ({cell.X}, {cell.Y}) {cell.Width} × {cell.Height} px"
        : LocalizationService.Shared.Get("Sprite.NoCellSelected");
    public string SelectedFrameInfo => SelectedSequenceFrame is { } frame
        ? LocalizationService.Shared.Get("Sprite.FrameDetails", Frames.IndexOf(frame) + 1, CellNameFor(frame.CellId),
            DurationToMilliseconds(frame.DurationFrames).ToString("G6", CultureInfo.InvariantCulture))
        : LocalizationService.Shared.Get("Sprite.NoFrameSelected");
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string CatalogStatus => Thumbnail is null ? Catalog.Status : string.Empty;
    public string PreviewSource => TextureBitmap is null ? string.Empty :
        LocalizationService.Shared.Get(SourceBitmap is not null || _localPreview
            ? "Sprite.LocalPreview" : "Sprite.EnginePreview");
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
            OnPropertyChanged(nameof(TextureBitmap));
        }
    }

    public SpriteEditorViewModel(MainViewModel main, bool loadSelected = true)
    {
        _main = main;
        Catalog = new EngineAssetsWindowViewModel(main) { AssetClass = "texture" };
        AddPart();
        Cells.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSpriteData));
        _peerId = Catalog.SelectedPeer?.ClientId;
        Catalog.PropertyChanged += CatalogChanged;
        _main.PropertyChanged += MainChanged;
        CaptureContext();
        if (loadSelected) LoadSelected();
    }

    private bool _catalogInitialized;

    internal void InitializeCatalog()
    {
        Catalog.RefreshPeers();
        if (_catalogInitialized) return;
        _catalogInitialized = true;
        if (Catalog.SelectedPeer is not null) _ = Catalog.SearchCommand.ExecuteAsync(null);
    }

    internal bool SelectInitialAsset(Guid peerId, EngineAsset asset)
    {
        if (!string.Equals(asset.AssetClass, "texture", StringComparison.OrdinalIgnoreCase)) return false;
        Catalog.RefreshPeers();
        var peer = Catalog.Peers.FirstOrDefault(candidate => candidate.ClientId == peerId);
        if (peer is null) return false;
        Catalog.SelectedPeer = peer;
        Catalog.Assets.Clear();
        Catalog.Assets.Add(asset);
        Catalog.SelectedAsset = asset;
        _catalogInitialized = true;
        return true;
    }

    private void CaptureContext()
    {
        if (_graph is not null)
        {
            _graph.PropertyChanged -= GraphChanged;
            _graph.History.StateChanged -= HistoryChanged;
        }
        _document = _main.ActiveDocument;
        _graph = _document is null ? null : _main.ActiveGraphProvider.Graph;
        _target = _graph?.SelectedNode;
        _contextCurrent = _graph is not null;
        if (_graph is not null)
        {
            _graph.PropertyChanged += GraphChanged;
            _graph.History.StateChanged += HistoryChanged;
        }
    }

    private void MainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ActiveDocument) or nameof(MainViewModel.Graph))
            _contextCurrent = false;
    }

    private void GraphChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphWorkspaceViewModel.IsRootGraph)) _contextCurrent = false;
    }

    private void HistoryChanged(object? sender, EventArgs e) => _contextCurrent = false;

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
            OnPropertyChanged(nameof(CanApply));
            var path = Catalog.SelectedAsset?.Path;
            if (path != _previewAssetPath)
            {
                _previewAssetPath = path;
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
        var hadDraft = HasSpriteData;
        var previousWidth = SourceBitmap?.PixelSize.Width ?? _draftTextureWidth;
        var previousHeight = SourceBitmap?.PixelSize.Height ?? _draftTextureHeight;
        SpriteAnimationData? pending = null;
        if (HasSpriteData && TryBuildData(out var draft, false)) pending = draft;
        _thumbnailRequest?.Cancel();
        _localPreview = true;
        SourceBitmap = bitmap;
        SetSourceSizeMismatch(false);
        var existing = _loadedData ?? pending;
        if (existing is { } loaded)
        {
            if (loaded.TextureWidth != bitmap.PixelSize.Width || loaded.TextureHeight != bitmap.PixelSize.Height)
            {
                SetSourceSizeMismatch(true, loaded.TextureWidth, loaded.TextureHeight);
                Status = LocalizationService.Shared.Get("Sprite.SourceSizeMismatch",
                    loaded.TextureWidth, loaded.TextureHeight, bitmap.PixelSize.Width, bitmap.PixelSize.Height);
            }
        }
        else if (hadDraft && previousWidth > 0 && previousHeight > 0)
        {
            if (previousWidth != bitmap.PixelSize.Width || previousHeight != bitmap.PixelSize.Height)
            {
                SetSourceSizeMismatch(true, previousWidth, previousHeight);
                Status = LocalizationService.Shared.Get("Sprite.SourceSizeMismatch",
                    previousWidth, previousHeight, bitmap.PixelSize.Width, bitmap.PixelSize.Height);
            }
        }
        else
            InitializeFullImageCell(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        if (!_sourceSizeMismatch) Status = LocalizationService.Shared.Get("Sprite.LocalPreview");
    }

    public void ClearSourceImage()
    {
        _localPreview = false;
        var hadMismatch = _sourceSizeMismatch;
        SetSourceSizeMismatch(false);
        SourceBitmap = null;
        if (hadMismatch) RefreshCellFields();
        if (Catalog.SelectedAsset is not null) _ = LoadThumbnailAsync();
        Status = LocalizationService.Shared.Get("Sprite.EnginePreview");
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
        _elapsedSeconds = seconds;
        if (!Playing) return;
        if (IsComposition)
        {
            if (Playing && Number(SequenceFps, out var fps) && fps > 0)
            {
                var frame = _playbackStart + seconds * fps;
                _playhead = Loop ? frame % TotalFrames : Math.Min(frame, TotalFrames);
                _keyTime = _playhead.ToString("G8", CultureInfo.InvariantCulture);
                OnPropertyChanged(nameof(Playhead));
                OnPropertyChanged(nameof(KeyTime));
                RefreshPoseFields();
            }
            PreviewFrame = FrameIndexAt(Frames, Playhead);
            return;
        }
        if (TryBuildData(out var data, false) && data.Frames.Count > 0)
        {
            var sequenceFrame = Playing
                ? data.FrameAt(-1, seconds, true)
                : SelectedSequenceFrame ?? data.Frames[Math.Clamp(ParseInt(Frame, 0), 0, data.Frames.Count - 1)];
            PreviewFrame = data.Frames.IndexOf(sequenceFrame);
        }
        else if (TrySettings(out var settings))
            PreviewFrame = settings.FrameAt(seconds);
        _playhead = Frames.Take(Math.Max(0, PreviewFrame)).Sum(frame => frame.DurationFrames);
        OnPropertyChanged(nameof(Playhead));
    }

    public void SelectFrame(int frame)
    {
        Frame = frame.ToString(CultureInfo.InvariantCulture);
        Playing = false;
        if (frame >= 0 && frame < Frames.Count) SelectedSequenceFrame = Frames[frame];
        _playhead = Frames.Take(Math.Max(0, frame)).Sum(item => item.DurationFrames);
        _keyTime = _playhead.ToString("G8", CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(Playhead));
        OnPropertyChanged(nameof(KeyTime));
        RefreshPoseFields();
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (TryBuildData(out var data, false) && data.Frames.Count > 0)
        {
            var frame = Playing
                ? data.FrameAt(-1, _elapsedSeconds, true)
                : SelectedSequenceFrame ?? data.Frames[Math.Clamp(ParseInt(Frame, 0), 0, data.Frames.Count - 1)];
            PreviewFrame = data.Frames.IndexOf(frame);
        }
        else if (TrySettings(out var settings)) PreviewFrame = settings.Frame;
    }

}
