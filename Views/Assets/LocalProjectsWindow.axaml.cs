using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Gpu;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Main;
using System.Text.Json;

namespace LiberaFx.Views.Assets;

public partial class LocalProjectsWindow : UserControl
{
    private MainViewModel? _main;
    private CancellationTokenSource? _load;
    private IReadOnlyList<LocalProjectAsset> _assets = [];
    private bool _closed;
    private static string SettingsFile => Path.Combine(AppDataDirectory.Root, "LiberaFx", "local-project-folder.json");

    public LocalProjectsWindow()
    {
        InitializeComponent();
        AttachedToVisualTree += async (_, _) =>
        {
            _closed = false;
            try
            {
                Folder.Text = File.Exists(SettingsFile)
                    ? JsonSerializer.Deserialize<string>(await File.ReadAllTextAsync(SettingsFile))
                    : Path.Combine(AppContext.BaseDirectory, "Assets", "Samples");
                await RefreshAsync();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            { Status.Text = e.Message; }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _closed = true;
            _load?.Cancel();
        };
    }

    public LocalProjectsWindow(MainViewModel main) : this() => _main = main;

    private async void OnChooseFolder(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { StorageProvider: { } storage }) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LocalizationService.Shared.Get("Library.ChooseFolder"), AllowMultiple = false
        });
        if (_closed || folders.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        Folder.Text = path;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            await GpuParticleEditorService.WriteAtomicAsync(SettingsFile, JsonSerializer.Serialize(path));
            await RefreshAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { Status.Text = exception.Message; }
    }

    private async Task RefreshAsync()
    {
        if (_closed || Folder is null || Recursive is null || string.IsNullOrEmpty(Folder.Text)) return;
        _load?.Cancel();
        var source = new CancellationTokenSource();
        _load = source;
        var cancellation = source.Token;
        var path = Folder.Text;
        var recursive = Recursive.IsChecked == true;
        _assets = [];
        Filter();
        Status.Text = LocalizationService.Shared.Get("Library.Loading");
        try
        {
            var page = await Task.Run(() => LocalProjectCatalog.Read(path, recursive, cancellation), cancellation);
            if (_closed || cancellation.IsCancellationRequested) return;
            _assets = page.Assets;
            Filter();
            Status.Text = LocalizationService.Shared.Get(page.Truncated ? "Library.Truncated" : "Library.Count", _assets.Count);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        { if (!_closed) Status.Text = exception.Message; }
        finally
        {
            if (ReferenceEquals(_load, source)) _load = null;
            source.Dispose();
        }
    }

    private void Filter()
    {
        var query = Query.Text ?? "";
        Projects.ItemsSource = _assets.Where(asset => asset.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            asset.Path.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    private void OnQueryChanged(object? sender, TextChangedEventArgs e) { if (Projects is not null) Filter(); }
    private async void OnRefresh(object? sender, RoutedEventArgs e) => await RefreshAsync();
    private async void OnRecursiveChanged(object? sender, RoutedEventArgs e) => await RefreshAsync();
    private async void OnOpenProject(object? sender, RoutedEventArgs e)
    {
        if (_main is not null && Projects.SelectedItem is LocalProjectAsset asset)
        {
            try { await _main.OpenProjectPathAsync(asset.Path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            { Status.Text = exception.Message; }
        }
    }
}
