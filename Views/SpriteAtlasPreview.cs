using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public sealed class SpriteAtlasPreview : Control
{
    private enum DragMode { None, NewCell, MoveCell, ResizeCell, Pan }
    private SpriteEditorViewModel? Model => DataContext as SpriteEditorViewModel;
    private Rect _imageRect;
    private DragMode _dragMode;
    private Point _dragStart, _lastPointer, _panStart;
    private Point _pan;
    private Rect _originalCell, _draggedCell;
    private double _zoom = 1;
    public bool IsFramePreview { get; set; }

    public SpriteAtlasPreview()
    {
        DataContextChanged += (_, _) => InvalidateVisual();
        Focusable = true;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerWheelChanged += OnPointerWheelChanged;
        KeyDown += OnKeyDown;
    }

    public void ResetZoom()
    {
        _zoom = 1;
        _pan = default;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        DrawChecker(context, new Rect(Bounds.Size));
        if (Model?.TextureBitmap is not { } bitmap) return;
        var sourceWidth = Model.SourceBitmap?.PixelSize.Width ?? bitmap.PixelSize.Width;
        var sourceHeight = Model.SourceBitmap?.PixelSize.Height ?? bitmap.PixelSize.Height;
        if (Model.TryBuildData(out var data, false) && data.TextureWidth > 0 && data.TextureHeight > 0)
        {
            sourceWidth = data.TextureWidth;
            sourceHeight = data.TextureHeight;
        }
        var fit = Math.Min(Bounds.Width / sourceWidth, Bounds.Height / sourceHeight);
        if (fit <= 0) return;
        var width = sourceWidth * fit * _zoom;
        var height = sourceHeight * fit * _zoom;
        _imageRect = new Rect((Bounds.Width - width) / 2 + _pan.X, (Bounds.Height - height) / 2 + _pan.Y, width, height);
        using (context.PushClip(new Rect(Bounds.Size)))
        {
            if (IsFramePreview)
            {
                var frameRect = CurrentFrameRect(Model, data, sourceWidth, sourceHeight);
                if (frameRect is { Width: > 0, Height: > 0 } preview && preview.X >= 0 && preview.Y >= 0 &&
                    preview.Right <= sourceWidth && preview.Bottom <= sourceHeight)
                {
                    var source = new Rect(preview.X * bitmap.Size.Width / sourceWidth,
                        preview.Y * bitmap.Size.Height / sourceHeight, preview.Width * bitmap.Size.Width / sourceWidth,
                        preview.Height * bitmap.Size.Height / sourceHeight);
                    var frameFit = Math.Min(Bounds.Width / preview.Width, Bounds.Height / preview.Height);
                    var area = new Rect((Bounds.Width - preview.Width * frameFit) / 2,
                        (Bounds.Height - preview.Height * frameFit) / 2, preview.Width * frameFit, preview.Height * frameFit);
                    context.DrawImage(bitmap, source, area);
                }
                return;
            }
            context.DrawImage(bitmap, new Rect(bitmap.Size), _imageRect);

            if (Model.SnapToGrid && Model.TryGridSize(out var gridWidth, out var gridHeight))
            {
                var pen = new Pen(Brushes.Gray, 0.75);
                var stepX = gridWidth * Math.Max(1, (int)Math.Ceiling(6 / (gridWidth * fit * _zoom)));
                var stepY = gridHeight * Math.Max(1, (int)Math.Ceiling(6 / (gridHeight * fit * _zoom)));
                for (var x = stepX; x < sourceWidth; x += stepX)
                {
                    var canvasX = _imageRect.X + (double)x / sourceWidth * _imageRect.Width;
                    context.DrawLine(pen, new Point(canvasX, _imageRect.Y), new Point(canvasX, _imageRect.Bottom));
                }
                for (var y = stepY; y < sourceHeight; y += stepY)
                {
                    var canvasY = _imageRect.Y + (double)y / sourceHeight * _imageRect.Height;
                    context.DrawLine(pen, new Point(_imageRect.X, canvasY), new Point(_imageRect.Right, canvasY));
                }
            }
            if (data is not null)
            {
                foreach (var cell in data.Cells)
                {
                    var area = PixelToCanvas(new Rect(cell.X, cell.Y, cell.Width, cell.Height),
                        data.TextureWidth, data.TextureHeight);
                    var selected = Model.SelectedCell?.Id == cell.Id;
                    context.DrawRectangle(null, new Pen(selected ? Brushes.Yellow : Brushes.White,
                        selected ? 2 : 1), area);
                    context.DrawText(new FormattedText(cell.Name, System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, Typeface.Default, 11, selected ? Brushes.Yellow : Brushes.White),
                        new Point(area.X + 2, area.Y + 2));
                    if (selected)
                        context.DrawRectangle(Brushes.Yellow, null, new Rect(area.Right - 4, area.Bottom - 4, 8, 8));
                }
            }
            else if (Model.TrySettings(out var settings))
            {
                var grid = new Pen(new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)), 0.75);
                var cellWidth = width / settings.Columns;
                var cellHeight = height / settings.Rows;
                if (settings.Columns <= 256)
                    for (var x = 1; x < settings.Columns; x++)
                        context.DrawLine(grid, new Point(_imageRect.X + x * cellWidth, _imageRect.Y),
                            new Point(_imageRect.X + x * cellWidth, _imageRect.Bottom));
                if (settings.Rows <= 256)
                    for (var y = 1; y < settings.Rows; y++)
                        context.DrawLine(grid, new Point(_imageRect.X, _imageRect.Y + y * cellHeight),
                            new Point(_imageRect.Right, _imageRect.Y + y * cellHeight));
            }
            if (_dragMode is DragMode.NewCell or DragMode.MoveCell or DragMode.ResizeCell)
                context.DrawRectangle(null, new Pen(Brushes.Orange, 2),
                    PixelToCanvas(_draggedCell, sourceWidth, sourceHeight));
        }
    }

    private static void DrawChecker(DrawingContext context, Rect area)
    {
        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(38, 38, 38)), null, area);
        const double tile = 16;
        var light = new SolidColorBrush(Color.FromRgb(58, 58, 58));
        for (var y = 0d; y < area.Height; y += tile)
            for (var x = 0d; x < area.Width; x += tile)
                if (((int)(x / tile) + (int)(y / tile)) % 2 == 0)
                    context.DrawRectangle(light, null, new Rect(x, y, tile, tile));
    }

    private Rect PixelToCanvas(Rect rect, int width, int height) =>
        new(_imageRect.X + rect.X / width * _imageRect.Width,
            _imageRect.Y + rect.Y / height * _imageRect.Height,
            rect.Width / width * _imageRect.Width, rect.Height / height * _imageRect.Height);

    private static Rect? CurrentFrameRect(SpriteEditorViewModel model, SpriteAnimationData? data,
        int sourceWidth, int sourceHeight)
    {
        if (data is not null && data.Frames.Count > 0)
        {
            var index = Math.Clamp(model.PreviewFrame, 0, data.Frames.Count - 1);
            var frame = data.Frames[index];
            var cell = data.Cells.FirstOrDefault(item => item.Id == frame.CellId);
            return cell is null ? null : new Rect(cell.X, cell.Y, cell.Width, cell.Height);
        }
        if (model.TrySettings(out var settings))
            return new Rect(model.PreviewFrame % settings.Columns * sourceWidth / settings.Columns,
                model.PreviewFrame / settings.Columns * sourceHeight / settings.Rows,
                (double)sourceWidth / settings.Columns, (double)sourceHeight / settings.Rows);
        return null;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsFramePreview || Model is not { CanDrawCells: true } model || model.SourceBitmap is not { } bitmap) return;
        Focus();
        var point = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsMiddleButtonPressed || properties.IsRightButtonPressed)
        {
            _dragMode = DragMode.Pan;
            _lastPointer = point;
            _panStart = _pan;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (!properties.IsLeftButtonPressed || !_imageRect.Contains(point)) return;
        var pixel = CanvasToPixel(point, bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        var selected = model.SelectedCell;
        if (model.IsDrawingNewCell)
            _dragMode = DragMode.NewCell;
        else if (selected is not null)
        {
            var selectedCanvas = PixelToCanvas(new Rect(selected.X, selected.Y, selected.Width, selected.Height),
                bitmap.PixelSize.Width, bitmap.PixelSize.Height);
            var handle = new Rect(selectedCanvas.Right - 8, selectedCanvas.Bottom - 8, 16, 16);
            if (handle.Contains(point)) _dragMode = DragMode.ResizeCell;
            else if (selectedCanvas.Contains(point)) _dragMode = DragMode.MoveCell;
            else _dragMode = DragMode.NewCell;
            _originalCell = new Rect(selected.X, selected.Y, selected.Width, selected.Height);
        }
        else
            _dragMode = DragMode.NewCell;
        if (_dragMode == DragMode.NewCell)
        {
            pixel = new Point(Math.Min(bitmap.PixelSize.Width - 1, model.SnapCoordinate(pixel.X, true)),
                Math.Min(bitmap.PixelSize.Height - 1, model.SnapCoordinate(pixel.Y, false)));
            model.TryGridSize(out var gridWidth, out var gridHeight);
            _draggedCell = new Rect(pixel, new Size(
                Math.Min(bitmap.PixelSize.Width - pixel.X, model.SnapToGrid ? Math.Max(1, gridWidth) : 1),
                Math.Min(bitmap.PixelSize.Height - pixel.Y, model.SnapToGrid ? Math.Max(1, gridHeight) : 1)));
        }
        else _draggedCell = _originalCell;
        _dragStart = pixel;
        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(this);
        if (_dragMode == DragMode.Pan)
        {
            _pan = _panStart + (point - _lastPointer);
            InvalidateVisual();
            return;
        }
        if (_dragMode is not (DragMode.NewCell or DragMode.MoveCell or DragMode.ResizeCell) ||
            Model is not { } model || model.SourceBitmap is not { } bitmap) return;
        var pixel = CanvasToPixel(point, bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        var dx = pixel.X - _dragStart.X;
        var dy = pixel.Y - _dragStart.Y;
        if (_dragMode == DragMode.NewCell)
        {
            pixel = new Point(model.SnapCoordinate(pixel.X, true), model.SnapCoordinate(pixel.Y, false));
            var x = Math.Clamp(Math.Min(_dragStart.X, pixel.X), 0, bitmap.PixelSize.Width - 1);
            var y = Math.Clamp(Math.Min(_dragStart.Y, pixel.Y), 0, bitmap.PixelSize.Height - 1);
            var right = Math.Clamp(Math.Max(_dragStart.X, pixel.X), x + 1, bitmap.PixelSize.Width);
            var bottom = Math.Clamp(Math.Max(_dragStart.Y, pixel.Y), y + 1, bitmap.PixelSize.Height);
            _draggedCell = new Rect(Math.Floor(x), Math.Floor(y), Math.Max(1, Math.Ceiling(right) - Math.Floor(x)),
                Math.Max(1, Math.Ceiling(bottom) - Math.Floor(y)));
        }
        else if (_dragMode == DragMode.MoveCell)
        {
            var x = Math.Clamp(model.SnapCoordinate(_originalCell.X + dx, true), 0, bitmap.PixelSize.Width - _originalCell.Width);
            var y = Math.Clamp(model.SnapCoordinate(_originalCell.Y + dy, false), 0, bitmap.PixelSize.Height - _originalCell.Height);
            _draggedCell = new Rect(Math.Round(x), Math.Round(y), _originalCell.Width, _originalCell.Height);
        }
        else
        {
            var width = Math.Clamp(model.SnapCoordinate(_originalCell.Right + dx, true) - _originalCell.X,
                1, bitmap.PixelSize.Width - _originalCell.X);
            var height = Math.Clamp(model.SnapCoordinate(_originalCell.Bottom + dy, false) - _originalCell.Y,
                1, bitmap.PixelSize.Height - _originalCell.Y);
            _draggedCell = new Rect(_originalCell.X, _originalCell.Y, Math.Round(width), Math.Round(height));
        }
        InvalidateVisual();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if ((_dragMode is DragMode.NewCell or DragMode.MoveCell or DragMode.ResizeCell) && Model is { } model)
            model.SetDraggedRectangle((int)_draggedCell.X, (int)_draggedCell.Y,
                (int)_draggedCell.Width, (int)_draggedCell.Height, _dragMode == DragMode.NewCell);
        _dragMode = DragMode.None;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), 1, 12);
        InvalidateVisual();
        e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { } model) return;
        if (e.Key == Key.Space)
        {
            model.Playing = !model.Playing;
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && model.SelectedCell is not null)
        {
            model.RemoveCell();
            e.Handled = true;
        }
        else if (e.Key == Key.Left && model.Frames.Count > 0)
        {
            model.SelectFrame(Math.Max(0, model.PreviewFrame - 1));
            e.Handled = true;
        }
        else if (e.Key == Key.Right && model.Frames.Count > 0)
        {
            model.SelectFrame(Math.Min(model.Frames.Count - 1, model.PreviewFrame + 1));
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Up)
        {
            model.MoveFrame(-1);
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Down)
        {
            model.MoveFrame(1);
            e.Handled = true;
        }
    }

    private Point CanvasToPixel(Point point, int width, int height) =>
        new(Math.Clamp((point.X - _imageRect.X) / _imageRect.Width * width, 0, width),
            Math.Clamp((point.Y - _imageRect.Y) / _imageRect.Height * height, 0, height));
}
