using System.ComponentModel;
using System.Globalization;
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed partial class SpriteEditorViewModel
{
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

    public void AddFrame() => AddFrameAt(Frames.Count);

    public void InsertFrame()
    {
        var index = SelectedSequenceFrame is { } current ? Frames.IndexOf(current) : -1;
        AddFrameAt(index < 0 ? Frames.Count : index + 1);
    }

    private void AddFrameAt(int index)
    {
        if (SelectedCell is null && Cells.Count > 0)
        {
            SelectedCell = Cells[0];
        }
        if (SelectedCell is not { } selected)
        {
            Status = LocalizationService.Shared.Get("Sprite.CreateCellFirst");
            return;
        }
        if (Frames.Count >= 4096)
        {
            Status = LocalizationService.Shared.Get("Sprite.FrameLimit");
            return;
        }
        var oldLast = Frames.Count - 1;
        var start = ParseInt(RangeStart, 0);
        var end = ParseInt(RangeEnd, oldLast);
        var frame = new SpriteAnimationFrame(Guid.NewGuid(), selected.Id, 1);
        Frames.Insert(index, frame);
        SelectedSequenceFrame = frame;
        if (oldLast < 0)
        {
            RangeStart = RangeEnd = "0";
        }
        else
        {
            if (index <= start)
            {
                RangeStart = (start + 1).ToString(CultureInfo.InvariantCulture);
            }
            if (index <= end || (index == oldLast + 1 && end == oldLast))
            {
                RangeEnd = (end + 1).ToString(CultureInfo.InvariantCulture);
            }
        }
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

}
