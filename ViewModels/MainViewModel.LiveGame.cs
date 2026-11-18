using Avalonia.Controls;
using Avalonia.Threading;
using LiberaFx.Services;
using LiberaFx.Views;

namespace LiberaFx.ViewModels;

public partial class MainViewModel
{
    private LiveGameWindow? liveGameWindow;
    public void ShowLiveGameWindow(Window owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (liveGameWindow is not null) { liveGameWindow.Activate(); return; }
        var model = new LiveGameWindowViewModel(() =>
        {
            Dispatcher.UIThread.VerifyAccess();
            var document = ActiveDocument ?? throw new InvalidOperationException(
                LocalizationService.Shared.Get("LiveGame.NoDocument"));
            // Full active document, including nested edits, not a stale or
            // currently navigated child graph. No history/settings mutation.
            return new LiveGameGraphSource(document.Workspace.CreateSnapshot(), document.FileName,
                TeamProfiles.Active.GroupDirectory);
        });
        liveGameWindow = new LiveGameWindow(model);
        liveGameWindow.Closed += (_, _) => liveGameWindow = null;
        liveGameWindow.Show(owner);
    }
    public void CloseLiveGameWindow() => liveGameWindow?.Close();
}
