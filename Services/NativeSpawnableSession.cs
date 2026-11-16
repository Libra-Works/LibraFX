using System.Runtime.InteropServices;
using LiberaFx.Services.Compilation;

namespace LiberaFx.Services;

public sealed record SpawnableSessionEvent(Guid ContainerId, ulong InstanceId, uint Kind, double Time, ulong Seed);
public sealed record SpawnableSessionValue(double[] Lanes, int Width);

/// <summary>
/// Owner-thread host bridge. All numeric execution and lifetime expiration use
/// the portable C++ runtime. Rendering stays with the host; this is not a UE
/// preview implementation. Caller supplies an explicit native library path.
/// </summary>
public sealed class NativeSpawnableSession : IDisposable
{
    private static int _activeOwners;
    public static int ActiveOwnerCount => Volatile.Read(ref _activeOwners);
    private readonly SpawnableGroupPackage _package;
    private readonly nint _library;
    private readonly Dictionary<Guid, TemplateState> _templates = [];
    private readonly List<SpawnableSessionEvent> _events = [];
    private readonly Dictionary<Guid, uint> _parentValues = [];
    private ulong _parentProgram, _parentInstance;
    private double _time = -1;
    private bool _disposed;
    private bool _counted;
    private sealed class TemplateState(SpawnableTemplate template)
    {
        public SpawnableTemplate Template { get; } = template;
        public ulong Program, Pool;
        public Dictionary<ulong, (double Start, double Life)> Instances { get; } = [];
    }
    public IReadOnlyList<SpawnableSessionEvent> Events => _events;
    public int ActiveCount => _templates.Values.Sum(state => state.Instances.Count);
    public IReadOnlyList<(Guid Container, ulong Instance)> ActiveInstances =>
        _templates.SelectMany(pair => pair.Value.Instances.Keys.Select(id => (pair.Key, id))).ToArray();

    public NativeSpawnableSession(SpawnableGroupPackage package, string nativeLibraryPath)
    {
        if (!Path.IsPathFullyQualified(nativeLibraryPath)) throw new ArgumentException("Use an absolute native library path.");
        _package = package;
        _library = NativeLibrary.Load(nativeLibraryPath);
        try
        {
            foreach (var template in package.Templates)
            {
                if (template.GpuContainers.Count != 0)
                    throw new NotSupportedException("This CPU host bridge cannot execute GPU containers.");
                var state = new TemplateState(template);
                _templates.Add(template.ContainerId, state);
                Check(template.Bytecode[4] == 3
                    ? Fn<CreateParent>("libra_program_create_spawnable")(template.Bytecode, (ulong)template.Bytecode.Length, [], 0, out state.Program)
                    : Fn<CreateProgram>("libra_program_create")(template.Bytecode, (ulong)template.Bytecode.Length, out state.Program), template.ContainerId);
                Check(Fn<CreatePool>("libra_generator_pool_create")(state.Program, Id(template.ContainerId), 32, 4 * 1024 * 1024, 128, out state.Pool), template.ContainerId);
                var lifecycle = new NativeLifecycle { Start = Id(template.Lifecycle?.OnStartNodeId ?? Guid.Empty),
                    Life = Id(template.Lifecycle?.LifeNodeId ?? Guid.Empty),
                    Flags = (template.Lifecycle?.OnStartNodeId is not null ? 1u : 0) |
                        (template.Lifecycle?.LifeNodeId is not null ? 2u : 0), Budget = 10000 };
                Check(Fn<Configure>("libra_generator_configure_lifecycle")(state.Pool, ref lifecycle), template.ContainerId);
            }
            var bindings = _templates.Values.Select(state =>
            {
                var nodes = new byte[64];
                var widths = new byte[4];
                var colors = new byte[4];
                for (var i = 0; i < state.Template.Inputs.Count; ++i)
                {
                    Id(state.Template.Inputs[i].TemplateNodeId).CopyTo(nodes, i * 16);
                    widths[i] = checked((byte)state.Template.Inputs[i].Width);
                    colors[i] = state.Template.Inputs[i].IsColor ? (byte)1 : (byte)0;
                }
                return new NativeBinding { Container = Id(state.Template.ContainerId), Type = Id(state.Template.SignalTypeId),
                    Pool = state.Pool, Count = (uint)state.Template.Inputs.Count, Widths = widths, Colors = colors, Nodes = nodes };
            }).ToArray();
            Check(package.ParentBytecode[4] == 3
                ? Fn<CreateParent>("libra_program_create_spawnable")(package.ParentBytecode, (ulong)package.ParentBytecode.Length,
                    bindings, (uint)bindings.Length, out _parentProgram)
                : Fn<CreateProgram>("libra_program_create")(package.ParentBytecode, (ulong)package.ParentBytecode.Length, out _parentProgram));
            Check(Fn<CreateInstance>("libra_instance_create")(_parentProgram, out _parentInstance));
            _counted = true;
            Interlocked.Increment(ref _activeOwners);
        }
        catch { Dispose(); throw; }
    }

    public void Advance(double time)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(time) || time < 0 || time < _time)
            throw new ArgumentException("Advance uses nonnegative monotonic seconds. Recreate session for deterministic reset.");
        if (_time < 0)
        {
            Check(Fn<EvaluateParent>("libra_evaluate")(_parentInstance, 0, [], 0, []));
            if (_package.OnStartNodeId is { } start)
                Check(Fn<Dispatch>("libra_dispatch_signal")(_parentInstance, Id(start), 0, 10000));
        }
        Pump(0);
        Check(Fn<EvaluateParent>("libra_evaluate")(_parentInstance, time, [], 0, []));
        // Each routed spawn is evaluated this frame, including a skipped-over
        // expiry. No flattening or Time substitution is performed.
        var evaluated = new HashSet<(Guid, ulong)>();
        for (var round = 0; round < 128; ++round)
        {
            var pending = ActiveInstances.Where(key => !evaluated.Contains(key)).ToArray();
            if (pending.Length == 0) { _time = time; return; }
            foreach (var key in pending)
            {
                evaluated.Add(key);
                var state = _templates[key.Container];
                Check(Fn<EvaluateChild>("libra_generator_evaluate")(state.Pool, key.Instance, time), key.Container, key.Instance);
                Pump(time);
            }
        }
        throw new InvalidOperationException("Spawnable routing exceeded the bounded per-frame work limit.");
    }

    private void Pump(double time)
    {
        for (var round = 0; round < 128; ++round)
        {
            var progress = false;
            foreach (var state in _templates.Values)
            {
                Check(Fn<Process>("libra_generator_process_spawns")(state.Pool, 128, out var processed), state.Template.ContainerId);
                progress |= processed != 0;
                while (true)
                {
                    Check(Fn<NextEvent>("libra_generator_next_event")(state.Pool, out var value, out var has), state.Template.ContainerId);
                    if (has == 0) break;
                    progress = true;
                    var container = state.Template.ContainerId;
                    if (value.Kind == 1)
                    {
                        Check(Fn<InstanceAction>("libra_generator_start")(state.Pool, value.Instance), container, value.Instance);
                        var life = state.Template.Lifecycle?.LifeNodeId is { } lifeId ? Read(container, value.Instance, lifeId).Lanes[0] : double.PositiveInfinity;
                        if (life < 0 || double.IsNaN(life) || (state.Template.Lifecycle?.LifeNodeId is not null && !double.IsFinite(life)))
                            throw new InvalidDataException($"Spawnable {container} instance {value.Instance}: Life Time must be nonnegative finite seconds.");
                        state.Instances.Add(value.Instance, (value.Start, life));
                        _events.Add(new(container, value.Instance, 1, value.Start, value.Seed));
                    }
                    else if (value.Kind == 3)
                    {
                        var timing = state.Instances[value.Instance];
                        var expiry = timing.Start + timing.Life;
                        _events.Add(new(container, value.Instance, 3, expiry, value.Seed));
                        Check(Fn<EvaluateParent>("libra_evaluate")(_parentInstance, expiry, [], 0, []));
                        foreach (var route in _package.DestroyRoutes.Where(route => route.SourceContainerId == container))
                        {
                            var captures = new List<NativeCapture>();
                            foreach (var binding in route.Captures)
                            {
                                var captured = binding.SourceContainerId is { } source
                                    ? Read(source, value.Instance, binding.SourceNodeId)
                                    : ReadParent(binding.SourceNodeId);
                                if (binding.SourceComponent + binding.Width > captured.Width)
                                    throw new InvalidDataException("Destroy capture exceeds the source value width.");
                                for (var lane = 0; lane < binding.Width; ++lane)
                                    captures.Add(new NativeCapture { Node = Id(binding.TargetNodeId), Component = (uint)lane,
                                        Value = captured.Lanes[binding.SourceComponent + lane] });
                            }
                            var target = _templates[route.TargetContainerId];
                            Check(Fn<Spawn>("libra_generator_spawn")(target.Pool, expiry, captures.ToArray(),
                                (uint)captures.Count, out _), target.Template.ContainerId);
                        }
                        // Reading/routing happens before retirement.
                        Check(Fn<InstanceAction>("libra_generator_finalize")(state.Pool, value.Instance), container, value.Instance);
                        Check(Fn<EvaluateParent>("libra_evaluate")(_parentInstance, time, [], 0, []));
                    }
                    else if (value.Kind == 2)
                    {
                        state.Instances.Remove(value.Instance);
                        _events.Add(new(container, value.Instance, 2, time, value.Seed));
                    }
                    else throw new InvalidDataException("Unknown native generator event.");
                }
            }
            if (!progress) return;
        }
        throw new InvalidOperationException("Spawnable event routing exceeded its bounded work limit.");
    }

    public SpawnableSessionValue Read(Guid container, ulong instance, Guid node)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Check(Fn<ReadChild>("libra_generator_value")(_templates[container].Pool, instance, Id(node), out var value), container, instance);
        return new([value.X, value.Y, value.Z, value.W], checked((int)value.Count));
    }
    private SpawnableSessionValue ReadParent(Guid node)
    {
        if (!_parentValues.TryGetValue(node, out var binding))
        {
            Check(Fn<BindValue>("libra_bind_value")(_parentInstance, Id(node), out binding));
            _parentValues.Add(node, binding);
        }
        Check(Fn<ReadValues>("libra_values_read")(_parentInstance, [binding], 1, out var value));
        return new([value.X, value.Y, value.Z, value.W], checked((int)value.Count));
    }
    public SpawnableSessionValue ReadRoot(Guid node)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ReadParent(node);
    }
    public double InstanceStartTime(Guid container, ulong instance)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _templates[container].Instances[instance].Start;
    }
    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var state in _templates.Values) Check(Fn<PoolAction>("libra_generator_reset")(state.Pool), state.Template.ContainerId);
        // Reset records are drained without executing OnStart or any Destroy.
        Pump(_time < 0 ? 0 : _time);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var failures = new List<string>();
        void ReleaseHandle(string function, ulong handle)
        {
            if (handle == 0) return;
            try
            {
                var result = Fn<Release>(function)(handle);
                if (result != 0) failures.Add($"{function}: result={result}");
            }
            catch (Exception ex) { failures.Add(function + ": " + ex.Message); }
        }
        ReleaseHandle("libra_instance_release", _parentInstance);
        ReleaseHandle("libra_program_release", _parentProgram);
        foreach (var state in _templates.Values)
        {
            ReleaseHandle("libra_generator_pool_release", state.Pool);
            ReleaseHandle("libra_program_release", state.Program);
        }
        NativeLibrary.Free(_library);
        if (_counted) Interlocked.Decrement(ref _activeOwners);
        if (failures.Count != 0) throw new InvalidOperationException("Native preview owner cleanup failed: " + string.Join("; ", failures));
    }
    private T Fn<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
    private static byte[] Id(Guid id) => id.ToByteArray(bigEndian: true);
    private static void Check(int result, Guid container = default, ulong instance = 0)
    {
        if (result != 0) throw new InvalidOperationException($"Native lifecycle failed: result={result}, container={container}, instance={instance}.");
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct NativeBinding
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Container;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Type;
        public ulong Pool;
        public uint Count, Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public byte[] Widths;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public byte[] Colors;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] Nodes;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct NativeLifecycle
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Start;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Life;
        public uint Flags, Budget;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct NativeCapture
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Node;
        public uint Slot, Component;
        public double Value;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct NativeEvent
    {
        public uint Kind, Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Container;
        public ulong Instance;
        public double Start;
        public ulong Seed;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct NativeValue { public double X, Y, Z, W; public uint Count, Reserved; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateProgram(byte[] bytes, ulong size, out ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateParent(byte[] bytes, ulong size, [In] NativeBinding[] bindings, uint count, out ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreatePool(ulong program, byte[] container, uint max, ulong budget, uint events, out ulong pool);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateInstance(ulong program, out ulong instance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Release(ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EvaluateParent(ulong instance, double time, uint[] bindings, uint count, NativeValue[] values);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Dispatch(ulong instance, byte[] node, double time, uint budget);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Process(ulong pool, uint max, out uint processed);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Configure(ulong pool, ref NativeLifecycle lifecycle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NextEvent(ulong pool, out NativeEvent value, out uint has);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EvaluateChild(ulong pool, ulong instance, double time);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadChild(ulong pool, ulong instance, byte[] node, out NativeValue value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int BindValue(ulong instance, byte[] node, out uint binding);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadValues(ulong instance, uint[] bindings, uint count, out NativeValue value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InstanceAction(ulong pool, ulong instance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PoolAction(ulong pool);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Spawn(ulong pool, double time, [In] NativeCapture[] captures, uint count, out ulong instance);
}
