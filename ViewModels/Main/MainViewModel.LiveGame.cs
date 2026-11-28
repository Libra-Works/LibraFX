using Avalonia.Threading;
using LiberaFx.Services;
using LiberaFx.Views;

namespace LiberaFx.ViewModels;

public partial class MainViewModel
{
    internal LiveGameWindow CreateLiveGameView()
    {
        Dispatcher.UIThread.VerifyAccess();
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
        return new LiveGameWindow(model);
    }
}
