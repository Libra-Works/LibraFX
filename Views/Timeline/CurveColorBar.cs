using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using LiberaFx.ViewModels.Panels;

namespace LiberaFx.Views.Timeline;

/// <summary>Horizontal color gradient bar aligned with the F-Curve time axis for RGBA color curves.</summary>
public sealed class CurveColorBar : Control
{
    private CurveEditorViewModel? _model;
    private double _originX = 50;
    private double _scaleX = 5;

    public CurveColorBar()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public void SyncView(double originX, double scaleX)
    {
        if (Math.Abs(_originX - originX) < 1e-6 && Math.Abs(_scaleX - scaleX) < 1e-6) return;
        _originX = originX;
        _scaleX = scaleX;
        InvalidateVisual();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        _model = DataContext as CurveEditorViewModel;
        if (_model is not null) _model.PropertyChanged += OnModelChanged;
        InvalidateVisual();
        base.OnDataContextChanged(e);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    private double DisplayTime(double timeSeconds) => _model?.Playback?.DisplayUnits(timeSeconds) ?? timeSeconds;
    private double TimeSeconds(double displayTime) => _model?.Playback?.TimeSecondsFromDisplay(displayTime) ?? displayTime;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_model is not { IsColorCurve: true } || Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var rect = new Rect(0, 0, Bounds.Width, Bounds.Height);
        // Alpha checkerboard background
        const double cell = 6;
        var dark = new ImmutableSolidColorBrush(Color.FromRgb(42, 45, 54));
        var light = new ImmutableSolidColorBrush(Color.FromRgb(64, 68, 82));
        context.DrawRectangle(dark, null, rect);
        for (double y = 0; y < Bounds.Height; y += cell)
        {
            var row = (int)(y / cell);
            for (double x = (row % 2) * cell; x < Bounds.Width; x += cell * 2)
            {
                context.DrawRectangle(light, null,
                    new Rect(x, y, Math.Min(cell, Bounds.Width - x), Math.Min(cell, Bounds.Height - y)));
            }
        }

        // Sample color gradient across visible width
        const double step = 3;
        for (double x = 0; x < Bounds.Width; x += step)
        {
            var displayTime = (x + step * 0.5 - _originX) / _scaleX;
            var seconds = TimeSeconds(displayTime);
            var color = _model.EvaluateColorAtTime(seconds);
            var w = Math.Min(step + 0.5, Bounds.Width - x);
            context.DrawRectangle(new ImmutableSolidColorBrush(color), null, new Rect(x, 0, w, Bounds.Height));
        }

        // Keyframe tick markers
        var outerPen = new Pen(Brushes.Black, 2);
        var innerPen = new Pen(Brushes.White, 1);
        foreach (var timeSeconds in _model.ColorKeyTimes())
        {
            var x = _originX + DisplayTime(timeSeconds) * _scaleX;
            if (x < -4 || x > Bounds.Width + 4) continue;
            context.DrawLine(outerPen, new Point(x, 0), new Point(x, Bounds.Height));
            context.DrawLine(innerPen, new Point(x, 1), new Point(x, Bounds.Height - 1));
        }

        // Playhead indicator
        if (_model.Playback is { } playback)
        {
            var px = _originX + DisplayTime(playback.TimeSeconds) * _scaleX;
            if (px >= 0 && px <= Bounds.Width)
            {
                context.DrawLine(new Pen(Brushes.Orange, 1.5), new Point(px, 0), new Point(px, Bounds.Height));
            }
        }

        context.DrawRectangle(null, new Pen(new ImmutableSolidColorBrush(Color.FromRgb(80, 86, 104)), 1), rect);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_model is not { IsColorCurve: true } || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var pos = e.GetPosition(this);
        var displayTime = (pos.X - _originX) / _scaleX;
        if (_model.SnapToFrames) displayTime = Math.Round(displayTime);
        var seconds = TimeSeconds(displayTime);
        var current = _model.EvaluateColorAtTime(seconds);

        var picker = new ColorView
        {
            Color = current,
            IsAlphaEnabled = true,
            IsAlphaVisible = true,
            IsHexInputVisible = true
        };
        picker.ColorChanged += (_, args) => _model.SetColorKeyAtTime(seconds, args.NewColor);

        var flyout = new Flyout
        {
            Content = picker,
            Placement = PlacementMode.Top
        };
        flyout.ShowAt(this, true);
        e.Handled = true;
    }
}
