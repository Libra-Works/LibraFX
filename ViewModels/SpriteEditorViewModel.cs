using System.ComponentModel;
using System.Globalization;
using System.Collections.ObjectModel;
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

    public void LoadSelected()
    {
        var node = _main.ActiveDocument is null ? null : _main.ActiveGraphProvider.Graph.SelectedNode;
        if (node?.Definition.TypeName == "SpriteComposition")
        {
            try
            {
                var parameter = node.Definition.Parameters.Single(item => item.Id == "compositionData");
                var composition = SpriteCompositionData.Deserialize(node.GetParameterValue(parameter))
                    ?? throw new InvalidDataException("Missing sprite composition.");
                _loadedComposition = composition;
                _loadedData = null;
                _draftTextureWidth = composition.TextureWidth;
                _draftTextureHeight = composition.TextureHeight;
                Cells.Clear();
                foreach (var cell in composition.Cells) Cells.Add(cell);
                SelectedCell = Cells.FirstOrDefault();
                ClearParts();
                foreach (var part in composition.Tracks) AttachPart(new SpritePartViewModel(part));
                SelectedPart = Parts.FirstOrDefault();
                SequenceFps = composition.Fps.ToString("G17", CultureInfo.InvariantCulture);
                Loop = composition.Loop;
                SetSourceSizeMismatch(SourceBitmap is { } bitmap &&
                    (bitmap.PixelSize.Width != composition.TextureWidth || bitmap.PixelSize.Height != composition.TextureHeight),
                    composition.TextureWidth, composition.TextureHeight);
                ScrubAt(0);
                OnPropertyChanged(nameof(TextureDimensions));
                OnPropertyChanged(nameof(TexturePixelWidth));
                OnPropertyChanged(nameof(TexturePixelHeight));
                OnPropertyChanged(nameof(HasSpriteData));
                OnPropertyChanged(nameof(IsComposition));
                OnPropertyChanged(nameof(TotalFrames));
                Status = LocalizationService.Shared.Get("Sprite.Loaded", node.GetParameterValue(
                    node.Definition.Parameters.Single(item => item.Id == "assetPath")).GetString() ?? "");
                CaptureContext();
            }
            catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException or ArgumentException)
            {
                Status = LocalizationService.Shared.Get("Sprite.InvalidSavedData", exception.Message);
            }
            return;
        }
        if (node?.Definition.TypeName is not ("SpriteDraw" or "SpriteAnimation")) return;
        _loadedComposition = null;
        ClearParts();
        AddPart();
        string Read(string id) => node.GetParameterValue(node.Definition.Parameters.Single(p => p.Id == id)).ToString();
        Catalog.Columns = Read("columns");
        Catalog.Rows = Read("rows");
        Catalog.Fps = Read("fps");
        var frame = node.GetParameterValue(node.Definition.Parameters.Single(p => p.Id == "frame")).GetDouble();
        Frame = Math.Max(0, (int)frame).ToString(CultureInfo.InvariantCulture);
        var savedPlaying = frame < 0 && node.GetParameterValue(node.Definition.Parameters.Single(p => p.Id == "playing")).GetBoolean();
        SetSourceSizeMismatch(false);
        try
        {
            var dataParameter = node.Definition.Parameters.FirstOrDefault(parameter => parameter.Id == "spriteData");
            _loadedData = dataParameter is null ? null :
                SpriteAnimationData.Deserialize(node.GetParameterValue(dataParameter));
            if (_loadedData is { } data)
            {
                ReplaceDraft(data);
                SetSourceSizeMismatch(SourceBitmap is { } source &&
                    (source.PixelSize.Width != data.TextureWidth || source.PixelSize.Height != data.TextureHeight),
                    data.TextureWidth, data.TextureHeight);
                if (frame >= 0) SelectFrame(Math.Clamp((int)frame, 0, data.Frames.Count - 1));
            }
            else
            {
                if (SourceBitmap is { } bitmap)
                    InitializeFullImageCell(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
                else
                {
                    Cells.Clear();
                    Frames.Clear();
                    _draftTextureWidth = 0;
                    _draftTextureHeight = 0;
                    SelectedCell = null;
                    SelectedSequenceFrame = null;
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException or ArgumentException)
        {
            _loadedData = null;
            OnPropertyChanged(nameof(TextureDimensions));
            OnPropertyChanged(nameof(TexturePixelWidth));
            OnPropertyChanged(nameof(TexturePixelHeight));
            Status = LocalizationService.Shared.Get("Sprite.InvalidSavedData", exception.Message);
            return;
        }
        Playing = savedPlaying;
        OnPropertyChanged(nameof(TextureDimensions));
        OnPropertyChanged(nameof(TexturePixelWidth));
        OnPropertyChanged(nameof(TexturePixelHeight));
        OnPropertyChanged(nameof(HasSpriteData));
        Status = _sourceSizeMismatch
            ? LocalizationService.Shared.Get("Sprite.SourceSizeMismatch",
                _loadedData!.TextureWidth, _loadedData.TextureHeight, SourceBitmap!.PixelSize.Width, SourceBitmap.PixelSize.Height)
            : LocalizationService.Shared.Get("Sprite.Loaded", Read("assetPath"));
        CaptureContext();
    }

    public void Apply(bool selected)
    {
        if (_disposed) return;
        if (_main.ActiveDocument is not { } document)
        {
            Status = LocalizationService.Shared.Get("Sprite.NoDocument");
            return;
        }
        if (!_contextCurrent || !ReferenceEquals(document, _document) || _graph is null ||
            !ReferenceEquals(_main.ActiveGraphProvider.Graph, _graph) ||
            (selected && (_target is null || !ReferenceEquals(_graph.SelectedNode, _target))))
        {
            Status = LocalizationService.Shared.Get("Sprite.ContextChanged");
            return;
        }
        if (Catalog.SelectedAsset is not { } asset)
        {
            Status = LocalizationService.Shared.Get("Sprite.SelectTexture");
            return;
        }
        if (_sourceSizeMismatch)
        {
            Status = LocalizationService.Shared.Get("Sprite.SourceSizeMismatch",
                _mismatchExpectedWidth, _mismatchExpectedHeight,
                SourceBitmap?.PixelSize.Width ?? 0, SourceBitmap?.PixelSize.Height ?? 0);
            return;
        }
        if (IsComposition)
        {
            if (!TryBuildComposition(out var composition))
            {
                Status = LocalizationService.Shared.Get("Sprite.InvalidComposition");
                return;
            }
            if (selected && _target?.Definition.TypeName != "SpriteComposition")
            {
                Status = LocalizationService.Shared.Get("Sprite.CreateCompositionFirst");
                return;
            }
            try
            {
                SpriteCompositionAuthoring.Apply(_graph, selected ? _target : null, asset, composition);
                CaptureContext();
                Status = LocalizationService.Shared.Get("Sprite.Applied");
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or InvalidDataException)
            {
                Status = LocalizationService.Shared.Get("Asset.AuthoringFailed", exception.Message);
            }
            return;
        }
        SpriteAnimationData? spriteData = null;
        SpriteEditorSettings settings;
        if (HasSpriteData)
        {
            if (!TryBuildData(out spriteData, true) || spriteData is null)
            {
                Status = LocalizationService.Shared.Get("Sprite.InvalidSequence");
                return;
            }
            var selectedFrame = SelectedSequenceFrame is null ? spriteData.StartFrame :
                Math.Clamp(Frames.IndexOf(SelectedSequenceFrame), spriteData.StartFrame, spriteData.EndFrame);
            settings = new SpriteEditorSettings(Math.Max(1, spriteData.Frames.Count), 1,
                spriteData.Fps, Playing ? 0 : selectedFrame, Playing);
        }
        else if (!TrySettings(out settings))
        {
            Status = LocalizationService.Shared.Get("Sprite.InvalidSettings");
            return;
        }
        try
        {
            var target = selected ? _graph.SelectedNode ??
                throw new InvalidOperationException(LocalizationService.Shared.Get("Sprite.SelectNode")) : null;
            SpriteEditorAuthoring.Apply(_graph, target, asset, settings, spriteData);
            CaptureContext();
            Status = LocalizationService.Shared.Get("Sprite.Applied");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            Status = LocalizationService.Shared.Get("Asset.AuthoringFailed", exception.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _main.PropertyChanged -= MainChanged;
        if (_graph is not null)
        {
            _graph.PropertyChanged -= GraphChanged;
            _graph.History.StateChanged -= HistoryChanged;
        }
        _thumbnailRequest?.Cancel();
        _thumbnailRequest?.Dispose();
        Catalog.PropertyChanged -= CatalogChanged;
        Catalog.Dispose();
        ClearParts();
        Thumbnail = null;
        SourceBitmap = null;
    }

    public void AddCell()
    {
        if (_sourceSizeMismatch)
        {
            Status = LocalizationService.Shared.Get("Sprite.SourceSizeMismatch",
                _mismatchExpectedWidth, _mismatchExpectedHeight,
                SourceBitmap?.PixelSize.Width ?? 0, SourceBitmap?.PixelSize.Height ?? 0);
            return;
        }
        if (SourceBitmap is null)
        {
            Status = LocalizationService.Shared.Get("Sprite.OpenSourceBeforeDrawing");
            return;
        }
        _drawingNewCell = true;
        OnPropertyChanged(nameof(IsDrawingNewCell));
        Status = LocalizationService.Shared.Get("Sprite.DragToAddCell");
    }

    public void AddFrame()
    {
        if (SelectedCell is null && Cells.Count > 0) SelectedCell = Cells[0];
        if (SelectedCell is not { } selected)
        {
            Status = LocalizationService.Shared.Get("Sprite.CreateCellFirst");
            return;
        }
        if (Frames.Count >= 4096) { Status = LocalizationService.Shared.Get("Sprite.FrameLimit"); return; }
        var frame = new SpriteAnimationFrame(Guid.NewGuid(), selected.Id, 1);
        Frames.Add(frame);
        SelectedSequenceFrame = frame;
        RangeEnd = Math.Max(0, Frames.Count - 1).ToString(CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(HasSpriteData));
    }

    public void DuplicateFrame()
    {
        if (SelectedSequenceFrame is not { } selected) return;
        if (Frames.Count >= 4096) { Status = LocalizationService.Shared.Get("Sprite.FrameLimit"); return; }
        var index = Frames.IndexOf(selected);
        var copy = selected with { Id = Guid.NewGuid() };
        Frames.Insert(index + 1, copy);
        SelectedSequenceFrame = copy;
        RangeEnd = (Frames.Count - 1).ToString(CultureInfo.InvariantCulture);
    }

    public void RemoveFrame()
    {
        if (SelectedSequenceFrame is not { } selected) return;
        var index = Frames.IndexOf(selected);
        Frames.RemoveAt(index);
        SelectedSequenceFrame = Frames.Count == 0 ? null : Frames[Math.Min(index, Frames.Count - 1)];
        RangeStart = Math.Clamp(ParseInt(RangeStart, 0), 0, Math.Max(0, Frames.Count - 1)).ToString(CultureInfo.InvariantCulture);
        RangeEnd = Math.Clamp(ParseInt(RangeEnd, 0), 0, Math.Max(0, Frames.Count - 1)).ToString(CultureInfo.InvariantCulture);
    }

    public void MoveFrame(int offset)
    {
        if (SelectedSequenceFrame is not { } selected) return;
        var index = Frames.IndexOf(selected);
        var target = Math.Clamp(index + offset, 0, Frames.Count - 1);
        MoveFrameTo(target);
    }

    public void MoveFrameTo(int target)
    {
        if (SelectedSequenceFrame is not { } selected || Frames.Count == 0) return;
        var index = Frames.IndexOf(selected);
        if (index < 0) return;
        target = Math.Clamp(target, 0, Frames.Count - 1);
        if (target == index) return;
        Frames.Move(index, target);
        _frame = target.ToString(CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(Frame));
        OnPropertyChanged(nameof(SelectedFrameInfo));
        ScrubAt(Frames.Take(target).Sum(frame => frame.DurationFrames));
        RefreshPreview();
    }

    public void SetFrameDuration(double duration)
    {
        if (SelectedSequenceFrame is not { } frame || !double.IsFinite(duration) ||
            duration <= 0 || duration > 1_000_000) return;
        ReplaceSelectedFrame(frame with { DurationFrames = duration });
        RefreshDurationField();
        RefreshPreview();
    }

    public bool GenerateGridFlipbook()
    {
        if (!CanDrawCells || !TryGridSize(out var width, out var height))
        {
            Status = LocalizationService.Shared.Get("Sprite.InvalidGrid");
            return false;
        }
        var columns = (TexturePixelWidth + width - 1) / width;
        var rows = (TexturePixelHeight + height - 1) / height;
        if ((long)columns * rows > 512)
        {
            Status = LocalizationService.Shared.Get("Sprite.CellLimit");
            return false;
        }
        var cells = Cells.ToList();
        var sequence = new List<SpriteAnimationFrame>();
        var names = cells.Select(cell => cell.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var y = 0; y < TexturePixelHeight; y += height)
            for (var x = 0; x < TexturePixelWidth; x += width)
            {
                var cellWidth = Math.Min(width, TexturePixelWidth - x);
                var cellHeight = Math.Min(height, TexturePixelHeight - y);
                var cell = cells.FirstOrDefault(item =>
                    item.X == x && item.Y == y && item.Width == cellWidth && item.Height == cellHeight);
                if (cell is null)
                {
                    var number = cells.Count + 1;
                    while (!names.Add($"Cell {number}")) number++;
                    cell = new(Guid.NewGuid(), $"Cell {number}", x, y, cellWidth, cellHeight);
                    cells.Add(cell);
                }
                var existing = Frames.FirstOrDefault(frame => frame.CellId == cell.Id);
                sequence.Add(existing ?? new SpriteAnimationFrame(Guid.NewGuid(), cell.Id, 1));
            }
        if (cells.Count > 512)
        {
            Status = LocalizationService.Shared.Get("Sprite.CellLimit");
            return false;
        }
        Playing = false;
        foreach (var cell in cells.Skip(Cells.Count)) Cells.Add(cell);
        Frames.Clear();
        foreach (var frame in sequence) Frames.Add(frame);
        RangeStart = "0";
        RangeEnd = (Frames.Count - 1).ToString(CultureInfo.InvariantCulture);
        SelectedCell = cells.First(cell => cell.Id == sequence[0].CellId);
        SelectFrame(0);
        SnapToGrid = true;
        Status = LocalizationService.Shared.Get("Sprite.GridGenerated", sequence.Count);
        return true;
    }

    public void RemoveCell()
    {
        if (SelectedCell is not { } selected) return;
        foreach (var part in Parts)
            foreach (var frame in part.Frames.Where(frame => frame.CellId == selected.Id).ToArray())
                part.Frames.Remove(frame);
        Cells.Remove(selected);
        SelectedCell = Cells.FirstOrDefault();
        SelectedSequenceFrame = Frames.FirstOrDefault();
        RangeStart = Math.Clamp(ParseInt(RangeStart, 0), 0, Math.Max(0, Frames.Count - 1)).ToString(CultureInfo.InvariantCulture);
        RangeEnd = Math.Clamp(ParseInt(RangeEnd, 0), 0, Math.Max(0, Frames.Count - 1)).ToString(CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(HasSpriteData));
    }

    public void SetDraggedRectangle(int x, int y, int width, int height, bool create)
    {
        if (_sourceSizeMismatch || SourceBitmap is not { } bitmap || x < 0 || y < 0 || width <= 0 || height <= 0 ||
            (long)x + width > bitmap.PixelSize.Width || (long)y + height > bitmap.PixelSize.Height)
            return;
        if (create || SelectedCell is null)
        {
            if (Cells.Count >= 512) { Status = LocalizationService.Shared.Get("Sprite.CellLimit"); return; }
            var index = Cells.Count + 1;
            while (Cells.Any(cell => string.Equals(cell.Name.Trim(), $"Cell {index}", StringComparison.OrdinalIgnoreCase))) index++;
            var cell = new SpriteAnimationCell(Guid.NewGuid(), $"Cell {index}", x, y, width, height);
            Cells.Add(cell);
            SelectedCell = cell;
            if (Frames.Count == 0) AddFrame();
            _drawingNewCell = false;
            OnPropertyChanged(nameof(IsDrawingNewCell));
        }
        else
        {
            var cell = SelectedCell with { X = x, Y = y, Width = width, Height = height };
            ReplaceSelectedCell(cell);
        }
        OnPropertyChanged(nameof(HasSpriteData));
        Status = LocalizationService.Shared.Get("Sprite.RectangleSelected", x, y, width, height);
    }

    public bool TryBuildData(out SpriteAnimationData data, bool validate = true)
    {
        data = null!;
        if (_sourceSizeMismatch || !_cellFieldsValid) return false;
        var width = SourceBitmap?.PixelSize.Width ?? _draftTextureWidth;
        var height = SourceBitmap?.PixelSize.Height ?? _draftTextureHeight;
        if (width <= 0 || height <= 0 ||
            !double.TryParse(SequenceFps, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) ||
            !int.TryParse(RangeStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) ||
            !int.TryParse(RangeEnd, NumberStyles.Integer, CultureInfo.InvariantCulture, out var end))
            return false;
        data = new SpriteAnimationData
        {
            TextureWidth = width,
            TextureHeight = height,
            Cells = Cells.ToList(),
            Frames = Frames.ToList(),
            Fps = fps,
            StartFrame = start,
            EndFrame = end,
            Loop = Loop
        };
        if (!validate) return true;
        try { data.Validate(); return true; }
        catch (InvalidDataException) { return false; }
    }

    private void InitializeFullImageCell(int width, int height)
    {
        _draftTextureWidth = width;
        _draftTextureHeight = height;
        Cells.Clear();
        Frames.Clear();
        var cell = new SpriteAnimationCell(Guid.NewGuid(), "Cell 1", 0, 0, width, height);
        Cells.Add(cell);
        SelectedCell = cell;
        var frame = new SpriteAnimationFrame(Guid.NewGuid(), cell.Id, 1);
        Frames.Add(frame);
        SelectedSequenceFrame = frame;
        _rangeStart = "0";
        _rangeEnd = "0";
        OnPropertyChanged(nameof(RangeStart));
        OnPropertyChanged(nameof(RangeEnd));
        OnPropertyChanged(nameof(HasSpriteData));
    }

    public void AddPart()
    {
        if (Parts.Count >= 32) { Status = LocalizationService.Shared.Get("Sprite.PartLimit"); return; }
        var index = 1;
        string name;
        do { name = LocalizationService.Shared.Get("Sprite.PartDefault", index++); }
        while (Parts.Any(part => string.Equals(part.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)));
        var data = new SpriteCompositionTrack { Id = Guid.NewGuid(), Name = name };
        var selected = SelectedCell ?? Cells.FirstOrDefault();
        if (selected is not null) data.Frames.Add(new(Guid.NewGuid(), selected.Id, 1));
        var part = new SpritePartViewModel(data);
        AttachPart(part);
        SelectedPart = part;
    }

    private void AttachPart(SpritePartViewModel part)
    {
        part.PropertyChanged += PartChanged;
        part.Frames.CollectionChanged += PartContentsChanged;
        part.Keys.CollectionChanged += PartContentsChanged;
        Parts.Add(part);
        OnPropertyChanged(nameof(IsComposition));
    }

    private void PartChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(nameof(Parts));
    private void PartContentsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(FrameMaximum));
        OnPropertyChanged(nameof(HasSpriteData));
        OnPropertyChanged(nameof(IsComposition));
        OnPropertyChanged(nameof(TotalFrames));
    }

    private void DetachPart(SpritePartViewModel part)
    {
        part.PropertyChanged -= PartChanged;
        part.Frames.CollectionChanged -= PartContentsChanged;
        part.Keys.CollectionChanged -= PartContentsChanged;
        Parts.Remove(part);
    }

    private void ClearParts()
    {
        foreach (var part in Parts.ToArray()) DetachPart(part);
        _selectedPart = null;
    }

    public void RemovePart()
    {
        if (SelectedPart is not { } part || Parts.Count <= 1) return;
        DetachPart(part);
        SelectedPart = Parts.First();
        OnPropertyChanged(nameof(IsComposition));
    }

    public void MovePart(int offset)
    {
        if (SelectedPart is not { } part) return;
        var index = Parts.IndexOf(part);
        Parts.Move(index, Math.Clamp(index + offset, 0, Parts.Count - 1));
        SelectedPart = part;
        OnPropertyChanged(nameof(Parts));
    }

    public bool TryBuildComposition(out SpriteCompositionData data, bool validate = true)
    {
        data = new SpriteCompositionData
        {
            TextureWidth = TexturePixelWidth, TextureHeight = TexturePixelHeight,
            Fps = Number(SequenceFps, out var fps) ? fps : 0, Loop = Loop,
            Cells = Cells.ToList(), Tracks = Parts.Select(part => part.Snapshot()).ToList()
        };
        if (!validate) return !_sourceSizeMismatch;
        if (_sourceSizeMismatch || !_cellFieldsValid) return false;
        try { data.Validate(); return true; }
        catch (InvalidDataException) { return false; }
    }

    public void ScrubAt(double frame) => ScrubAt(frame, true);
    private void ScrubAt(double frame, bool updateText)
    {
        if (!double.IsFinite(frame) || frame is < 0 or > 4_096_000_000) return;
        Playing = false;
        _playhead = frame;
        if (updateText)
        {
            _keyTime = frame.ToString("G8", CultureInfo.InvariantCulture);
            OnPropertyChanged(nameof(KeyTime));
        }
        var index = FrameIndexAt(Frames, frame);
        _selectedSequenceFrame = index >= 0 ? Frames[index] : null;
        _frame = Math.Max(0, index).ToString(CultureInfo.InvariantCulture);
        RefreshDurationField();
        OnPropertyChanged(nameof(SelectedSequenceFrame));
        OnPropertyChanged(nameof(SelectedFrameInfo));
        OnPropertyChanged(nameof(Frame));
        OnPropertyChanged(nameof(Playhead));
        PreviewFrame = Math.Max(0, index);
        RefreshPoseFields();
    }

    public SpriteAnimationCell? CellAt(SpritePartViewModel part, double frame)
    {
        var index = FrameIndexAt(part.Frames, frame);
        return index < 0 ? null : Cells.FirstOrDefault(cell => cell.Id == part.Frames[index].CellId);
    }

    private static int FrameIndexAt(IReadOnlyList<SpriteAnimationFrame> frames, double frame)
    {
        if (frames.Count == 0) return -1;
        for (var index = 0; index < frames.Count; index++)
        {
            if (frame < frames[index].DurationFrames) return index;
            frame -= frames[index].DurationFrames;
        }
        return frames.Count - 1;
    }

    public void SetTransformKey()
    {
        Playing = false;
        if (SelectedPart is not { } part || !Number(PositionX, out var x) || !Number(PositionY, out var y) ||
            !Number(Rotation, out var rotation) || !Number(ScaleX, out var scaleX) || !Number(ScaleY, out var scaleY) ||
            Math.Abs(x) > 1_000_000 || Math.Abs(y) > 1_000_000 || Math.Abs(rotation) > 1_000_000 ||
            Math.Abs(scaleX) > 10000 || Math.Abs(scaleY) > 10000 || scaleX == 0 || scaleY == 0)
        {
            Status = LocalizationService.Shared.Get("Sprite.InvalidPose");
            return;
        }
        var existing = part.Keys.FirstOrDefault(key => key.Frame == Playhead);
        var key = new SpriteTransformKey(existing?.Id ?? Guid.NewGuid(), Playhead, x, y, rotation, scaleX, scaleY);
        if (existing is not null) part.Keys[part.Keys.IndexOf(existing)] = key;
        else
        {
            if (part.Keys.Count >= 256) { Status = LocalizationService.Shared.Get("Sprite.KeyLimit"); return; }
            var index = 0;
            while (index < part.Keys.Count && part.Keys[index].Frame < Playhead) index++;
            part.Keys.Insert(index, key);
        }
        OnPropertyChanged(nameof(Playhead));
    }

    public void RemoveTransformKey()
    {
        if (SelectedPart?.Keys.FirstOrDefault(key => key.Frame == Playhead) is not { } key) return;
        SelectedPart.Keys.Remove(key);
        RefreshPoseFields();
    }

    public void MoveTransformKey(Guid id, double frame)
    {
        if (SelectedPart is not { } part || !double.IsFinite(frame) || frame is < 0 or > 4_096_000_000 ||
            part.Keys.FirstOrDefault(key => key.Id == id) is not { } key ||
            part.Keys.Any(other => other.Id != id && other.Frame == frame)) return;
        part.Keys.Remove(key);
        var updated = key with { Frame = frame };
        var index = 0;
        while (index < part.Keys.Count && part.Keys[index].Frame < frame) index++;
        part.Keys.Insert(index, updated);
        ScrubAt(frame);
    }

    public void MoveSelectedPart(double x, double y)
    {
        PositionX = x.ToString("G8", CultureInfo.InvariantCulture);
        PositionY = y.ToString("G8", CultureInfo.InvariantCulture);
        SetTransformKey();
    }

    private void RefreshPoseFields()
    {
        var pose = SelectedPart?.PoseAt(Playhead) ?? new SpritePose(0, 0, 0, 1, 1);
        PositionX = pose.X.ToString("G8", CultureInfo.InvariantCulture);
        PositionY = pose.Y.ToString("G8", CultureInfo.InvariantCulture);
        Rotation = pose.RotationDegrees.ToString("G8", CultureInfo.InvariantCulture);
        ScaleX = pose.ScaleX.ToString("G8", CultureInfo.InvariantCulture);
        ScaleY = pose.ScaleY.ToString("G8", CultureInfo.InvariantCulture);
    }

    private static bool Number(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && double.IsFinite(result);

    private void ReplaceDraft(SpriteAnimationData data)
    {
        _draftTextureWidth = data.TextureWidth;
        _draftTextureHeight = data.TextureHeight;
        Cells.Clear();
        foreach (var cell in data.Cells) Cells.Add(cell);
        Frames.Clear();
        foreach (var frame in data.Frames) Frames.Add(frame);
        _sequenceFps = data.Fps.ToString("G17", CultureInfo.InvariantCulture);
        _rangeStart = data.StartFrame.ToString(CultureInfo.InvariantCulture);
        _rangeEnd = data.EndFrame.ToString(CultureInfo.InvariantCulture);
        _loop = data.Loop;
        OnPropertyChanged(nameof(SequenceFps));
        OnPropertyChanged(nameof(RangeStart));
        OnPropertyChanged(nameof(RangeEnd));
        OnPropertyChanged(nameof(Loop));
        SelectedCell = Cells.FirstOrDefault();
        SelectedSequenceFrame = Frames.FirstOrDefault();
        OnPropertyChanged(nameof(HasSpriteData));
    }

    private void RefreshCellFields()
    {
        var cell = SelectedCell;
        _cellName = cell?.Name ?? string.Empty;
        _cellX = (cell?.X ?? 0).ToString(CultureInfo.InvariantCulture);
        _cellY = (cell?.Y ?? 0).ToString(CultureInfo.InvariantCulture);
        _cellWidth = (cell?.Width ?? 1).ToString(CultureInfo.InvariantCulture);
        _cellHeight = (cell?.Height ?? 1).ToString(CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(CellName));
        OnPropertyChanged(nameof(CellX));
        OnPropertyChanged(nameof(CellY));
        OnPropertyChanged(nameof(CellWidth));
        OnPropertyChanged(nameof(CellHeight));
        OnPropertyChanged(nameof(SelectedCellInfo));
        _cellFieldsValid = true;
    }

    private void UpdateSelectedCell()
    {
        if (_sourceSizeMismatch)
        {
            Status = LocalizationService.Shared.Get("Sprite.SourceSizeMismatch",
                _mismatchExpectedWidth, _mismatchExpectedHeight,
                SourceBitmap?.PixelSize.Width ?? 0, SourceBitmap?.PixelSize.Height ?? 0);
            return;
        }
        if (SelectedCell is not { } cell) { _cellFieldsValid = true; return; }
        if (!int.TryParse(CellX, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ||
            !int.TryParse(CellY, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ||
            !int.TryParse(CellWidth, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(CellHeight, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) ||
            string.IsNullOrWhiteSpace(CellName) || CellName.Trim().Length > 80 ||
            Cells.Any(other => other.Id != cell.Id &&
                string.Equals(other.Name.Trim(), CellName.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            _cellFieldsValid = false;
            Status = LocalizationService.Shared.Get("Sprite.InvalidCellInput");
            return;
        }
        var updated = cell with { Name = CellName.Trim(), X = x, Y = y, Width = width, Height = height };
        var sourceWidth = SourceBitmap?.PixelSize.Width ?? _draftTextureWidth;
        var sourceHeight = SourceBitmap?.PixelSize.Height ?? _draftTextureHeight;
        if (x < 0 || y < 0 || width <= 0 || height <= 0 ||
            (long)x + width > sourceWidth || (long)y + height > sourceHeight)
        {
            _cellFieldsValid = false;
            Status = LocalizationService.Shared.Get("Sprite.InvalidCellInput");
            return;
        }
        _cellFieldsValid = true;
        ReplaceSelectedCell(updated);
    }

    private void ReplaceSelectedCell(SpriteAnimationCell updated)
    {
        var index = Cells.IndexOf(SelectedCell!);
        if (index < 0) return;
        Cells[index] = updated;
        _selectedCell = updated;
        OnPropertyChanged(nameof(SelectedCell));
        OnPropertyChanged(nameof(SelectedCellInfo));
        OnPropertyChanged(nameof(SelectedFrameInfo));
        RefreshCellFields();
    }

    private void ReplaceSelectedFrame(SpriteAnimationFrame updated)
    {
        var index = Frames.IndexOf(SelectedSequenceFrame!);
        if (index < 0) return;
        Frames[index] = updated;
        _selectedSequenceFrame = updated;
        OnPropertyChanged(nameof(SelectedSequenceFrame));
        OnPropertyChanged(nameof(SelectedFrameInfo));
    }

    private string CellNameFor(Guid id) => Cells.FirstOrDefault(cell => cell.Id == id)?.Name ?? "?";
    private double DurationToMilliseconds(double ticks) =>
        double.TryParse(SequenceFps, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) && fps > 0
            ? ticks / fps * 1000 : 0;
    private void RefreshDurationField()
    {
        if (_selectedSequenceFrame is { } frame)
            _durationMilliseconds = DurationToMilliseconds(frame.DurationFrames).ToString("G8", CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(DurationMilliseconds));
    }
    private static int ParseInt(string value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private void SetSourceSizeMismatch(bool value, int expectedWidth = 0, int expectedHeight = 0)
    {
        if (_sourceSizeMismatch == value)
        {
            if (value)
            {
                _mismatchExpectedWidth = expectedWidth;
                _mismatchExpectedHeight = expectedHeight;
            }
            return;
        }
        _sourceSizeMismatch = value;
        _mismatchExpectedWidth = value ? expectedWidth : 0;
        _mismatchExpectedHeight = value ? expectedHeight : 0;
        OnPropertyChanged(nameof(TexturePixelWidth));
        OnPropertyChanged(nameof(TexturePixelHeight));
        OnPropertyChanged(nameof(CanDrawCells));
        OnPropertyChanged(nameof(CanApply));
    }
}
