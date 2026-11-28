using LiberaFx.Services.Compilation;

namespace LiberaFx.Services.Preview;

public sealed record SpawnableSessionEvent(Guid ContainerId, ulong InstanceId, uint Kind, double Time, ulong Seed,
    Guid SignalNodeId = default);
public sealed record SpawnableSessionValue(double[] Lanes, int Width);

/// <summary>Owner-thread bridge to the native LFXP package host.</summary>
public sealed class NativeSpawnableSession : IDisposable
{
    private readonly EditorNativeHostSession _host;
    private static int _activeOwners;
    private double _time = -1;
    private bool _disposed;

    public static int ActiveOwnerCount => Volatile.Read(ref _activeOwners);
    public IReadOnlyList<SpawnableSessionEvent> Events => _host.Events;
    public int ActiveCount => _host.ActiveCount;
    public bool RootVisible => _host.RootVisible;
    public IReadOnlyList<(Guid Container, ulong Instance)> ActiveInstances => _host.ActiveInstances;

    public NativeSpawnableSession(SpawnableGroupPackage package, string nativeLibraryPath, uint instructionBudget = 10000)
    {
        if (!Path.IsPathFullyQualified(nativeLibraryPath))
        {
            throw new ArgumentException("Use an absolute native library path.", nameof(nativeLibraryPath));
        }
        _host = new EditorNativeHostSession(package, nativeLibraryPath, instructionBudget);
        Interlocked.Increment(ref _activeOwners);
    }

    public void Advance(double time) => Advance(time, _time < 0 ? time : time - _time);

    public void Advance(double time, double frameDelta)
    {
        _host.Advance(time, frameDelta);
        _time = time;
    }

    public void DispatchSignal(Guid nodeId) => _host.SendSignal(nodeId);
    public int TryDispatchSignal(Guid nodeId) => _host.TrySendSignal(nodeId);
    public SpawnableSessionValue Read(Guid container, ulong instance, Guid node) => _host.Read(container, instance, node);
    public SpawnableSessionValue ReadRoot(Guid node) => _host.ReadRoot(node);
    public double InstanceStartTime(Guid container, ulong instance) => _host.InstanceStartTime(container, instance);
    public void ForceTerminate(Guid container, ulong instance) => _host.ForceTerminate(container, instance);
    public void Stop() => _host.Stop();
    public void End() => _host.End();
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            _host.Dispose();
        }
        finally
        {
            Interlocked.Decrement(ref _activeOwners);
        }
    }
}
