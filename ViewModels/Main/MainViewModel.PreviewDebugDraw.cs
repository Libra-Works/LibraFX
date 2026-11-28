namespace LiberaFx.ViewModels;

public partial class MainViewModel
{
    private bool _includePreviewDebugDraw;
    // Session-local transport option, not project data or an export preference.
    public bool IncludePreviewDebugDraw
    {
        get => _includePreviewDebugDraw;
        set => SetProperty(ref _includePreviewDebugDraw, value);
    }
}
