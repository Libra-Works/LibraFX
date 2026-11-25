using System.Runtime.InteropServices;
using LiberaFx.Services.Compilation;

namespace LiberaFx.Services;

public sealed record EditorNativeGameIoEvent(Guid ContainerId, ulong InstanceId, string PortName,
    byte Kind, double[] Values);

public sealed class NativeHostException(int result) : InvalidOperationException("Native preview host failed with result " + result + ".")
{
    public int Result { get; } = result;
}

public sealed class EditorNativeHostSession : IDisposable
{
    private readonly nint _library;
    private readonly SpawnableGroupPackage _package;
    private readonly Dictionary<string, Delegate> _functions = [];
    private readonly Dictionary<(Guid Container, ulong Instance), double> _instances = [];
    private readonly List<SpawnableSessionEvent> _events = [];
    private readonly List<EditorNativeGameIoEvent> _gameEvents = [];
    private ulong _handle;
    private bool _disposed;
    private bool _counted;
    private bool _finished;
    private static int _activeOwners;
    public static int ActiveOwnerCount => Volatile.Read(ref _activeOwners);
    public int ActiveCount => _instances.Count;
    public bool RootVisible => !_finished;
    public IReadOnlyList<SpawnableSessionEvent> Events => _events;
    public IReadOnlyList<EditorNativeGameIoEvent> GameIoEvents => _gameEvents;
    public IReadOnlyList<(Guid Container, ulong Instance)> ActiveInstances => _package.Templates
        .SelectMany(template => _instances.Keys.Where(key => key.Container == template.ContainerId)
            .OrderBy(key => key.Instance)).ToArray();

    public EditorNativeHostSession(SpawnableGroupPackage package, string library, uint instructionBudget = 10000)
    {
        _package = package;
        _library = NativeLibrary.Load(library);
        try
        {
            if (!NativeLibrary.TryGetExport(_library, "libra_spawnable_host_create", out _))
            {
                throw new NotSupportedException("The native runtime does not support LFXP package hosts.");
            }
            var bytes = SpawnableGroupPackageBinary.Serialize(package);
            Check(Fn<Create>("libra_spawnable_host_create")(bytes, (ulong)bytes.Length,
                32, 4 * 1024 * 1024, 128, instructionBudget, out _handle));
            _counted = true;
            Interlocked.Increment(ref _activeOwners);
        }
        catch { Dispose(); throw; }
    }

    public void Advance(double time, double delta = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (var attempt = 0; attempt < 64; ++attempt)
        {
            var result = Fn<AdvanceHost>("libra_spawnable_host_advance")(_handle, time, delta);
            Drain();
            if (result == 6) continue;
            Check(result);
            Check(Fn<Finished>("libra_spawnable_host_root_finished")(_handle, out var finished));
            _finished = finished != 0;
            return;
        }
        throw new InvalidOperationException("Native preview exceeded its bounded event-drain budget.");
    }

    public void SendSignal(Guid node)
    {
        Check(TrySendSignal(node));
    }

    public int TrySendSignal(Guid node)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = Fn<Dispatch>("libra_spawnable_host_dispatch_signal")(_handle, Id(node));
        Drain();
        Check(Fn<Finished>("libra_spawnable_host_root_finished")(_handle, out var finished));
        _finished = finished != 0;
        return result;
    }

    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Check(Fn<Release>("libra_spawnable_host_stop")(_handle));
        Drain();
        _finished = true;
    }

    public void End()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Check(Fn<Release>("libra_spawnable_host_request_natural_end")(_handle));
        Drain();
        Check(Fn<Finished>("libra_spawnable_host_root_finished")(_handle, out var finished));
        _finished = finished != 0;
    }

    public void ForceTerminate(Guid container, ulong instance)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Check(Fn<Force>("libra_spawnable_host_force_terminate")(_handle, 1, Id(container), instance));
        Drain();
    }

    private void Drain()
    {
        while (true)
        {
            Check(Fn<NextEvent>("libra_spawnable_host_next_event")(_handle, out var item, out var has));
            if (has == 0) break;
            var container = new Guid(item.Container, bigEndian: true);
            if (_events.Count >= 100_000)
                throw new InvalidDataException("Native preview event history exceeded its bounded limit; resync preview.");
            _events.Add(new(container, item.Instance, item.Kind, item.Time, item.Seed,
                new Guid(item.Signal, bigEndian: true)));
            if (item.Kind == 1) _instances.Add((container, item.Instance), item.Time);
            else if (item.Kind is 2 or 3 || (item.Kind == 5 && item.Instance != 0))
            {
                _instances.Remove((container, item.Instance));
            }
            else if (item.Kind == 5 && container == Guid.Empty && item.Instance == 0)
            {
                _instances.Clear();
            }
        }
        while (true)
        {
            Check(Fn<NextGameEvent>("libra_spawnable_host_next_game_io_event")(_handle, out var item, out var has));
            if (has == 0) break;
            if (item.Port >= _package.GameIoPorts.Count || item.Width > 16)
                throw new InvalidDataException("Native preview returned an invalid Game I/O event.");
            if (_gameEvents.Count >= 100_000)
                throw new InvalidDataException("Native preview Game I/O history exceeded its bounded limit; resync preview.");
            _gameEvents.Add(new(new Guid(item.Container, bigEndian: true), item.Instance,
                _package.GameIoPorts[(int)item.Port].Name, item.Kind, item.Values[..item.Width]));
        }
    }

    public double InstanceStartTime(Guid container, ulong instance) => _instances[(container, instance)];
    public SpawnableSessionValue ReadRoot(Guid node)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Check(Fn<ReadRootValue>("libra_spawnable_host_root_value")(_handle, Id(node), out var value));
        return Value(value);
    }
    public SpawnableSessionValue Read(Guid container, ulong instance, Guid node)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Check(Fn<ReadChildValue>("libra_spawnable_host_child_value")(_handle, Id(container), instance, Id(node), out var value));
        return Value(value);
    }

    private static SpawnableSessionValue Value(HostValue value)
    {
        if (value.Width is < 1 or > 4) throw new InvalidDataException("Invalid native preview value width.");
        return new([value.X, value.Y, value.Z, value.W], (int)value.Width);
    }
    private static byte[] Id(Guid id)
    {
        var bytes = new byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        return bytes;
    }
    private T Fn<T>(string name) where T : Delegate
    {
        if (!_functions.TryGetValue(name, out var value))
            _functions.Add(name, value = Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name)));
        return (T)value;
    }
    private static void Check(int result)
    {
        if (result != 0)
        {
            throw new NativeHostException(result);
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_handle != 0)
            {
                var handle = _handle;
                _handle = 0;
                Check(Fn<Release>("libra_spawnable_host_release")(handle));
            }
        }
        finally
        {
            NativeLibrary.Free(_library);
            _functions.Clear();
            if (_counted) { _counted = false; Interlocked.Decrement(ref _activeOwners); }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct HostValue { public double X, Y, Z, W; public uint Width, Reserved; }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct HostEvent
    {
        public uint Kind, Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Container;
        public ulong Instance;
        public double Time;
        public ulong Seed;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Signal;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct GameEvent
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Container;
        public ulong Instance;
        public uint Port;
        public byte Kind, Width, Color, Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public double[] Values;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Create(byte[] data, ulong bytes, uint active, ulong memory, uint events, uint budget, out ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AdvanceHost(ulong handle, double time, double delta);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Dispatch(ulong handle, byte[] node);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Force(ulong handle, uint scope, byte[] container, ulong instance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NextEvent(ulong handle, out HostEvent item, out uint has);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NextGameEvent(ulong handle, out GameEvent item, out uint has);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadRootValue(ulong handle, byte[] node, out HostValue value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadChildValue(ulong handle, byte[] container, ulong instance, byte[] node, out HostValue value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Finished(ulong handle, out uint finished);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Release(ulong handle);
}
