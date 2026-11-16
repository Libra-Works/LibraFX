using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Globalization;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Appearance;
using Avalonia.Markup.Xaml.MarkupExtensions;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public sealed class ParameterEditorHost : ContentControl
{
    private readonly List<RadioButton> _booleanOptions = [];
    private readonly List<TextBlock> _scrubLabels = [];
    private TextBlock? _missingEditorMessage;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _booleanOptions.Clear();
        _scrubLabels.Clear();
        _missingEditorMessage = null;
        if (DataContext is not NodeParameterEditorViewModel parameter)
        {
            Content = null;
            return;
        }
        var editorKind = parameter.Editor?.Kind;
        var custom = editorKind is null
            ? InspectorEditors.CreateEditor(parameter)
            : InspectorEditors.CreateNamedEditor(parameter, editorKind);
        var editor = custom ?? (editorKind == "slider" ? CreateSlider(parameter) : CreateStandard(parameter));
        editor.Bind(IsEnabledProperty, new Binding(nameof(parameter.IsEnabled)));
        editor.Bind(AutomationProperties.NameProperty, new Binding(nameof(parameter.Name)));
        var status = new TextBlock { Name = "ParameterStatus", Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(parameter.Status)));
        status.Bind(IsVisibleProperty, new Binding(nameof(parameter.Status)) { Converter = StringConverters.IsNotNullOrEmpty });
        _missingEditorMessage = new TextBlock
        {
            IsVisible = custom is null && editorKind is not (null or "slider" or "text" or "radio" or "multiline"),
            Foreground = Brushes.OrangeRed,
            TextWrapping = TextWrapping.Wrap
        };
        var missing = _missingEditorMessage;
        missing.IsVisible = custom is null && editorKind is not (null or "slider" or "text" or "radio" or "multiline");
        var label = new TextBlock { FontWeight = FontWeight.Normal };
        label.Bind(TextBlock.TextProperty, new Binding(nameof(parameter.Name)));
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        label.Bind(ToolTip.TipProperty, new Binding(nameof(parameter.Description)));
        if (parameter.Type is "float" or "int" && editor is TextBox scalar)
            EnableScrubbing(label, scalar, parameter, nameof(parameter.ValueText));
        var disconnect = CreateActionButton("DisconnectParameterButton", EditorIcon.Disconnect);
        disconnect.Bind(Button.CommandProperty, new Binding(nameof(parameter.DisconnectCommand)));
        disconnect.Bind(IsVisibleProperty, new Binding(nameof(parameter.HasConnection)));
        disconnect.Bind(ToolTip.TipProperty, new Binding(nameof(parameter.DisconnectHint)));
        disconnect.Bind(AutomationProperties.NameProperty, new Binding(nameof(parameter.DisconnectHint)));
        var reset = CreateActionButton("ResetParameterButton", EditorIcon.Reset);
        reset.Bind(Button.CommandProperty, new Binding(nameof(parameter.ResetCommand)));
        reset.Bind(ToolTip.TipProperty, new Binding(nameof(parameter.ResetHint)));
        reset.Bind(AutomationProperties.NameProperty, new Binding(nameof(parameter.ResetHint)));
        var publish = CreateActionButton("PublishGroupParameterButton", EditorIcon.Pin);
        publish.Bind(Button.CommandProperty, new Binding(nameof(parameter.PublishCommand)));
        publish.Bind(IsVisibleProperty, new Binding(nameof(parameter.ShowPublish)));
        publish.Bind(ToolTip.TipProperty, new Binding(nameof(parameter.PublishHint)));
        publish.Bind(AutomationProperties.NameProperty, new Binding(nameof(parameter.PublishHint)));
        var actions = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children = { publish, disconnect, reset }
        };
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("124,*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            ColumnSpacing = 6,
            RowSpacing = 2,
            MinHeight = 28,
            Children = { label, editor, actions, status, missing }
        };
        Grid.SetColumn(editor, 1);
        Grid.SetColumn(actions, 2);
        Grid.SetColumn(status, 1);
        Grid.SetRow(status, 1);
        Grid.SetColumn(missing, 1);
        Grid.SetRow(missing, 2);
        Content = row;
        RefreshLocalizedLabels(editorKind);
    }

    private static IconButton CreateActionButton(string name, EditorIcon icon)
    {
        var button = new IconButton
        {
            Name = name, Icon = icon,
            Width = 24, Height = 24, MinWidth = 24, MinHeight = 24,
            Padding = new Thickness(3),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        button.Bind(ForegroundProperty, new DynamicResourceExtension("EffectMutedForegroundBrush"));
        if (button.Content is Control glyph)
        {
            glyph.Width = 14;
            glyph.Height = 14;
        }
        return button;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LocalizationService.Shared.LanguageChanged += OnLanguageChanged;
        RefreshLocalizedLabels(DataContext is NodeParameterEditorViewModel parameter ? parameter.Editor?.Kind : null);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LocalizationService.Shared.LanguageChanged -= OnLanguageChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        RefreshLocalizedLabels(DataContext is NodeParameterEditorViewModel parameter ? parameter.Editor?.Kind : null);

    private void RefreshLocalizedLabels(string? editorKind)
    {
        if (_missingEditorMessage is not null && DataContext is NodeParameterEditorViewModel parameter)
            _missingEditorMessage.Text = LocalizationService.Shared.Get("Panels.MissingInspector", editorKind);
        if (_booleanOptions.Count == 2)
        {
            _booleanOptions[0].Content = LocalizationService.Shared.Get("Panels.True");
            _booleanOptions[1].Content = LocalizationService.Shared.Get("Panels.False");
        }
        foreach (var label in _scrubLabels)
            ToolTip.SetTip(label, LocalizationService.Shared.Get("Panels.ScrubValueHelp"));
    }

    private Control CreateStandard(NodeParameterEditorViewModel parameter)
    {
        if (parameter.Type == "bool")
        {
            if (parameter.Editor?.Kind == "radio") return CreateBooleanRadio(parameter);
            var check = new CheckBox();
            check.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(parameter.BoolValue)) { Mode = BindingMode.TwoWay });
            return check;
        }
        if (GraphEnumDefinition.TryGetTypeId(parameter.Type, out _))
            return CreateEnumEditor(parameter);
        if (parameter.Type is "vector2" or "vector3" or "vector4")
        {
            var count = parameter.Type == "vector2" ? 2 : parameter.Type == "vector3" ? 3 : 4;
            var panel = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", count))), ColumnSpacing = 4 };
            var index = 0;
            foreach (var component in new[] { "X", "Y", "Z", "W" }.Take(count))
            {
                var box = CreateTextBox(component);
                box.MinWidth = 35;
                var label = new TextBlock { Text = component, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                EnableScrubbing(label, box, parameter, component);
                var cell = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                    ColumnSpacing = 3,
                    Children = { label, box }
                };
                Grid.SetColumn(box, 1);
                Grid.SetColumn(cell, index++);
                panel.Children.Add(cell);
            }
            return panel;
        }
        if (parameter.Type == "color")
        {
            var button = new Button { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            void RefreshButton()
            {
                var color = parameter.ColorValue;
                button.Content = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new Border { Width = 24, Height = 18, Child = new ColorSwatch { Color = color },
                            BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) },
                        new TextBlock { Text = parameter.ColorComponents.Intensity > 1
                            ? $"HDR {parameter.DisplayValue}" : color.ToString(), TextTrimming = TextTrimming.CharacterEllipsis }
                    }
                };
            }
            RefreshButton();
            parameter.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(parameter.ColorValue)) RefreshButton();
            };
            button.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(button) is not Window owner) return;
                var dialog = new ColorEditorWindow(parameter.ColorComponents);
                var selected = await dialog.ShowDialog<RgbaColor?>(owner);
                if (selected is { } color) parameter.SetColor(color);
            };
            return button;
        }
        var text = CreateTextBox(nameof(parameter.ValueText));
        if (parameter.Editor?.Kind == "multiline")
        {
            text.AcceptsReturn = true;
            text.TextWrapping = TextWrapping.Wrap;
            text.MinHeight = 100;
            text.MaxHeight = 280;
        }
        return text;
    }

    private Control CreateBooleanRadio(NodeParameterEditorViewModel parameter)
    {
        var group = Guid.NewGuid().ToString();
        var on = new RadioButton { GroupName = group };
        var off = new RadioButton { GroupName = group };
        _booleanOptions.Add(on);
        _booleanOptions.Add(off);
        var syncing = false;
        void Refresh()
        {
            syncing = true;
            on.IsChecked = parameter.BoolValue;
            off.IsChecked = !parameter.BoolValue;
            syncing = false;
        }
        Refresh();
        on.IsCheckedChanged += (_, _) => { if (!syncing && on.IsChecked == true) parameter.BoolValue = true; };
        off.IsCheckedChanged += (_, _) => { if (!syncing && off.IsChecked == true) parameter.BoolValue = false; };
        parameter.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(parameter.BoolValue)) Refresh(); };
        return new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Children = { on, off } };
    }

    private static Control CreateEnumEditor(NodeParameterEditorViewModel parameter)
    {
        var combo = new ComboBox { DisplayMemberBinding = new Binding(nameof(GraphEnumValue.Name)) };
        var syncing = false;
        void Refresh()
        {
            syncing = true;
            var values = parameter.EnumValues;
            combo.ItemsSource = values;
            combo.SelectedItem = values.FirstOrDefault(item => item.Value == parameter.EnumValue);
            syncing = false;
        }
        Refresh();
        combo.SelectionChanged += (_, _) =>
        {
            if (!syncing && combo.SelectedItem is GraphEnumValue value) parameter.EnumValue = value.Value;
        };
        parameter.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(parameter.EnumValue)) Refresh(); };
        return combo;
    }

    private static Control CreateSlider(NodeParameterEditorViewModel parameter)
    {
        var options = parameter.Editor!;
        var box = CreateTextBox(nameof(parameter.ValueText));
        var slider = new Slider
        {
            Minimum = options.Minimum!.Value,
            Maximum = options.Maximum!.Value,
            TickFrequency = options.Step ?? (parameter.Type == "int" ? 1 : (options.Maximum.Value - options.Minimum.Value) / 100),
            IsSnapToTickEnabled = options.Step.HasValue || parameter.Type == "int"
        };
        var syncing = false;
        var dragging = false;
        var changed = false;
        void RefreshSlider()
        {
            syncing = true;
            slider.Value = Math.Clamp(parameter.NumericValue, slider.Minimum, slider.Maximum);
            syncing = false;
        }
        RefreshSlider();
        parameter.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(parameter.NumericValue)) RefreshSlider();
        };
        slider.ValueChanged += (_, _) =>
        {
            if (syncing) return;
            if (dragging)
            {
                changed = true;
                box.SetCurrentValue(TextBox.TextProperty, slider.Value.ToString("G15", CultureInfo.CurrentCulture));
            }
            else parameter.NumericValue = slider.Value;
        };
        slider.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed) return;
            dragging = true;
            changed = false;
        }, RoutingStrategies.Tunnel);
        slider.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (!dragging || e.InitialPressMouseButton != MouseButton.Left) return;
            dragging = false;
            if (changed) parameter.NumericValue = slider.Value;
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        slider.PointerCaptureLost += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!dragging) return;
            dragging = false;
            parameter.Refresh();
        });
        var panel = new Grid { ColumnDefinitions = new ColumnDefinitions("80,*"), ColumnSpacing = 8, Children = { box, slider } };
        Grid.SetColumn(slider, 1);
        return panel;
    }

    private void EnableScrubbing(TextBlock label, TextBox box, NodeParameterEditorViewModel parameter, string property)
    {
        _scrubLabels.Add(label);
        ToolTip.SetTip(label, LocalizationService.Shared.Get("Panels.ScrubValueHelp"));
        IPointer? active = null;
        Point start = default;
        double initial = 0;
        string? original = null;
        string? preview = null;
        label.Cursor = new Cursor(StandardCursorType.SizeWestEast);
        label.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(label).Properties.IsLeftButtonPressed ||
                !double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out initial) ||
                !double.IsFinite(initial) || TopLevel.GetTopLevel(label) is not { } top) return;
            start = e.GetPosition(top);
            original = box.Text;
            active = e.Pointer;
            preview = null;
            active.Capture(label);
            e.Handled = true;
        };
        label.PointerMoved += (_, e) =>
        {
            if (active != e.Pointer || TopLevel.GetTopLevel(label) is not { } top) return;
            var delta = e.GetPosition(top).X - start.X;
            if (Math.Abs(delta) < 2)
            {
                preview = null;
                box.SetCurrentValue(TextBox.TextProperty, original);
                return;
            }
            var step = parameter.Editor?.Step ?? (parameter.Type == "int" ? 1 : 0.1);
            var value = initial + Math.Round(delta) * step;
            if (!double.IsFinite(value) || (parameter.Type == "int" && (value < int.MinValue || value > int.MaxValue))) return;
            preview = (parameter.Type == "int" ? Math.Round(value) : value).ToString("G15", CultureInfo.CurrentCulture);
            box.SetCurrentValue(TextBox.TextProperty, preview);
            e.Handled = true;
        };
        label.PointerReleased += (_, e) =>
        {
            if (active != e.Pointer) return;
            active = null;
            e.Pointer.Capture(null);
            if (preview is { } value)
            {
                switch (property)
                {
                    case nameof(parameter.X): parameter.X = value; break;
                    case nameof(parameter.Y): parameter.Y = value; break;
                    case nameof(parameter.Z): parameter.Z = value; break;
                    case nameof(parameter.W): parameter.W = value; break;
                    default: parameter.ValueText = value; break;
                }
            }
            e.Handled = true;
        };
        label.PointerCaptureLost += (_, _) =>
        {
            if (active is null) return;
            active = null;
            parameter.Refresh();
        };
    }

    private static TextBox CreateTextBox(string property)
    {
        var box = new TextBox
        {
            MinWidth = 0, MinHeight = 26,
            Padding = new Thickness(6, 3),
            CornerRadius = new CornerRadius(2),
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        box.Bind(TextBox.TextProperty, new Binding(property)
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
        });
        return box;
    }
}
