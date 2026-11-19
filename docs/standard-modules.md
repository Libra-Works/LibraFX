# Standard module library

Scope checked against the public Niagara and Unity documentation on 2026-11-17.
This is a small, deliberately non-compatible LIBERA FX library: its assets are
ordinary editable graph groups with stable IDs and revisions, not copied vendor
modules, assets, namespaced stack definitions, or interchange formats.

## Authoring

The standard groups are bundled in `Assets/Modules` and appear in a new
project's node catalog and graph quick search without configuring a project
group folder. Search `Euler`, `velocity`, `direction`, `F-Curve`, or the listed
`billboard`, `camera`, or Japanese keywords. Selecting a group shows its typed sockets, public controls,
phase, units, state ownership, and backend scope. Built-ins are locked. To
change their internal graph, use **Clone to Project Library** after configuring
a project group directory in project settings; the clone receives a new asset
ID and revision 1. Inserting a built-in does not modify the source asset.

### Minimal motion setup

For each simulation step, connect persistent `Position Previous`, persistent
`Velocity`, and `Delta Time (seconds)` to **Euler Position Step**, then write
`Next Position` back to the caller's position state. Likewise,
**Euler Velocity Step** takes persistent `Velocity`, `Acceleration`, and delta
time and returns the next velocity. Choose the order of those two operations
explicitly in the calling simulation. These groups calculate one Euler step;
they are not stateful integrators, do not own an update loop, and do not
silently replace previous state with `velocity * Time`.

**Initial Velocity from Direction and Speed** normalizes an explicit direction
and multiplies it by an explicit speed. **Effect-Time F-Curve** exposes an
editable scalar curve whose key times use seconds and are sampled using the
runtime effect clock. It is not a normalized-particle-age curve.

`Time Scale` and `Speed Scale` are per-instance sliders. No group creates a
LifeTime. The existing convention that an absent LifeTime is infinite remains
unchanged.

### Orientation and billboards

Insert **Billboard** and select `Mode` in the inspector. **Spherical Billboard**
and **World Look At** use the full direction to `Target`. **Tree Billboard**
projects that direction onto the plane perpendicular to `World Up`, preserving
the up axis. All modes expose `Position`, `Target`,
`World Up`, mesh-local `Local Forward` (+X by default), `Local Up` (+Z by
default), `Scale`, `Pivot`, and `Tolerance`; outputs are `World Matrix` and
`Valid`. Degenerate target/up/local axes produce a zero matrix and `Valid = 0`.
Existing projects keep their original group IDs and revisions. The three
legacy variants are no longer offered in node search.

For camera-facing effects, add **Current Camera** at the graph root,
connect `Position` to Billboard's `Target`, and check `Valid` before using the
result. The package host samples before the initial advance and each frame
advance. Unity and Unreal use the per-effect override when assigned and otherwise select the
running Game View/player camera. Unity's default screen-view selection picks
the highest-depth eligible camera on the primary display when outside a render
callback. Unreal prefers the effect owner's local player, then the world's
first local player. For split-screen, render textures, or other multi-view
cases, assign an override for each effect instance. Without a camera the node
yields zero position and `Valid = 0`. The Billboard group itself neither
queries cameras nor draws geometry. Raw bytecode and hosts without a camera
adapter retain the invalid default unless the host publishes the camera input.
When the Billboard lives in a Spawnable template, pass the root Position
through a Live Vector3 input so it follows the camera after spawning.
Camera positions use the playing engine's world coordinates; choose `World Up`
to match those coordinates.

On **GPU Particle**, connect a world transform to `Draw Matrix` and deliver
the graph as an `.lfxp` package. Each root or Spawnable instance reads its own
matrix. The matrix affects already-living particles as well as future draws;
it changes neither simulation, emitter spawn, nor FlowMap sampling. An
unconnected port keeps the legacy placement. Use the updated engine adapter
and its GPU billboard shader/material when this port is connected.

## Capability matrix

“Generic CPU” means the graph is resolved and lowered through the existing
portable VFX bytecode path for the C++ Core/converter. It does not assert that a
Unity or Unreal renderer, engine preview, or pixels were verified for this
issue.

| Purpose / mapped public feature | Phase and inputs → outputs | State owner and units | Existing primitives / group | Backend and status |
|---|---|---|---|---|
| One Euler position step (common Niagara/Unity movement composition) | Caller-invoked update step. Previous Position (Vector3), Velocity (Vector3), Delta Time (Float seconds) → Next Position (Vector3). | Caller retains position and determines invocation cadence/order. Position units are caller-defined; velocity is position-units/second; delta time is seconds. | `Multiply`, `ScalarToVector3`, `MultiplyVector3`, `AddVector3`; bundled locked **Euler Position Step** group with exposed Time Scale [0,5]. | Generic CPU graph compilation; host rendering/backend pixels not verified here. Implemented as a pure step kernel, not as a persistent simulator. |
| One Euler velocity/acceleration step (including caller-supplied gravity) | Caller-invoked update step. Previous Velocity (Vector3), Acceleration (Vector3), Delta Time (Float seconds) → Next Velocity (Vector3). | Caller retains velocity and decides whether/how to feed gravity or other acceleration. Velocity is position-units/second; acceleration is position-units/second²; delta time is seconds. | `Multiply`, `ScalarToVector3`, `MultiplyVector3`, `AddVector3`; bundled locked **Euler Velocity Step** group with exposed Time Scale [0,5]. | Generic CPU graph compilation; no gravity source, drag law, or hidden state. Engine runtime/rendering not verified here. |
| Initialize velocity from an explicit direction and speed | Initialization or any caller-selected phase. Direction (Vector3), Speed (Float) → Velocity (Vector3). | Stateless. Direction is dimensionless; speed and result are caller-defined position-units/second. No random seed. | `NormalizeVector3`, `Multiply`, `ScalarToVector3`, `MultiplyVector3`; bundled locked **Initial Velocity from Direction and Speed** group with exposed Speed Scale [0,5]. | Generic CPU graph compilation; engine rendering not verified here. |
| Scalar effect-time curve | Effect-time evaluation. No graph input → Float value. | Stateless curve data. Curve key times/runtime effect time are seconds; output units are caller-defined. It is not per-particle normalized age. | Existing `FloatFCurve` inside a bundled group; the curve itself is exposed for per-instance editing. | Existing curve-bytecode path in portable C++ Core/converter. Unity/Unreal module-level rendering and preview remain unverified. |
| Billboard transform | Orientation/render-transform preparation. Mode, Position, Target, World Up, Local Forward/Up, Scale, Pivot, Tolerance → World Matrix, Valid. | Stateless. Target is explicit or supplied by Current Camera. Pivot is mesh-local. | One **Billboard** group using enum selection and existing vector/matrix primitives. Defaults are local +X forward and +Z up. | Portable CPU graph; GPU and mesh rendering remain separate features. |
| On Start signal | Root effect or directly within an existing Spawnable Group. Signal → ordered signal work. | Existing lifecycle owner / Spawnable runtime; event cadence is one start event, not an update loop. | Existing Core `OnStart`, `Sequence`, and Spawnable lifecycle. Not wrapped as a standard group: lifecycle entries are scope constrained and cannot safely live inside ordinary Graph Groups. | Existing package/runtime feature; no new module or adapter guarantee in this library. |
| Generator / particle template | Spawnable Group invocation; typed spawn inputs and optional signals → captured outputs/events/particle instances. | Existing host/runtime owns template instances and scheduling. Absent LifeTime means infinite. A rate/burst scheduler is not implied. | Existing `SpawnableGroup`, Spawn Input/Output and optional OnStart/LifeTime constructs. Not duplicated as a standard Graph Group because it owns state. | Existing Spawnable package path; this task does not add emitter scheduling or certify engine rendering. |
| Standard emission rate, Burst, Box/Cone/Sphere sampling and random distribution | Spawn/update spawn phase; would need rate/burst parameters, bounds/transforms and seed → sampled spawn data. | Requires scheduler, shape sampling and reproducible random-state ownership; units/space would need explicit contracts. | No standard group in this set. A vector transform alone is not a volume sampler or RNG. | Unsupported follow-on. Do not substitute a time expression or deterministic fixed point. |
| Size/Color/Rotation over normalized particle age, including vector/color curves | Particle update or output phase; normalized age and curve(s) → size/color/rotation. | Requires a declared age source/normalization basis and component-wise curve semantics. F-Curve's existing runtime clock is effect seconds, not normalized particle age. | Not included. `FloatFCurve` and `InstanceNormalizedAge` do not alone establish age-driven curve input or vector/color curve composition. | Unsupported follow-on; no automatic LifeTime is added. |
| Drag, collision, attraction, vector field, curl noise, mesh/texture sampling | Usually particle update/spawn; requires persistent velocity/state and/or host geometry/field/resource queries → updated state/samples/events. | Requires documented integration order, state ownership, coordinate space/units, collision queries, sampling resources, or random seed. | No falsely equivalent standard group. Existing arithmetic can express a caller-provided acceleration step but not these queries/laws. | Unsupported follow-on; requires new host interfaces or primitives where appropriate. |
| Sprite/mesh rendering and sprite animation | Output/render; particle transforms/UV/frame selection → renderer. | Renderer owns camera-facing and material/texture behavior; coordinate basis must be explicit. | Existing Core Sprite/Model output and Sprite Animation features are separate from these transform groups. | Existing nodes and transform groups are not a claim that all render backends are covered. |

### Standard asset registration

File-backed modules use `Assets/Modules/manifest.json` plus one
`<stable-id-without-hyphens>.lfxgroup` asset. The Billboard manifest entry
uses the deterministic graph in `Services/BillboardGraphLibrary.cs`.
Runtime providers can register
groups on the active `GroupAssetCatalog` using
`RegisterStandardModules(IEnumerable<StandardGroupModuleDefinition>)`.
Registration validates stable metadata/asset IDs, a locked asset, exposed
parameter references, and the graph before adding any module; each catalog
refresh then exposes it to search and insertion. See the record definitions in
`Services/StandardGroupAssetLibrary.cs` before adding more modules.

## Public reference basis

- [Epic: Overview of Niagara Effects](https://dev.epicgames.com/documentation/unreal-engine/overview-of-niagara-effects-for-unreal-engine?lang=en-US) — describes system/emitter/particle scopes and event/update responsibilities.
- [Epic: System and Emitter Module Reference](https://dev.epicgames.com/documentation/unreal-engine/system-and-emitter-module-reference-for-niagara-effects-in-unreal-engine?lang=en-US) — system/emitter update and execution-state concepts.
- [Epic: Particle Update Group Reference](https://dev.epicgames.com/documentation/unreal-engine/particle-update-group-reference-for-niagara-effects-in-unreal-engine) — includes drag, force solving, and particle-state responsibilities.
- [Unity VFX Graph 17.0: Graph Logic and Philosophy](https://docs.unity3d.com/Packages/com.unity.visualeffectgraph@17.0//manual/GraphLogicAndPhilosophy.html) — Spawn, Initialize, Update, Output and Block/Context roles.
- [Unity VFX Graph 17.1: Blocks](https://docs.unity3d.com/Packages/com.unity.visualeffectgraph@17.1//manual/Blocks.html) — block composition, parameter settings and activation semantics.
- [Unity Particle System module reference](https://docs.unity3d.com/6000.4/Documentation/Manual/ParticleSystemModules.html) — module families including color/size/rotation over lifetime, shape, velocity and forces.

The matrix maps those public concepts into the current LIBERA FX primitive and
host boundaries. It does not promise full product compatibility. No vendor
code, graph files, or assets are copied.
