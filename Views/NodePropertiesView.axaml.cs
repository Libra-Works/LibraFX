using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using LiberaFx.ViewModels;
using LiberaFx.Services;

namespace LiberaFx.Views;

public partial class NodePropertiesView : UserControl
{
    public NodePropertiesView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            LocalizationService.Shared.LanguageChanged += OnLanguageChanged;
            RefreshLocalizedText();
        };
        DetachedFromVisualTree += (_, _) => LocalizationService.Shared.LanguageChanged -= OnLanguageChanged;
        DataContextChanged += (_, _) => RefreshLocalizedText();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshLocalizedText();

    private void OnTogglePinClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is NodePropertiesPaneViewModel pane) pane.TogglePin();
    }

    private void OnParameterDividerDrag(object? sender, VectorEventArgs e)
    {
        var column = ParameterHeader.ColumnDefinitions[0];
        column.Width = new GridLength(Math.Clamp(column.Width.Value + e.Vector.X, 64, 320));
        ParameterList.SetValue(ParameterEditorHost.LabelColumnWidthProperty, column.Width);
    }

    private void RefreshLocalizedText()
    {
        if (DataContext is NodePropertiesPaneViewModel pane) pane.RefreshLocalizedText();
    }

    private void OnDisconnectClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is NodePropertiesPaneViewModel pane &&
            sender is Button { Tag: GraphConnectionViewModel connection })
        {
            pane.Graph.DisconnectConnection(connection);
        }
    }

    private async void OnEditGroupCurvesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NodePropertiesPaneViewModel pane ||
            TopLevel.GetTopLevel(this) is not Window owner) return;
        using var editor = pane.CreateGroupCurveEditor();
        var window = new Window
        {
            Title = LocalizationService.Shared.Get("Panels.EditGroupCurves"),
            Width = 1100, Height = 650, MinWidth = 650, MinHeight = 350,
            Content = new CurveEditorView { DataContext = editor }
        };
        await window.ShowDialog(owner);
    }

    private async void OnConfigureGroupItemsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NodePropertiesPaneViewModel pane ||
            pane.Graph.SelectedNode is not { IsGraphGroup: true } group ||
            TopLevel.GetTopLevel(this) is not Window owner) return;
        await new GroupExposedEditorWindow(pane.Graph, group.Id).ShowDialog(owner);
    }
}
