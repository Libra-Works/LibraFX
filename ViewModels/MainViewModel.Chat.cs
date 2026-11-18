using Avalonia.Controls;
using LiberaFx.Views;

namespace LiberaFx.ViewModels;

public partial class MainViewModel
{
    private ChatWindow? _chatWindow;

    public void ShowChatWindow(Window owner)
    {
        if (_chatWindow is not null) { _chatWindow.Activate(); return; }
        _chatWindow = new ChatWindow(this);
        _chatWindow.Closed += (_, _) => _chatWindow = null;
        _chatWindow.Show(owner);
    }

    public void CloseChatWindow() => _chatWindow?.Close();
}
