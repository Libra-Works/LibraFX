# Procedural mesh and texture implementation contract

Status: design contract. See [the implemented workflow and validation status](procedural-resources.md).
This contract alone does not establish exported-player support.

## Authoring and samples

`Procedural Model` owns a basic shape and an ordered modifier Flow. Its typed
model output feeds `Model Draw`. `Procedural Texture` feeds
`Procedural Material`, which supplies the draw material. These three producers
share a Procedural palette category and Flow interaction. The draw retains the
existing transform and tint inputs. An
engine mesh asset is not required. The requested reference is
[MeshCreate](https://gameanimation.info/mesh-create/); this is an independent
implementation of the authoring workflow, not its project-file format.

The shape catalog follows the supplied feature summary: ribbon, slash, spiral,
wave, ring, arc, disk, torus, cylinder, cone, flare, sphere, hemisphere, capsule,
plane, box, crossed planes, spike, leaf/feather, planar swirl, flame ring,
double helix, coil and knot. The modifier catalog comprises twist, bend, taper,
wave, swirl, noise, round, inflate, transform, mirror, array, subdivide and
solidify. Ordering is meaningful. Modifiers have stable IDs, enable/disable,
duplicate/remove/reorder and undoable properties.

`Procedural Texture` uses the same Flow interaction for UV transforms, gradients,
noise, masks, compositing and color. The primary output is a GPU Texture2D,
initially 256 by 256. Time-dependent noise and UV/domain motion must be possible;
the output is not restricted to a static bake. Generated outputs feed the draw's
texture/material inputs and can be reused by multiple consumers in a scope.

`Procedural Material` combines generated textures, UV
controls, color, opacity, emission and blend settings. Its initial effect
material path is unlit, with alpha/additive modes. Noise-heavy generation is
primarily evaluated once into shared procedural textures, rather than repeated
for every draw fragment. Direct analytic material operators can be added within
the same bounded material contract. Model and material connect independently
to `Model Draw`; existing asset-based Model Draw graphs retain their behavior.

Bundled synthetic examples will cover a slash, a shockwave and a beam. They
must demonstrate shape, modifier order, pivot and animated texture editing
without requiring user-authored engine mesh assets.

## Pivot semantics

The source supports center, base/tip, origin and explicit XYZ pivots, with
start/end anchors for path-like shapes. Anchor choices shown by the UI must
be appropriate for the shape. Explicit vertex anchors can reference the
undeformed base topology; an invalid vertex is a diagnostic.

The pivot is resolved against the undeformed base, not repeatedly recentered
against animated bounds. Modifiers can use that pivot or an explicit local
center. Final drawing uses the same resolved origin and applies its offset
exactly once. Changing the pivot does not require rebuilding index topology.
Tests must distinguish pivot changes from vertex animation and from a draw
transform; multiple consumers must not apply the offset twice.

## Performance contract

- Compile and validate topology when authored shape/subdivision/copy settings
  change. Retain index buffers and precomputed subdivision/copy maps while
  continuous deformation parameters animate.
- Preserve arbitrary modifier order. A subdivision or array after an animated
  deformation still evaluates the changed positions through cached maps; it
  must not silently move ahead of that deformation to obtain a static bake.
- Share immutable definitions and static meshes. Mutable vertex buffers belong
  to a scope instance, and a mesh used by multiple draws updates once per scope.
- Cache static prefixes. Skip evaluation and uploads when effective inputs did
  not change. Bound and preallocate work buffers; do not recreate mesh objects,
  materials, index buffers or per-vertex scene components each frame.
- Recalculate geometry-derived normals/tangents only when required by changed
  attributes or a downstream operation. Preserve UV seams and reflection
  winding; solidify must distinguish open boundaries from duplicated UV seams.
- Evaluate animated textures on the GPU without normal-playback CPU readback.
  Static outputs are reused. Bound resolution, graph size and noise octaves;
  update a producer once before all consumers, not once per material draw.
- Check predicted vertex/index growth and aggregate memory before allocation.
  Over-budget graphs produce explicit diagnostics. Do not silently freeze an
  animated resource or truncate geometry to fit a budget.
- Expose diagnostic counts for topology builds, vertex evaluations/uploads,
  texture dispatches, allocations and retained bytes. Measure representative
  workloads before claiming frame-time or scaling guarantees.

## Portable and engine boundaries

Portable C++ owns mesh generation and modifier mathematics. A bounded managed
recipe owns project/history/MCP data and compiler validation. Texture evaluation
uses a bounded portable pixel graph and matching trusted engine shaders.
Shape math and pixel semantics must have numerical reference tests.

Unity, Unreal and Godot adapters own GPU resources, scene submission, engine
coordinate conversion and queued-work retirement. Immutable static outputs can share leases across effects; animated resources
remain private to an instance. Stop, restart, producer
retirement and stopping an unrelated instance are explicit acceptance cases.
Materials and optional external textures retain the existing binding contract;
the generated mesh itself must not resolve through an engine asset path.

Editor requirements include localized English/Japanese controls, Flow
reordering, graph inputs for continuous parameters, persistence, Undo/Redo,
copy/remapped IDs and MCP discovery/edit/export. Topology-changing controls
must not accidentally become per-frame unbounded rebuild inputs.

User-facing names are Procedural Model / Procedural Texture / Procedural
Material (Japanese: プロシージャルモデル / プロシージャルテクスチャ /
プロシージャルマテリアル). New kind identifiers are ProceduralModel,
ProceduralTexture and ProceduralMaterial. Do not introduce the earlier
provisional DynamicMesh / DynamicModelDraw names into new schemas or APIs.

## Verification sequence

Write the complete implementation across shared contracts, authoring, native
generation, texture shaders and adapters before the expensive acceptance batch.
Then verify bounded portable geometry and pixel references, actual editor
controls/history/MCP, compiled package delivery and real GPU frames on isolated
Unity, Unreal and Godot projects. Include static/no-change controls, animated
states, pivot controls, shared consumers, independent instances, Stop/restart,
finite attributes, UV/normals/winding and measured allocation/update counts.

Existing fluid work is the clean repository baseline `2350057`; the user's
untracked `.agents/` directory is unrelated and must be preserved. This work
does not authorize commit, push, issue updates or publication.
