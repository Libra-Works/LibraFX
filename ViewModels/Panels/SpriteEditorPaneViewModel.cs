using Dock.Model.Mvvm.Controls;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed class SpriteEditorPaneViewModel(MainViewModel main) : Tool, IDisposable
{
    private SpriteEditorViewModel? _editor;

    public SpriteEditorViewModel Editor => _editor ??= new SpriteEditorViewModel(main);

    public bool TrySelectAsset(Guid peerId, EngineAsset asset)
    {
        if (_editor is not null) return false;
        var editor = new SpriteEditorViewModel(main, loadSelected: false);
        if (!editor.SelectInitialAsset(peerId, asset))
        {
            editor.Dispose();
            return false;
        }
        _editor = editor;
        return true;
    }

    public void Dispose() => _editor?.Dispose();
}
