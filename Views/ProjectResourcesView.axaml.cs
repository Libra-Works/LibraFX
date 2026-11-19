using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class ProjectResourcesView : UserControl
{
    private bool _framePending;

    public ProjectResourcesView()
    {
        InitializeComponent();
        ResourceGraphPreview.DataContextChanged += (_, _) => FramePreview();
        ResourceGraphPreview.SizeChanged += (_, _) => FramePreview();
    }

    private void FramePreview()
    {
        if (_framePending) return;
        _framePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _framePending = false;
            ResourceGraphPreview.FrameAll();
        }, DispatcherPriority.Loaded);
    }

    private async void OnChooseFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectResourcesViewModel model || TopLevel.GetTopLevel(this) is not { } owner) return;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = LocalizationService.Shared.Get("Library.ChooseFolder"), AllowMultiple = false });
        if (!ReferenceEquals(DataContext, model) || folders.FirstOrDefault()?.TryGetLocalPath() is not { } folder) return;
        model.Folder = folder;
        await model.RefreshCommand.ExecuteAsync(null);
    }

    private async void OnVersionControl(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProjectResourcesViewModel model || model.VersionControlTarget() is not { } target ||
            TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }
        var dialog = new Window
        {
            Width = 1080, Height = 720, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = LocalizationService.Shared.Get("Vcs.Title") + " — " + Path.GetFileName(target.File),
            Content = new RevisionHistoryWindow(target.Main, target.File, target.CompareGraphs)
        };
        await dialog.ShowDialog(owner);
    }
}
