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
- The engine adapter owns the GPU buffers and resolved resources. It submits
  spawn, update and render in authored order and retires old resources only
  after submitted work no longer references them. Portable worker jobs never
  call engine graphics APIs. Successful submission is not a GPU fence.
- Reject unknown backend IDs, incompatible versions, unsupported instructions
  and resource types, and missing bindings before replacing an active program.
  Do not reinterpret a failed GPU program as CPU playback.

## Adding a backend

Register a `GpuBackendCapabilities` descriptor in a host-owned
`GpuBackendRegistry`. `Profiles` exposes exactly the descriptors used by
`Validate`; IDs are case-sensitive and cannot replace built-in profiles. Validate
the compiled program before packet upload or shader generation. Registration
declares capabilities; it does not install an execution adapter, resolve assets,
or enable that backend in the editor's Unity/Unreal delivery routes.

An interpreter reuses the register packet across assets but must implement all
advertised operations and validate packet ranges at the runtime boundary.
Generated shaders can use an engine shader compiler to optimize each program,
but their metadata and shaders must come from the same validated IR and preserve
its typed math and sampling policies. In either case, link the portable Core,
connect scope start/stop to the host GPU adapter, and run the canonical fixtures
in `Gpu\LibraGpu.Core.Tests` before comparing readback on the intended engine and
graphics backend. A compiler fixture does not establish device parity or support
for an independent engine or console SDK.

See the [Unity GPU guide](../Runtime/Integrations/Unity/GPU-USAGE.txt) and
[Unreal integration guide](../Runtime/Integrations/Unreal/USAGE.txt) for
engine-specific fixture and runtime instructions.
