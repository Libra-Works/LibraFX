using System.Runtime.InteropServices;
using LiberaFx.Services.Compilation;

namespace LiberaFx.Services;

public sealed record SpawnableSessionEvent(Guid ContainerId, ulong InstanceId, uint Kind, double Time, ulong Seed,
    Guid SignalNodeId = default);
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
    private readonly HashSet<(Guid Container, ulong Instance)> _ownerDestroyNotified = [];
    private readonly Dictionary<ulong, (SpawnableDestroyRoute Route, NativeCapture[] Frozen)> _scheduledRoutes = [];
    private readonly Dictionary<Guid, uint> _parentValues = [];
    private ulong _parentProgram, _parentInstance;
    private double _time = -1;
    private double _rootAge;
    private double _rootFinishedAt;
    private double _frameDelta;
    private bool _rootEnding, _rootFinished, _rootStopped, _rootDestroySent;
    private bool _disposed;
    private bool _counted;
    private bool _failed;
    private ulong _nextScheduledToken = 1;
    private sealed class TemplateState(SpawnableTemplate template)
    {
        public SpawnableTemplate Template { get; } = template;
        public ulong Program, Pool;
        public Dictionary<ulong, (double Start, double Life)> Instances { get; } = [];
    }
    public IReadOnlyList<SpawnableSessionEvent> Events => _events;
    public int ActiveCount => _templates.Values.Sum(state => state.Instances.Count);
    public bool RootVisible => !_rootStopped && (!_rootFinished || _time <= _rootFinishedAt);
    public IReadOnlyList<(Guid Container, ulong Instance)> ActiveInstances =>
        _templates.SelectMany(pair => pair.Value.Instances.Keys.Select(id => (pair.Key, id))).ToArray();

    public NativeSpawnableSession(SpawnableGroupPackage package, string nativeLibraryPath)
    {
        if (!Path.IsPathFullyQualified(nativeLibraryPath)) throw new ArgumentException("Use an absolute native library path.");
        _package = package;
        _library = NativeLibrary.Load(nativeLibraryPath);
        try
        {
            if (package.HasDeferredSignals &&
                (!NativeLibrary.TryGetExport(_library, "libra_effect_next_deferred_signal", out _) ||
                 !NativeLibrary.TryGetExport(_library, "libra_effect_dispatch_deferred_signal", out _)))
                throw new NotSupportedException("General Delay signals require a matching native runtime.");
            if (!NativeLibrary.TryGetExport(_library, "libra_effect_request_natural_end", out _))
                throw new NotSupportedException("Natural root shutdown requires a matching native runtime.");
            if (package.HasChildDeferredSignals &&
                (!NativeLibrary.TryGetExport(_library, "libra_generator_next_deferred_signal", out _) ||
                 !NativeLibrary.TryGetExport(_library, "libra_generator_dispatch_deferred_signal", out _) ||
                 !NativeLibrary.TryGetExport(_library, "libra_generator_elapsed", out _)))
                throw new NotSupportedException("Spawnable Delay signals require a matching native runtime.");
            if (package.HasLiveInputs && !NativeLibrary.TryGetExport(_library, "libra_generator_set_live", out _))
                throw new NotSupportedException("Live Spawn inputs require a matching native runtime.");
            if (package.Templates.Any(template => template.CapturedInputs is { Count: > 0 }) &&
                !NativeLibrary.TryGetExport(_library, "libra_generator_set_captured", out _))
                throw new NotSupportedException("Extended CaptureAtSpawn inputs require a matching native runtime.");
            if (package.Templates.Any(template => template.SignalInputs is { Count: > 0 }) &&
                (!NativeLibrary.TryGetExport(_library, "libra_generator_dispatch_signal", out _) ||
                 !NativeLibrary.TryGetExport(_library, "libra_effect_dispatch_signal", out _) ||
                 !NativeLibrary.TryGetExport(_library, "libra_program_create_spawnable_v2", out _)))
                throw new NotSupportedException("Spawnable Signal inputs require a matching native runtime.");
            if (package.Templates.Any(template => template.SignalOutputs is { Count: > 0 }) &&
                (!NativeLibrary.TryGetExport(_library, "libra_generator_set_signal_outputs", out _) ||
                 !NativeLibrary.TryGetExport(_library, "libra_generator_next_signal_event", out _) ||
                 !NativeLibrary.TryGetExport(_library, "libra_effect_dispatch_signal", out _) ||
                 !NativeLibrary.TryGetExport(_library, "libra_program_create_spawnable_v2", out _)))
                throw new NotSupportedException("Spawnable Signal outputs require a matching native runtime.");
            foreach (var template in package.Templates)
            {
                if (template.GpuContainers.Count != 0)
                    throw new NotSupportedException("This CPU host bridge cannot execute GPU containers.");
                var state = new TemplateState(template);
                _templates.Add(template.ContainerId, state);
                Check(template.Bytecode[4] >= 3
                    ? Fn<CreateParent>("libra_program_create_spawnable")(template.Bytecode, (ulong)template.Bytecode.Length, [], 0, out state.Program)
                    : Fn<CreateProgram>("libra_program_create")(template.Bytecode, (ulong)template.Bytecode.Length, out state.Program), template.ContainerId);
                Check(Fn<CreatePool>("libra_generator_pool_create")(state.Program, Id(template.ContainerId),
                    32, 4 * 1024 * 1024, 128, out state.Pool), template.ContainerId);
                if (template.SignalOutputs is { Count: > 0 } outputs)
                    Check(Fn<SetSignalOutputs>("libra_generator_set_signal_outputs")(state.Pool,
                        outputs.SelectMany(output => Id(output.TemplateNodeId)).ToArray(), (uint)outputs.Count),
                        template.ContainerId);
                var lifecycle = new NativeLifecycle { Start = Id(template.Lifecycle?.OnStartNodeId ?? Guid.Empty),
                    Life = Id(template.Lifecycle?.LifeNodeId ?? Guid.Empty),
                    Flags = (template.Lifecycle?.OnStartNodeId is not null ? 1u : 0) |
                        (template.Lifecycle?.LifeNodeId is not null ? 2u : 0) |
                        (package.HasChildDeferredSignals ? 4u : 0), Budget = 10000 };
                Check(Fn<Configure>("libra_generator_configure_lifecycle")(state.Pool, ref lifecycle), template.ContainerId);
            }
            var bindings = _templates.Values.Select(state =>
            {
                var inputs = state.Template.UsesExtendedCaptureInputs
                    ? Array.Empty<SpawnableInput>() : state.Template.Inputs;
                var nodes = new byte[64];
                var widths = new byte[4];
                var colors = new byte[4];
                for (var index = 0; index < inputs.Count; ++index)
                {
                    Id(inputs[index].TemplateNodeId).CopyTo(nodes, index * 16);
                    widths[index] = checked((byte)inputs[index].Width);
                    colors[index] = inputs[index].IsColor ? (byte)1 : (byte)0;
                }
                return new NativeBinding { Container = Id(state.Template.ContainerId), Type = Id(state.Template.SignalTypeId),
                    Pool = state.Pool, Count = (uint)inputs.Count, Widths = widths, Colors = colors, Nodes = nodes };
            }).ToArray();
            if (package.HasSignalPorts)
            {
                var signalInputs = package.Templates.SelectMany(template =>
                    (template.SignalInputs ?? []).Select(input => new NativeSignalInputBinding
                    {
                        Container = Id(template.ContainerId),
                        Type = Id(SpawnableGroupPackage.SignalInputTypeId(template.ContainerId, input.TemplateNodeId)),
                        Pool = _templates[template.ContainerId].Pool,
                        Proxy = Id(input.ProxyNodeId),
                        Node = Id(input.TemplateNodeId)
                    })).ToArray();
                Check(Fn<CreateParentWithSignals>("libra_program_create_spawnable_v2")(
                    package.ParentBytecode, (ulong)package.ParentBytecode.Length, bindings, (uint)bindings.Length,
                    signalInputs, (uint)signalInputs.Length, out _parentProgram));
            }
            else
                Check(package.ParentBytecode[4] >= 3
                    ? Fn<CreateParent>("libra_program_create_spawnable")(package.ParentBytecode, (ulong)package.ParentBytecode.Length,
                        bindings, (uint)bindings.Length, out _parentProgram)
                    : Fn<CreateProgram>("libra_program_create")(package.ParentBytecode, (ulong)package.ParentBytecode.Length, out _parentProgram));
            Check(Fn<CreateInstance>("libra_instance_create")(_parentProgram, out _parentInstance));
            _counted = true;
            Interlocked.Increment(ref _activeOwners);
        }
        catch { Dispose(); throw; }
    }

    public void Advance(double time) => Advance(time, _time < 0 ? time : time - _time);

    public void DispatchSignal(Guid nodeId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failed) throw new InvalidOperationException("Recreate the session after a failed lifecycle operation.");
        if (nodeId == Guid.Empty || _time < 0 || _rootEnding || _rootFinished || _rootStopped)
            throw new InvalidOperationException("Dispatch requires a live, started root effect and a valid signal node.");
        try
        {
            SetRootTimeContext(_time);
            Check(Fn<DispatchEffectSignal>("libra_effect_dispatch_signal")(
                _parentInstance, Id(nodeId), _time, 10000));
            Pump(_time);
        }
        catch { _failed = true; throw; }
    }

    public void Advance(double time, double frameDelta)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(time) || time < 0 || time < _time)
            throw new ArgumentException("Advance uses nonnegative monotonic seconds. Recreate session for deterministic reset.");
        if (!double.IsFinite(frameDelta) || frameDelta < 0)
            throw new ArgumentException("Frame delta must be nonnegative finite seconds.", nameof(frameDelta));
        if (_failed) throw new InvalidOperationException("Recreate the session after a failed lifecycle operation.");
        try
        {
            _frameDelta = frameDelta;
            AdvanceManaged(time);
        }
        catch { _failed = true; throw; }
    }

    private void AdvanceManaged(double time)
    {
        if (_rootStopped) throw new InvalidOperationException("Recreate the session after stopping the root effect.");
        if (_time < 0)
        {
            NativeLifetimeSample initial;
            var startNode = _package.OnStartNodeId is { } start ? Id(start) : null;
            SetRootTimeContext(0);
            Check(Fn<StartInfinite>("libra_effect_start")(_parentInstance, 0, 1, startNode, 10000, out initial));
            _rootEnding = initial.Expired != 0;
            _rootAge = 0;
            _time = 0;
            Pump(0);
            DispatchScheduled(0);
            BeginAutoEndIfIdle(0);
            NotifyOwnerDestroy(0);
            FinishManagedIfQuiescent();
        }
        if (!_rootFinished)
        {
            for (var round = 0; round < 128; ++round)
            {
                var childExpiry = _templates.Values.SelectMany(state => state.Instances.Values)
                    .Select(instance => instance.Start + instance.Life)
                    .Where(expiry => expiry > _rootAge && expiry <= time)
                    .DefaultIfEmpty(double.PositiveInfinity).Min();
                var next = Math.Min(time, Math.Min(NextScheduledTime(), childExpiry));
                if (next <= _rootAge) break;
                EvaluateManagedFrame(next);
                DispatchScheduled(next);
                BeginAutoEndIfIdle(next);
                NotifyOwnerDestroy(next);
                FinishManagedIfQuiescent();
                if (_rootFinished || next >= time) break;
                if (round == 127) throw new InvalidOperationException("Spawnable expiry routing exceeded its bounded per-frame work limit.");
            }
        }
        _time = time;
    }

    private void FinishManagedIfQuiescent()
    {
        if (!_rootEnding || _rootFinished || ActiveCount != 0 || _scheduledRoutes.Count != 0 ||
            NextDeferredTime() != double.PositiveInfinity) return;
        if (!_rootDestroySent && _package.OnDestroyNodeId is { } node)
        {
            Check(Fn<DestroyEffect>("libra_effect_destroy")(_parentInstance, Id(node), 10000));
            _rootDestroySent = true;
            Pump(_rootAge);
            DispatchScheduled(_rootAge);
            NotifyOwnerDestroy(_rootAge);
            if (ActiveCount != 0 || _scheduledRoutes.Count != 0 ||
                NextDeferredTime() != double.PositiveInfinity) return;
        }
        Check(Fn<Release>("libra_effect_finish")(_parentInstance));
        _rootFinished = true;
        _rootFinishedAt = _rootAge;
    }

    private void BeginAutoEndIfIdle(double time)
    {
        if (_rootEnding || _rootFinished || !_package.AutoDestroyWhenIdle || _templates.Count == 0 ||
            _scheduledRoutes.Count != 0 || NextDeferredTime() != double.PositiveInfinity) return;
        RequestNaturalEnd(time);
    }

    private void RequestNaturalEnd(double time)
    {
        if (_rootEnding || _rootFinished || _rootStopped) return;
        Check(Fn<RequestNaturalEnd>("libra_effect_request_natural_end")(_parentInstance));
        _rootEnding = true;
        NotifyOwnerDestroy(time);
        FinishManagedIfQuiescent();
    }

    private double NextScheduledTime()
    {
        Check(Fn<ScheduledEventAction>("libra_effect_next_scheduled")(_parentInstance, out var item, out var has));
        return Math.Min(has == 0 ? double.PositiveInfinity : item.Time, Math.Min(NextDeferredTime(), NextChildDeferredTime()));
    }

    private double NextChildDeferredTime()
    {
        if (!_package.HasChildDeferredSignals) return double.PositiveInfinity;
        var next = double.PositiveInfinity;
        foreach (var state in _templates.Values)
        {
            foreach (var id in state.Instances.Keys)
            {
                Check(Fn<NextChildDeferred>("libra_generator_next_deferred_signal")(state.Pool, id, out var time, out var has),
                    state.Template.ContainerId, id);
                if (has != 0) next = Math.Min(next, time);
            }
        }
        return next;
    }

    private double NextDeferredTime()
    {
        if (!_package.HasDeferredSignals) return double.PositiveInfinity;
        Check(Fn<NextDeferred>("libra_effect_next_deferred_signal")(_parentInstance, out var time, out var has));
        return has == 0 ? double.PositiveInfinity : time;
    }

    private void DispatchScheduled(double time)
    {
        for (var round = 0; round < 128; ++round)
        {
            Check(Fn<ScheduledEventAction>("libra_effect_take_scheduled")(_parentInstance, out var item, out var has));
            if (has != 0)
            {
                if (item.Time != time || !_scheduledRoutes.Remove(item.Token, out var pending))
                    throw new InvalidOperationException("Invalid scheduled Spawnable continuation.");
                SpawnRoute(pending.Route, time, pending.Frozen);
            }
            else if (NextDeferredTime() == time)
                Check(Fn<DispatchDeferred>("libra_effect_dispatch_deferred_signal")(_parentInstance, time, 10000));
            else if (NextChildDeferredTime() == time)
            {
                foreach (var state in _templates.Values)
                {
                    foreach (var id in state.Instances.Keys.ToArray())
                    {
                        Check(Fn<NextChildDeferred>("libra_generator_next_deferred_signal")(state.Pool, id, out var due, out var pending),
                            state.Template.ContainerId, id);
                        if (pending == 0 || due != time) continue;
                        Check(Fn<DispatchChildDeferred>("libra_generator_dispatch_deferred_signal")(state.Pool, id, time, 10000),
                            state.Template.ContainerId, id);
                    }
                }
            }
            else return;
            Pump(time);
        }
        if (NextScheduledTime() == time)
            throw new InvalidOperationException("Scheduled Spawnable routing exceeded the bounded work limit.");
    }

    private void SpawnRoute(SpawnableDestroyRoute route, double time, IReadOnlyList<NativeCapture> frozen)
    {
        var captures = new List<NativeCapture>(frozen);
        foreach (var binding in route.Captures.Where(item => item.SourceContainerId is null))
            AppendCapture(captures, binding, ReadParent(binding.SourceNodeId));
        var target = _templates[route.TargetContainerId];
        Check(Fn<Spawn>("libra_generator_spawn")(target.Pool, time, captures.ToArray(),
            (uint)captures.Count, out _), target.Template.ContainerId);
    }

    private static void AppendCapture(List<NativeCapture> captures, SpawnableRouteCapture binding, SpawnableSessionValue value)
    {
        if (binding.SourceComponent + binding.Width > value.Width)
            throw new InvalidDataException("Destroy capture exceeds the source value width.");
        for (var lane = 0; lane < binding.Width; ++lane)
            captures.Add(new NativeCapture { Node = Id(binding.TargetNodeId), Component = (uint)lane,
                Value = value.Lanes[binding.SourceComponent + lane] });
    }

    private void NotifyOwnerDestroy(double time)
    {
        if (!_rootEnding) return;
        for (var round = 0; round < 128; ++round)
        {
            var pending = ActiveInstances.Where(key => !_ownerDestroyNotified.Contains(key)).ToArray();
            if (pending.Length == 0) return;
            foreach (var key in pending)
            {
                var state = _templates[key.Container];
                if (state.Template.Lifecycle?.OwnerDestroyNodeId is { } node)
                    Check(Fn<OwnerDestroy>("libra_generator_owner_destroy")(state.Pool, key.Instance, Id(node), time, 10000),
                        key.Container, key.Instance);
                _ownerDestroyNotified.Add(key);
            }
            Pump(time);
            DispatchScheduled(time);
        }
        throw new InvalidOperationException("Owner Destroy routing exceeded the bounded work limit.");
    }

    private void EvaluateManagedParent(double time)
    {
        if (time < _rootAge) throw new InvalidOperationException("Root effect time cannot evaluate backwards.");
        if (time == _rootAge) return;
        SetRootTimeContext(time);
        if (_rootEnding)
        {
            Check(Fn<DrainEffect>("libra_effect_drain")(_parentInstance, time));
            _rootAge = time;
        }
        else
        {
            Check(Fn<AdvanceEffect>("libra_effect_advance")(_parentInstance, time, out var sample));
            _rootAge = sample.Age;
            _rootEnding = sample.Expired != 0;
        }
    }

    private void EvaluateManagedFrame(double time)
    {
        EvaluateManagedParent(time);
        var evaluated = new HashSet<(Guid, ulong)>();
        for (var round = 0; round < 128; ++round)
        {
            var pending = ActiveInstances.Where(key => !evaluated.Contains(key)).ToArray();
            if (pending.Length == 0) return;
            foreach (var key in pending)
            {
                evaluated.Add(key);
                var state = _templates[key.Container];
                SetChildTimeContext(state.Pool, key.Instance, time);
                ApplyLiveInputs(state, key.Instance);
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
                        SetChildTimeContext(state.Pool, value.Instance, value.Start);
                        ApplyCapturedInputs(state, value.Instance);
                        ApplyLiveInputs(state, value.Instance);
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
                        if (_package.HasChildDeferredSignals)
                        {
                            Check(Fn<ChildElapsed>("libra_generator_elapsed")(state.Pool, value.Instance, out var elapsed),
                                container, value.Instance);
                            expiry = timing.Start + elapsed;
                        }
                        _events.Add(new(container, value.Instance, 3, expiry, value.Seed));
                        EvaluateManagedParent(expiry);
                        foreach (var route in _package.DestroyRoutes.Where(route => route.SourceContainerId == container))
                        {
                            var captures = new List<NativeCapture>();
                            foreach (var binding in route.Captures.Where(item => item.SourceContainerId is not null))
                                AppendCapture(captures, binding, Read(binding.SourceContainerId!.Value, value.Instance, binding.SourceNodeId));
                            if (route.DelayNodeId is { } delayId)
                            {
                                var delay = ReadParent(delayId).Lanes[0];
                                var due = expiry + delay;
                                if (!double.IsFinite(delay) || delay < 0 || !double.IsFinite(due))
                                    throw new InvalidDataException("Delay must be nonnegative finite seconds.");
                                var token = _nextScheduledToken++;
                                if (token == 0) throw new InvalidOperationException("Scheduled continuation identity exhausted.");
                                Check(Fn<Schedule>("libra_effect_schedule")(_parentInstance, due, token));
                                _scheduledRoutes.Add(token, (route, captures.ToArray()));
                            }
                            else SpawnRoute(route, expiry, captures);
                        }
                        // Reading/routing happens before retirement.
                        Check(Fn<InstanceAction>("libra_generator_finalize")(state.Pool, value.Instance), container, value.Instance);
                        EvaluateManagedParent(time);
                    }
                    else if (value.Kind == 2)
                    {
                        state.Instances.Remove(value.Instance);
                        _ownerDestroyNotified.Remove((container, value.Instance));
                        _events.Add(new(container, value.Instance, 2, time, value.Seed));
                    }
                    else throw new InvalidDataException("Unknown native generator event.");
                }
                while (true)
                {
                    Check(Fn<NextSignalEvent>("libra_generator_next_signal_event")(
                        state.Pool, out var signal, out var has), state.Template.ContainerId);
                    if (has == 0) break;
                    progress = true;
                    var node = new Guid(signal.Node);
                    _events.Add(new(state.Template.ContainerId, signal.Instance, 4,
                        signal.Time, signal.Seed, node));
                    SetRootTimeContext(signal.Time);
                    foreach (var route in _package.SignalRoutes.Where(route =>
                                 route.SourceContainerId == state.Template.ContainerId &&
                                 route.SourceNodeId == node))
                        Check(Fn<DispatchEffectSignal>("libra_effect_dispatch_signal")(
                            _parentInstance, Id(route.TargetNodeId), signal.Time, 10000));
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
    private void ApplyLiveInputs(TemplateState state, ulong instance)
    {
        if (state.Template.LiveInputs is not { Count: > 0 } inputs) return;
        var captures = new List<NativeCapture>();
        foreach (var input in inputs) AppendCapture(captures, input, ReadParent(input.SourceNodeId));
        Check(Fn<SetLive>("libra_generator_set_live")(state.Pool, instance, captures.ToArray(),
            (uint)captures.Count), state.Template.ContainerId, instance);
    }
    private void ApplyCapturedInputs(TemplateState state, ulong instance)
    {
        if (state.Template.CapturedInputs is not { Count: > 0 } inputs) return;
        var captures = new List<NativeCapture>();
        foreach (var input in inputs) AppendCapture(captures, input, ReadParent(input.SourceNodeId));
        Check(Fn<SetCaptured>("libra_generator_set_captured")(state.Pool, instance, captures.ToArray(),
            (uint)captures.Count), state.Template.ContainerId, instance);
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
        Pump(_time < 0 ? 0 : _rootAge);
        if (_time >= 0 && !_rootFinished && !_rootStopped)
        {
            Check(Fn<Release>("libra_effect_stop")(_parentInstance));
            _scheduledRoutes.Clear();
        }
        _rootStopped = true;
    }
    public void End()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failed) throw new InvalidOperationException("Recreate the session after a failed lifecycle operation.");
        if (_time < 0) throw new InvalidOperationException("Advance the root before requesting natural end.");
        try { RequestNaturalEnd(_rootAge); }
        catch { _failed = true; throw; }
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
    private void SetRootTimeContext(double time)
    {
        if (NativeLibrary.TryGetExport(_library, "libra_instance_set_time_context", out var address))
            Check(Marshal.GetDelegateForFunctionPointer<SetRootTime>(address)(_parentInstance, time, _frameDelta));
    }
    private void SetChildTimeContext(ulong pool, ulong instance, double time)
    {
        if (NativeLibrary.TryGetExport(_library, "libra_generator_set_time_context", out var address))
            Check(Marshal.GetDelegateForFunctionPointer<SetChildTime>(address)(pool, instance, time, _frameDelta));
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
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct NativeSignalInputBinding
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Container;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Type;
        public ulong Pool;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Proxy;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Node;
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
    private struct NativeSignalEvent
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Container;
        public ulong Instance;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Node;
        public double Time;
        public ulong Seed;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct NativeValue { public double X, Y, Z, W; public uint Count, Reserved; }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct NativeLifetimeSample
    {
        public double Age, Remaining, NormalizedAge;
        public uint Expired, Infinite;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct NativeScheduledEvent { public double Time; public ulong Token; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateProgram(byte[] bytes, ulong size, out ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateParent(byte[] bytes, ulong size, [In] NativeBinding[] bindings, uint count, out ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateParentWithSignals(byte[] bytes, ulong size,
        [In] NativeBinding[] bindings, uint count, [In] NativeSignalInputBinding[] signalInputs,
        uint signalInputCount, out ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreatePool(ulong program, byte[] container, uint max, ulong budget, uint events, out ulong pool);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateInstance(ulong program, out ulong instance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetRootTime(ulong instance, double effectTime, double frameDelta);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetChildTime(ulong pool, ulong instance, double effectTime, double frameDelta);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetLive(ulong pool, ulong instance, [In] NativeCapture[] values, uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetCaptured(ulong pool, ulong instance, [In] NativeCapture[] values, uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetSignalOutputs(ulong pool, [In] byte[] nodeIds, uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DispatchGeneratorSignal(ulong pool, byte[] nodeId, double time, uint budget);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NextDeferred(ulong instance, out double time, out uint has);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DispatchDeferred(ulong instance, double time, uint budget);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NextChildDeferred(ulong pool, ulong instance, out double time, out uint has);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DispatchChildDeferred(ulong pool, ulong instance, double time, uint budget);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ChildElapsed(ulong pool, ulong instance, out double elapsed);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Release(ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StartCaptured(ulong instance, byte[] lifeNode, byte[]? onStart, uint budget, out NativeLifetimeSample sample);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StartInfinite(ulong instance, double duration, uint infinite, byte[]? onStart, uint budget, out NativeLifetimeSample sample);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AdvanceEffect(ulong instance, double time, out NativeLifetimeSample sample);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DrainEffect(ulong instance, double time);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DestroyEffect(ulong instance, byte[] node, uint budget);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RequestNaturalEnd(ulong instance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Schedule(ulong instance, double time, ulong token);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ScheduledEventAction(ulong instance, out NativeScheduledEvent value, out uint has);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Process(ulong pool, uint max, out uint processed);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Configure(ulong pool, ref NativeLifecycle lifecycle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NextEvent(ulong pool, out NativeEvent value, out uint has);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NextSignalEvent(ulong pool, out NativeSignalEvent value, out uint has);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EvaluateChild(ulong pool, ulong instance, double time);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OwnerDestroy(ulong pool, ulong instance, byte[] node, double time, uint budget);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadChild(ulong pool, ulong instance, byte[] node, out NativeValue value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int BindValue(ulong instance, byte[] node, out uint binding);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadValues(ulong instance, uint[] bindings, uint count, out NativeValue value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InstanceAction(ulong pool, ulong instance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PoolAction(ulong pool);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Spawn(ulong pool, double time, [In] NativeCapture[] captures, uint count, out ulong instance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DispatchEffectSignal(ulong instance, byte[] nodeId, double time, uint budget);
}
