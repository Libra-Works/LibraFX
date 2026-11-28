using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace LiberaFx.Views.Graph;

public sealed class GraphPositionConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count == 2 && values[0] is double position && values[1] is double origin
            ? position + origin
            : AvaloniaProperty.UnsetValue;
}
