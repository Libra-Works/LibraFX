namespace LiberaFx.Services;

// UI-thread owned. Selection uses its existing independent request channel;
// at most one speculative request is sent for the currently visible rows.
public sealed class VisibleAssetThumbnails(
    Func<string, CancellationToken, Task<EngineAssetThumbnail>> fetch) : IDisposable
{
    private const int MaximumCached = 64;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<object, (string Path, Action<EngineAssetThumbnail> Apply)> _visible = [];
    private readonly Dictionary<string, EngineAssetThumbnail> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recent = [];
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private bool _running, _disposed;
    private string? _selected;

    public void Select(string? path)
    {
        _selected = path;
        Start();
    }

    public IDisposable Observe(string path, Action<EngineAssetThumbnail> apply)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = new object();
        _visible.Add(key, (path, apply));
        if (TryGet(path, out var image)) apply(image!);
        Start();
        return new Subscription(() =>
        {
            _visible.Remove(key);
            if (!_visible.Values.Any(row => row.Path == path)) _failed.Remove(path);
        });
    }

    public bool TryGet(string path, out EngineAssetThumbnail? image)
    {
        if (!_cache.TryGetValue(path, out image)) return false;
        _recent.Remove(path);
        _recent.AddLast(path);
        return true;
    }

    public void Store(EngineAssetThumbnail image)
    {
        if (_disposed) return;
        _cache[image.Path] = image;
        _recent.Remove(image.Path);
        _recent.AddLast(image.Path);
        while (_recent.Count > MaximumCached)
        {
            _cache.Remove(_recent.First!.Value);
            _recent.RemoveFirst();
        }
        foreach (var row in _visible.Values.Where(row => row.Path == image.Path).ToArray()) row.Apply(image);
    }

    private void Start()
    {
        if (_running || _disposed) return;
        _running = true;
        _ = PumpAsync();
    }

    private async Task PumpAsync()
    {
        // Let layout finish registering all visible rows and the selected row.
        await Task.Yield();
        try
        {
            while (!_disposed)
            {
                var path = _visible.Values.Select(row => row.Path).FirstOrDefault(path =>
                    path != _selected && !_cache.ContainsKey(path) && !_failed.Contains(path));
                if (path is null) return;
                try { Store(await fetch(path, _lifetime.Token)); }
                catch (OperationCanceledException) when (_disposed) { return; }
                catch (Exception error) when (error is OperationCanceledException or IOException or
                    InvalidOperationException or ArgumentException or TimeoutException or FormatException)
                { _failed.Add(path); }
                // Re-evaluate visibility after every response. Never drain a stale page queue.
            }
        }
        finally { _running = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _visible.Clear(); _cache.Clear(); _recent.Clear(); _failed.Clear();
    }

    private sealed class Subscription(Action stop) : IDisposable
    {
        private Action? _stop = stop;
        public void Dispose() => Interlocked.Exchange(ref _stop, null)?.Invoke();
    }
}
