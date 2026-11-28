using Avalonia.Controls;
using Avalonia.Input;
using LiberaFx.ViewModels.Panels;

namespace LiberaFx.Views.Panels;

public partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();
        Focusable = true;
        KeyDown += OnHistoryKeyDown;
    }

    private void OnHistoryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || DataContext is not HistoryPaneViewModel vm)
        {
            return;
        }
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        var targetId = focused?.DataContext is HistoryEntryViewModel entry ? entry.Id : vm.CurrentNodeId;
        if (vm.DeleteHistoryEntry(targetId))
        {
            e.Handled = true;
        }
    }
}
