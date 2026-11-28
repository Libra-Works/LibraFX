using Avalonia.Controls;
using Avalonia.Interactivity;
using LiberaFx.ViewModels.Main;

namespace LiberaFx.Views.Windows;

public partial class UnsavedChangesWindow : Window
{
    public UnsavedChangesWindow()
    {
        InitializeComponent();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(UnsavedChangesChoice.Cancel);
    }

    private void OnDiscardClick(object? sender, RoutedEventArgs e)
    {
        Close(UnsavedChangesChoice.Discard);
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel &&
            await viewModel.SaveAllDocumentsAsync())
        {
            Close(UnsavedChangesChoice.Save);
        }
    }
}
