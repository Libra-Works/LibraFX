using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace LiberaFx.Views;

public partial class ColorEditorWindow : Window
{
    public ColorEditorWindow() => InitializeComponent();

    public ColorEditorWindow(Color color) : this() => Picker.Color = color;

    private void OnAccept(object? sender, RoutedEventArgs e) => Close(Picker.Color);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
