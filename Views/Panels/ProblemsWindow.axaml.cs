using System.ComponentModel;
using Avalonia.Controls;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class ProblemsWindow : UserControl
{
    private ActiveGraphProvider? _provider;

    public ProblemsWindow()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            if (_provider is null) return;
            _provider.PropertyChanged += OnActiveGraphChanged;
            DataContext = _provider.Graph;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_provider is not null) _provider.PropertyChanged -= OnActiveGraphChanged;
            DataContext = null;
        };
    }

    public ProblemsWindow(MainViewModel main) : this()
    {
        _provider = main.ActiveGraphProvider;
    }

    private void OnActiveGraphChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActiveGraphProvider.Graph) && _provider is not null)
            DataContext = _provider.Graph;
    }
}
