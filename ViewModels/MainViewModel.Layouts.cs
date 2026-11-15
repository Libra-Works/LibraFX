namespace LiberaFx.ViewModels;

public partial class MainViewModel
{
    private WindowLayoutsViewModel? _windowLayouts;
    public WindowLayoutsViewModel WindowLayouts => _windowLayouts ??= new(_dockFactory, this);
    public bool ShowDockPanel(string id) => _dockFactory.ShowTool(id);
    internal void EnableDockWindows() => _dockFactory.EnableFloatingWindows();
    internal void RestoreLayoutActiveDocument(GraphDocumentViewModel document) => SetActiveDocument(document);
}
