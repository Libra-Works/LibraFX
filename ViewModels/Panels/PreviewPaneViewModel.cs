using Dock.Model.Mvvm.Controls;
using LiberaFx.ViewModels.Main;

namespace LiberaFx.ViewModels.Panels;

public sealed class PreviewPaneViewModel(MainViewModel main) : Tool
{
    public MainViewModel Main { get; } = main;
}
