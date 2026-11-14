using Avalonia.Controls;
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

    private void RefreshProjectSelection()
    {
        if (DataContext is MainViewModel model)
        {
            ProjectList.SelectedItem = model.TeamProfiles.Active;
            ProjectNameBox.Text = model.TeamProfiles.Active.Name;
            GroupDirectoryBox.Text = model.TeamProfiles.Active.GroupDirectory;
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
            if (model.TeamProfiles.Save(selected.Id, ProjectNameBox.Text ?? "", GroupDirectoryBox.Text ?? ""))
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
            if (model.TeamProfiles.Save(null, ProjectNameBox.Text ?? "", GroupDirectoryBox.Text ?? ""))
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
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LocalizationService.Shared.Get("Editor.BrowseDirectory"),
            AllowMultiple = false
        });
        if (folders.Count == 0) return;
        using var folder = folders[0];
        if (folder.TryGetLocalPath() is { } path) GroupDirectoryBox.Text = path;
    }
}
