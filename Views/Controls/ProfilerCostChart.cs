using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using LiberaFx.Services.Compilation;
using LiberaFx.Services.Preview;
using LiberaFx.Services.Settings;

namespace LiberaFx.Views.Controls;

public sealed class ProfilerCostChart : Control
{
    public static readonly StyledProperty<IBrush> ForegroundProperty =
        ProfilerHistoryChart.ForegroundProperty.AddOwner<ProfilerCostChart>();
    private IReadOnlyList<ProfileCost> _costs = [];
    private Func<Guid, string?> _nodeName = _ => null;
    private int _selected = -1;
    public event Action<ProfileCost>? CostSelected;
    static ProfilerCostChart() => AffectsRender<ProfilerCostChart>(ForegroundProperty);
    public ProfilerCostChart()
    {
        this.Bind(ForegroundProperty,
            new DynamicResourceExtension("EffectForegroundBrush"));
        PointerPressed += (_, e) =>
        {
            var row = (int)(e.GetPosition(this).Y / 34);
            if (row < 0 || row >= _costs.Count || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _selected = row;
            CostSelected?.Invoke(_costs[row]);
            InvalidateVisual();
        };
    }

    public void Update(IReadOnlyList<ProfileCost> costs, Func<Guid, string?> nodeName)
    {
        _costs = costs;
        _nodeName = nodeName;
        _selected = -1;
        Height = Math.Max(60, costs.Count * 34);
        InvalidateVisual();
    }
    private IBrush Foreground => GetValue(ForegroundProperty);
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        var maximum = Math.Max(0.000001, _costs.Select(cost => cost.ExclusiveMs).DefaultIfEmpty(0).Max());
        var labelWidth = Math.Min(280, Bounds.Width * 0.36);
        var available = Math.Max(1, Bounds.Width - labelWidth - 100);
        void Text(string text, Point point) => context.DrawText(new FormattedText(text, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Inter"), 12, Foreground), point);
        if (_costs.Count == 0) Text(LocalizationService.Shared.Get("Profiler.NoActivity"), new(12, 16));
        for (var row = 0; row < _costs.Count; ++row)
        {
            var cost = _costs[row];
            var y = row * 34 + 4;
            var brush = ProfilerHistoryChart.Palette[PreviewProfileHistory.Category(cost.Metric.Event)];
            if (row == _selected) context.DrawRectangle(new SolidColorBrush(Color.FromArgb(35, 130, 145, 165)), null,
                new Rect(0, y - 2, Bounds.Width, 32));
            context.DrawRectangle(brush, null, new Rect(5, y + 5, 4, 15));
            var name = cost.Metric.NodeId is { } id ? _nodeName(id) ?? id.ToString("D") : cost.Metric.Event;
            using (context.PushClip(new Rect(14, y, Math.Max(1, labelWidth - 24), 27))) Text(name, new(14, y + 3));
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(25, 130, 145, 165)), null,
                new Rect(labelWidth, y + 3, available, 20));
            context.DrawRectangle(brush, null, new Rect(labelWidth, y + 3, available * cost.ExclusiveMs / maximum, 20));
            Text(cost.ExclusiveMs.ToString("0.####", CultureInfo.CurrentCulture) + " ms", new(labelWidth + available + 8, y + 3));
        }
    }
}
