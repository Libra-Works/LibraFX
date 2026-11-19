using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class SettingsWindow : Window
{
    private bool _updatingProjects;
    private bool _updatingLanguage;
    private bool _updatingInspectorDefault;
    public SettingsWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => RefreshProjectSelection();
        Closed += (_, _) => DataContext = null;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void OnExtensionToggleClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model || sender is not CheckBox { DataContext: EditorExtensionEntry entry } toggle)
        {
            return;
        }
        toggle.SetCurrentValue(CheckBox.IsCheckedProperty, entry.Enabled);
        if (entry.Enabled)
        {
            model.Extensions.SetEnabled(entry.Id, false, explicitlyTrusted: false);
            return;
        }
        var text = LocalizationService.Shared;
        var dialog = new Window
        {
            Title = text.Get("Extensions.Confirm"), Width = 480, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var cancel = new Button { Content = text.Get("Extensions.Cancel"), IsCancel = true };
        var trust = new Button { Content = text.Get("Extensions.Enable") };
        cancel.Click += (_, _) => dialog.Close(false);
        trust.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20), Spacing = 12,
            Children =
            {
                new TextBlock { Text = $"{entry.Name} ({entry.Id}) {entry.Version}", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = text.Get("Extensions.Trust"), TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, trust } }
            }
        };
        if (await dialog.ShowDialog<bool>(this) && ReferenceEquals(DataContext, model))
        {
            model.Extensions.SetEnabled(entry.Id, true, explicitlyTrusted: true);
        }
    }

    private async void OnExtensionRunClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model &&
            ExtensionCommandList.SelectedItem is RegisteredExtensionCommand command)
            await model.Extensions.ExecuteAsync(command.Id);
    }

    private void OnLanguageSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingLanguage || DataContext is not MainViewModel model ||
            LanguageList.SelectedItem is not EditorLanguage language) return;
        _updatingLanguage = true;
        try
        {
            model.Localization.SelectedLanguage = language;
            LanguageList.SetCurrentValue(ComboBox.SelectedItemProperty, model.Localization.SelectedLanguage);
        }
        finally { _updatingLanguage = false; }
    }

    private void OnShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel model || sender is not TextBox { Tag: string id }) return;
        e.Handled = true;
        if (e.Key != Key.Escape) model.Shortcuts.Assign(id, e.Key, e.KeyModifiers);
    }

    private void OnResetShortcutsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model) model.Shortcuts.Reset();
    }

    private void OnSaveServersClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model) model.ServerSettings.Save();
    }

    private void OnInspectorSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (DataContext is MainViewModel model) model.InspectorPlugins.FilterPlugins(InspectorSearchBox.Text);
    }

    private async void OnInspectorAddClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LocalizationService.Shared.Get("Editor.InspectorPickFolder"),
            AllowMultiple = false
        });
        if (folders.Count == 0) return;
        using var folder = folders[0];
        if (folder.TryGetLocalPath() is not { } path)
        {
            model.InspectorPlugins.SetOperationStatus(LocalizationService.Shared.Get("Editor.InspectorLocalFolderRequired"));
            return;
        }
        model.InspectorPlugins.TryInstallPluginFolder(path, out _);
    }

    private async void OnInspectorToggleClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model || sender is not CheckBox { DataContext: InspectorPluginEntry plugin } toggle)
        {
            return;
        }
        var service = model.InspectorPlugins;
        toggle.SetCurrentValue(CheckBox.IsCheckedProperty, plugin.IsEnabled);
        if (plugin.IsEnabled)
        {
            service.TrySetEnabled(plugin.Id, false, userConfirmed: false);
            return;
        }
        if (!plugin.IsCompatible)
        {
            service.SetOperationStatus(plugin.Status);
            return;
        }

        var dialog = new InspectorTrustDialog(plugin.Name, plugin.Version);
        if (await dialog.ShowDialog<bool>(this) && ReferenceEquals(DataContext, model))
        {
            service.TrySetEnabled(plugin.Id, true, userConfirmed: true);
        }
    }

    private void OnInspectorDefaultSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingInspectorDefault || sender is not ComboBox { DataContext: InspectorDefaultChoice choice } combo ||
            combo.SelectedItem is not InspectorDefaultOption selected) return;
        _updatingInspectorDefault = true;
        try
        {
            choice.Selected = selected;
            combo.SetCurrentValue(ComboBox.SelectedItemProperty, choice.Selected);
        }
        finally { _updatingInspectorDefault = false; }
    }

    private void RefreshProjectSelection()
    {
        if (DataContext is MainViewModel model)
        {
            ProjectList.SelectedItem = model.TeamProfiles.Active;
            ProjectNameBox.Text = model.TeamProfiles.Active.Name;
            GroupDirectoryBox.Text = model.TeamProfiles.Active.GroupDirectory;
            NodeDirectoryBox.Text = model.TeamProfiles.Active.NodeDirectory;
        }
    }

    private void OnProjectSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingProjects || DataContext is not MainViewModel model || ProjectList.SelectedItem is not TeamProfile selected) return;
        _updatingProjects = true;
        try
        {
            model.TeamProfiles.Select(selected.Id);
            RefreshProjectSelection();
        }
        finally { _updatingProjects = false; }
    }

    private void OnSaveProjectProfileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model || ProjectList.SelectedItem is not TeamProfile selected) return;
        _updatingProjects = true;
        try
        {
            if (model.TeamProfiles.Save(selected.Id, ProjectNameBox.Text ?? "", GroupDirectoryBox.Text ?? "", NodeDirectoryBox.Text ?? ""))
                RefreshProjectSelection();
        }
        finally { _updatingProjects = false; }
    }

    private void OnAddProjectProfileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;
        _updatingProjects = true;
        try
        {
            if (model.TeamProfiles.Save(null, ProjectNameBox.Text ?? "", GroupDirectoryBox.Text ?? "", NodeDirectoryBox.Text ?? ""))
                RefreshProjectSelection();
        }
        finally { _updatingProjects = false; }
    }

    private void OnRemoveProjectProfileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model || ProjectList.SelectedItem is not TeamProfile selected) return;
        _updatingProjects = true;
        try
        {
            if (model.TeamProfiles.Remove(selected.Id)) RefreshProjectSelection();
        }
        finally { _updatingProjects = false; }
    }

    private async void OnBrowseGroupDirectoryClick(object? sender, RoutedEventArgs e)
        => await BrowseDirectoryAsync(GroupDirectoryBox);

    private async void OnBrowseNodeDirectoryClick(object? sender, RoutedEventArgs e)
        => await BrowseDirectoryAsync(NodeDirectoryBox);

    private async Task BrowseDirectoryAsync(TextBox target)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LocalizationService.Shared.Get("Editor.BrowseDirectory"),
            AllowMultiple = false
        });
        if (folders.Count == 0) return;
        using var folder = folders[0];
        if (folder.TryGetLocalPath() is { } path) target.Text = path;
    }

    private sealed class InspectorTrustDialog : Window
    {
        public InspectorTrustDialog(string name, string version)
        {
            var text = LocalizationService.Shared;
            var cancel = new Button
            {
                Content = text.Get("Editor.InspectorCancel"),
                IsCancel = true,
                Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => Close(false))
            };
            AutomationProperties.SetName(cancel, text.Get("Editor.InspectorCancel"));
            var trust = new Button
            {
                Content = text.Get("Editor.InspectorTrustConfirm"),
                IsDefault = true,
                Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => Close(true))
            };
            AutomationProperties.SetName(trust, text.Get("Editor.InspectorTrustConfirm"));
            Title = text.Get("Editor.InspectorTrustTitle");
            Width = 480;
            SizeToContent = SizeToContent.Height;
            MinHeight = 250;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = text.Get("Editor.InspectorTrustPrompt", name, version),
                        TextWrapping = TextWrapping.Wrap,
                        FontWeight = FontWeight.SemiBold
                    },
                    new TextBlock
                    {
                        Text = text.Get("Editor.InspectorTrustWarning"),
                        TextWrapping = TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, trust }
                    }
                }
            };
        }
    }
}
