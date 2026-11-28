using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LiberaFx.Services.Sprite;

namespace LiberaFx.Views.Sprite;

public sealed class SpriteCellThumbnail : Control
{
    public static readonly StyledProperty<Bitmap?> BitmapProperty =
        AvaloniaProperty.Register<SpriteCellThumbnail, Bitmap?>(nameof(Bitmap));
    public static readonly StyledProperty<IEnumerable<SpriteAnimationCell>?> CellsProperty =
        AvaloniaProperty.Register<SpriteCellThumbnail, IEnumerable<SpriteAnimationCell>?>(nameof(Cells));
    public static readonly StyledProperty<Guid> CellIdProperty =
        AvaloniaProperty.Register<SpriteCellThumbnail, Guid>(nameof(CellId));
    public static readonly StyledProperty<int> TextureWidthProperty =
        AvaloniaProperty.Register<SpriteCellThumbnail, int>(nameof(TextureWidth));
    public static readonly StyledProperty<int> TextureHeightProperty =
        AvaloniaProperty.Register<SpriteCellThumbnail, int>(nameof(TextureHeight));

    public Bitmap? Bitmap { get => GetValue(BitmapProperty); set => SetValue(BitmapProperty, value); }
    public IEnumerable<SpriteAnimationCell>? Cells { get => GetValue(CellsProperty); set => SetValue(CellsProperty, value); }
    public Guid CellId { get => GetValue(CellIdProperty); set => SetValue(CellIdProperty, value); }
    public int TextureWidth { get => GetValue(TextureWidthProperty); set => SetValue(TextureWidthProperty, value); }
    public int TextureHeight { get => GetValue(TextureHeightProperty); set => SetValue(TextureHeightProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BitmapProperty || change.Property == CellsProperty || change.Property == CellIdProperty ||
            change.Property == TextureWidthProperty || change.Property == TextureHeightProperty)
            InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var background = new SolidColorBrush(Color.FromRgb(32, 32, 32));
        context.DrawRectangle(background, null, new Rect(Bounds.Size));
        const double tile = 8;
        var first = new SolidColorBrush(Color.FromRgb(54, 54, 54));
        var second = new SolidColorBrush(Color.FromRgb(82, 82, 82));
        for (var y = 0d; y < Bounds.Height; y += tile)
            for (var x = 0d; x < Bounds.Width; x += tile)
                if (((int)(x / tile) + (int)(y / tile)) % 2 == 0)
                    context.DrawRectangle(first, null, new Rect(x, y, tile, tile));
                else
                    context.DrawRectangle(second, null, new Rect(x, y, tile, tile));
        if (Bitmap is not { } bitmap || Cells?.FirstOrDefault(cell => cell.Id == CellId) is not { } cell)
            return;
        var textureWidth = TextureWidth > 0 ? TextureWidth : bitmap.PixelSize.Width;
        var textureHeight = TextureHeight > 0 ? TextureHeight : bitmap.PixelSize.Height;
        var scaleX = (double)bitmap.PixelSize.Width / textureWidth;
        var scaleY = (double)bitmap.PixelSize.Height / textureHeight;
        var source = new Rect(cell.X * scaleX, cell.Y * scaleY, cell.Width * scaleX, cell.Height * scaleY);
        if (source.Width <= 0 || source.Height <= 0 ||
            source.X + source.Width > bitmap.PixelSize.Width || source.Y + source.Height > bitmap.PixelSize.Height)
            return;
        var ratio = Math.Min(Bounds.Width / source.Width, Bounds.Height / source.Height);
        if (ratio <= 0) return;
        var width = source.Width * ratio;
        var height = source.Height * ratio;
        var destination = new Rect((Bounds.Width - width) / 2, (Bounds.Height - height) / 2, width, height);
        using (context.PushClip(new Rect(Bounds.Size)))
            context.DrawImage(bitmap, source, destination);
    }
}
