using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public sealed class SpriteCompositionPreview : Control
{
    private SpriteEditorViewModel? Model => DataContext as SpriteEditorViewModel;
    private double _zoom = 1, _fit = 1;
    private Vector _pan, _initialPan;
    private Point _press, _center;
    private bool _moving, _panning;
    private double _initialX, _initialY;

    public SpriteCompositionPreview()
    {
        ClipToBounds = true;
        PointerPressed += Pressed;
        PointerMoved += Moved;
        PointerReleased += (_, e) => { _moving = _panning = false; e.Pointer.Capture(null); };
        PointerCaptureLost += (_, _) => _moving = _panning = false;
        PointerWheelChanged += (_, e) =>
        {
            _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), .05, 32);
            InvalidateVisual();
            e.Handled = true;
        };
    }

    public void ResetZoom() { _zoom = 1; _pan = default; InvalidateVisual(); }

    private IBrush Brush(string key) => this.FindResource(key) as IBrush ?? Brushes.Transparent;

    private Matrix Transform(SpritePartViewModel part)
    {
        var pose = part.PoseAt(Model?.Playhead ?? 0);
        return Matrix.CreateScale(pose.ScaleX, pose.ScaleY) *
            Matrix.CreateRotation(-pose.RotationDegrees * Math.PI / 180) *
            Matrix.CreateScale(_fit, _fit) *
            Matrix.CreateTranslation(_center.X + _pan.X + pose.X * _fit, _center.Y + _pan.Y - pose.Y * _fit);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(Brush("EffectWindowBackgroundBrush"), null, new Rect(Bounds.Size));
        if (Model is not { TextureBitmap: { } bitmap } model ||
            model.TexturePixelWidth <= 0 || model.TexturePixelHeight <= 0) return;
        _fit = Math.Max(.001, Math.Min(Bounds.Width / model.TexturePixelWidth,
            Bounds.Height / model.TexturePixelHeight) * .85 * _zoom);
        _center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var origin = _center + _pan;
        var gridPen = new Pen(Brush("EffectBorderBrush"));
        context.DrawLine(gridPen, new Point(0, origin.Y), new Point(Bounds.Width, origin.Y));
        context.DrawLine(gridPen, new Point(origin.X, 0), new Point(origin.X, Bounds.Height));
        foreach (var part in model.Parts)
        {
            if (!part.Visible || model.CellAt(part, model.Playhead) is not { } cell ||
                cell.X < 0 || cell.Y < 0 || cell.Width <= 0 || cell.Height <= 0 ||
                cell.X + cell.Width > model.TexturePixelWidth || cell.Y + cell.Height > model.TexturePixelHeight) continue;
            var source = new Rect(cell.X * bitmap.Size.Width / model.TexturePixelWidth,
                cell.Y * bitmap.Size.Height / model.TexturePixelHeight,
                cell.Width * bitmap.Size.Width / model.TexturePixelWidth,
                cell.Height * bitmap.Size.Height / model.TexturePixelHeight);
            var destination = new Rect(-cell.Width / 2d, -cell.Height / 2d, cell.Width, cell.Height);
            using (context.PushTransform(Transform(part)))
            {
                context.DrawImage(bitmap, source, destination);
                if (ReferenceEquals(part, model.SelectedPart))
                    context.DrawRectangle(null, new Pen(Brush("EffectForegroundBrush"), 1 / _fit), destination);
            }
        }
    }

    private void Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (Model is not { } model) return;
        var point = e.GetCurrentPoint(this);
        _press = point.Position;
        if (point.Properties.IsMiddleButtonPressed || point.Properties.IsRightButtonPressed)
        {
            _panning = true;
            _initialPan = _pan;
        }
        else if (point.Properties.IsLeftButtonPressed)
        {
            foreach (var part in model.Parts.Reverse())
            {
                if (!part.Visible || model.CellAt(part, model.Playhead) is not { } cell ||
                    !Transform(part).TryInvert(out var inverse)) continue;
                var local = inverse.Transform(_press);
                if (!new Rect(-cell.Width / 2d, -cell.Height / 2d, cell.Width, cell.Height).Contains(local)) continue;
                model.Playing = false;
                model.SelectedPart = part;
                var pose = part.PoseAt(model.Playhead);
                _initialX = pose.X;
                _initialY = pose.Y;
                _moving = true;
                break;
            }
        }
        if (!_moving && !_panning) return;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void Moved(object? sender, PointerEventArgs e)
    {
        var delta = e.GetPosition(this) - _press;
        if (_panning) _pan = _initialPan + delta;
        else if (_moving && Model is { } model && delta.X * delta.X + delta.Y * delta.Y > 4)
        {
            var x = _initialX + delta.X / _fit;
            var y = _initialY - delta.Y / _fit;
            if (model.SnapToGrid && model.TryGridSize(out var width, out var height))
            {
                x = Math.Round(x / width, MidpointRounding.AwayFromZero) * width;
                y = Math.Round(y / height, MidpointRounding.AwayFromZero) * height;
            }
            model.MoveSelectedPart(x, y);
        }
        else return;
        InvalidateVisual();
        e.Handled = true;
    }
}
