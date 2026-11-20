# GPU resource plan compiler

`LibraGpu.Core` provides `GpuResourcePlanCompiler` as a portable source
compiler contract. The existing Fluid2D source metadata uses it. It does not
execute arbitrary passes in Unity, Unreal or Godot, and is not a replacement
for the existing particle program or effect package format. General user-defined runtime passes remain separate work. Fluid output consumers
use the same scoped producer/resource identities for normal drawing and GPU sampling.

## Identity and allocation

`GpuFlowResource.Id` identifies an allocation within one effect scope
instance. It is separate from an engine asset reference. Independent effect
instances must allocate independent mutable storage even when their compiled
resource IDs match. A display name or engine path must not become the global
identity of an allocation.

Declarations distinguish storage buffers, particle buffers, Texture2D,
Texture3D and render targets. Format and dimensions determine the byte count.
Particle buffers use the existing 80-byte particle format. A storage buffer
uses `Width` as its element count and has height/depth 1.

- Persistent resources survive substeps/frames and are released with their
  scope, after outstanding GPU work completes. Zero initialization happens
  when allocating or resetting storage, not every frame.
- Temporary resources have a compiled first/last access interval within the
  plan. The compiler does not automatically alias allocations or perform
  allocation/retirement on the host.
- External resources are provided and owned by the host. Their binding,
  dimensions, format and allowed access must match the actual capability
  descriptor. They are not counted in owned memory or freed by the effect.

An engine's main color target is available only if the current rendering
pipeline explicitly provides a suitable binding. Providing a target does not
automatically grant sampling or storage writes. `GpuResourceCapabilities`
checks per-binding access as well as global resource/access support and the
owned memory budget. Compiling without capabilities validates the portable
contract; it does not establish device support.

## Versions, dependencies and hazards

Each `GpuResourcePass` names its operation and binds explicit `GpuResourceUse`
entries. Each use specifies allocation identity, content version, access and
frame. Operation names are symbolic; an executing adapter must separately
resolve them to trusted supported kernels or drawing operations.

Version 0 means incoming content. Reading it requires initialized or external
storage. Writes start at version 1 without gaps and have exactly one producer
per version. Subsequent writes replace the same physical allocation; versions
are not automatically new allocations.

The compiler derives producer-before-reader, writer-before-next-writer and
reader-before-overwrite dependencies. `After` adds explicit prerequisites.
Input list order does not establish resource dependencies. A deterministic
topological sort uses ordinal pass IDs to break ties. Missing producers,
undefined input, duplicate bindings/writers and cycles are diagnostics.

Reading and writing the same physical allocation in a pass is rejected even
when the version numbers differ. Ping-pong requires two declared allocations.
Simultaneous incompatible access modes for the same allocation are rejected.
Compiled transitions include storage hazards and compute-write to sampled-read
changes. Adapters map these transitions to their own barriers/layouts and must
honor the full pass order and GPU completion before retirement.

`GpuResourceFrame.Previous` explicitly reads previous-frame persistent storage.
It uses version 0, requires zero initialization, and reserves a second physical
allocation. Current-frame writes must not change the previous-frame snapshot.
An adapter must swap/preserve history at the frame boundary and clear both
allocations on reset. This is a compiler description, not an implemented
cross-frame runtime facility. Same-frame feedback is rejected as a cycle.

## Workspace declaration editing

Both particle and fluid Resources / Targets sections expose a bounded JSON
source-plan editor. `resource-plan` applies validated version-1 plan JSON in
`GpuEdit.Text`; `resource-plan-reset` removes it. The optional
`GpuEditorState.ResourcePlanDraft` is omitted from legacy states, persists in
projects/history and refreshes the workspace on Undo/Redo. MCP discovers both
actions and exports the source with `format=resources`. Matching metadata
reports `runtimeExecuted=false` and whether the current fixed fluid backend
supports exactly that canonical plan.

An override equal to the inferred fluid plan preserves effect playback.
General layouts and symbolic operations are source contracts: shader/effect
export rejects them with `ResourcePlanRuntimeUnsupported`, while the resources
source export remains available. No adapter silently ignores a changed plan.
External target declarations still require actual per-host capability bindings
before execution; this editor does not grant main-framebuffer access.

## Persistence and existing fluid export

`Parse`/`Serialize` use version 1 JSON with enum names, bounded input and strict
properties. Unknown properties, duplicate/case-variant keys, null collections
and invalid values are rejected. Serialization is canonical; compiled output
snapshots authoring collections.

`Fluid2DCompiler.ResourcePlan(settings)` describes one existing fluid substep
and an explicit visualization consumer. `Fluid2DCompiler.Metadata` includes
its compiled form in `ResourcePlan`. The existing native adapter solver
contracts and their runtime pass execution remain in place. This addition
does not by itself connect generated fields to other GPU containers. A separate
executable-payload v3 binding now connects a fluid's `DyeOutput` to a GPU
particle billboard in the same scope, as described below.

The plan accounts for seven Float4 buffers, two RGBA16Float output textures
and one RGBA32Float output texture: 144 bytes per cell. Velocity/dye are persistent; scratch allocations are
temporary. Pressure versions preserve odd/even Jacobi parity. The final
output has an explicit storage-write to sampled-read transition.

Run the focused source-contract tests with:

```powershell
dotnet run --project Gpu/LibraGpu.Core.Tests --no-restore
```

These tests establish compilation and persistence behavior, not GPU playback
of arbitrary resource plans.

## Generated texture bindings

`GpuEditorState.GeneratedResources` maps a particle Texture2D resource ID to
`GeneratedGpuResourceReference(ProducerNodeId, OutputId)`. The producer must be
an active fluid container in the same executable scope, and the supported output
is currently `DyeOutput` (premultiplied RGBA16Float). Engine asset paths and
generated references are mutually exclusive. Assigning either source replaces
the other in one undoable edit. References survive project/history persistence.

Whole-effect export carries the references in payload version 3, alongside
optional `automaticPlane` on fluid programs. Without an explicit choice, a
consumed output suppresses its source plane; an unconsumed fluid preserves its
existing automatic plane. Explicit true/false overrides that policy. Legacy
particle-only v1 and ordinary fluid v2 exports remain unchanged. The CPU package
envelope and particle register ABI are unchanged.

Adapters instantiate fluid producers before particle consumers and advance
producers first. Consumers bind the native GPU output directly. Billboard
sampling handles texture orientation and converts premultiplied dye to the
existing straight-alpha particle material convention. The generated binding is
per live scope; it does not introduce a global texture name or CPU field copy.
Unreal particle render state retains its producer until queued draws retire.
Unity binds a RenderTexture and Godot binds a Texture2DRD on the global device.

Current local GPU evidence covers Windows Unity 7000.0.0a16 D3D11 with Built-in
RP fallback and Godot 4.7.2 Vulkan Mobile / Forward+. Both execute the four
fluid models, ordered cell forces, ordinary Sprite/Flexible Plane/material
consumers and generated particle velocity/density sampling. Diagnostic readbacks
check finite fields, closed-domain water mass, hydrostatic rest, hot/cold fuel,
and an independent combustion-step oracle. Framebuffer controls cover source
plane suppression, Stop and fresh replay. Unity also checks translated, rotated
and nonuniformly scaled producer coordinates. Production sampling stays on GPU.
Unreal 5.7 D3D11 SM5 also passed real-scene model rendering, finite fields,
water mass/rest, hot/cold fuel, normal generated-texture draws, Stop and replay.
Its dedicated view-extension surface currently has no scene-depth occlusion
or global transparent-object sorting. A separate Unreal GPU test also passed
raw velocity and density-mask comparisons before and after producer translation,
rotation and nonuniform scaling.

## Liquid scope and remaining integration

| Solver mode | Implemented state and behavior | Current GPU evidence |
| --- | --- | --- |
| Side-view free surface | MAC face velocities, water/air pressure boundary, gravity, conservative water-fraction transport, pooling and resolved splash foundation | Unity D3D11, Unreal D3D11 SM5 and Godot Vulkan Mobile / Forward+ |
| Top-down shallow water | Flat-bed HLL water depth and horizontal momentum, with GPU-displaced surface rendering | Unity D3D11, Unreal D3D11 SM5 and Godot Vulkan Mobile / Forward+ |

Combustion evolves fuel, temperature, smoke and reaction. Ordered cell-force
nodes support position/time, noise/curl noise, field masks and math. Ordinary
Sprite/Plane/material bindings and particle field sampling consume per-instance
GPU outputs. These are implemented fluid paths, rather than features implied
by the generic resource declarations.

Arbitrary editable resource/pass graphs, custom runtime compute execution,
Texture3D execution, previous-frame sharing across containers and writable
engine main targets still need adapter work. Three-dimensional fluids follow
the completed and verified 2D work. Detached particle droplets, solid obstacles
and shallow-water bathymetry are outside this foundation.

## Development model and consumer contract

The fluid uniform block is now 512 bytes: the original five vectors, model and
reaction vectors, and 25 force vectors. Thirteen trusted kernels include water
initialization, shallow-water evolution, raw-field publication and straight-color
publication. Force inputs are earlier stable node IDs, encoded as one-based
indices in animation.yz; animation.w marks force sinks. Forward references and
cycles are rejected. No engine executes delivered shader text for trusted fluids.

FieldOutput is RGBA32F (velocity.xy, density/depth, temperature), DyeOutput and
ColorOutput are RGBA16F premultiplied/straight display respectively. Typed raw
sampling views alias FieldOutput. Version-3 generatedDrawBindings have exactly
nodeId, slot, name, association, index, producerNodeId, outputId and mainTexture.
The compiler copies resolved material parameter names into both the render
configuration and executable manifest. Ordinary main textures use straight color.
Adapters create producers before consumers, bind per-instance GPU textures and
invalidate borrowed texture wrappers during retirement. No production field
readback is required.

FreeSurface pressure is persistent and warm-started. Smoke/combustion pressure
is temporary and cleared before solving. Resource-plan metadata describes the
substep; water's nonzero initial state is separately established by its trusted
initialization kernel. The GPU checks above cover model, force and normal-draw
behavior on the stated adapters; other backends require separate acceptance.

`ColorOutput` uses the engine's ordinary image UV orientation. `DyeOutput` and `FieldOutput` retain solver coordinates with row zero at the lower edge; they are intended for explicit data sampling or materials that handle that orientation. Native ColorOutput publication flips rows on Unreal/Godot so ordinary draws need no special shader UV code.

Unity generated-plane particle kernels declare an additional structured mapping
buffer in source metadata (256 Float4 elements, 16-byte stride). The particle
constant block remains 48 bytes. Each instance uploads its mappings and binds
its own buffer to sampling kernels, retiring it with queued GPU work. This is
separate from the 512-byte fluid solver block.

## Local acceptance evidence (2026-11-20)

These are source-development checks on Windows with Radeon RX 6700 XT. They
do not describe a rebuilt published SDK, exported player, console or other API.

| Boundary | Observation |
| --- | --- |
| Portable compiler | 35 focused tests passed, including resource versions/hazards, invalid plans, canonical serialization, legacy particle ABI and generated mapping-buffer metadata |
| Trusted shader sources | All 13 fluid entry points compiled with strict DXC (`cs_6_0`, `-WX`, `-Ges`); shipped Unity/Unreal/Godot sources match generator output |
| Native contract | Clang standalone fixture agrees with managed compiler on the 512-byte fluid uniform and complete pass plan |
| Editor | Actual authoring checks passed for models/force connections, save/load, Undo/Redo, generated output bindings, remapped copied IDs and MCP; resource-plan declarations/unsupported-playback checks and EN/JA capture/localization also checked |
| Unity 7000.0.0a16 D3D11 Built-in fallback | Baseline, vorticity, model/force/reaction oracle, generated billboard and five ordinary draw cases passed; a later dedicated run passed raw velocity/density and transformed coordinates after the mapping-buffer fix |
| Godot 4.7.2 Vulkan Mobile / Forward+ | Seven suites passed on each renderer: models, ordinary generated draws, particle field consumers, generated billboards, vorticity, mixed fluids/particles and GPU scopes |
| Unreal 5.7 D3D11 SM5 | Six tests passed across the scene/model batch and separate raw-field batch: execution/plane, models/scene framebuffer, vorticity, compiler packages, ordinary draw framebuffer and generated field consumers |

Representative Godot diagnostics: closed free-surface mass changed from
335.99999 to 336.000498 over 120 fixed updates (about 1.5 ppm), with rest velocity
below 2.9e-7. Shallow-water mass remained 240 with rest velocity below 7.6e-8.
Hot combustion rendered nonzero pixels; cold fuel produced no reaction.
These are bounded synthetic test settings, not a general numerical-error bound.

The Unreal raw-field workload declares **138,240 bytes** of fluid storage for
24×40 cells (144 bytes/cell), plus its particle/register allocations and driver
overhead. One run took **40.334 ms** for velocity and **33.365 ms** for the density
mask, each covering 120 submitted simulation frames plus two transformed
sample checks and explicit diagnostic readbacks/synchronization. These are
whole-test elapsed times, not GPU timestamp measurements, production frame
budgets or scaling estimates for larger grids. Memory budgets bound aggregate
scope allocation; water CFL and maximum substeps bound catch-up work.

All readbacks in this evidence are explicit diagnostics. Normal simulation,
generated particle sampling and drawing retain their fields on the GPU.
Raw logs and unselected captures remain in the local validation directory.
