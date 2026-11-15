using System.ComponentModel;
using Avalonia.Controls;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class ProblemsWindow : Window
{
    private ActiveGraphProvider? _provider;

    public ProblemsWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            if (_provider is not null) _provider.PropertyChanged -= OnActiveGraphChanged;
            _provider = null;
            DataContext = null;
        };
    }

    public ProblemsWindow(MainViewModel main) : this()
    {
        _provider = main.ActiveGraphProvider;
        _provider.PropertyChanged += OnActiveGraphChanged;
        DataContext = _provider.Graph;
    }

    private void OnActiveGraphChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActiveGraphProvider.Graph) && _provider is not null)
            DataContext = _provider.Graph;
    }
}
