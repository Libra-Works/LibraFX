using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using LiberaFx.Inspector.Abstractions;

namespace LiberaFx.SampleInspectors.ColorRgb;

public sealed class RgbPlugin : IInspectorPlugin
{
#if LIBERAFX_BUNDLED_INSPECTOR
    public string Id => "liberafx.inspector.color.rgb";
#else
    public string Id => "sample.color.rgb";
#endif
    public string Name => "Color RGB Inspector";
    public Version Version => new(1, 0, 0);
    public int ApiVersion => InspectorSdk.ApiVersion;
    public IReadOnlyList<IInspectorDefinition> Inspectors { get; } = [new RgbInspector()];
}

public sealed class RgbInspector : IInspectorDefinition
{
    public string Id => "rgb-sliders";
    public string Name => "RGB sliders";
    public string Description => "Edits red, green, blue and alpha channels independently.";
    public IReadOnlyList<string> SupportedTypes { get; } = ["color"];

    public Control CreateEditor(IInspectorContext context) => new RgbColorEditor(context);
}

internal sealed class RgbColorEditor : UserControl, IDisposable
{
    private readonly IInspectorContext _context;
    private readonly Slider[] _sliders;
    private bool _refreshing;

    public RgbColorEditor(IInspectorContext context)
    {
        _context = context;
        var names = new[] { "R", "G", "B", "A" };
        _sliders = names.Select(_ => new Slider { Minimum = 0, Maximum = 1, TickFrequency = 0.01 }).ToArray();
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
        _refreshing = true;
        for (var index = 0; index < _sliders.Length; index++)
            _sliders[index].Value = Math.Clamp(_context.Value[index].GetDouble(), 0, 1);
        _refreshing = false;
    }

    private void Save(int changed)
    {
        if (_refreshing || _context.IsReadOnly) return;
        var values = _context.Value.EnumerateArray().Select(value => value.GetDouble()).ToArray();
        values[changed] = _sliders[changed].Value;
        _context.TrySetValue(JsonSerializer.SerializeToElement(values), out _);
    }
}
