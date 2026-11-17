using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using LiberaFx.Inspector.Abstractions;

namespace LiberaFx.SampleInspectors.ColorHsv;

public sealed class HsvPlugin : IInspectorPlugin
{
    public string Id => "sample.color.hsv";
    public string Name => "Color HSV Inspector";
    public Version Version => new(1, 0, 0);
    public int ApiVersion => InspectorSdk.ApiVersion;
    public IReadOnlyList<IInspectorDefinition> Inspectors { get; } = [new HsvInspector()];
}

public sealed class HsvInspector : IInspectorDefinition
{
    public string Id => "hsv-sliders";
    public string Name => "HSV sliders";
    public string Description => "Edits hue, saturation, value and alpha.";
    public IReadOnlyList<string> SupportedTypes { get; } = ["color"];

    public Control CreateEditor(IInspectorContext context) => new HsvColorEditor(context);
}

internal sealed class HsvColorEditor : UserControl, IDisposable
{
    private readonly IInspectorContext _context;
    private readonly Slider[] _sliders =
    [
        new() { Minimum = 0, Maximum = 360, TickFrequency = 1 },
        new() { Minimum = 0, Maximum = 1, TickFrequency = 0.01 },
        new() { Minimum = 0, Maximum = 1, TickFrequency = 0.01 },
        new() { Minimum = 0, Maximum = 1, TickFrequency = 0.01 }
    ];
    private bool _refreshing;

    public HsvColorEditor(IInspectorContext context)
    {
        _context = context;
        var names = new[] { "H", "S", "V", "A" };
        var rows = new StackPanel { Spacing = 4 };
        for (var index = 0; index < names.Length; index++)
        {
            var slider = _sliders[index];
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*"), ColumnSpacing = 6 };
            row.Children.Add(new TextBlock { Text = names[index], VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(slider);
            Grid.SetColumn(slider, 1);
            rows.Children.Add(row);
            var component = index;
            slider.ValueChanged += (_, _) => Save(component);
        }
        Content = rows;
        context.ValueChanged += Refresh;
        context.Lifetime.Register(() => context.ValueChanged -= Refresh);
        Refresh(this, EventArgs.Empty);
    }

    public void Dispose() => _context.ValueChanged -= Refresh;

    private void Refresh(object? sender, EventArgs e)
    {
        if (_context.Value.ValueKind != JsonValueKind.Array || _context.Value.GetArrayLength() != 4) return;
        var rgba = _context.Value.EnumerateArray().Select(value => value.GetDouble()).ToArray();
        var (hue, saturation, value) = ToHsv(rgba[0], rgba[1], rgba[2]);
        _refreshing = true;
        _sliders[0].Value = hue;
        _sliders[1].Value = saturation;
        _sliders[2].Value = value;
        _sliders[3].Value = Math.Clamp(rgba[3], 0, 1);
        _refreshing = false;
    }

    private void Save(int changed)
    {
        if (_refreshing || _context.IsReadOnly) return;
        var rgba = _context.Value.EnumerateArray().Select(value => value.GetDouble()).ToArray();
        var (hue, saturation, value) = ToHsv(rgba[0], rgba[1], rgba[2]);
        if (changed == 0) hue = _sliders[0].Value;
        if (changed == 1) saturation = _sliders[1].Value;
        if (changed == 2) value = _sliders[2].Value;
        if (changed == 3) rgba[3] = _sliders[3].Value;
        var rgb = ToRgb(hue, saturation, value);
        _context.TrySetValue(JsonSerializer.SerializeToElement(new[] { rgb.R, rgb.G, rgb.B, rgba[3] }), out _);
    }

    private static (double Hue, double Saturation, double Value) ToHsv(double red, double green, double blue)
    {
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        var delta = maximum - minimum;
        var hue = delta == 0 ? 0 :
            maximum == red ? 60 * (((green - blue) / delta) % 6) :
            maximum == green ? 60 * ((blue - red) / delta + 2) :
            60 * ((red - green) / delta + 4);
        if (hue < 0) hue += 360;
        return (hue, maximum == 0 ? 0 : delta / maximum, maximum);
    }

    private static (double R, double G, double B) ToRgb(double hue, double saturation, double value)
    {
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        var minimum = value - chroma;
        var (red, green, blue) = hue switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x)
        };
        return (red + minimum, green + minimum, blue + minimum);
    }
}
