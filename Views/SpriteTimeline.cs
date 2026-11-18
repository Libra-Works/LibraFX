using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public sealed class SpriteTimeline : Control
{
    private SpriteEditorViewModel? Model => DataContext as SpriteEditorViewModel;
    private double _scale = 80, _pressX, _initialDuration, _pointerX;
    private int _dragIndex = -1;
    private bool _resize, _moved;
    private Guid? _dragKey;
    private bool _scrubbing;
    private double TimeScale => Math.Min(_scale, 1_000_000 / Math.Max(1, Model?.TotalFrames ?? 1));
    private const double HeaderWidth = 150;

    public SpriteTimeline()
    {
        Height = 136;
        Focusable = true;
        ClipToBounds = true;
        Avalonia.Automation.AutomationProperties.SetName(this, LocalizationService.Shared.Get("Sprite.Timeline"));
        DataContextChanged += (_, _) => Refresh();
        PointerPressed += Pressed;
        PointerMoved += Moved;
        PointerReleased += Released;
        PointerCaptureLost += (_, _) => { _dragIndex = -1; _dragKey = null; _scrubbing = false; InvalidateVisual(); };
        PointerWheelChanged += Wheel;
        KeyDown += KeyPressed;
    }

    private double ClipWidth(double duration) =>
        (double.IsFinite(duration) ? Math.Max(0, duration) : 1) * TimeScale;

    public void Refresh()
    {
        Width = Math.Max(600, HeaderWidth + 16 + (Model?.TotalFrames ?? 1) * TimeScale);
        Height = 30 + Math.Max(1, Model?.Parts.Count ?? 1) * 100;
        InvalidateVisual();
    }

    internal Rect FrameBounds(int index)
    {
        if (Model is not { } model || index < 0 || index >= model.Frames.Count) return default;
        return new Rect(HeaderWidth + model.Frames.Take(index).Sum(frame => ClipWidth(frame.DurationFrames)),
            26 + Math.Max(0, model.Parts.IndexOf(model.SelectedPart!)) * 100,
            ClipWidth(model.Frames[index].DurationFrames), 100);
    }

    internal int FrameIndexAt(double x)
    {
        if (Model is not { } model) return -1;
        var right = HeaderWidth;
        for (var index = 0; index < model.Frames.Count; index++)
        {
            right += ClipWidth(model.Frames[index].DurationFrames);
            if (x < right) return index;
        }
        return model.Frames.Count - 1;
    }

    private IBrush Brush(string key) => this.FindResource(key) as IBrush ?? Brushes.Transparent;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var foreground = Brush("EffectForegroundBrush");
        var muted = Brush("EffectMutedForegroundBrush");
        var border = Brush("EffectBorderBrush");
        context.DrawRectangle(Brush("EffectWindowBackgroundBrush"), null, new Rect(Bounds.Size));
        if (Model is not { } model) return;
        var scroll = this.FindAncestorOfType<ScrollViewer>();
        var visibleLeft = scroll?.Offset.X ?? 0;
        var visibleRight = visibleLeft + (scroll?.Viewport.Width ?? Bounds.Width);
        for (var partIndex = 0; partIndex < model.Parts.Count; partIndex++)
        {
            var part = model.Parts[partIndex];
            var y = 26 + partIndex * 100;
            var x = HeaderWidth;
            var ticks = 0d;
            for (var index = 0; index < part.Frames.Count; index++)
            {
            var frame = part.Frames[index];
            var width = ClipWidth(frame.DurationFrames);
            var rect = new Rect(x + 1, y + 20, Math.Max(1, width - 2), 76);
            if (rect.Right >= visibleLeft && rect.X <= visibleRight)
            {
                var cell = model.Cells.FirstOrDefault(item => item.Id == frame.CellId);
                var selected = ReferenceEquals(part, model.SelectedPart) && frame.Id == model.SelectedSequenceFrame?.Id;
                context.DrawRectangle(Brush("EffectSurfaceBrush"),
                    new Pen(selected ? foreground : border, selected ? 2 : 1), rect);
                using (context.PushClip(rect.Deflate(3)))
                {
                    if (cell is not null && model.TextureBitmap is { } bitmap &&
                        model.TexturePixelWidth > 0 && model.TexturePixelHeight > 0 &&
                        cell.Width > 0 && cell.Height > 0 && cell.X >= 0 && cell.Y >= 0 &&
                        cell.X + cell.Width <= model.TexturePixelWidth && cell.Y + cell.Height <= model.TexturePixelHeight)
                    {
                        var sx = bitmap.Size.Width / model.TexturePixelWidth;
                        var sy = bitmap.Size.Height / model.TexturePixelHeight;
                        var source = new Rect(cell.X * sx, cell.Y * sy, cell.Width * sx, cell.Height * sy);
                        var ratio = Math.Min(Math.Max(0, rect.Width - 8) / source.Width, 32 / source.Height);
                        var destination = new Rect(rect.Center.X - source.Width * ratio / 2, rect.Y + 22,
                            source.Width * ratio, source.Height * ratio);
                        if (ratio > 0) context.DrawImage(bitmap, source, destination);
                    }
                    Text(context, $"{index + 1}: {cell?.Name ?? "?"}", new Point(rect.X + 4, rect.Y + 3), foreground);
                    Text(context, $"{frame.DurationFrames:0.##} f", new Point(rect.X + 4, rect.Bottom - 20), muted);
                }
                if (partIndex == 0)
                    Text(context, ticks.ToString("0.##", CultureInfo.InvariantCulture), new Point(x, 5), muted);
            }
            x += width;
            ticks += frame.DurationFrames;
            }
            foreach (var key in part.Keys)
            {
                var keyX = HeaderWidth + key.Frame * TimeScale;
                if (keyX < visibleLeft || keyX > visibleRight) continue;
                context.DrawLine(new Pen(foreground, 2), new Point(keyX - 4, y + 8), new Point(keyX, y + 3));
                context.DrawLine(new Pen(foreground, 2), new Point(keyX, y + 3), new Point(keyX + 4, y + 8));
                context.DrawLine(new Pen(foreground, 2), new Point(keyX + 4, y + 8), new Point(keyX, y + 13));
                context.DrawLine(new Pen(foreground, 2), new Point(keyX, y + 13), new Point(keyX - 4, y + 8));
            }
            context.DrawRectangle(Brush("EffectWindowBackgroundBrush"), null,
                new Rect(visibleLeft, y, HeaderWidth - 4, 100));
            using (context.PushClip(new Rect(visibleLeft + 4, y, HeaderWidth - 12, 100)))
            {
                Text(context, part.Name, new Point(visibleLeft + 8, y + 22),
                    ReferenceEquals(part, model.SelectedPart) ? foreground : muted);
                Text(context, LocalizationService.Shared.Get("Sprite.TransformKeys"),
                    new Point(visibleLeft + 8, y + 3), muted);
            }
        }
        var playhead = HeaderWidth + model.Playhead * TimeScale;
        context.DrawLine(new Pen(foreground, 2), new Point(playhead, 20), new Point(playhead, Height));
        if (_dragIndex >= 0 && !_resize && _moved)
            context.DrawLine(new Pen(foreground, 3), new Point(_pointerX, 26), new Point(_pointerX, 126));
    }

    private static void Text(DrawingContext context, string value, Point point, IBrush brush) =>
        context.DrawText(new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, 12, brush), point);

    private void Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Model is not { } model) return;
        Focus();
        var point = e.GetPosition(this);
        if (model.Parts.Count == 0) return;
        model.SelectedPart = model.Parts[Math.Clamp((int)((point.Y - 26) / 100), 0, model.Parts.Count - 1)];
        if (point.X < HeaderWidth) { e.Handled = true; return; }
        var keyY = 26 + model.Parts.IndexOf(model.SelectedPart) * 100 + 8;
        var key = model.SelectedPart.Keys.FirstOrDefault(item =>
            Math.Abs(HeaderWidth + item.Frame * TimeScale - point.X) <= 7 && Math.Abs(keyY - point.Y) <= 9);
        if (key is not null)
        {
            _dragKey = key.Id;
            model.ScrubAt(key.Frame);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        model.ScrubAt(Math.Max(0, (point.X - HeaderWidth) / TimeScale));
        if (point.Y < 26 || (point.Y - 26) % 100 < 20)
        {
            _scrubbing = true;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        var index = FrameIndexAt(point.X);
        if (index < 0) return;
        _dragIndex = index;
        _pressX = _pointerX = point.X;
        _initialDuration = model.Frames[index].DurationFrames;
        _resize = Math.Abs(point.X - FrameBounds(index).Right) < 9;
        _moved = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void Moved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(this);
        if (_scrubbing)
        {
            Model?.ScrubAt(Math.Max(0, (point.X - HeaderWidth) / TimeScale));
            e.Handled = true;
            return;
        }
        if (_dragKey is { } keyId)
        {
            Model?.MoveTransformKey(keyId, Math.Clamp(Math.Round((point.X - HeaderWidth) / TimeScale * 4) / 4,
                0, 4_096_000_000));
            e.Handled = true;
            return;
        }
        if (_dragIndex < 0)
        {
            var index = FrameIndexAt(point.X);
            Cursor = new Cursor(index >= 0 && Math.Abs(point.X - FrameBounds(index).Right) < 9
                ? StandardCursorType.SizeWestEast : StandardCursorType.Hand);
            return;
        }
        _pointerX = point.X;
        _moved |= Math.Abs(point.X - _pressX) > 3;
        if (_resize && _moved)
            Model?.SetFrameDuration(Math.Clamp(Math.Round((_initialDuration + (point.X - _pressX) / TimeScale) * 4) / 4,
                0.25, 1_000_000));
        InvalidateVisual();
        e.Handled = true;
    }

    private void Released(object? sender, PointerReleasedEventArgs e)
    {
        if (_scrubbing) { _scrubbing = false; e.Pointer.Capture(null); e.Handled = true; return; }
        if (_dragKey is not null)
        {
            _dragKey = null;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }
        if (_dragIndex < 0) return;
        if (!_resize && _moved) Model?.MoveFrameTo(FrameIndexAt(e.GetPosition(this).X));
        _dragIndex = -1;
        e.Pointer.Capture(null);
        InvalidateVisual();
        e.Handled = true;
    }

    private void Wheel(object? sender, PointerWheelEventArgs e)
    {
        var scroll = this.FindAncestorOfType<ScrollViewer>();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _scale = Math.Clamp(_scale * (e.Delta.Y > 0 ? 1.2 : 1 / 1.2), 24, 200);
            Refresh();
        }
        else if (scroll is not null)
            scroll.Offset = new Vector(Math.Max(0, scroll.Offset.X - e.Delta.Y * 80), 0);
        e.Handled = true;
    }

    private void KeyPressed(object? sender, KeyEventArgs e)
    {
        if (Model is not { } model) return;
        if (e.Key == Key.Space) model.Playing = !model.Playing;
        else if (e.Key == Key.Delete) model.RemoveFrame();
        else if (e.Key is Key.Left or Key.Right)
        {
            var offset = e.Key == Key.Left ? -1 : 1;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) model.MoveFrame(offset);
            else model.SelectFrame(Math.Clamp(model.PreviewFrame + offset, 0, model.FrameMaximum));
        }
        else return;
        e.Handled = true;
    }
}
