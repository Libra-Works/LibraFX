using Avalonia;
using Avalonia.Controls;
using LiberaFx.Services;

namespace LiberaFx.Views;

public sealed class LocalizedOptionText : TextBlock
{
    public static readonly StyledProperty<object?> ValueProperty =
        AvaloniaProperty.Register<LocalizedOptionText, object?>(nameof(Value));

    public object? Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public LocalizedOptionText()
    {
        AttachedToVisualTree += (_, _) =>
        {
            LocalizationService.Shared.LanguageChanged += OnLanguageChanged;
            RefreshText();
        };
        DetachedFromVisualTree += (_, _) => LocalizationService.Shared.LanguageChanged -= OnLanguageChanged;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty) RefreshText();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshText();

    private void RefreshText()
    {
        var value = Value?.ToString() ?? string.Empty;
        var key = "Editor.Option." + value;
        var translated = LocalizationService.Shared.Get(key);
        Text = translated == key ? value : translated;
    }
}
