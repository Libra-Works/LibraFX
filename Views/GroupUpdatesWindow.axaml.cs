using Avalonia.Controls;
using Avalonia.Interactivity;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class GroupUpdatesWindow : Window
{
    public GroupUpdatesWindow() => InitializeComponent();

    public GroupUpdatesWindow(IReadOnlyList<GroupUpdateCandidate> candidates) : this()
    {
        var eligible = candidates.Count(item => !item.HasLocalChanges);
        var conflicts = candidates.Count - eligible;
        Summary.Text = $"{candidates.Count} linked group(s) have newer versions. Update all unchanged groups, or keep the versions currently embedded in your documents.";
        var lines = candidates.Take(30).Select(item =>
            $"{item.Name}: v{item.CurrentRevision} -> v{item.LatestRevision}" +
            (item.HasLocalChanges ? " (conflict; kept)" : ""));
        var note = conflicts == 0
            ? "Updates can be undone in each document."
            : $"{conflicts} group(s) have local changes, a nested conflict, or a missing original revision and will not be overwritten. Updates can be undone in each document.";
        Details.Text = string.Join(Environment.NewLine, lines) +
            (candidates.Count > 30 ? $"{Environment.NewLine}..." : "") + $"{Environment.NewLine}{Environment.NewLine}{note}";
        ApplyButton.IsEnabled = eligible > 0;
    }

    private void OnKeepClick(object? sender, RoutedEventArgs e) => Close(false);
    private void OnApplyClick(object? sender, RoutedEventArgs e) => Close(true);
}
