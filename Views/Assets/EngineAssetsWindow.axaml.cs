using Avalonia.Controls;
using Avalonia.Interactivity;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class EngineAssetsWindow : UserControl
{
    private MainViewModel? _main;
    private AssetBrowserCoordinator? _coordinator;
    private EngineAssetsWindowViewModel? _viewModel;
    private ResourceIdentityPaneViewModel? _resourceIdentityViewModel;

    private async void OnFilterSelectedFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EngineAssetsWindowViewModel { SelectedAsset: { } asset } model) return;
        await model.ChooseEngineFolderAsync(EngineAssetFolder.Parent(asset.Path));
    }

    public EngineAssetsWindow()
    {
        InitializeComponent();
        // The identity tab's compiled bindings must not inherit the browser's view model before Opened.
        ResourceIdentityPane.DataContext = null;
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnOpened;
        DetachedFromVisualTree += OnClosed;
    }

    public EngineAssetsWindow(MainViewModel main) : this()
    {
        InitializeModel(main);
        _ = main.AssetBrowser.BeginForCurrentSelection();
    }
    public EngineAssetsWindow(AssetBrowserCoordinator coordinator) : this() => InitializeModel(coordinator);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel main) InitializeModel(main);
    }

    private void InitializeModel(MainViewModel main)
    {
        ArgumentNullException.ThrowIfNull(main);
        _main = main;
        InitializeModel(main.AssetBrowser);
    }

    private void InitializeModel(AssetBrowserCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        if (ReferenceEquals(_coordinator, coordinator)) return;
        _coordinator = coordinator;
        _main = coordinator.ViewModel.Main;
        _viewModel = coordinator.ViewModel;
        DataContext = _viewModel;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (_viewModel is not { } viewModel) return;
        _resourceIdentityViewModel?.Dispose();
        _resourceIdentityViewModel = new ResourceIdentityPaneViewModel(viewModel.Main);
        ResourceIdentityPane.DataContext = _resourceIdentityViewModel;
        viewModel.RefreshPeers();
        if (viewModel.SelectedPeer is not null && _coordinator?.Request?.RevealOnly != true)
            _ = viewModel.SearchCommand.ExecuteAsync(null);
    }

    private async void OnReconnectClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedPeer is not { } peer || _main is not { } main) return;
        try
        {
            await main.PreviewAssets.ReconnectAsync(peer.ClientId);
            _viewModel?.RefreshPeers();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or
                                           IOException or TimeoutException)
        {
            main.Log.Add("Preview", LocalizationService.Shared.Get("Asset.RequestFailed", exception.Message));
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _resourceIdentityViewModel?.Dispose();
        _resourceIdentityViewModel = null;
        ResourceIdentityPane.DataContext = null;
    }

    private void OnOpenSpriteEditor(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { SelectedPeer: { } peer, SelectedAsset: { } asset } model) return;
        if (_main?.OpenSpriteEditorForAsset(peer.ClientId, asset) != true)
        {
            model.SetCallerError(LocalizationService.Shared.Get("Sprite.SourceNotOpened"));
        }
    }
}
