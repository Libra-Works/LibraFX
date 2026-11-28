using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using LiberaFx.Models.Graph;
using LiberaFx.Services;

namespace LiberaFx.Views;

public sealed class GraphPortBrushConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count != 2 || values[1] is not IBrush fallback) return AvaloniaProperty.UnsetValue;
        return values[0] is GraphPortCategory category
            ? ThemeSettingsService.EditorBrush(category == GraphPortCategory.Signal ? "GraphSignal" : "GraphParameter")
            : fallback;
    }
}
