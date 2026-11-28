using System.ComponentModel;
using System.Globalization;
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed partial class SpriteEditorViewModel
{
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
