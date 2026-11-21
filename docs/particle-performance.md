# Particle performance and shared scheduling

Status: measurement tools, CPU batch execution and prepared host pools now connect
to a bounded shared effect-world executor and persistent component-wise particle
storage. See [current integration and limits](runtime-shared-simulation.md).
The historical measurements below predate this integration; the shared-manager
requirements include further work such as mid-host slicing and asynchronous
presentation, not guarantees supplied by the current executor.

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
Runtime/build-perf/Release/liberafx_particle_host_benchmarks.exe G:/LfxV/new-benchmark-fixtures/CpuFountain-10000.lfxb 8 120 --interpreter
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

The first implementation decodes eligible V3 Update stages once and executes
32-particle blocks using node/component/particle scratch arrays. Arithmetic is
dispatched per block; a validated linear chain of local particle setters and
movement is executed together. Source node IDs remain in the plan. There are
no additional worker threads or per-particle/per-frame allocations.

Eligibility is deliberately limited: at most 64 nodes, supported arithmetic,
copy/broadcast, particle attributes, Clamp/Compose and local setters/movement;
no external IO, stateful or unknown callbacks, branches, Signal fanout or later
bytecode formats. Other stages automatically use the interpreter. The first
iteration covered Update only; eligible Spawn stages are now batched as described
below. Persistent particle storage is still slot-based: this is batched
SoA scratch, not yet a complete persistent SoA or cross-instance ECS manager.

On the same 9950X/Release fixture, alternating interpreter and automatic execution
three times gave the following **simulation-only** means for 10,000 particles ×
8 hosts (120 measured frames per run):

| Run | Interpreter (ms) | Batch plan (ms) |
| --- | --- | --- |
| 1 | 12.867 | 1.286 |
| 2 | 12.793 | 1.287 |
| 3 | 12.860 | 1.229 |

Checksums matched. A separate automatic run measured 0.147 ms for one host and
7.868 ms for 32 hosts. Snapshot extraction in the 32-host run still averaged
5.522 ms. Eight-host initial emission was about 41 ms in both modes: the Update
improvement does not eliminate burst spikes or establish end-to-end engine speed.

The explicit `liberafx_cpu_particle_batch_tests` command compares every particle
snapshot byte, age/lifetime, stable ordering and CPU event payload/order over 300
frames, including varying delta, pause/slow, emission, retirement, slot reuse,
vector/scalar math and Stop. Division-by-zero and mixed per-particle failures
also compare the exact first error. On a pure-evaluation failure, the untouched
block is replayed in interpreter order so a later particle's error cannot
overtake an earlier particle's Signal failure. The test asserts eligible stages selected
the batch plan and external-IO stages selected fallback. The release-gate job
generates fixtures with the matching editor compiler and runs this comparison;
generic CTest skips this fixture-dependent command without arguments.

Use `SpawnableHostOptions::cpuParticleBatching = false` or the benchmark's
`--interpreter` argument for controlled reference runs. This changes no saved
bytecode/package format. The shared manager remains separate work: one reduces
computation, the other schedules it.

## Hot particle storage and bulk rendering reads

Particle values now live separately from the per-particle VM objects. The VM
array retains the original per-particle destruction order; VM cleanup happens
before their borrowed programs/descriptors and particle data are released.
This is a hot/cold split, not yet fully separate position/velocity/color columns.

Eligible stateless Update stages also reuse one VM for spawn-time Update and
ordered error fallback. They no longer initialize one Update VM per newborn
particle or retain its per-particle work slab. Unsupported stages and explicit
interpreter mode keep their independent VMs. The measurements in this section
predate the batched Spawn implementation below.

Native `SpawnableHost::cpuParticleRead` copies a caller-sized range with optional
age/lifetime data. The C export `libra_spawnable_host_scoped_cpu_particle_read`
accepts 1–256 records and an optional timing array. It preserves the existing
80-byte snapshot and stable live ordering, and supports root and child scopes.
Unity, Godot and Unreal now read their CPU render data in 256-particle blocks.
Unity reuses managed buffers and pins them only during each call. A 10,000-
particle draw needs 40 snapshot calls instead of 10,000; Flipbook timing travels
in the same call. Use matching updated native libraries and adapter sources.

After the storage split, alternating individual and bulk reads for the same
10,000 × 8 fixture produced these **snapshot-extraction-only** means:

| Run | Individual reads (ms) | Bulk reads (ms) |
| --- | --- | --- |
| 1 | 0.570 | 0.205 |
| 2 | 0.550 | 0.195 |
| 3 | 0.551 | 0.223 |

Update averaged about 1.17–1.25 ms in these runs. Initial emission still took
about 36–37 ms. One 32-host run measured 5.743 ms Update and 1.534 ms bulk
extraction. These exclude engine geometry conversion, submission and drawing.
The benchmark accepts `--bulk` independently of `--interpreter`.

Parity checks compare bulk records/timing against individual reads, including
short tails, empty/end ranges, invalid ranges and buffer canaries. C ABI tests
also compare 1,000 compiled particles and timing records across partial blocks.
The disposable Windows release-gate native test build uses AddressSanitizer to
check lifetime and memory access; distributed SDK builds remain uninstrumented.

## Batched initial emission

Eligible Spawn and Update stages now both use 32-particle blocks. Birth batching
requires both plans to be isolated: no external IO, mutable custom state or
unrecognized operations. Sphere emission uses the same seeded random function,
computed once per particle. The supported numeric/local-command subset is shared
with Update. An unsupported Spawn retains its interpreter while an eligible
Update can still batch normally.

The runtime decodes plans before allocating particle VM storage. When both stages
qualify, it allocates two shared reference VMs and hot particle slots, omitting
all per-particle VM objects and stage work slabs. Shared VMs are destroyed before
their borrowed programs/descriptors. There are no extra threads, per-frame
allocations, delayed births or changes to package/ABI formats.

Spawn and newborn Update run with zero particle delta. A zero Spawn lifetime
skips newborn Update. If a speculative block fails, it is reset and replayed
in original per-particle Spawn → Update order, preserving the first error even
when one particle fails in Update before a later particle fails in Spawn.
Normal frames preserve stable slot/ID ordering and lifetime retirement.

Three Release runs on the same Ryzen 9 9950X, 10,000 particles × 8 hosts,
180 measured frames after 30 warmup frames:

| Run | Host creation (ms) | Initial emission (ms) | Update mean (ms) | Bulk snapshot mean (ms) |
| --- | --- | --- | --- | --- |
| 1 | 3.335 | 5.557 | 1.138 | 0.192 |
| 2 | 2.995 | 5.452 | 1.142 | 0.195 |
| 3 | 3.009 | 5.487 | 1.130 | 0.201 |

Initial emission previously took about 36–37 ms after the hot/cold split.
It now takes about 5.5 ms for all 80,000 births in that fixture. This is
simulation and snapshot extraction on the calling thread, without geometry
conversion or drawing; it does not establish Niagara parity or a frame budget
for every effect. Complex/stateful Spawn graphs can still use the slower path.
The benchmark reports host creation separately from initial emission.

The matching-compiler differential fixtures now also cover birth-time division
by zero, mixed Spawn/Update failure precedence, zero-lifetime births and an
unsupported Spawn with batched Update. Tests assert that eligible stages omit
per-particle VMs, compare full state/timing/events with the interpreter, and run
under AddressSanitizer in the release gate.

## Compact execution records and fused numeric work

The in-memory particle execution format now separates hot operations from cold
constants and source IDs. Each hot instruction is 12 bytes: an operation, width,
output register and four 16-bit operand addresses. A tagged operand addresses
an immutable stage-level constant pool or a node/component scratch register. Identical
constant vectors share storage, including preservation of signed-zero bit
patterns. The old decoded records were 192 bytes on the measured x64 build;
constants and diagnostic IDs still take separate storage in the new layout.
This is an execution-layout change after loading, not a new on-disk LFXB version.

Copies and identity color conversions redirect their consumers to the original
register or constants. A recognized lifetime-color chain—Age / Life, Clamp(0,1),
1 minus the result, then Compose RGBA with constant RGB—becomes one block
operation. Fusion requires a single consumer for every removed intermediate;
shared values retain the generic path. It samples age/life before all Signal
commands, preserves the original binary64 operations and finite checks, and
uses ordered interpreter replay for exceptional values. For legacy input, the authoring graph and original bytecode remain available,
with original node IDs in the plan. Offline exports below retain the editable
source separately and omit the fused intermediates from their runtime binary.

Shrinking records alone did not produce a reliable speed improvement. A first
operand-stride implementation regressed Update to about 1.45 ms and was removed.
The compact addressed records plus lifetime fusion produced these alternating
measurements against a separately built clean `2fe44d4` baseline, using the same
MSVC Release configuration and the same 10,000-particle × 8-host fixture:

| Run | Previous Update mean (ms) | Compact + fused mean (ms) |
| --- | --- | --- |
| 1 | 1.150 | 0.678 |
| 2 | 1.146 | 0.688 |
| 3 | 1.159 | 0.681 |

All checksums matched. New initial emission was 5.050, 5.355 and 5.053 ms;
host creation was 3.245, 4.026 and 3.039 ms. The improvement is approximately
41% for **this matching lifetime-color workload**, measured without drawing.
It does not imply that every graph or instance-creation path improved equally.
The release-gate fixtures assert that the ordinary Fountain uses fusion and
that a graph sharing its intermediate attributes keeps the generic path; both
are compared byte-for-byte with the interpreter over varying time steps.

Persistent SoA storage, a shared cross-instance scheduler, reusable prepared
package definitions and broader precompiled kernels remain further work. A
compact file by itself does not remove execution dispatch or per-instance
state initialization; these need separate measurements.

## Ahead-of-time CPU binary optimization

Distribution compilation now performs lifetime-color fusion **before writing**
the CPU stage bytecode. The optimized V3 program contains an internal CPU kernel
instruction (`7c7e1000-2521-4bdb-b8a0-210000000049`) in place of five intermediate
nodes. Numeric references and Signal destinations are relocated to the new node
indices. This uses the existing registered-instruction mechanism rather than a
new editor node, package envelope or LFXB record size.

The editable graph is preserved. Live debug transfer keeps original source
instructions for stepping. Set `VfxCompileOptions.OptimizeCpuParticleKernels`
to `false` for an explicit unoptimized export. Unknown callbacks, IO boundaries,
shared intermediate consumers, guards and newer bytecode formats retain the
ordinary code. Previously exported files remain loadable; newly optimized
exports need the matching updated native runtime/engine SDK, which supplies the
internal kernel descriptor. Older runtimes cannot execute this new instruction.

For the 10,000-particle Fountain fixture the full package shrank from 5,399 to
4,119 bytes (1,280 bytes, approximately 24%). This optimization benefits the
ordinary VM too: paired interpreter-only runs measured roughly 13.0–13.2 ms
Update before and 9.3–9.4 ms after for eight hosts. Automatic compact/batched
execution remains around 0.7 ms for the same fixture. These are CPU simulation
measurements without drawing, not universal speed or instantiation guarantees.

Tests compare **two different binaries**, unoptimized and ahead-of-time optimized,
with automatic batching both enabled and disabled. All particle snapshot bytes,
IDs, timing, events, retirement/reuse, pause/slow and Stop must agree over 300
frames. The release gate also checks that export reduces the binary while
preserving the source graph and live-debug code. Existing compiled packages can also be optimized without the source project:

```powershell
LiberaFxConverter optimize input.lfxb output.lfxb
```

Input and output must be different files. Existing outputs require `--force`;
writing uses a temporary file and an atomic move. The command traverses root
and nested-template CPU payloads, leaves unknown/unsupported stages unchanged,
and is idempotent. Raw non-package bytecode and resource manifests are not this
command's input format. Use the matching updated native runtime for the output.
The release gate compares both source-compiled and binary-rewritten optimized
packages against the unoptimized interpreter.

## Smaller particle counts

Three separate-process Release runs with one host, 30 warmup frames and 600
measured frames per run used the binary-preoptimized Fountain. The table shows
the median of three host-creation/spawn measurements and the median of three
per-run Update means on the same Ryzen 9 9950X:

| Particles | Host creation (ms) | Initial emission (ms) | Update per frame (ms) |
| --- | --- | --- | --- |
| 100 | 0.264 | 0.042 | 0.00086 |
| 512 | 0.255 | 0.067 | 0.00411 |
| 1,000 | 0.262 | 0.098 | 0.00796 |
| 10,000 | 0.422 | 0.686 | 0.08106 |

Creation and initial emission are separate one-time costs; add them when
estimating the initial `create` + `advance(0,0)` work. Creation retains fixed
program/descriptor and host setup costs, so it does not fall in direct
proportion to particle count. The package was already loaded before timing;
file IO, package deserialization, engine geometry/material work and drawing are
excluded. Other graphs can have very different costs.

`--particle-benchmark-fixtures` now emits 100/512-particle cases as well as
1,000/10,000, each with original, source-preoptimized and binary-preoptimized
packages. Run the ordinary benchmark with one host and `600 --bulk` to reproduce
this comparison. Local raw measurements are in the ignored
`build/cpu-particle-count-scaling.json` file.

## Preallocated Core host pool and parallel preparation

`SpawnableHostPool` prepares complete instances before gameplay, including their
parsed programs, particle slots, VM buffers, defaults, handles and event queues.
Acquire takes an already initialized slot; release resets its state and returns
it to the free list. Neither operation allocates memory in the tested Core paths.
This reuses whole instance storage rather than merely caching raw allocation
blocks and repeating the same program setup on every cast.

Preparation uses a bounded worker group (default four, configurable 1–16 and
clamped to hardware/capacity). The caller participates; workers are joined before
`create` returns. No worker thread is created per playback or per particle.
The preparation time is a **loading cost**, separate from acquire, initial
particle emission, regular Update and drawing. This does not parallelize the
regular owner-thread Update or implement the proposed shared frame scheduler.

```cpp
#include "liberafx/LbSpawnableHostPool.h"

liberafx::SpawnableHostPoolOptions options;
options.capacity = 32;
options.preparationWorkers = 4;
std::unique_ptr<liberafx::SpawnableHostPool> pool;
// During loading: check the Result before using the pool.
auto result = liberafx::SpawnableHostPool::create(package, options, pool);

// During playback, on the creating owner thread:
liberafx::SpawnableHostLease lease;
liberafx::SpawnableHost* host = nullptr;
result = pool->acquire(lease, host);
// Set coordinate basis, owner matrix, quality and initial inputs for this lease.
// Then advance as usual. Return at an idle boundary when playback is finished.
result = pool->release(lease);
```

Capacity is fixed. Exhaustion returns `InsufficientWork`, with no hidden heap
growth or fallback allocation. Return resets clocks/time scale, basis/quality,
authored parameter defaults, particle IDs/seed progression, emission counters,
VM state, named inputs/outputs, event queues and debug hooks. Release discards
this playback's events rather than dispatching new lifecycle signals. Force
cleans state immediately but retains pooled storage. Stale/cross-pool leases and
old debug targets cannot access a recycled playback. Failed resets quarantine
a slot. A host pointer must not be retained after returning its lease.

Initial eligibility is deliberately bounded: root-only packages using Core
builtins, including CPU Stage IO and named Game IO/Owner Matrix. Spawnable
hierarchies, external project-node callbacks and procedural mesh emitters use
the existing ordinary host API. Renderer/GPU resource preparation is outside
this Core pool and must follow the engine's own thread and ownership rules.
At the pool-only revision, engine playback adapters had not been switched to automatic pooling; consumers
opt in through this Core API or the new C ABI. Pool memory grows with reserved
instance count; immutable package sharing across hosts remains separate work.

The C ABI provides `libra_spawnable_host_pool_create`, `_acquire`, `_stats` and
`_release`. Acquired handles work with existing `libra_spawnable_host_*` calls;
`libra_spawnable_host_release` returns the slot. Reacquisition issues a fresh
handle. Pool destruction returns Busy while any host is checked out. Native
handle-map nodes and buckets are reserved during preparation too, so acquire
can rekey existing nodes. C calls enforce the creating owner thread.

On the same 9950X/Release Fountain, preparing **32 hosts of 100 particles** took
6.94–8.43 ms with one worker and 3.63–3.97 ms with four in alternating runs.
A further 180-cycle comparison after 30 warmup cycles measured all 32 hosts:

| Operation | Fresh hosts, three means (ms) | Reused hosts, three means (ms) |
| --- | --- | --- |
| Create / acquire | 3.989 / 3.879 / 3.603 | 0.000118 / 0.000119 / 0.000129 |
| Initial emission | 0.199 / 0.200 / 0.200 | 0.177 / 0.178 / 0.177 |
| Destroy / reset and return | 1.509 / 1.417 / 1.306 | 0.00701 / 0.00684 / 0.00665 |

Acquire is close to timer granularity; use "under 0.001 ms for all 32" rather
than treating the last digits as a universal per-host latency. Fresh create and
destroy showed substantial allocator-related variability. A separate 50-host
run measured initial emission at 0.278 ms for 100 particles each, and 2.898 ms
for 1,000 particles each. These are **Core-only** measurements without file IO,
package loading, engine geometry/material conversion or drawing, not Niagara
comparisons. Raw local logs are in ignored `build/host-pool-benchmarks.log`.

Build `liberafx_host_pool_benchmarks` and run:

```powershell
liberafx_host_pool_benchmarks.exe CpuFountain-binary-100.lfxb 32 4 180
liberafx_host_pool_benchmarks.exe CpuFountain-binary-100.lfxb 32 1 180 --fresh
```

Core tests count ordinary/aligned allocations across acquire and reset/return.
They compare worker-prepared and repeatedly recycled instances with fresh hosts,
including interpreter and batched CPU stages, slot reuse, lifetime retirement,
pause/slow, errors, Stop/Force, Owner Matrix and named properties/signals.
The C ABI tests check exhaustion, ownership, stale handles and snapshot equality.
Both run with AddressSanitizer in the release gate; MCP authoring remains a
separate required gate.

## Engine comparison still required

For a paired Niagara comparison use the same hardware, Unreal version, RHI,
particle count, lifetime, spawn rate/burst, gravity/noise, material, blend,
sprite size, visible pixel coverage and camera. Compare CPU and GPU simulation
separately. Record game/render-thread time, GPU compute/draw time, memory,
spawn spikes and frame-time percentiles. The GPU Curl100k example has not been
measured against Niagara by the native CPU tool.
