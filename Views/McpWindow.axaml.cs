using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class McpWindow : Window
{
    public McpWindow()
    {
        InitializeComponent();
        OperatorSkillPathBox.Text = OperatorSkillPackage.BundledSkillPath;
    }

    private async void OnCopyOperatorSkillPathClick(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(OperatorSkillPackage.BundledSkillPath); }
        catch (Exception ex) { OperatorSkillStatus.Text = LocalizationService.Shared.Get("OperatorSkill.Failed", ex.Message); }
    }

    private async void OnInstallOperatorSkillClick(object? sender, RoutedEventArgs e) =>
        await InstallOperatorSkillAsync();

    private async Task InstallOperatorSkillAsync()
    {
        InstallOperatorSkillButton.IsEnabled = false;
        try
        {
            var result = await OperatorSkillPackage.InstallAsync(OperatorSkillsDirectoryBox.Text ?? "",
                ReplaceOperatorSkillBox.IsChecked == true);
            OperatorSkillStatus.Text = result.BackupPath is { } backup
                ? LocalizationService.Shared.Get("OperatorSkill.Updated", result.SkillPath, backup)
                : LocalizationService.Shared.Get(result.Changed ? "OperatorSkill.Installed" : "OperatorSkill.Current", result.SkillPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            OperatorSkillStatus.Text = LocalizationService.Shared.Get("OperatorSkill.Failed", ex.Message);
        }
        finally { InstallOperatorSkillButton.IsEnabled = true; }
    }

    private void OnShowTokenChanged(object? sender, RoutedEventArgs e) =>
        TokenBox.PasswordChar = sender is CheckBox { IsChecked: true } ? '\0' : '●';

    private async void OnCopyTokenClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model && Clipboard is { } clipboard)
        {
            try { await clipboard.SetTextAsync(model.McpAccessToken); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Clipboard copy failed: {0}", ex.Message); }
        }
    }
}
