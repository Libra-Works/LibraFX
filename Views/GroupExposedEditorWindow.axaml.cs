using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class GroupExposedEditorWindow : Window
{
    private readonly GraphWorkspaceViewModel _graph;
    private readonly Guid _groupId;

    public GroupExposedEditorWindow()
    {
        InitializeComponent();
        _graph = null!;
        ApplyButton.IsEnabled = false;
    }

    public GroupExposedEditorWindow(GraphWorkspaceViewModel graph, Guid groupId) : this()
    {
        _graph = graph;
        _groupId = groupId;
        ApplyButton.IsEnabled = true;
        ItemSelector.ItemsSource = graph.GetNode(groupId).ChildGraph?.ExposedItems?.OrderBy(item => item.Order).ToArray() ?? [];
        ItemSelector.SelectedIndex = 0;
    }

    private void OnItemChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ItemSelector.SelectedItem is not GroupExposedItem item) return;
        NameField.Text = item.Name;
        DescriptionField.Text = item.Description;
        CategoryField.Text = item.Category;
        OrderField.Text = item.Order.ToString(CultureInfo.CurrentCulture);
        MinimumField.Text = Format(item.Minimum);
        MaximumField.Text = Format(item.Maximum);
        StepField.Text = Format(item.Step);
        PresentationField.SelectedIndex = item.Presentation switch { "text" => 1, "slider" => 2, _ => 0 };
        ErrorText.Text = "";
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (ItemSelector.SelectedItem is not GroupExposedItem previous)
                throw new InvalidOperationException(LocalizationService.Shared.Get("Panels.ExposedSelectItem"));
            var group = _graph.GetNode(_groupId);
            var current = group.ChildGraph?.ExposedItems?.FirstOrDefault(item => item.Id == previous.Id);
            if (current is null || !GroupExposedItems.Equal([previous], [current]))
                throw new InvalidOperationException(LocalizationService.Shared.Get("Panels.ExposedChanged"));
            if (!int.TryParse(OrderField.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var order))
                throw new ArgumentException(LocalizationService.Shared.Get("Panels.ExposedInvalidOrder"));
            var updated = previous with
            {
                Name = NameField.Text ?? "",
                Description = DescriptionField.Text ?? "",
                Category = CategoryField.Text ?? "",
                Order = order,
                Minimum = ParseOptional(MinimumField.Text),
                Maximum = ParseOptional(MaximumField.Text),
                Step = ParseOptional(StepField.Text),
                Presentation = PresentationField.SelectedIndex switch { 1 => "text", 2 => "slider", _ => null }
            };
            _graph.UpdateGroupExposedItem(group, updated);
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   InvalidOperationException or ArgumentException)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private static double? ParseOptional(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) || !double.IsFinite(value))
            throw new ArgumentException(LocalizationService.Shared.Get("Panels.ExposedInvalidNumber"));
        return value;
    }

    private static string Format(double? value) => value?.ToString("G17", CultureInfo.CurrentCulture) ?? "";
}
