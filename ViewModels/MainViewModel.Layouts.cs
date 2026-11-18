namespace LiberaFx.ViewModels;

public partial class MainViewModel
{
    private WindowLayoutsViewModel? _windowLayouts;
    public WindowLayoutsViewModel WindowLayouts => _windowLayouts ??= new(_dockFactory, this);
    public bool ShowDockPanel(string id) => _dockFactory.ShowTool(id);
    internal bool OpenSpriteEditorForAsset(Guid peerId, Services.EngineAsset asset)
    {
        var pane = _dockFactory.Tools.OfType<SpriteEditorPaneViewModel>().Single();
        var selected = pane.TrySelectAsset(peerId, asset);
        if (selected || pane.Owner is not null) ShowDockPanel(pane.Id);
        return selected;
    }
    internal void EnableDockWindows() => _dockFactory.EnableFloatingWindows();
    internal void RestoreLayoutActiveDocument(GraphDocumentViewModel document) => SetActiveDocument(document);
}
