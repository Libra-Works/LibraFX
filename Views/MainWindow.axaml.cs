using Avalonia.Controls;
using Avalonia.Input;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class MainWindow : Window
{
    private LogWindow? _logWindow;
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnMainKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;
        if (model.Shortcuts.Matches("undo", e) && model.UndoCommand.CanExecute(null))
        {
            model.UndoCommand.Execute(null);
            e.Handled = true;
        }
        else if (model.Shortcuts.Matches("redo", e) && model.RedoCommand.CanExecute(null))
        {
            model.RedoCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async void OnSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var settings = new SettingsWindow
        {
            DataContext = DataContext
        };
        await settings.ShowDialog(this);
    }

    private void OnLogClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_logWindow is not null)
        {
            _logWindow.Activate();
            return;
        }

        _logWindow = new LogWindow { DataContext = DataContext };
        _logWindow.Closed += (_, _) => _logWindow = null;
        _logWindow.Show(this);
    }

    private async void OnCloseDocumentClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || viewModel.ActiveDocument is not { } document)
        {
            return;
        }

        if (!document.IsDirty)
        {
            viewModel.CloseActiveDocument(discardChanges: false);
            return;
        }

        var dialog = new UnsavedChangesWindow
        {
            DataContext = viewModel
        };
        var choice = await dialog.ShowDialog<UnsavedChangesChoice>(this);
        if (choice == UnsavedChangesChoice.Discard)
        {
            viewModel.CloseActiveDocument(discardChanges: true);
        }
        else if (choice == UnsavedChangesChoice.Save &&
                 await viewModel.SaveAllDocumentsAsync())
        {
            viewModel.CloseActiveDocument(discardChanges: false);
        }
    }

    public async Task<bool> ConfirmCloseAsync()
    {
        if (DataContext is not MainViewModel viewModel ||
            viewModel.Documents.All(document => !document.IsDirty))
        {
            return true;
        }

        var dialog = new UnsavedChangesWindow
        {
            DataContext = viewModel
        };
        var choice = await dialog.ShowDialog<UnsavedChangesChoice>(this);
        return choice switch
        {
            UnsavedChangesChoice.Save => await viewModel.SaveAllDocumentsAsync(),
            UnsavedChangesChoice.Discard => true,
            _ => false
        };
    }
}