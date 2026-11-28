using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia;
using LiberaFx.Services.Settings;
using LiberaFx.Views.Controls;
using LiberaFx.Models.Graph;
using System.Globalization;

namespace LiberaFx.Views.Windows;

public partial class ColorEditorWindow : Window
{
    private readonly ColorPaletteService _palette = new();
    private readonly List<MenuItem> _favoriteMenuItems = [];
    private RgbaColor _color = new(1, 1, 1, 1);
    private bool _updating;
    private string? _componentStatusKey;

    public ColorEditorWindow()
    {
        InitializeComponent();
        LocalizationService.Shared.LanguageChanged += OnLanguageChanged;
        Closed += OnClosed;
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

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_componentStatusKey is { } key)
            ComponentStatus.Text = LocalizationService.Shared.Get(key);
        UpdatePaletteStatus();
        foreach (var item in _favoriteMenuItems)
            item.Header = LocalizationService.Shared.Get("Panels.RemoveFavorite");
    }

    private void OnClosed(object? sender, EventArgs e) =>
        LocalizationService.Shared.LanguageChanged -= OnLanguageChanged;

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
                SetComponentStatus("Panels.InvalidRgbaNumber");
                return false;
            }
        }
        color = new RgbaColor(values[0], values[1], values[2], values[3]);
        if (color.IsValid)
            SetComponentStatus(null);
        else
            SetComponentStatus("Panels.InvalidRgbaRange");
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
        SetComponentStatus(null);
    }

    private void RefreshPalette()
    {
        Populate(FavoritesPanel, _palette.Favorites, true);
        Populate(RecentPanel, _palette.Recent, false);
        UpdatePaletteStatus();
    }

    private void UpdatePaletteStatus()
    {
        const string loadFailure = "Could not load colors: ";
        const string saveFailure = "Could not save colors: ";
        var status = _palette.Status;
        PaletteStatus.Text = status switch
        {
            "Invalid color." => LocalizationService.Shared.Get("Panels.PaletteInvalidColor"),
            "Favorites are full." => LocalizationService.Shared.Get("Panels.PaletteFavoritesFull"),
            _ when status.StartsWith(loadFailure, StringComparison.Ordinal) =>
                LocalizationService.Shared.Get("Panels.PaletteLoadFailed", status[loadFailure.Length..]),
            _ when status.StartsWith(saveFailure, StringComparison.Ordinal) =>
                LocalizationService.Shared.Get("Panels.PaletteSaveFailed", status[saveFailure.Length..]),
            _ => status
        };
    }

    private void Populate(WrapPanel panel, IReadOnlyList<RgbaColor> colors, bool favorite)
    {
        panel.Children.Clear();
        if (favorite) _favoriteMenuItems.Clear();
        foreach (var color in colors)
        {
            var swatch = new Button
            {
                Width = 28, Height = 28, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 4),
                Content = new ColorSwatch { Color = color.Preview },
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1)
            };
            var description = LocalizationService.Shared.Get(
                "Panels.ColorSwatch", color.R, color.G, color.B, color.A);
            ToolTip.SetTip(swatch, description);
            AutomationProperties.SetName(swatch, description);
            swatch.Click += (_, _) => { _color = color; UpdateControls(); };
            if (favorite)
            {
                var remove = new MenuItem
                {
                    Header = LocalizationService.Shared.Get("Panels.RemoveFavorite"),
                    Icon = new EditorIconView { Icon = LiberaFx.Models.Appearance.EditorIcon.Delete }
                };
                _favoriteMenuItems.Add(remove);
                remove.Click += (_, _) => { _palette.RemoveFavorite(color); RefreshPalette(); };
                swatch.ContextMenu = new ContextMenu { Items = { remove } };
            }
            panel.Children.Add(swatch);
        }
    }

    private void SetComponentStatus(string? key)
    {
        _componentStatusKey = key;
        ComponentStatus.Text = key is null ? "" : LocalizationService.Shared.Get(key);
    }
}
