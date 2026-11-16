using Avalonia.Threading;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services;

public sealed class NodeDefinitionCatalog : IDisposable
{
    private readonly TeamProfilesService _profiles;
    private FileSystemWatcher? _watcher;
    private string? _watchedDirectory;
    private int _refreshScheduled;
    private bool _disposed;

    public IReadOnlyList<GraphNodeDefinition> AvailableDefinitions { get; private set; } = GraphNodeDefinition.AuthoringCore;
    public string ProjectName => _profiles.Active.Name;
    public string Warnings { get; private set; } = string.Empty;
    public event EventHandler? Changed;

    public NodeDefinitionCatalog(TeamProfilesService profiles)
    {
        _profiles = profiles;
        _profiles.Changed += OnProfilesChanged;
        Refresh();
    }

    public void Refresh()
    {
        if (_disposed) return;
        IReadOnlyList<GraphNodeDefinition> active = [];
        var warnings = new List<string>();
        foreach (var profile in _profiles.Profiles.OrderByDescending(profile => profile.Id == _profiles.Active.Id))
        {
            if (string.IsNullOrWhiteSpace(profile.NodeDirectory)) continue;
            try
            {
                var definitions = GraphNodeDefinition.LoadDirectory(profile.NodeDirectory, out var errors);
                if (profile.Id == _profiles.Active.Id) active = definitions;
                warnings.AddRange(errors.Select(error => $"{profile.Name}: {error}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                warnings.Add($"{profile.Name}: {ex.Message}");
            }
        }
        AvailableDefinitions = Array.AsReadOnly<GraphNodeDefinition>([.. GraphNodeDefinition.AuthoringCore, .. active]);
        try { UpdateWatcher(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            warnings.Add(ex.Message);
        }
        Warnings = string.Join(Environment.NewLine, warnings);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnProfilesChanged(object? sender, EventArgs e) => Refresh();

    private void UpdateWatcher()
    {
        var directory = _profiles.Active.NodeDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) directory = null;
        if (string.Equals(directory, _watchedDirectory, StringComparison.OrdinalIgnoreCase)) return;
        _watcher?.Dispose();
        _watcher = null;
        _watchedDirectory = null;
        if (directory is null) return;
        var watcher = new FileSystemWatcher(directory, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        watcher.Changed += OnFilesChanged;
        watcher.Created += OnFilesChanged;
        watcher.Deleted += OnFilesChanged;
        watcher.Renamed += OnFilesChanged;
        try { watcher.EnableRaisingEvents = true; }
        catch
        {
            watcher.Dispose();
            throw;
        }
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
