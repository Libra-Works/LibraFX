using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LiberaFx.Models.Appearance;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

internal sealed class FlexiblePlanePointsEditor : StackPanel
{
    private readonly NodeParameterEditorViewModel _parameter;
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly Button _addButton = new() { Classes = { "editor-action" } };
    private bool _syncing;

    public FlexiblePlanePointsEditor(NodeParameterEditorViewModel parameter)
    {
        _parameter = parameter;
        Spacing = 6;
        _addButton.Content = LocalizationService.Shared.Get("FlexiblePlane.AddPoint");
        _addButton.Click += (_, _) => AddPoint();
        Children.Add(_list);
        Children.Add(_addButton);
        AttachedToVisualTree += (_, _) => _parameter.PropertyChanged += OnParameterChanged;
        DetachedFromVisualTree += (_, _) => _parameter.PropertyChanged -= OnParameterChanged;
        Rebuild();
    }

    private void OnParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_syncing && e.PropertyName is nameof(NodeParameterEditorViewModel.ValueText) or nameof(NodeParameterEditorViewModel.Name))
            Rebuild();
    }

    private List<FlexiblePlaneDrawSchema.Point> ReadPoints()
    {
        try { return [.. FlexiblePlaneDrawSchema.ParsePoints(_parameter.ValueText)]; }
        catch (InvalidDataException)
        {
            return
            [
                new([0, 0, 0], 1, [1, 1, 1, 1]),
                new([1, 0, 0], 1, [1, 1, 1, 1])
            ];
        }
    }

    private void Commit(IReadOnlyList<FlexiblePlaneDrawSchema.Point> points)
    {
        _syncing = true;
        try
        {
            var payload = points.Select(point => new
            {
                position = point.Position,
                width = point.Width,
                color = point.Color
            });
            var json = JsonSerializer.Serialize(payload);
            _parameter.TrySetInspectorValue(JsonSerializer.SerializeToElement(json));
        }
        finally { _syncing = false; }
        Rebuild();
    }

    private void AddPoint()
    {
        var points = ReadPoints();
        if (points.Count >= 64) return;
        var last = points[^1];
        points.Add(new([last.Position[0] + 1, last.Position[1], last.Position[2]], last.Width, [.. last.Color]));
        Commit(points);
    }

    private void RemovePoint(int index)
    {
        var points = ReadPoints();
        if (points.Count <= 2 || index < 0 || index >= points.Count) return;
        points.RemoveAt(index);
        Commit(points);
    }

    private void Rebuild()
    {
        _addButton.Content = LocalizationService.Shared.Get("FlexiblePlane.AddPoint");
        var points = ReadPoints();
        _addButton.IsEnabled = points.Count < 64;
        _list.Children.Clear();
        for (var i = 0; i < points.Count; i++)
        {
            var index = i;
            var point = points[i];
            var card = new Border
            {
                Padding = new Thickness(8, 6),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1)
            };
            card.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("EffectSurfaceBrush"));
            card.Bind(Border.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("EffectBorderBrush"));

            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            header.Children.Add(new TextBlock
            {
                Text = LocalizationService.Shared.Get("FlexiblePlane.PointItem", index),
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            var remove = new IconButton
            {
                Icon = EditorIcon.Delete,
                Width = 24, Height = 24, MinWidth = 24, MinHeight = 24,
                Padding = new Thickness(3),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                IsEnabled = points.Count > 2
            };
            ToolTip.SetTip(remove, LocalizationService.Shared.Get("FlexiblePlane.RemovePoint"));
            remove.Click += (_, _) => RemovePoint(index);
            Grid.SetColumn(remove, 1);
            header.Children.Add(remove);

            var posGrid = CreateVectorRow(
                LocalizationService.Shared.Get("FlexiblePlane.Position"),
                ["X", "Y", "Z"],
                point.Position,
                (axis, val) =>
                {
                    var current = ReadPoints();
                    if (index >= current.Count) return;
                    var pos = (double[])current[index].Position.Clone();
                    pos[axis] = val;
                    current[index] = current[index] with { Position = pos };
                    Commit(current);
                });

            var widthBox = CreateNumberBox(point.Width, val =>
            {
                if (val < 0) return;
                var current = ReadPoints();
                if (index >= current.Count) return;
                current[index] = current[index] with { Width = val };
                Commit(current);
            });
            var widthRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6 };
            var widthLabel = new TextBlock
            {
                Text = LocalizationService.Shared.Get("FlexiblePlane.Width"),
                VerticalAlignment = VerticalAlignment.Center
            };
            widthRow.Children.Add(widthLabel);
            Grid.SetColumn(widthBox, 1);
            widthRow.Children.Add(widthBox);

            var colorGrid = CreateVectorRow(
                LocalizationService.Shared.Get("FlexiblePlane.Color"),
                ["R", "G", "B", "A"],
                point.Color,
                (channel, val) =>
                {
                    var current = ReadPoints();
                    if (index >= current.Count) return;
                    var col = (double[])current[index].Color.Clone();
                    col[channel] = val;
                    current[index] = current[index] with { Color = col };
                    Commit(current);
                });

            card.Child = new StackPanel
            {
                Spacing = 4,
                Children = { header, posGrid, widthRow, colorGrid }
            };
            _list.Children.Add(card);
        }
    }

    private static Grid CreateVectorRow(string title, string[] labels, double[] values, Action<int, double> onCommit)
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnDefinitions = new ColumnDefinitions(string.Join(",", labels.Select(_ => "Auto,*"))),
            ColumnSpacing = 4,
            RowSpacing = 2
        };
        var titleBlock = new TextBlock { Text = title };
        titleBlock.Bind(TextBlock.ForegroundProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("EffectMutedForegroundBrush"));
        Grid.SetColumnSpan(titleBlock, labels.Length * 2);
        grid.Children.Add(titleBlock);
        for (var i = 0; i < labels.Length; i++)
        {
            var idx = i;
            var label = new TextBlock { Text = labels[i], VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, 1);
            Grid.SetColumn(label, i * 2);
            var box = CreateNumberBox(values[i], v => onCommit(idx, v));
            Grid.SetRow(box, 1);
            Grid.SetColumn(box, i * 2 + 1);
            grid.Children.Add(label);
            grid.Children.Add(box);
        }
        return grid;
    }

    private static TextBox CreateNumberBox(double value, Action<double> onCommit)
    {
        var box = new TextBox
        {
            MinWidth = 0, MinHeight = 24,
            Padding = new Thickness(4, 2),
            Text = value.ToString("G9", CultureInfo.CurrentCulture),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        box.LostFocus += (_, _) =>
        {
            if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed) &&
                double.IsFinite(parsed))
                onCommit(parsed);
            else
                box.Text = value.ToString("G9", CultureInfo.CurrentCulture);
        };
        return box;
    }
}
