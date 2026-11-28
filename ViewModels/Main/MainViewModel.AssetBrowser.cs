using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public partial class MainViewModel
{
    private AssetBrowserCoordinator? _assetBrowser;

    public AssetBrowserCoordinator AssetBrowser =>
        _assetBrowser ??= new AssetBrowserCoordinator(this);
    internal EngineAssetsWindowViewModel? CachedAssetBrowserViewModel => _assetBrowser?.ViewModel;

    public void DisposeAssetBrowser()
    {
        _assetBrowser?.Dispose();
        _assetBrowser = null;
    }
}
