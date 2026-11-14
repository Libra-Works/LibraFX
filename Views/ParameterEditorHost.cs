using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System.Globalization;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public sealed class ParameterEditorHost : ContentControl
{
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
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
        var status = new TextBlock { Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(parameter.Status)));
        var missing = new TextBlock
        {
            Text = $"Inspector '{editorKind}' is unavailable; using the standard editor.",
            IsVisible = custom is null && editorKind is not (null or "slider" or "text"),
            Foreground = Brushes.OrangeRed,
            TextWrapping = TextWrapping.Wrap
        };
        var label = new TextBlock { Text = parameter.Name, FontWeight = FontWeight.SemiBold };
        if (parameter.Type is "float" or "int" && editor is TextBox scalar)
            EnableScrubbing(label, scalar, parameter, nameof(parameter.ValueText));
        Content = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                label,
                editor,
                status,
                missing
            }
        };
    }

    private static Control CreateStandard(NodeParameterEditorViewModel parameter)
    {
        if (parameter.Type == "bool")
        {
            var check = new CheckBox();
            check.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(parameter.BoolValue)) { Mode = BindingMode.TwoWay });
            return check;
        }
        if (parameter.Type is "vector2" or "vector3" or "vector4")
        {
            var count = parameter.Type == "vector2" ? 2 : parameter.Type == "vector3" ? 3 : 4;
            var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
            foreach (var component in new[] { "X", "Y", "Z", "W" }.Take(count))
            {
                var box = CreateTextBox(component);
                box.Width = 65;
                var label = new TextBlock { Text = component };
                EnableScrubbing(label, box, parameter, component);
                panel.Children.Add(new StackPanel
                {
                    Spacing = 2,
                    Children = { label, box }
                });
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
                        new Border { Width = 24, Height = 18, Background = new SolidColorBrush(color),
                            BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) },
                        new TextBlock { Text = color.ToString() }
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
                var dialog = new ColorEditorWindow(parameter.ColorValue);
                var selected = await dialog.ShowDialog<Color?>(owner);
                if (selected is { } color) parameter.SetColor(color);
            };
            return button;
        }
        return CreateTextBox(nameof(parameter.ValueText));
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
        return new StackPanel { Spacing = 4, Children = { slider, box } };
    }

    private static void EnableScrubbing(TextBlock label, TextBox box, NodeParameterEditorViewModel parameter, string property)
    {
        label.Cursor = new Cursor(StandardCursorType.SizeWestEast);
        ToolTip.SetTip(label, "Drag left or right to adjust the value.");
        IPointer? active = null;
        Point start = default;
        double initial = 0;
        string? original = null;
        string? preview = null;
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
        var box = new TextBox { MinWidth = 0 };
        box.Bind(TextBox.TextProperty, new Binding(property)
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
        });
        return box;
    }
}
