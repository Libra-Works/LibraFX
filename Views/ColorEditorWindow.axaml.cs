using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia;
using LiberaFx.Services;
using LiberaFx.Models.Graph;
using System.Globalization;

namespace LiberaFx.Views;

public partial class ColorEditorWindow : Window
{
    private readonly ColorPaletteService _palette = new();
    private RgbaColor _color = new(1, 1, 1, 1);
    private bool _updating;

    public ColorEditorWindow()
    {
        InitializeComponent();
        Picker.ColorChanged += (_, _) => OnPickerColorChanged();
        UpdateControls();
        RefreshPalette();
    }

    public ColorEditorWindow(RgbaColor color) : this()
    {
        _color = color;
        UpdateControls();
    }

    public ColorEditorWindow(Color color) : this(RgbaColor.FromColor(color)) { }

    private void OnAccept(object? sender, RoutedEventArgs e)
    {
        if (!TryReadComponents(out var color)) return;
        _color = color;
        _palette.AddRecent(_color);
        Close(_color);
    }
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnAddFavorite(object? sender, RoutedEventArgs e)
    {
        if (!TryReadComponents(out var color)) return;
        _color = color;
        UpdateControls();
        _palette.AddFavorite(_color);
        RefreshPalette();
    }

    private void OnComponentsEdited(object? sender, RoutedEventArgs e)
    {
        if (_updating || !TryReadComponents(out var color)) return;
        _color = color;
        UpdateControls();
    }

    private bool TryReadComponents(out RgbaColor color)
    {
        var boxes = new[] { RedBox, GreenBox, BlueBox, AlphaBox };
        var values = new double[4];
        for (var i = 0; i < boxes.Length; i++)
        {
            if (!double.TryParse(boxes[i].Text, NumberStyles.Float, CultureInfo.CurrentCulture, out values[i]))
            {
                color = default;
                ComponentStatus.Text = "Enter finite numeric RGBA values.";
                return false;
            }
        }
        color = new RgbaColor(values[0], values[1], values[2], values[3]);
        ComponentStatus.Text = color.IsValid ? "" : "RGB must be non-negative and alpha must be between 0 and 1.";
        return color.IsValid;
    }

    private void OnPickerColorChanged()
    {
        if (_updating) return;
        var preview = _color.Preview;
        var picked = Picker.Color;
        var rgbUnchanged = picked.R == preview.R && picked.G == preview.G && picked.B == preview.B;
        var updated = rgbUnchanged ? _color : RgbaColor.FromColor(picked, _color.Intensity);
        _color = updated with { A = picked.A == preview.A ? _color.A : picked.A / 255.0 };
        UpdateComponentFields();
    }

    private void UpdateControls()
    {
        _updating = true;
        Picker.Color = _color.Preview;
        UpdateComponentFields();
        _updating = false;
    }

    private void UpdateComponentFields()
    {
        CurrentSwatch.Color = _color.Preview;
        var boxes = new[] { RedBox, GreenBox, BlueBox, AlphaBox };
        var components = _color.Components;
        for (var i = 0; i < boxes.Length; i++)
            boxes[i].Text = components[i].ToString("G17", CultureInfo.CurrentCulture);
        ComponentStatus.Text = "";
    }

    private void RefreshPalette()
    {
        Populate(FavoritesPanel, _palette.Favorites, true);
        Populate(RecentPanel, _palette.Recent, false);
        PaletteStatus.Text = _palette.Status;
    }

    private void Populate(WrapPanel panel, IReadOnlyList<RgbaColor> colors, bool favorite)
    {
        panel.Children.Clear();
        foreach (var color in colors)
        {
            var swatch = new Button
            {
                Width = 28, Height = 28, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 4),
                Content = new ColorSwatch { Color = color.Preview },
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1)
            };
            ToolTip.SetTip(swatch, $"R {color.R:G5}  G {color.G:G5}  B {color.B:G5}  A {color.A:G5}");
            swatch.Click += (_, _) => { _color = color; UpdateControls(); };
            if (favorite)
            {
                var remove = new MenuItem { Header = "Remove favorite" };
                remove.Click += (_, _) => { _palette.RemoveFavorite(color); RefreshPalette(); };
                swatch.ContextMenu = new ContextMenu { Items = { remove } };
            }
            panel.Children.Add(swatch);
        }
    }
}
