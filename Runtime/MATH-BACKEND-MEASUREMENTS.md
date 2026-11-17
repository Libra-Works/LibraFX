# Math backend measurement and boundary

This note records repeatable, host-local measurements for the portable math
backend. The benchmark is diagnostic, not a regression test: it has no
machine-specific pass/fail thresholds, performs its own correctness checks,
and allocates all working data before timed regions. Timed outputs are consumed
after each sample and checked against independent scalar or matrix identities
so the compiler cannot discard the measured work.

## Reproduce

From the repository root, use separate Release build directories for the
native-SIMD and forced-scalar variants:

On Windows, start from a Visual Studio Developer PowerShell (or call
`vcvars64.bat` in the same shell before invoking CMake), then set
`$clang` to the installed `clang++.exe` path:

```powershell
$clang = 'C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Tools\Llvm\x64\bin\clang++.exe'
cmake -S Runtime -B <native-build> -G Ninja `
  -DCMAKE_CXX_COMPILER="$clang" -DCMAKE_BUILD_TYPE=Release `
  -DLIBERAFX_BUILD_INTEROP=OFF -DLIBERAFX_BUILD_BENCHMARKS=ON
cmake --build <native-build> --target liberafx_math_backend_benchmarks
<native-build>/liberafx_math_backend_benchmarks.exe `
  --samples 9 --vector-ops 131072 --matrix-ops 16384 --vm-instructions 1048576

cmake -S Runtime -B <scalar-build> -G Ninja `
  -DCMAKE_CXX_COMPILER="$clang" -DCMAKE_BUILD_TYPE=Release `
  -DLIBERAFX_BUILD_INTEROP=OFF -DLIBERAFX_BUILD_BENCHMARKS=ON `
  -DLIBERAFX_MATH_FORCE_SCALAR=ON
cmake --build <scalar-build> --target liberafx_math_backend_benchmarks
<scalar-build>/liberafx_math_backend_benchmarks.exe `
  --samples 9 --vector-ops 131072 --matrix-ops 16384 --vm-instructions 1048576
```

Keep the two build trees and outputs separate. Do not compare runs from
different compiler versions, build modes, architectures, or power states as
though they were controlled. The executable prints compiler, architecture,
configuration, and selected math backend. Report the median of at least nine
samples plus the observed min/max; do not treat these values as portable
performance guarantees.

The benchmark covers vector add/subtract/multiply/divide at widths 3 and 4,
then matrix transform construction, inversion, and TRS decomposition. Each
operation runs against small (64-item) and large (4096-item) caller-owned
working sets. Arithmetic mutates its input in place using stable values;
matrix inputs are varied between calls. Setup and verification are outside the
timed interval. The matrix backend has no explicit SIMD path in either
configuration today;
its measurements characterize cost and validate results, but do not claim a
SIMD comparison. `LIBERAFX_MATH_FORCE_SCALAR` disables the handwritten native
vector path; it does not disable compiler auto-vectorization.

The extended benchmark also runs production typed-v2 bytecode through
`Program::load`, caller-aligned work storage, `Instance::initialize`, and
`Instance::updateAsync`. Each chain has two Source nodes feeding Add, Multiply,
Subtract and Divide, then a connected Source copy as the consumed sink. Widths
3 and 4 are separate cases. Small programs have 8 chains / 56 instructions,
large programs 1024 chains / 7168 instructions. The bytecode occupies 5,392 /
688,144 bytes; value work slots occupy 1,792 / 229,376 bytes. All chains are
live and all active lanes (including intermediate values) are compared to an
independent scalar evaluation after every sample. These are native production
VM instructions, not descriptor callbacks or a mock interpreter.

`--vm-instructions` (default 1,048,576) controls the approximate executed
instruction count per sample, rounded up to whole program updates. VM and
direct graph results report **ns per instruction-equivalent including Source
and sink copies**, not ns per arithmetic operation. Multiply by 56 or 7168
to obtain ns/update. The direct comparator evaluates the identical seven live
slots using the checked math API without general VM routing, decoding or
instance/scheduler machinery; it is an overhead reference, not a proposed
replacement for the VM. The inline synchronous scheduler includes production
enqueue/phase/result overhead once per update but no thread-pool queue wait.
Graph construction, loading, allocation, warmup and verification are outside
the timed loop. A compiler memory barrier after **each** update makes output
storage observable and prevents collapsing repeated deterministic updates.
There is no host engine conversion or virtual dispatch per math operation.

Six `matrix.ordered_*` cases measure final #32's native ordered-transform
function. They compare every matrix lane with explicit scalar multiplication
of independently assembled T/R/S factors in the requested order; they do not
try to decompose sheared, non-TRS matrices. This comparator shares only the
existing rotation convention. Their matrix algorithm is unchanged by the
force-scalar switch, just like the other matrix cases.

## Recorded baseline

The measurements below are from an isolated archive of commit
`e826bbf063aaad6b41fc46c104ad59dd7ef406d0`, with only this benchmark,
its CMake target, and this document overlaid. They were built with Visual
Studio's Clang 19.1.5 (`x86_64-pc-windows-msvc`), CMake 4.4.2, Ninja 1.13.2,
on Windows 11 Pro (build 26300), AMD Ryzen 9 9950X (16 cores / 32 logical
processors). Both builds used CMake `Release`: `-O3 -DNDEBUG -std=gnu++17`.
The SIMD build used the default backend; the scalar build added
`LIBERAFX_MATH_FORCE_SCALAR=1`. No explicit `-march`/`/arch` tuning was used.
The snapshot intentionally excludes concurrent, uncommitted work elsewhere in
the worktree; rerun against the final worktree before treating future matrix
changes as measured.

Each cell is median ns/op (min–max), from nine samples. The native/SIMD and
scalar binaries were run sequentially with identical operation counts and
inputs. These are measurements of the existing backend implementation, not a
before/after comparison of a newly added optimization.

### Vector arithmetic

| Operation | Width | Working set | Native SIMD | Forced scalar |
|---|---:|---|---:|---:|
| Add | 3 | small-64 | 2.889 (2.830–3.430) | 2.497 (2.494–3.258) |
| Add | 3 | large-4096 | 2.911 (2.785–3.014) | 2.513 (2.512–2.598) |
| Subtract | 3 | small-64 | 2.863 (2.653–4.176) | 2.486 (2.479–2.536) |
| Subtract | 3 | large-4096 | 2.818 (2.801–3.281) | 2.539 (2.525–2.612) |
| Multiply | 3 | small-64 | 2.646 (2.610–3.942) | 2.509 (2.506–2.535) |
| Multiply | 3 | large-4096 | 2.660 (2.639–3.055) | 2.532 (2.524–2.580) |
| Divide | 3 | small-64 | 3.802 (3.748–4.216) | 3.785 (3.769–4.097) |
| Divide | 3 | large-4096 | 4.175 (4.086–5.994) | 4.211 (4.092–6.219) |
| Add | 4 | small-64 | 3.128 (3.064–3.540) | 3.363 (2.961–4.044) |
| Add | 4 | large-4096 | 3.165 (3.140–3.212) | 3.099 (2.962–6.123) |
| Subtract | 4 | small-64 | 3.104 (3.079–3.165) | 2.929 (2.904–2.947) |
| Subtract | 4 | large-4096 | 3.262 (3.145–3.829) | 2.905 (2.893–3.039) |
| Multiply | 4 | small-64 | 3.149 (3.050–4.025) | 2.964 (2.924–3.040) |
| Multiply | 4 | large-4096 | 3.793 (3.204–7.352) | 3.018 (2.982–3.146) |
| Divide | 4 | small-64 | 4.756 (4.700–5.476) | 4.830 (4.786–5.553) |
| Divide | 4 | large-4096 | 5.352 (5.184–11.070) | 5.160 (5.122–5.280) |

### Matrix functions

The `LIBERAFX_MATH_FORCE_SCALAR` switch does not change these matrix
implementations; the two columns are separate runs and their spread reflects
host/run variation, not a matrix-SIMD comparison.

| Operation | Working set | Default build | Forced-scalar build |
|---|---|---:|---:|
| makeTransform | small-64 | 11.462 (10.663–14.532) | 10.144 (10.107–13.171) |
| invertMatrix | small-64 | 42.975 (40.723–60.107) | 42.218 (40.674–58.728) |
| decomposeTransform | small-64 | 64.355 (57.483–89.459) | 58.191 (57.324–71.002) |
| makeTransform | large-4096 | 15.332 (10.999–23.523) | 10.889 (10.632–11.395) |
| invertMatrix | large-4096 | 41.266 (40.503–51.843) | 45.062 (40.533–52.264) |
| decomposeTransform | large-4096 | 79.437 (57.758–94.318) | 59.369 (57.147–92.462) |

Every benchmark emitted `correctness=passed`. Vector results matched the
independent scalar loop exactly (reported relative error 0), and checksums were
identical across both backend configurations. Matrix inverse/product and
decompose/reconstruct checks passed; maximum reported relative error was
`4.44e-16`.

On this host, SIMD is not consistently faster for any tested width/operation
across both working-set sizes. The forced-scalar path was faster for most
vector cases; the exceptions were width-4 small add (about 7% faster with
SIMD), width-3 divide (effectively tied), and width-4 small divide (effectively
tied). Some native-SIMD sample ranges were wide, so small differences should
not be treated as stable wins. This evidence does not justify broadening SIMD
or adding matrix SIMD. Keep the engine-independent portable contract and
force-scalar fallback, and keep any existing native vector path an internal
optional implementation detail; do not add more specializations until
repeatable target-specific evidence exists. These single-host measurements
are not a sufficient reason to add size-dependent runtime dispatch or to infer
that all vector intrinsics help. CPU affinity, frequency, and unrelated
background load were not controlled; the observed ranges are included rather
than hiding that variability.

## Final #32 math / pre-#34 VM measurement

The final-math snapshot deliberately uses committed `e826bbf` Runtime plus
exactly these worktree overlays: `Core/BuiltinNodes.cpp`,
`include/liberafx/math/MatrixMath.h`, `tests/BuiltinMathTests.cpp`,
`CMakeLists.txt`, and `benchmarks/MathBackendBenchmarks.cpp`. Thus it includes
final #32 ordered transforms and 71 built-in descriptors, but **not #34
conditional execution, branch guards, or enum changes**. #33 adds ordinary
Groups only, so no additional native matrix primitive is represented.
These VM timings measure the existing unconditional typed-v2 interpreter;
the absence of guards is **not evidence of zero branch cost**. Future #34
measurements require a separately identified build and representative
branching programs.

The source snapshot, local archive and full per-file SHA-256 manifest are in:

```text
%USERPROFILE%\.copilot\session-state\b0239237-70c4-498d-9509-32041af4d31b\verify\issue18-final-pre34\
  Runtime\
  source-manifest.json
  source.tar.gz
  build-clang-simd\
  build-clang-scalar\
```

The source tree SHA-256 is
`7a6e68c6ee06071cbd9a62accc2ccc3974613888ef87c95891af631b04a6cf73`.
It hashes UTF-8 lines `SHA256<two spaces>Runtime/relative/path<LF>` for all
189 files, in manifest order (Windows `pathlib` path sort). The archive SHA-256 is
`c8d3b91ac94eab551e9717106f3ac0f28004baf5c7ca3c1a3dcc808091b0e923`.
The manifest records every file, size, overlay and baseline commit; key hashes:

| Source | SHA-256 |
|---|---|
| Core/VfxRuntime.cpp (committed, pre-#34) | `26ba0f53b70d7c0666f80ce8edbfd80a94409cc08d89916905352500d30112da` |
| Core/BuiltinNodes.cpp (#32) | `3b13e608f9b128d2d728a19c8d81326a8b6b5dbf8529797130dd378e44290d87` |
| include/liberafx/math/MatrixMath.h (#32) | `c92dc22b4c2ee611693072308aa64513fc0031d48fe64dc3c5691247865071fe` |
| tests/BuiltinMathTests.cpp (#32) | `f484f47f08e08b2613cc6f78a8882f493211356d82757d6e9a87afc3a163579b` |
| benchmarks/MathBackendBenchmarks.cpp | `76b25cda1615680132cc27e84c982bec8885b3671acbffe907e95c9aebbf5900` |
| CMakeLists.txt | `5e3616862198df3d3be487906537f5f9991eb46cb743a4589038420bc5337f31` |

Hashes describe the exact archived/build-input bytes (including archive CRLF
line endings), not Git blob IDs. The archived VfxRuntime.cpp was also compared
with `git show e826bbf:Runtime/Core/VfxRuntime.cpp` after CRLF normalization
and matched, establishing that no concurrent #34 source entered this build.

Both variants used the same compiler, architecture, flags, host and operation
counts as the baseline above. Builds and focused tests finished before
measurement; native and forced-scalar benchmarks then ran sequentially, not
against one another. Each case had one warmup and nine timed samples. There
was no affinity/frequency/background-load control. Min/max are retained even
when large outliers occurred; these are not statistically established speedups.

### Direct vector comparison: baseline to final

Median ns per arithmetic API call; each final cell also includes final
min–max. Baseline medians and ranges are recorded above. The vector source
itself was unchanged, so differences between snapshots must not be attributed
to a vector optimization.

| Operation / width | Set | Baseline native / scalar | Final native | Final scalar |
|---|---|---:|---:|---:|
| Add / 3 | 64 | 2.889 / 2.497 | 2.827 (2.743–3.273) | 2.577 (2.528–2.798) |
| Add / 3 | 4096 | 2.911 / 2.513 | 2.859 (2.772–3.436) | 2.624 (2.532–3.369) |
| Subtract / 3 | 64 | 2.863 / 2.486 | 2.934 (2.830–3.029) | 2.624 (2.620–2.695) |
| Subtract / 3 | 4096 | 2.818 / 2.539 | 3.014 (2.849–4.378) | 2.623 (2.615–2.690) |
| Multiply / 3 | 64 | 2.646 / 2.509 | 2.808 (2.776–3.016) | 2.568 (2.525–3.002) |
| Multiply / 3 | 4096 | 2.660 / 2.532 | 2.792 (2.769–6.152) | 2.560 (2.534–2.635) |
| Divide / 3 | 64 | 3.802 / 3.785 | 3.889 (3.799–4.442) | 3.834 (3.781–4.197) |
| Divide / 3 | 4096 | 4.175 / 4.211 | 5.988 (4.222–7.168) | 4.139 (4.057–4.693) |
| Add / 4 | 64 | 3.128 / 3.363 | 3.184 (3.138–3.386) | 2.980 (2.931–3.114) |
| Add / 4 | 4096 | 3.165 / 3.099 | 3.226 (3.175–6.282) | 2.985 (2.966–3.983) |
| Subtract / 4 | 64 | 3.104 / 2.929 | 3.203 (3.154–3.235) | 3.128 (2.991–3.379) |
| Subtract / 4 | 4096 | 3.262 / 2.905 | 3.223 (3.181–3.996) | 3.013 (2.950–3.394) |
| Multiply / 4 | 64 | 3.149 / 2.964 | 3.326 (3.101–5.350) | 2.960 (2.908–2.986) |
| Multiply / 4 | 4096 | 3.793 / 3.018 | 3.204 (3.136–3.244) | 3.015 (2.962–3.529) |
| Divide / 4 | 64 | 4.756 / 4.830 | 5.816 (4.908–10.296) | 5.237 (4.981–8.015) |
| Divide / 4 | 4096 | 5.352 / 5.160 | 6.034 (5.405–8.665) | 5.186 (5.137–5.235) |

### Matrix comparison: baseline to final

Median ns/call (final min–max). **Neither column is an explicit SIMD matrix
implementation**: the switch changes vector arithmetic, not these matrix
algorithms. Differences between build columns are run/code-layout/compiler
variation, not measured matrix-SIMD benefit.

| Operation | Set | Baseline native / scalar | Final default | Final scalar build |
|---|---|---:|---:|---:|
| makeTransform | 64 | 11.462 / 10.144 | 12.378 (10.229–18.805) | 10.559 (10.535–28.235) |
| invertMatrix | 64 | 42.975 / 42.218 | 41.077 (40.680–43.201) | 46.185 (42.798–53.583) |
| decomposeTransform | 64 | 64.355 / 58.191 | 59.918 (57.678–62.018) | 58.777 (57.581–63.733) |
| makeTransform | 4096 | 15.332 / 10.889 | 11.255 (10.754–11.743) | 11.163 (10.962–12.787) |
| invertMatrix | 4096 | 41.266 / 45.062 | 42.004 (40.814–58.667) | 42.157 (40.936–56.262) |
| decomposeTransform | 4096 | 79.437 / 59.369 | 60.889 (58.087–89.282) | 60.651 (58.038–79.547) |

### Final ordered transforms

Nine samples, median ns/call (min–max). No pre-#32 baseline exists for the
six-order API. The implementation is the same in both build columns.

| Order | Set | Default build | Forced-scalar build |
|---|---|---:|---:|
| TRS | 64 | 11.005 (10.913–12.292) | 14.752 (10.925–22.217) |
| TSR | 64 | 12.067 (11.902–12.573) | 12.415 (11.792–19.684) |
| RTS | 64 | 12.848 (12.646–14.020) | 14.111 (12.549–17.279) |
| RST | 64 | 12.964 (12.762–17.303) | 13.300 (12.646–24.084) |
| STR | 64 | 12.360 (11.926–13.470) | 11.841 (11.792–14.838) |
| SRT | 64 | 12.683 (12.604–18.658) | 12.939 (12.634–15.118) |
| TRS | 4096 | 11.530 (11.328–12.134) | 11.340 (11.127–12.531) |
| TSR | 4096 | 12.494 (12.311–64.539) | 12.274 (12.213–17.603) |
| RTS | 4096 | 13.196 (12.750–19.196) | 13.806 (12.817–32.166) |
| RST | 4096 | 22.113 (12.988–38.434) | 13.177 (12.793–13.690) |
| STR | 4096 | 13.086 (12.238–19.525) | 15.448 (12.042–31.757) |
| SRT | 4096 | 14.087 (13.104–20.258) | 13.727 (12.817–20.398) |

### Production VM and equivalent direct graph (pre-#34 only)

Nine samples; median **ns/instruction-equivalent** (min–max), including
source/sink instructions. Small: 18,725 updates / 1,048,600 executed
instructions per sample. Large: 147 updates / 1,053,696 instructions per
sample. The earlier baseline benchmark did not include VM cases, so no
historical VM speedup is claimed.

| Workload | Width | Instructions/update | Native SSE2 | Forced scalar |
|---|---:|---:|---:|---:|
| Production VM | 3 | 56 | 7.006 (6.151–8.253) | 6.480 (5.806–8.211) |
| Direct graph | 3 | 56 | 4.173 (3.834–5.356) | 2.737 (2.665–4.062) |
| Production VM | 3 | 7168 | 6.770 (6.036–7.799) | 6.755 (5.755–8.294) |
| Direct graph | 3 | 7168 | 4.058 (3.788–4.313) | 2.646 (2.430–4.098) |
| Production VM | 4 | 56 | 7.346 (6.552–8.657) | 7.416 (6.427–8.934) |
| Direct graph | 4 | 56 | 4.403 (4.119–4.654) | 3.081 (2.884–3.829) |
| Production VM | 4 | 7168 | 7.518 (6.351–10.120) | 6.708 (6.216–8.569) |
| Direct graph | 4 | 7168 | 4.375 (3.867–4.710) | 3.037 (2.853–3.599) |

Both backend configurations produced identical checksums for every case.
VM/direct active lanes matched the independent scalar evaluation exactly
(relative error 0), including Source, intermediate arithmetic and sink slots.
The six ordered transforms agreed with explicit scalar T/R/S multiplication
to at most `5.55e-17`; existing matrix identity/reconstruction checks remained
within `4.44e-16`. All finite timing, error and checksum checks passed.
`liberafx_vector_math_tests`, `liberafx_builtin_math_tests` and
`liberafx_runtime_tests` also built and passed in both snapshot configurations.
No engine project or user settings were used.

For interpretation: production VM medians were about 6.48–7.52 ns/instruction
and equivalent direct graph medians 2.65–4.40 ns/instruction-equivalent.
This gap includes source routing, decoding, validation, loop layout, and
per-update scheduling/phase management. It does not isolate a pure dispatch
cost, and does not justify removing validation or changing the VM. Native SIMD
again did not produce a consistent effect-level win across widths and sizes.
Keep the existing boundary: no additional speculative SIMD, no per-operation
virtual dispatch or engine-type conversions, portable and disabled paths
preserved. These results establish neither console performance nor #34
conditional/branch-guard performance.

### Exact rerun of the archived final-math/pre-#34 snapshot

Run in Visual Studio Developer PowerShell; paths below use the original
session's local archive. Use the same command against a newly identified
source tree for future worktree measurements, not by mutating this snapshot.

```powershell
$snapshot = Join-Path $env:USERPROFILE '.copilot\session-state\b0239237-70c4-498d-9509-32041af4d31b\verify\issue18-final-pre34'
$clang = 'C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Tools\Llvm\x64\bin\clang++.exe'
cmake -S "$snapshot\Runtime" -B "$snapshot\build-clang-simd" -G Ninja `
  -DCMAKE_CXX_COMPILER="$clang" -DCMAKE_BUILD_TYPE=Release `
  -DLIBERAFX_BUILD_INTEROP=OFF -DLIBERAFX_BUILD_TESTS=ON -DLIBERAFX_BUILD_BENCHMARKS=ON
cmake --build "$snapshot\build-clang-simd" --target liberafx_math_backend_benchmarks liberafx_vector_math_tests liberafx_builtin_math_tests liberafx_runtime_tests
& "$snapshot\build-clang-simd\liberafx_math_backend_benchmarks.exe" --samples 9 --vector-ops 131072 --matrix-ops 16384 --vm-instructions 1048576

cmake -S "$snapshot\Runtime" -B "$snapshot\build-clang-scalar" -G Ninja `
  -DCMAKE_CXX_COMPILER="$clang" -DCMAKE_BUILD_TYPE=Release `
  -DLIBERAFX_BUILD_INTEROP=OFF -DLIBERAFX_BUILD_TESTS=ON -DLIBERAFX_BUILD_BENCHMARKS=ON `
  -DLIBERAFX_MATH_FORCE_SCALAR=ON
cmake --build "$snapshot\build-clang-scalar" --target liberafx_math_backend_benchmarks liberafx_vector_math_tests liberafx_builtin_math_tests liberafx_runtime_tests
& "$snapshot\build-clang-scalar\liberafx_math_backend_benchmarks.exe" --samples 9 --vector-ops 131072 --matrix-ops 16384 --vm-instructions 1048576

foreach ($build in 'build-clang-simd', 'build-clang-scalar') {
  foreach ($test in 'liberafx_vector_math_tests', 'liberafx_builtin_math_tests', 'liberafx_runtime_tests') {
    & "$snapshot\$build\$test.exe"
    if ($LASTEXITCODE -ne 0) { throw "$build/$test failed" }
  }
}
```

This is host evidence only. It does not establish console, mobile, or other
target-platform performance.

## Responsibility boundary

The runtime owns portable, engine-independent math contracts. Keep matrices,
vectors, finite/domain checks, and scalar fallback behavior correct and
available on every supported target. SIMD is an optional implementation detail
behind that API; it must preserve active-lane, aliasing, error, and finite-result
semantics. The caller/runtime owns storage, and a math call must not perform
engine-type conversion or a virtual dispatch for each arithmetic operation.
Host-specific representations or dispatch belong at explicit integration
boundaries, not in this loop.

Measured differences justify specialization only when they repeat materially
across representative workload sizes on the relevant targets and do not weaken
correctness or portability. Tiny effect workloads and matrix kernels must be
considered separately: a faster bulk vector result alone is not evidence that
per-operation dispatch, matrix SIMD, or a more complex backend abstraction is
worthwhile. Retain the scalar-disabled build as a correctness path. Local
desktop results cannot substitute for measurements on console hardware and
SDKs.
