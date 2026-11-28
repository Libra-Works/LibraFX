using System.ComponentModel;
using System.Globalization;
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed partial class SpriteEditorViewModel
{
    public void LoadSelected()
    {
        var node = _main.ActiveDocument is null ? null : _main.ActiveGraphProvider.Graph.SelectedNode;
        var particle = ParticleSpriteAuthoring.Supports(node);
        (SpriteAnimationData? Animation,SpriteCompositionData? Composition,string Path) particleData=(null,null,"");
        if (particle)
        {
            try { particleData=ParticleSpriteAuthoring.Read(node!); }
            catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException or ArgumentException)
            {
                Status=LocalizationService.Shared.Get("Sprite.InvalidSavedData",error.Message);
                return;
            }
        }
        if (node?.Definition.TypeName == "SpriteComposition" || particleData.Composition is not null)
        {
            try
            {
                var composition = particle ? particleData.Composition : SpriteCompositionData.Deserialize(node!.GetParameterValue(
                    node.Definition.Parameters.Single(item => item.Id == "compositionData")));
                composition = composition
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
                Status = LocalizationService.Shared.Get("Sprite.Loaded", particle ? particleData.Path : node!.GetParameterValue(
                    node.Definition.Parameters.Single(item => item.Id == "assetPath")).GetString() ?? "");
                CaptureContext();
            }
            catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException or ArgumentException)
            {
                Status = LocalizationService.Shared.Get("Sprite.InvalidSavedData", exception.Message);
            }
            return;
        }
        if (!particle && node?.Definition.TypeName is not ("SpriteDraw" or "SpriteAnimation")) return;
        _loadedComposition = null;
        ClearParts();
        AddPart();
        string Read(string id) => particle ? id switch
        {
            "columns" or "rows" => "1",
            "fps" => (particleData.Animation?.Fps ?? 12).ToString(CultureInfo.InvariantCulture),
            "assetPath" => particleData.Path,
            _ => ""
        } : node!.GetParameterValue(node.Definition.Parameters.Single(p => p.Id == id)).ToString();
        Catalog.Columns = Read("columns");
        Catalog.Rows = Read("rows");
        Catalog.Fps = Read("fps");
        var frame = particle ? -1 : node!.GetParameterValue(node.Definition.Parameters.Single(p => p.Id == "frame")).GetDouble();
        Frame = Math.Max(0, (int)frame).ToString(CultureInfo.InvariantCulture);
        var savedPlaying = particle || frame < 0 && node!.GetParameterValue(node.Definition.Parameters.Single(p => p.Id == "playing")).GetBoolean();
        SetSourceSizeMismatch(false);
        try
        {
            var dataParameter = node!.Definition.Parameters.FirstOrDefault(parameter => parameter.Id == "spriteData");
            _loadedData = particle ? particleData.Animation : dataParameter is null ? null :
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
        // Applying the retained draft to an explicitly selected particle target
        // is allowed within the same document/graph. Cross-document retargets
        // retain the existing context guard.
        if (selected && ReferenceEquals(document,_document) && _graph is not null &&
            ReferenceEquals(_main.ActiveGraphProvider.Graph,_graph) &&
            ParticleSpriteAuthoring.Supports(_graph.SelectedNode) && !ReferenceEquals(_target,_graph.SelectedNode))
        {
            _target=_graph.SelectedNode;
            _contextCurrent=true;
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
            if (selected && _target?.Definition.TypeName != "SpriteComposition" && !ParticleSpriteAuthoring.Supports(_target))
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

}
