using Dock.Model.Mvvm.Controls;

namespace LiberaFx.ViewModels;

public sealed class PreviewPaneViewModel(MainViewModel main) : Tool
{
    public MainViewModel Main { get; } = main;
}
