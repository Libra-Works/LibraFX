using Avalonia;
using Avalonia.Controls;

namespace LiberaFx.Views.Controls;

internal sealed class VectorComponentPanel : Panel
{
    private const double MinimumCellWidth = 64;
    private const double Spacing = 4;
    private double _rowHeight;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width)
            ? availableSize.Width
            : Math.Max(0, Children.Count * (MinimumCellWidth + Spacing) - Spacing);
        var (columns, cellWidth) = GetColumns(width);
        _rowHeight = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(cellWidth, availableSize.Height));
            _rowHeight = Math.Max(_rowHeight, child.DesiredSize.Height);
        }
        var rows = (Children.Count + columns - 1) / columns;
        return new Size(width, rows * _rowHeight + Math.Max(0, rows - 1) * Spacing);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (columns, cellWidth) = GetColumns(finalSize.Width);
        for (var index = 0; index < Children.Count; index++)
        {
            Children[index].Arrange(new Rect(index % columns * (cellWidth + Spacing),
                index / columns * (_rowHeight + Spacing), cellWidth, _rowHeight));
        }
        return finalSize;
    }

    private (int Columns, double CellWidth) GetColumns(double width)
    {
        var capacity = Math.Clamp((int)((width + Spacing) / (MinimumCellWidth + Spacing)), 1, 4);
        var columns = capacity >= Children.Count ? Math.Max(1, Children.Count) : Math.Min(capacity, 2);
        return (columns, Math.Max(0, (width - (columns - 1) * Spacing) / columns));
    }
}
