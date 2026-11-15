using Avalonia.Controls;
using Avalonia.Interactivity;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class EngineAssetsWindow : Window
{
    private MainViewModel? _main;
    private EngineAssetsWindowViewModel? _viewModel;

    public EngineAssetsWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Opened += OnOpened;
        Closed += OnClosed;
    }

    public EngineAssetsWindow(MainViewModel main) : this() => InitializeModel(main);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel main) InitializeModel(main);
    }

    private void InitializeModel(MainViewModel main)
    {
        ArgumentNullException.ThrowIfNull(main);
        if (ReferenceEquals(_main, main)) return;
        _viewModel?.Dispose();
        _main = main;
        _viewModel = new EngineAssetsWindowViewModel(main);
        DataContext = _viewModel;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (_viewModel is not { } viewModel) return;
        viewModel.RefreshPeers();
        if (viewModel.SelectedPeer is not null) _ = viewModel.SearchCommand.ExecuteAsync(null);
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
        DataContextChanged -= OnDataContextChanged;
        _viewModel?.Dispose();
        _viewModel = null;
        _main = null;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
