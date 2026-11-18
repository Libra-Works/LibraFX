using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class MainWindow
{
    private void OpenSharedAssetBrowser()
    {
        if (DataContext is MainViewModel main)
            main.AssetBrowser.OpenForCurrentSelection(this);
    }

    internal EngineAssetsWindowViewModel? SharedAssetBrowserViewModel =>
        (DataContext as MainViewModel)?.CachedAssetBrowserViewModel;

    private void DisposeSharedAssetBrowser() =>
        (DataContext as MainViewModel)?.DisposeAssetBrowser();
}
