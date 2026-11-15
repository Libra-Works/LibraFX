using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public sealed class SpriteAtlasPreview : Control
{
    private SpriteEditorViewModel? Model => DataContext as SpriteEditorViewModel;
    private Rect _imageRect;
    public bool IsFramePreview { get; set; }

    public SpriteAtlasPreview()
    {
        DataContextChanged += (_, _) => InvalidateVisual();
        PointerPressed += Select;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(Brushes.Black, null, new Rect(Bounds.Size));
        if (Model?.Thumbnail is not { } bitmap || !Model.TrySettings(out var settings)) return;
        var frame = Model.PreviewFrame;
        var source = IsFramePreview
            ? new Rect(frame % settings.Columns * bitmap.Size.Width / settings.Columns,
                frame / settings.Columns * bitmap.Size.Height / settings.Rows,
                bitmap.Size.Width / settings.Columns, bitmap.Size.Height / settings.Rows)
            : new Rect(bitmap.Size);
        var ratio = Math.Min(Bounds.Width / source.Width, Bounds.Height / source.Height);
        if (ratio <= 0) return;
        var width = source.Width * ratio;
        var height = source.Height * ratio;
        _imageRect = new Rect((Bounds.Width - width) / 2, (Bounds.Height - height) / 2, width, height);
        context.DrawImage(bitmap, source, _imageRect);
        if (IsFramePreview) return;
        var cellWidth = width / settings.Columns;
        var cellHeight = height / settings.Rows;
        var selection = new Rect(_imageRect.X + frame % settings.Columns * cellWidth,
            _imageRect.Y + frame / settings.Columns * cellHeight, cellWidth, cellHeight);
        context.DrawRectangle(null, new Pen(Brushes.Yellow, 2), selection);
        var grid = new Pen(Brushes.White, 0.5);
        if (settings.Columns <= 256)
            for (var x = 1; x < settings.Columns; x++)
                context.DrawLine(grid, new Point(_imageRect.X + x * cellWidth, _imageRect.Y),
                    new Point(_imageRect.X + x * cellWidth, _imageRect.Bottom));
        if (settings.Rows <= 256)
            for (var y = 1; y < settings.Rows; y++)
                context.DrawLine(grid, new Point(_imageRect.X, _imageRect.Y + y * cellHeight),
                    new Point(_imageRect.Right, _imageRect.Y + y * cellHeight));
    }

    private void Select(object? sender, PointerPressedEventArgs e)
    {
        if (IsFramePreview || Model is not { } model || model.Thumbnail is null ||
            !model.TrySettings(out var settings)) return;
        var point = e.GetPosition(this);
        if (!_imageRect.Contains(point)) return;
        var column = Math.Clamp((int)((point.X - _imageRect.X) / _imageRect.Width * settings.Columns), 0, settings.Columns - 1);
        var row = Math.Clamp((int)((point.Y - _imageRect.Y) / _imageRect.Height * settings.Rows), 0, settings.Rows - 1);
        model.SelectFrame(row * settings.Columns + column);
        InvalidateVisual();
    }
}
