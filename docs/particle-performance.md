# Particle performance and shared scheduling

Status: a measurement tool and baseline exist. The shared effect manager below
is a proposed next implementation, not a shipped scheduling guarantee.

## Current baseline

2026-11-21, Windows, Ryzen 9 9950X (16 cores / 32 logical processors), MSVC
Release. A compiler-produced CPU Fountain graph runs movement, gravity and
lifetime-based color fading. Each system emits a single burst and keeps all
particles alive during the measurement. Every measured frame verifies the count
and reads all 80-byte snapshots. Warmup: 30 frames; measurement: 120 frames;
fixed delta: 1/60 second. Multiple hosts currently advance serially on one
calling thread. There is no drawing, engine frame, GPU work or Niagara system in
this benchmark. Runs are local baselines, without affinity or power-state controls.

| Particles per host | Hosts | Simulation mean (ms) | p95 (ms) |
| --- | --- | --- | --- |
| 1,000 | 1 | 0.153 | 0.162 |
| 1,000 | 8 | 1.387 | 2.119 |
| 1,000 | 32 | 12.534 | 13.554 |
| 10,000 | 1 | 1.836 | 3.033 |
| 10,000 | 8 | 31.083 | 32.615 |
| 10,000 | 32 | 127.265 | 130.566 |

The 10,000-particle, one/eight-host rows are repeat runs; the other rows are
first runs. Snapshot extraction is separate: the eight-host repeat averages
1.485 ms, and the 32-host/10,000-particle run averages 6.150 ms. Initial emission
also needs attention: emitting all 10,000 particles takes 10.232 ms for one host,
77.428 ms for eight and 304.475 ms for 32. These numbers do not establish a
performance advantage over Niagara. They expose a substantial multiple-host
cost before rendering is included.

### First memory correction

Stages without connected external inputs now read constant parameters from the
shared bytecode instead of allocating and initializing mutable copies for every
particle. Stages with bound inputs retain mutable defaults. Repeating the same
10,000-particle fixture after this change gave:

| Hosts | Simulation mean (ms) | p95 (ms) | Initial emission, all hosts (ms) |
| --- | --- | --- | --- |
| 1 | 1.659 | 1.783 | 5.534 |
| 8 | 13.630 | 14.395 | 43.375 |
| 32 | 55.016 | 56.751 | 174.588 |

The benchmark's checksum matches the corresponding baseline runs. The eight-
and 32-host simulation means improve by about 2.3 times in these local runs,
but this does not solve game-frame latency, burst spikes or scheduling. Draw,
GPU and memory-residency measurements are still needed. Removing full-capacity
initialization of a temporary dirty-node array was also tried locally; it did
not improve the multiple-host case and was not retained.

## Reproduce

Build the smoke-test project in Release, using an isolated absolute
`LIBERAFX_DATA_ROOT`, then run its explicit fixture command:

```powershell
CommentImageSmokeTests.exe --particle-benchmark-fixtures G:/LfxV/new-benchmark-fixtures
cmake -S Runtime -B Runtime/build-perf -DLIBERAFX_BUILD_BENCHMARKS=ON
cmake --build Runtime/build-perf --config Release --target liberafx_particle_host_benchmarks
Runtime/build-perf/Release/liberafx_particle_host_benchmarks.exe G:/LfxV/new-benchmark-fixtures/CpuFountain-10000.lfxb 8 120
```

The fixture directory must be new. The tool reports creation, initial emission,
mean/p50/p95/p99/max update and snapshot-read time. Use 1, 8 and 32 hosts. Keep
raw CSV, source commit, compiler, CPU and machine load with comparisons. This is
an opt-in benchmark, not a timing-sensitive pass/fail test.

## Shared manager requirements

Place cross-instance scheduling in a core effect-world manager; keep engine
scene ownership, resource acquisition and rendering submission in the adapters.
Do not add one thread or one independent worker pool per effect instance.

1. Collect frame inputs and immutable resource/pose snapshots on the engine's
   required thread. Enqueue independent CPU work through a shared, bounded job
   interface, preferably using the engine's existing scheduler.
2. Bound total concurrent jobs across every managed instance, rather than per
   instance. Let the host reserve workers for input, rendering and other game
   work. Benchmark useful parallelism instead of assuming all logical CPUs
   should run effects at once.
3. Split work into bounded batches and schedule instances fairly. A large burst
   or a large system must not occupy a worker indefinitely while smaller
   instances wait. Spawn work needs measurement as well as steady-state Update.
4. Publish completed snapshots at an explicit synchronization boundary. Define
   a nonblocking presentation mode that can use the last completed snapshot;
   keep deterministic waiting available where event/lifecycle semantics require
   it. Never silently drop delta time, Signals or particle updates to meet a budget.
5. Preserve stable instance IDs, ordered Signals, coordinate conversion and each
   effect's real/scaled clocks. Stop invalidates presentation immediately and
   retires memory only after outstanding jobs can no longer reference it.
6. Declare worker-safe node/callback execution explicitly. Engine-object calls
   and unknown project callbacks stay on their required thread. The current
   `System::current` and shared event queues cannot simply be accessed from
   several particle jobs at once; partition or stage their state first.

Acceptance must include multiple simultaneously visible effects, overload,
slow effects alongside cheap ones, Stop during pending work, and unrelated game
thread latency. Do not advertise this manager as complete until adapters use it.

## Batch execution plan

The authoring graph should remain editable while runtime execution gets a
separate optimized representation. Decode and validate the source bytecode once,
then fuse supported sequences such as acceleration → velocity → position into
compact blocks. Execute a block over a group of particles, allowing SIMD where
its numeric and ordering semantics match. Repeated opcode dispatch and repeated
reads of the same constants should not be paid separately for every particle.

Keep source-node mappings for diagnostics. Treat Signals, stateful custom nodes,
external callbacks, branching and ordered outputs as explicit boundaries; never
reorder them just to form a larger block. Unsupported blocks must retain the
ordinary interpreter. Preserve seed/ID behavior, coordinate conversion and
real/scaled time. Differential tests must compare full particle state and event
order against the interpreter, rather than accepting only a similar image.

This execution plan is proposed, not implemented by the constant-sharing change.
It complements the shared manager: one reduces work, the other schedules work.

## Engine comparison still required

For a paired Niagara comparison use the same hardware, Unreal version, RHI,
particle count, lifetime, spawn rate/burst, gravity/noise, material, blend,
sprite size, visible pixel coverage and camera. Compare CPU and GPU simulation
separately. Record game/render-thread time, GPU compute/draw time, memory,
spawn spikes and frame-time percentiles. The GPU Curl100k example has not been
measured against Niagara by the native CPU tool.
