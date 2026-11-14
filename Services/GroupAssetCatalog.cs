using Avalonia.Threading;

namespace LiberaFx.Services;

public sealed class GroupAssetCatalog : IDisposable
{
    private readonly TeamProfilesService _profiles;
    private FileSystemWatcher? _watcher;
    private string? _watchedDirectory;
    private int _refreshScheduled;
    private bool _disposed;
    private string? _catalogDirectory;

    public IReadOnlyList<GroupAsset> Assets { get; private set; } = [];
    public bool HasLibraryDirectory => !string.IsNullOrWhiteSpace(_profiles.Active.GroupDirectory);
    public string Warnings { get; private set; } = string.Empty;
    public event EventHandler? Changed;

    public GroupAssetCatalog(TeamProfilesService profiles)
    {
        _profiles = profiles;
        profiles.Changed += OnProfilesChanged;
        Refresh();
    }

    public GroupAssetLibrary Library()
    {
        if (string.IsNullOrWhiteSpace(_profiles.Active.GroupDirectory))
            throw new InvalidOperationException(LocalizationService.Shared.Get("Editor.GroupNoLibrary"));
        return new GroupAssetLibrary(_profiles.Active.GroupDirectory);
    }

    public void Refresh()
    {
        if (_disposed) return;
        var oldAssets = Assets;
        var oldWarnings = Warnings;
        var directoryChanged = !string.Equals(_catalogDirectory, _profiles.Active.GroupDirectory, StringComparison.OrdinalIgnoreCase);
        _catalogDirectory = _profiles.Active.GroupDirectory;
        try
        {
            if (string.IsNullOrWhiteSpace(_profiles.Active.GroupDirectory))
            {
                Assets = [];
                Warnings = string.Empty;
            }
            else
            {
                Assets = Library().LoadAll(out var warnings);
                Warnings = string.Join(Environment.NewLine, warnings);
            }
            UpdateWatcher();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Assets = [];
            Warnings = LocalizationService.Shared.Get("Editor.LoadGroupLibraryFailed", ex.Message);
        }
        if (directoryChanged || Warnings != oldWarnings || !oldAssets.Select(Identity).SequenceEqual(Assets.Select(Identity)))
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private static (Guid Id, string Name, long Revision, bool Locked) Identity(GroupAsset asset) =>
        (asset.Id, asset.Name, asset.Revision, asset.Locked);

    private void OnProfilesChanged(object? sender, EventArgs e) => Refresh();

    private void UpdateWatcher()
    {
        var directory = _profiles.Active.GroupDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) directory = null;
        if (string.Equals(directory, _watchedDirectory, StringComparison.OrdinalIgnoreCase)) return;
        _watcher?.Dispose();
        _watcher = null;
        _watchedDirectory = null;
        if (directory is null) return;
        var watcher = new FileSystemWatcher(directory, "*" + GroupAssetLibrary.FileExtension)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        watcher.Changed += OnFilesChanged;
        watcher.Created += OnFilesChanged;
        watcher.Deleted += OnFilesChanged;
        watcher.Renamed += OnFilesChanged;
        watcher.EnableRaisingEvents = true;
        _watcher = watcher;
        _watchedDirectory = directory;
    }

    private void OnFilesChanged(object sender, FileSystemEventArgs e)
    {
        if (Interlocked.Exchange(ref _refreshScheduled, 1) != 0) return;
        Dispatcher.UIThread.Post(() => DispatcherTimer.RunOnce(() =>
        {
            Interlocked.Exchange(ref _refreshScheduled, 0);
            Refresh();
        }, TimeSpan.FromMilliseconds(300)));
    }

    public void Dispose()
    {
        _disposed = true;
        _profiles.Changed -= OnProfilesChanged;
        _watcher?.Dispose();
        _watcher = null;
    }
}
