using System.ComponentModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class LogWindow : UserControl
{
    private LogViewerViewModel? _viewer;
    private MainViewModel? _attachedModel;
    public LogWindow()
    {
        InitializeComponent();
        LogContent.DataContext = null;
        AttachedToVisualTree += (_, _) => AttachViewer();
        DetachedFromVisualTree += (_, _) => DetachViewer();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        AttachViewer();
    }

    private void AttachViewer()
    {
        if (LogContent is null) return;
        if (DataContext is not MainViewModel model)
        {
            DetachViewer();
            return;
        }
        if (_viewer is not null && ReferenceEquals(_attachedModel, model)) return;
        DetachViewer();
        _attachedModel = model;
        _viewer = new LogViewerViewModel(model.Log);
        _viewer.PropertyChanged += OnViewerChanged;
        LogContent.DataContext = _viewer;
        UpdateSelection();
    }

    private void DetachViewer()
    {
        if (_viewer is not null)
        {
            _viewer.PropertyChanged -= OnViewerChanged;
            _viewer.Dispose();
        }
        _viewer = null;
        _attachedModel = null;
        if (LogContent is not null) LogContent.DataContext = null;
    }

    private void OnViewerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LogViewerViewModel.MatchStart) or nameof(LogViewerViewModel.VisibleText) or
            nameof(LogViewerViewModel.FollowTail) or nameof(LogViewerViewModel.WrapLines))
            UpdateSelection();
    }

    private void UpdateSelection()
    {
        if (_viewer is null) return;
        LogText.TextWrapping = _viewer.WrapLines ? TextWrapping.Wrap : TextWrapping.NoWrap;
        if (_viewer.MatchStart >= 0)
        {
            LogText.CaretIndex = _viewer.MatchStart;
            LogText.SelectionStart = _viewer.MatchStart;
            LogText.SelectionEnd = _viewer.MatchStart + _viewer.MatchLength;
        }
        else if (_viewer.FollowTail && _viewer.SearchText.Length == 0 && LogText.SelectionStart == LogText.SelectionEnd)
            LogText.CaretIndex = _viewer.VisibleText.Length;
        else if (_viewer.SearchText.Length != 0)
        {
            LogText.CaretIndex = 0;
            LogText.SelectionStart = LogText.SelectionEnd = 0;
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _viewer is null) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) _viewer.PreviousMatchCommand.Execute(null);
        else _viewer.NextMatchCommand.Execute(null);
        e.Handled = true;
    }

    private async void OnLogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F3 && _viewer is not null)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) _viewer.PreviousMatchCommand.Execute(null);
            else _viewer.NextMatchCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            await SaveAsync(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        }
    }

    private async void OnSaveAllClick(object? sender, RoutedEventArgs e) => await SaveAsync(false);
    private async void OnSaveVisibleClick(object? sender, RoutedEventArgs e) => await SaveAsync(true);

    private async Task SaveAsync(bool visible)
    {
        var viewer = _viewer;
        if (viewer is null) return;
        try
        {
            if (TopLevel.GetTopLevel(this) is not { StorageProvider: { } storage }) return;
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LocalizationService.Shared.Get(visible ? "Logs.SaveVisible" : "Logs.SaveAll"),
                SuggestedFileName = "LiberaFx.log",
                DefaultExtension = "log",
                FileTypeChoices = [new FilePickerFileType("Log") { Patterns = ["*.log", "*.txt"] }]
            });
            if (file is null) return;
            using (file)
            {
                var text = visible ? viewer.VisibleText : viewer.AllText;
                if (file.TryGetLocalPath() is { } path)
                    await LogTextExporter.SaveAsync(path, text);
                else
                {
                    await using var output = await file.OpenWriteAsync();
                    var bytes = Encoding.UTF8.GetBytes(text);
                    if (output.CanSeek) output.Position = 0;
                    await output.WriteAsync(bytes);
                    if (output.CanSeek) output.SetLength(output.Position);
                    await output.FlushAsync();
                }
                viewer.SetStatus("Logs.Saved");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   InvalidOperationException or NotSupportedException)
        {
            viewer.SetStatus("Logs.SaveFailed", ex.Message);
        }
    }
}
