# Prepared playback and shared CPU simulation

This source revision connects prepared host pools, a shared simulation executor
and persistent component-wise particle storage. Install matching editor and SDK
builds: the C ABI functions are additive to ABI 1, but older native libraries do
not export the new functions. Package and project formats are unchanged.

## Frame ownership

1. On the engine thread, publish camera, pose, owner matrix, quality, named inputs
   and per-effect time scale.
2. Gather independent effects into one `EffectWorld::advance` call. The world
   shares a bounded worker allowance across those instances. Its default is four
   executing lanes including the calling thread, capped by available hardware.
3. Join simulation, then drain events and update engine geometry/material/GPU
   resources on their required thread. Stop/restart/destruction invalidate a
   pending presentation through the engine adapter's playback identity.

Core-owned executor threads are created once per world and sleep between frames.
Small collections stay inline below an estimated-work threshold, avoiding worker
wake/join overhead for a few ordinary low-count effects. Core callers can tune
`minimumParallelWork`; worker limits always include the calling thread.
Unreal supplies UE task jobs instead. Core can also accept a host `JobScheduler`;
rejected jobs run on the caller and accepted jobs are joined. No delta, Signal or
particle step is dropped to satisfy a frame budget.

Scheduling currently uses one job per host, prioritizing estimated smaller CPU
work and rotating equal-cost entries. This is not preemption inside a large host,
nor an asynchronous last-completed-frame presentation mode. One large system's
internal 32-particle blocks still execute on its assigned lane. Engine rendering
and custom callbacks are not made worker-safe by this scheduler.

The shared path accepts nested Spawnables and Core procedural mesh emitters.
One host and all its descendants stay on one exclusive lane, preserving their
Signal and retirement order. Attached execution debug hooks keep the host on
the dedicated debugger path. Whole-effect clocks, seeds/IDs, stable particle
order and coordinate conversion remain per instance.
Unknown/project callbacks join the shared batch on its calling thread after
workers finish. Only explicitly worker-safe callbacks run on workers.
Gathered effects are independent: cross-effect script responses are published
after the gathered simulation phase. Keep control chains that require the old
same-tick producer/consumer order on the immediate path by disabling shared
simulation for those components, rather than treating them as independent jobs.

## Loading and memory

Prepare capacity during level/resource loading.
The default reservation is **256 simultaneous playbacks per package/options
pool**, with four preparation workers. Explicit capacities and saved component
settings are preserved; change existing saved values when increasing them.

Acquiring a prepared host does
not grow its pool; exhaustion returns `InsufficientWork`. Returning a playback
resets clocks, inputs, matrices/basis, quality, IDs, emission and events while
retaining simulation storage. Configure each new lease before advancing it.

`host::PreparedPlaybackCache` stores at most 64 byte-identified package/options
combinations. It compares package contents so an edited asset cannot acquire a
stale instance. Cache eviction keeps checked-out playbacks alive through shared
pool ownership. Prepared memory scales with capacity and, for each template,
`maxActivePerTemplate` times its child VM and particle storage. Preparation
reserves the complete configured capacity and fails if its generator memory
budget is insufficient; it does not silently reduce that limit. Immutable
program sharing between hosts is still a separate optimization.

Core procedural mesh emitters evaluate their owned model before particle
sampling and publish a read-only surface snapshot. Animated revisions may
allocate a new surface during simulation. Nested routing also retains existing
temporary allocations: allocation-free acquire/return is not a promise that
every hierarchical or animated frame allocates nothing. This support covers
procedural meshes, not external static/skinned mesh bindings.

## Project callback policies

`CustomNodeDescriptor` ABI-1 is unchanged. Native integrations declare a separate
`CustomNodePolicy` matched against the complete descriptor, including context,
function, initialization and cleanup. Only descriptors used by the root, its
templates or their CPU programs affect eligibility.

```cpp
auto options = liberafx::SpawnableHostPoolOptions{};
options.registry = descriptors;
options.registryCount = descriptorCount;
// Assert these properties only if the implementation actually guarantees them.
options.host.customNodePolicies.push_back({descriptors[0], true, false, false});
```

The three booleans are independent:

- `reusable`: state and external resources tolerate cleanup/reinitialization
  between leases. Without it, a used project callback cannot enter a pool.
- `workerSafe`: evaluation and any birth/retirement initialization or cleanup
  may run on a shared worker. Without it, the host runs on the batch's caller
  after all worker jobs have joined, in original owner-host submission order.
- `parallelPreparation`: separate hosts may initialize concurrently, including
  failure cleanup. Without it, the pool prepares on its creating thread.

Unity's native bridge and Godot use the optional `policies`/`policyCount` fields
of `host::ProjectNodeRegistry`. Unreal exposes the two-argument
`FLibraNativeNodes::Register(descriptors, policies)` overload. Rebuild native
project modules with the matching SDK; callback code and contexts must outlive
all referencing hosts. Existing declarations default to no permissions.
The allocation guarantees cover Core-managed storage. A project callback may
allocate during its own initialization/reset; declaring it reusable does not
make that implementation allocation-free.
The additive C ABI `libra_spawnable_host_execution_capabilities` returns flags
shared batch=1, worker=2, pool reuse=4 and parallel preparation=8. Worker safety
alone is not the condition for joining a mixed shared batch.

CPU positions, velocities, colours, sizes, age/lifetime, real start time and IDs
now live in persistent arrays per component. The interpreter and batch executor
use the same storage. There is no second persistent AoS simulation mirror;
80-byte rendering snapshots are assembled at the read boundary. Slot indices
remain stable so retirement/reuse preserves observable order and particle IDs.

## Unity

`LiberaFxSpawnableComponent` exposes `usePreparedHosts`, `preparedHostCapacity`,
`preparationWorkers` and `useSharedSimulation` in its inspector. Assigned scene
effects are prepared in `Awake`, once per package content/options combination.
For dynamically configured effects, call `PrewarmEffect()` during loading.
Unprepared and unsupported packages use ordinary host creation; an exhausted
prepared pool reports its explicit capacity error instead of allocating a host.

Normal game playback publishes inputs in component `LateUpdate` and queues work.
The shared manager, at execution order 32000, executes the gathered frame and
publishes render updates afterwards. Manual preview timing and debugging keep
their dedicated paths. Keep the effect component's script execution order ahead
of this manager if overriding execution orders in the project.

Direct native consumers can use `NativePreparedHostPool.Create`, `Acquire` and
`NativeSpawnableHost.Dispose`. Dispose playbacks and pools on their creating
thread. Pool release returns Busy while playbacks are checked out and retains
the handle for retry. The Unity manager retires its pools after outstanding
playbacks return when it is destroyed.

## Unreal

The Spawnable component exposes **Use Prepared Hosts**, **Prepared Host
Capacity**, **Preparation Workers** and **Use Shared Simulation** under
`Libra Runtime|Performance`. `BeginPlay` prepares assigned packages before
automatic playback. Call the Blueprint **Prewarm Effect** action earlier during
loading when preparing a dynamically assigned package.

Game-world components gather simulation after publishing frame inputs. A shared
world service joins UE task jobs at `OnWorldPostActorTick`, then publishes events
and rendering. Editor/manual preview paths retain their immediate behavior.
World cleanup drops cache ownership; live leases retain their backing pools.
CPU Sprite material bindings remain explicit: provide
`builtin:cpu-particle-alpha` or `builtin:cpu-particle-additive` with the blend,
two-sided, instanced-mesh and Texture-parameter contract in the Unreal usage
guide. Pool preparation does not replace engine resource binding.

## Godot

Prepare a package from an in-tree player during loading:

```gdscript
var bytes := FileAccess.get_file_as_bytes("res://effects/fountain.lfxb")
var result: int = $LibraEffectPlayer.prewarm_package(bytes, 8, 4)
if result != 0:
    push_error("Effect preparation failed: %s" % result)
# Later, normal play_package uses the matching prepared pool.
result = $LibraEffectPlayer.play_package(bytes)
```

Normal `_process` playback gathers independent players into one internal
scene-tree world. Its late process priority joins simulation before updating
rendering. Explicit `advance(delta)` remains immediate for deterministic tests
and manual stepping. Stop/restart or a manual advance invalidates queued work's
presentation identity. Unprepared packages retain ordinary creation. A loader
can prepare a nondefault capacity once; subsequent players share a compatible
pool by package content and host settings. Stop a live playback before replacing
it when no spare pool slot is reserved; a failed replacement preserves the live
playback. Starting a valid new package discards the previous terminal event queue.

## Verification and measurements

Inspect actual playback, rather than assuming preparation implies pool use:
Unity exposes `UsesPreparedStorage` and `LastSimulationBatchSize` on the
component; Unreal exposes `UsesPreparedStorage()` and `LastSimulationBatchSize`.
Godot `get_render_stats()` adds `prepared_host`,
`simulation_shared_frame_effects` and `simulation_workers`.

`liberafx_effect_world_tests` accepts compiler-produced CPU package paths. It
checks reused/prepared hosts against the interpreter, external scheduler
acceptance/rejection, owner/debug/duplicate guards, clock scaling, retirement,
Stop/Force and cache eviction. The C ABI pool tests additionally exercise shared
frames with stale handles and invalid reserved flags. The release gate includes
these fixtures under AddressSanitizer.

Extended preparation tests cover repeated nested ownership/capture/retirement,
subtree Force, child CPU storage return, static and animated procedural mesh
emission in canonical and BS2 bases, and fresh versus shared/reused snapshots.
`liberafx_prepared_execution_tests` also checks callback policy identity,
independent permissions, mixed owner/worker execution and exact cleanup.
The allocation counter covers acquire/return with active child CPU systems.

Windows acceptance of the extended paths used Unity 7000.0.0a16 with the
isolated D3D11 unlit SRP (eight hosts), Godot 4.7.2/OpenGL (16 players), and
Unreal 5.7's `Libra.Runtime.SharedSimulation` (eight components). Both a
time-varying procedural mesh emitter and a nested CPU-retention fixture used
prepared storage and shared frames. Unity/Godot additionally verified visible
pixels, queued Stop/restart, reuse and framebuffer clearing. Unreal verified
its task/scene integration with explicit alpha/additive material bindings;
this is not a cooked-player or framebuffer-equivalence test.

The existing performance tool accepts `--world-workers 1..16` alongside `--bulk`:
Acceptance and benchmark sources live in the development repository; the source
SDK's default build does not bundle those test/benchmark executables.

```powershell
liberafx_particle_host_benchmarks.exe CpuFountain-binary-1000.lfxb 32 600 --bulk
liberafx_particle_host_benchmarks.exe CpuFountain-binary-1000.lfxb 32 600 --bulk --world-workers 4
```

Use the same fixture, hardware, backend and instance count when comparing.
Worker preparation, host preparation, births, steady simulation, snapshot reads
and drawing are separate costs. Existing [Core-only measurements](particle-performance.md)
do not establish a speed advantage over Niagara or another engine renderer.

## Observed acceptance for this integration

Windows, Ryzen 9 9950X, MSVC Release, 30 warmup frames followed by 600 measured
frames, three alternating runs of the same binary-preoptimized Fountain:

| Workload | Serial simulation (median mean, ms) | Shared four-lane simulation (median mean, ms) | Shared p95 (median, ms) |
| --- | --- | --- | --- |
| 1,000 particles × 32 hosts | 0.2771 | 0.0846 | 0.0969 |
| 10,000 particles × 8 hosts | 0.6735 | 0.1852 | 0.2544 |

All checksums matched. Snapshot extraction is separate: approximately 0.11 ms
for 32,000 particles and 0.27 ms for 80,000. The table excludes drawing, engine
frame work, loading and pool preparation. Four lanes include the calling thread;
this is not a four-thread pool attached to every effect. Raw local results live
in ignored `build/shared-world-benchmarks-final.json`.

- Core CTest: 31 passed; six compiler-fixture-dependent cases skipped by the
  default invocation. Explicit batch/interpreter, pool, C ABI and effect-world
  fixtures passed separately. AddressSanitizer checked the shared/pool paths.
  The allocation counter also covered joined Core frames after preparation.
- Released 2026.11.63 AoS DLL versus the new SoA DLL: seven CPU fixtures matched
  snapshots, timings, events and error outcomes in canonical and BS2 bases,
  including pause/slow, retirement and error cases.
- Godot 4.7.2, RX 6700 XT/OpenGL: 16 prepared players shared a frame with at most
  four lanes. A nondefault reservation was shared by newly created players;
  exhaustion, queued Stop/restart identity, reuse and framebuffer clearing passed.
  An event-producing one-slot pool could start subsequent playbacks after its
  previous terminal queue was replaced.
- Unity 7000.0.0a16, RX 6700 XT/D3D11, isolated unlit SRP: eight prepared components
  shared a frame. Queued Stop/restart invalidation, reuse, visible framebuffer
  output and Stop clearing passed. This does not certify Built-in RP or URP.
- Unreal 5.7: the Editor plugin built and
  `Libra.Runtime.SharedSimulation` passed with eight prepared components, UE
  tasks, queued pause clock preservation and Stop/restart identity. The test
  supplies the explicit CPU material binding and does not measure framebuffer
  equivalence or a cooked player.
- MCP authoring transitions, the operator-skill installation/update suite and
  documentation-link checks passed. These checks do not exercise console SDKs.
