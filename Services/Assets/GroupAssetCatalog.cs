using Avalonia.Threading;
using LiberaFx.Services.Settings;

namespace LiberaFx.Services.Assets;

public sealed class GroupAssetCatalog : IDisposable
{
    private readonly TeamProfilesService _profiles;
    private readonly StandardGroupAssetLibrary _standard;
    private FileSystemWatcher? _watcher;
    private string? _watchedDirectory;
    private int _refreshScheduled;
    private bool _disposed;
    private string? _catalogDirectory;

    public IReadOnlyList<GroupAsset> Assets { get; private set; } = [];
    public IEnumerable<GroupAsset> CreatableAssets => Assets.Where(asset =>
        asset.Origin != GroupAssetOrigin.Standard || Metadata(asset.Id)?.Legacy != true);
    public bool HasLibraryDirectory => !string.IsNullOrWhiteSpace(_profiles.Active.GroupDirectory);
    public string Warnings { get; private set; } = string.Empty;
    public event EventHandler? Changed;

    public GroupAssetCatalog(TeamProfilesService profiles, string? standardDirectory = null)
    {
        _profiles = profiles;
        _standard = new StandardGroupAssetLibrary(standardDirectory);
        profiles.Changed += OnProfilesChanged;
        Refresh();
    }

    public GroupAssetLibrary Library()
    {
        if (string.IsNullOrWhiteSpace(_profiles.Active.GroupDirectory))
            throw new InvalidOperationException(LocalizationService.Shared.Get("Editor.GroupNoLibrary"));
        return new GroupAssetLibrary(_profiles.Active.GroupDirectory);
    }

    public GroupAsset Load(Guid id)
    {
        try { return _standard.Load(id); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        return Library().Load(id);
    }

    public GroupAsset LoadRevision(Guid id, long revision)
    {
        try { return _standard.LoadRevision(id, revision); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        return Library().LoadRevision(id, revision);
    }

    public StandardGroupMetadata? Metadata(Guid id) => _standard.Metadata(id);

    public void RegisterStandardModule(StandardGroupModuleDefinition definition)
    {
        _standard.Register(definition);
        Refresh();
    }

    public void RegisterStandardModules(IEnumerable<StandardGroupModuleDefinition> definitions)
    {
        _standard.Register(definitions);
        Refresh();
    }

    public GroupAsset CloneToProject(Guid id, string? name = null)
    {
        var source = Load(id);
        if (source.Origin != GroupAssetOrigin.Standard)
            throw new InvalidOperationException("Only a standard group can be cloned with this operation.");
        return Library().Create(string.IsNullOrWhiteSpace(name) ? source.Name : name, source.Graph);
    }

    public void Refresh()
    {
        if (_disposed) return;
        var oldAssets = Assets;
        var oldWarnings = Warnings;
        var directoryChanged = !string.Equals(_catalogDirectory, _profiles.Active.GroupDirectory, StringComparison.OrdinalIgnoreCase);
        _catalogDirectory = _profiles.Active.GroupDirectory;
        var standardAssets = _standard.LoadAll(out var standardWarnings);
        var warnings = new List<string>(standardWarnings);
        IReadOnlyList<GroupAsset> projectAssets = [];
        if (!string.IsNullOrWhiteSpace(_profiles.Active.GroupDirectory))
        {
            try { projectAssets = Library().LoadAll(out var projectWarnings); warnings.AddRange(projectWarnings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                warnings.Add(LocalizationService.Shared.Get("Editor.LoadGroupLibraryFailed", ex.Message));
            }
        }
        var standardIds = standardAssets.Select(asset => asset.Id).ToHashSet();
        var conflicts = projectAssets.Where(asset => standardIds.Contains(asset.Id)).ToArray();
        if (conflicts.Length != 0)
            warnings.AddRange(conflicts.Select(asset =>
                $"{asset.Id:N}: A project asset conflicts with a standard group; the standard version remains active."));
        Assets = standardAssets.Concat(projectAssets.Where(asset => !standardIds.Contains(asset.Id))).ToArray();
        try { UpdateWatcher(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            warnings.Add(LocalizationService.Shared.Get("Editor.LoadGroupLibraryFailed", ex.Message));
        }
        Warnings = string.Join(Environment.NewLine, warnings);
        if (directoryChanged || Warnings != oldWarnings || !oldAssets.Select(Identity).SequenceEqual(Assets.Select(Identity)))
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private static (Guid Id, string Name, long Revision, bool Locked, GroupAssetOrigin Origin) Identity(GroupAsset asset) =>
        (asset.Id, asset.Name, asset.Revision, asset.Locked, asset.Origin);

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
