using Dock.Model.Mvvm.Controls;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed class ToolPaneViewModel : Tool
{
    private readonly string _description = string.Empty;
    public string DescriptionKey { get; init; } = string.Empty;
    public string Description
    {
        get => DescriptionKey.Length == 0 ? _description : LocalizationService.Shared.Get(DescriptionKey);
        init => _description = value;
    }

    public ToolPaneViewModel()
    {
        MessageLocalization.Observe(this, static pane => pane.OnPropertyChanged(nameof(Description)));
    }
}
