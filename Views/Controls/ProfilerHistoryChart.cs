using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using LiberaFx.Services.Compilation;
using LiberaFx.Services.Preview;
using LiberaFx.Services.Settings;

namespace LiberaFx.Views.Controls;

public sealed class ProfilerHistoryChart : Control
{
    public static readonly StyledProperty<IBrush> ForegroundProperty =
        AvaloniaProperty.Register<ProfilerHistoryChart, IBrush>(nameof(Foreground), Brushes.LightGray);
    public IBrush Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public static IReadOnlyList<IBrush> Palette { get; } = new IBrush[]
    {
        new SolidColorBrush(Color.Parse("#D9BA58")), new SolidColorBrush(Color.Parse("#62ADE9")),
        new SolidColorBrush(Color.Parse("#AB8BE2")), new SolidColorBrush(Color.Parse("#6DC991")),
        new SolidColorBrush(Color.Parse("#49BFC2")), new SolidColorBrush(Color.Parse("#8895A7"))
    };
    private IReadOnlyList<ProfileSample> _samples = [];
    private int _visibleCount = 120;
    private int _hover = -1;
    private ulong? _frozenEnd;
    private ProfileSample? _cachedFirst;
    private ProfileSample? _cachedLast;
    private Rect _cachedPlot;
    private StreamGeometry[] _areas = [];
    private double _maximum;
    public int SelectedIndex { get; private set; } = -1;
    public event Action<int>? SampleSelected;
    static ProfilerHistoryChart() => AffectsRender<ProfilerHistoryChart>(ForegroundProperty);

    public ProfilerHistoryChart()
    {
        ClipToBounds = true;
        this.Bind(ForegroundProperty, new DynamicResourceExtension("EffectForegroundBrush"));
        PointerMoved += (_, e) =>
        {
            var hover = IndexAt(e.GetPosition(this).X);
            if (hover == _hover) return;
            _hover = hover;
            ToolTip.SetTip(this, _hover < 0 ? null : LocalizationService.Shared.Get("Profiler.SampleTip",
                _samples[_hover].SnapshotId, _samples[_hover].ReceivedSeconds.ToString("0.00", CultureInfo.CurrentCulture),
                _samples[_hover].TotalMs.ToString("0.####", CultureInfo.CurrentCulture)));
            InvalidateVisual();
        };
        PointerExited += (_, _) => { _hover = -1; InvalidateVisual(); };
        PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var index = IndexAt(e.GetPosition(this).X);
            if (index >= 0) { SelectedIndex = index; SampleSelected?.Invoke(index); InvalidateVisual(); }
        };
        PointerWheelChanged += (_, e) =>
        {
            _visibleCount = Math.Clamp(_visibleCount + (e.Delta.Y > 0 ? -20 : 20), 20, PreviewProfileHistory.Capacity);
            e.Handled = true;
            InvalidateVisual();
        };
    }

    public void Update(IReadOnlyList<ProfileSample> samples, int selected, bool followLive = true)
    {
        _samples = samples;
        SelectedIndex = selected;
        if (followLive || samples.Count == 0) _frozenEnd = null;
        else if (_frozenEnd is null || samples.All(sample => sample.SnapshotId != _frozenEnd))
            _frozenEnd = samples[^1].SnapshotId;
        _hover = -1;
        InvalidateVisual();
    }

    private int End => _frozenEnd is { } id ?
        Enumerable.Range(0, _samples.Count).FirstOrDefault(i => _samples[i].SnapshotId == id, _samples.Count - 1) + 1 :
        _samples.Count;
    private int Start => Math.Max(0, End - _visibleCount);
    private Rect Plot => new(64, 24, Math.Max(1, Bounds.Width - 82), Math.Max(1, Bounds.Height - 52));
    private int IndexAt(double x)
    {
        if (_samples.Count == 0 || x < Plot.Left || x > Plot.Right) return -1;
        return Start + (int)Math.Round((x - Plot.Left) / Plot.Width * Math.Max(0, End - Start - 1));
    }
    private void Label(DrawingContext context, string text, Point point) =>
        context.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Inter"), 11, Foreground), point);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        var plot = Plot;
        var start = Start;
        var end = End;
        var count = end - start;
        if (count > 0 && (!ReferenceEquals(_cachedFirst, _samples[start]) ||
            !ReferenceEquals(_cachedLast, _samples[end - 1]) || _cachedPlot != plot))
        {
            _cachedFirst = _samples[start];
            _cachedLast = _samples[end - 1];
            _cachedPlot = plot;
            _maximum = Math.Max(0.001, _samples.Skip(start).Take(count).Max(sample => sample.TotalMs) * 1.15);
            _areas = BuildAreas(plot, start, count, _maximum);
        }
        var maximum = count == 0 ? 0.001 : _maximum;
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(45, 130, 145, 165)), 1);
        for (var line = 0; line <= 4; ++line)
        {
            var y = plot.Bottom - plot.Height * line / 4;
            context.DrawLine(grid, new(plot.Left, y), new(plot.Right, y));
            Label(context, (maximum * line / 4).ToString("0.####", CultureInfo.CurrentCulture), new(4, y - 7));
        }
        Label(context, "ms", new(4, 2));
        if (count == 0)
        {
            Label(context, LocalizationService.Shared.Get("Profiler.ProfileEmpty"), new(plot.Left + 12, plot.Top + 20));
            return;
        }
        double X(int i) => plot.Left + plot.Width * i / Math.Max(1, count - 1);
        for (var category = 0; category < _areas.Length; ++category)
            context.DrawGeometry(Palette[category], null, _areas[category]);
        for (var i = 0; i <= 4; ++i)
        {
            var index = (int)Math.Round((count - 1) * i / 4d);
            var x = X(index);
            context.DrawLine(grid, new(x, plot.Top), new(x, plot.Bottom));
            Label(context, _samples[start + index].ReceivedSeconds.ToString("0.0", CultureInfo.CurrentCulture) + " s",
                new(Math.Min(x, plot.Right - 40), plot.Bottom + 7));
        }
        var cursor = _hover >= 0 ? _hover : SelectedIndex;
        if (cursor >= start && cursor < end)
        {
            var x = X(cursor - start);
            context.DrawLine(new Pen(Foreground, 1.5), new(x, plot.Top), new(x, plot.Bottom));
            context.DrawEllipse(Foreground, null, new(x, plot.Bottom - _samples[cursor].TotalMs / maximum * plot.Height), 3, 3);
        }
    }

    private StreamGeometry[] BuildAreas(Rect plot, int start, int count, double maximum)
    {
        double X(int i) => plot.Left + plot.Width * i / Math.Max(1, count - 1);
        var areas = new StreamGeometry[6];
        var bases = new double[count];
        for (var category = 0; category < 6; ++category)
        {
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(new(X(0), plot.Bottom - bases[0] / maximum * plot.Height), true);
                for (var i = 0; i < count; ++i)
                    path.LineTo(new(X(i), plot.Bottom - (bases[i] + _samples[start + i].Categories[category]) / maximum * plot.Height));
                for (var i = count - 1; i >= 0; --i)
                    path.LineTo(new(X(i), plot.Bottom - bases[i] / maximum * plot.Height));
                path.EndFigure(true);
            }
            areas[category] = geometry;
            for (var i = 0; i < count; ++i) bases[i] += _samples[start + i].Categories[category];
        }
        return areas;
    }
}
