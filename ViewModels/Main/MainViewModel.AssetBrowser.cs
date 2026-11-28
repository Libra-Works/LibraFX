using LiberaFx.Services.Assets;
using LiberaFx.ViewModels.Assets;

namespace LiberaFx.ViewModels.Main;

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
