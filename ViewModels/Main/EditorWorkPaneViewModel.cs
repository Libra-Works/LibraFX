using Avalonia.Controls;
using Dock.Model.Mvvm.Controls;
using LiberaFx.Views.Windows;

namespace LiberaFx.ViewModels.Main;

public sealed class EditorWorkPaneViewModel(Func<Control> createView, bool retainView) : Tool
{
    private Control? _view;

    public Control View => retainView ? _view ??= createView() : createView();

    public void OnHidden()
    {
        if (_view is LiberaFx.Views.Windows.ChatWindow chat)
        {
            chat.EndSession();
        }
        else if (_view is LiberaFx.Views.Windows.LiveGameWindow game)
        {
            game.EndSession();
        }
    }

    public void ReleaseView() => (_view as IDisposable)?.Dispose();
}
