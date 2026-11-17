using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class AssetReferencesView : UserControl
{
    private bool _framePending;

    public AssetReferencesView()
    {
        InitializeComponent();
        ReferenceGraphPreview.DataContextChanged += (_, _) => FramePreview();
        ReferenceGraphPreview.SizeChanged += (_, _) => FramePreview();
    }

    private void FramePreview()
    {
        if (_framePending) return;
        _framePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _framePending = false;
            ReferenceGraphPreview.FrameAll();
        }, DispatcherPriority.Loaded);
    }

    private async void OnChooseFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AssetReferencesViewModel model || TopLevel.GetTopLevel(this) is not { } owner) return;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = LocalizationService.Shared.Get("References.ChooseFolder"), AllowMultiple = false });
        if (ReferenceEquals(DataContext, model) && folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            model.Folder = path;
    }
}
