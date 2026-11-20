# GPU backends and the portable Core Runtime

The Core Runtime remains required for an effect. It owns CPU execution, signals,
object lifetimes and package scopes. A GPU backend executes the `GpuParticle`
programs attached to those scopes; it is not an alternative Core Runtime. The
authoring compiler validates and lowers the graph to common typed GPU IR before
either engine consumes it.

| Backend ID | Input at runtime | ABI | Supported resource kinds |
|---|---|---|---|
| `unreal-lgpk` | Validated LGPK packets executed by the fixed USF interpreter | 1 | `Texture2D`, `FlowMap2D` |
| `unity-compute` | Generated ComputeShader with matching export metadata | 1 | `Texture2D`, `FlowMap2D` |

Both profiles cover GPU instruction IDs `0..41` (including noise and FlowMap
sampling); changes to that list belong in `GpuBackendCapabilities`. Neither
profile advertises writable storage textures or arbitrary buffers. Work memory
is capped at 256 MiB per program in the compiler profiles. The Unreal profile
also checks the 256-register limit and charges the declared scratch and packet
memory. Engine admission may impose additional limits on device resources or
aggregate usage.

## ABI, resources, and ownership

- LGPK v1 is little-endian, with a 64-byte header and 64-byte instructions.
  `CompiledRegisterKernel` is the authoritative encoder; do not marshal
  host-language structs directly. The shared particle stride is 80 bytes
  (`ParticleAbi`), and the exported dispatch width is 64 threads.
- Resolve each declared resource by its binding in the selected project. Do not
  substitute a same-named asset. `FlowMap2D` stores linear RG vector data,
  decoded to signed XY with linear-clamp sampling. World projection uses the
  authored axis pair and bounds; UV mode consumes authored UVs.
- The engine adapter owns the GPU buffers and resolved resources. Portable
  workers never call engine graphics APIs. The compiler requires Spawn before
  Update and all simulation kernels before rendering. Preserve the authored
  order within each phase, and make writes visible to later dispatches and
  draws according to the host graphics API. Draws consume the simulated state;
  they do not mutate it. Successful submission is not a GPU fence.
- Reject unknown backend IDs, incompatible versions, unsupported instructions
  and resource types, and missing bindings before replacing an active program.
  Do not reinterpret a failed GPU program as CPU playback.

## Connecting an engine adapter

These are responsibilities, not shared GPU function pointers or a C++ binary
plugin ABI. The public C++ integration point is `Runtime/include/liberafx/LbSpawnableHost.h`.
Link the portable Core into the host and use one `SpawnableHost` for each
playing package, including mixed or entirely GPU-rendered effects.

| Phase | Portable Core / engine boundary |
|---|---|
| Admit | Load the `.lfxp` via `loadSpawnablePackage`. Resolve its GPU payloads and binds, validate backend artifact versions, resources and limits, and prepare a replacement before retiring the current effect. |
| Advance | Capture inputs on the host owner thread. Advance Core; once its job is complete, consume scope start/stop events and read root or child values from that same host. Key child state by `(containerId, instanceId)`, not only the authored GPU node ID. |
| Submit | Advance each eligible scope on the engine's permitted thread. Submit simulation before rendering, with engine-owned barriers/lifetime. GPU draw Matrix, when present, changes draw placement and bounds, not spawn/update positions or FlowMap. |
| Retire | Stop issuing work for a retired scope. Retain buffers, bindings and materials until previously submitted GPU work can no longer reference them. Report errors to the host; never fall back to CPU or swallow a rejected GPU payload. |

Unreal validates LGPK packets in `ULibraGpuEffectAsset::ValidateProgram`,
submits simulation on the render thread, and renders billboards in its view
extension. Unity 7 validates imported shader metadata, kernel entry points,
thread-group sizes and resource bindings in `LibraGpuParticleRuntime`. Its
source hash check is Editor-only; an exported game must carry the imported
shader and matching metadata together. Unity submits compute and draw work via
engine APIs and retires in-flight resources separately. Godot has a
global RenderingDevice LGPK interpreter on the render thread. A GPU pass
publishes particle state to an RGBA32Float texture sampled by the MultiMesh
billboard shader; normal playback does not read particle state to CPU.
Root and child scopes own separate buffers/textures and read their own authored
draw Matrix rows. Fixed-capacity draws bypass CPU bounds culling, and command
processing currently synchronizes the main/render threads. This is a basis for
resource sharing, not implemented cross-container field binding. See the
[Godot integration guide](../Runtime/Integrations/Godot/USAGE.txt) for
admission limits and device verification.

## Adding and distributing a backend

Register a `GpuBackendCapabilities` descriptor in a host-owned
`GpuBackendRegistry`. `Profiles` exposes exactly the descriptors used by
`Validate`; IDs are case-sensitive and cannot replace built-in profiles. Validate
the compiled program before packet upload or shader generation. Registration
declares capabilities; it does not install an execution adapter, resolve assets,
or enable that backend in the editor's Unity/Unreal delivery routes. A
standalone engine can consume the compiler library offline: compile the graph
once, validate the resulting `CompiledGpuProgram` with the selected profile,
then export register packets with `CompiledRegisterKernel` or lower the same
IR to its shader target. Implement the runtime phases above in that host, not
in the graph node implementations.

An interpreter reuses the register packet across assets but must implement all
advertised operations and validate packet ranges at the runtime boundary.
Generated shaders can use an engine shader compiler to optimize each program,
but their metadata and shaders must come from the same validated IR and preserve
its typed math and sampling policies. In either case, link the portable Core,
connect scope start/stop to the host GPU adapter, and run the canonical fixtures
in `Gpu\LibraGpu.Core.Tests` before comparing readback on the intended engine and
graphics backend. Distribute the Core package and engine-imported artifacts
together; do not require the authoring project's paths or source files at
runtime. Package format, LGPK or shader-metadata ABI, and engine build
compatibility are separate checks. Incompatible versions must fail admission
rather than be guessed from available fields.

The canonical exporter supplies typed math, noise and FlowMap inputs and
reference outputs. Use Unity's `LibraGpuMathNoiseVerification.Run`, Unreal's
`Libra.GPU.Compute` automation cases, and Godot's
[canonical Vulkan harness](../Runtime/Integrations/Godot/Tests/GPU_CANONICAL.txt)
for device readback. Test draw placement separately with billboard framebuffer
tests. Compiler tests alone do not establish device parity. Standalone engines
and console SDKs are not covered by these tests. For a console target, validate the actual target
engine's shader compilation, build/cook and device execution before advertising
support.

On Windows, Unreal 5.7 passed the `Libra.GPU.Compute` suite on both D3D11
and D3D12/SM6, including spawn/update, canonical math and noise, and FlowMap
readback. `Libra.GPU.Render.BillboardFramebuffer` also passed on both RHIs.
D3D12 diagnostic readback submits the RHI work before polling for completion;
production dispatch does not wait on these diagnostic fences.

See the [Unity GPU guide](../Runtime/Integrations/Unity/GPU-USAGE.txt) and
[Unreal integration guide](../Runtime/Integrations/Unreal/USAGE.txt) for
engine-specific fixture and runtime instructions.
