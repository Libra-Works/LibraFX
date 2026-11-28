using Avalonia.Controls;
using Avalonia.Interactivity;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;

namespace LiberaFx.Views.Windows;

public partial class GroupUpdatesWindow : Window
{
    private IReadOnlyList<GroupUpdateCandidate> _candidates = [];

    public GroupUpdatesWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            LocalizationService.Shared.LanguageChanged += OnLanguageChanged;
            RefreshText();
        };
        Closed += (_, _) => LocalizationService.Shared.LanguageChanged -= OnLanguageChanged;
    }

    public GroupUpdatesWindow(IReadOnlyList<GroupUpdateCandidate> candidates) : this()
    {
        _candidates = candidates;
        RefreshText();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshText();

    private void RefreshText()
    {
        var text = LocalizationService.Shared;
        var eligible = _candidates.Count(item => !item.HasLocalChanges);
        var conflicts = _candidates.Count - eligible;
        Summary.Text = text.Get("Editor.GroupUpdatesSummary", _candidates.Count);
        var lines = _candidates.Take(30).Select(item =>
            $"{item.Name}: v{item.CurrentRevision} -> v{item.LatestRevision}" +
            (item.HasLocalChanges ? text.Get("Editor.GroupUpdateConflict") : ""));
        var note = conflicts == 0
            ? text.Get("Editor.GroupUpdateUndo")
            : text.Get("Editor.GroupUpdateConflicts", conflicts);
        Details.Text = string.Join(Environment.NewLine, lines) +
            (_candidates.Count > 30 ? $"{Environment.NewLine}..." : "") + $"{Environment.NewLine}{Environment.NewLine}{note}";
        ApplyButton.IsEnabled = eligible > 0;
    }

    private void OnKeepClick(object? sender, RoutedEventArgs e) => Close(false);
    private void OnApplyClick(object? sender, RoutedEventArgs e) => Close(true);
}
