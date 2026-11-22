---
title: Engine Integration
---

[Documentation home](index.md) · [Getting started](getting-started.md)

# Engine Integration

For the full development process, use the
[Unity / Unreal / Godot production workflow](production-workflow.md):
installation, desktop launch, engine resources, live preview, imported assets
and offline gameplay. See [Batch conversion](batch-conversion.md) for automation.

See [engine and file terms](glossary.md#engine) for SDK, runtime, editable projects, and compiled effects.

Download the matching SDK from the latest [Preview release](https://github.com/Libra-Works/LibraFX/releases).
Use the editor, converter and SDK from the same release. Legacy `.lfxp` packages remain supported inputs.

Existing raw-program assets do not automatically become package assets.
When replacing a legacy raw `.lfxb` with a new package export, import it as a new
asset and explicitly reassign its component references. Renaming a file or
reimporting it is not an asset-type migration. Unity also needs the matching
native plugin installed for import validation.
Close the engine before installing. Extract the archive and copy only the
top-level install folder shown below. Each folder includes `VERSION.json`.

| SDK | Install folder in your project | Included runtime |
| --- | --- | --- |
| LibraForUnity | `Packages/works.libra.liberafx` | Windows x64 native bridge |
| LibraForUnreal | `Plugins/LibraRuntimeForUnreal` | Source plugin, including the portable Core |
| LibraForGodot | `addons/liberafx` | Windows x64 Godot 4.7 GDExtension |

Back up any existing installation before updating. For Unity, keep existing
`.meta` files for unchanged assets and remove duplicate package or native
plugin registrations. For Godot, preserve any generated `.gdextension.uid`.
For Unreal, replace the installed folder rather than merging old and new files;
do not delete through a development junction.

### Verification boundaries

The SDK supports the rendering, Fluid2D and nested Spawnable/CPU features below. Local acceptance includes real rendered
pixels and cleanup in all three engines. Unreal has UE 5.7.4 Clang compilation
(`-NoLink`), MSVC Editor linking and D3D11/SM5 rendered-pixel coverage.
Clang compilation is not console SDK verification or linking against an
MSVC-built engine. See the [release notes](release-notes.md) for the complete
matrix; engine compilation/import tests alone are not visual acceptance.

## Coordinates and units

Author spatial values in **metres, X right, Y up, Z forward**. The runtime's coordinate basis converts spatial data at the host boundary; you should not rotate every effect graph to fit each engine.

| Host default | Canonical X / Y / Z map to | Units per metre |
| --- | --- | --- |
| Unity | +X / +Y / +Z | 1 |
| Unreal | +Y / +Z / +X | 100 |
| Godot | +X / +Y / −Z | 1 |

The SDK converts values according to their spatial role: position, vector, direction, normal, angular vector or transform. A colour or arbitrary numeric vector is not automatically a spatial value. Custom hosts must configure their signed axis mapping and units at the runtime boundary, including external spatial inputs. Do not apply the same conversion twice.

There is no editor-side Y-up/Z-up selector in this workflow. Keep authored graphs in the canonical Y-up basis. For a custom host such as a Z-up game, configure the SDK basis for that host rather than changing the meaning of the graph's axes.

<span id="rendering"></span>

<details class="page-toc" markdown="1">
<summary>On this page</summary>

* TOC
{:toc}

</details>

## Drawing without per-Draw scene components

The effect-level playback Component/Node remains. Ordinary Model/Sprite Draw,
CPU Sprite particles and FlexiblePlane use engine-owned rendering resources
without a MeshComponent, MeshRenderer or MeshInstance3D for each drawn item.
Engine assets and materials are still used; portable Core does not implement a
separate graphics engine.

| Adapter | Submission path |
| --- | --- |
| Unity | Direct `Graphics` drawing and compatible opaque instancing; reusable CPU/ribbon meshes. |
| Unreal | Native primitive descriptors and scene proxies; compatible opaque instancing, with separate transparent sorting primitives. |
| Godot | RenderingServer instance and Mesh/MultiMesh RIDs; bulk particle uploads and reusable ribbon surfaces. |

Batching depends on mesh, material, parameters, transforms and ordering compatibility.
All three adapters keep mirrored or singular world transforms out of ordinary
instancing batches to preserve the individual drawing path's face culling.
Removing scene components does not imply one draw call, no resources, or a
measured speedup. Lightweight native primitives/RIDs can remain separate where
required. Existing GPU simulation algorithms are unchanged.

### Unity Sprite ordering limitation

Layered Sprite compositions also use direct submissions, with **no
MeshRenderer fallback**. `LiberaFxRenderOrdering` retains render queue,
authored `drawOrder`, camera depth and stable registration identity in an
independent pre-batch sorting stage. Transparent and explicitly ordered entries
are not instanced together.

This defines submission order, **not guaranteed global transparent ordering**.
The tested Built-in/URP Graphics paths may re-sort by depth and do not reproduce
the old `Renderer.sortingOrder` behavior or interleave reliably with external
Renderers. Renderer removal is intentional; a future sorting/submission backend
can consume the retained metadata without changing graph files. That backend
is not implemented yet.

### Local verification and cost counters

Unity 7000.0.0a16/D3D11 Built-in and URP 17.7 have rendered-pixel coverage for
ordinary batching, CPU Sprite, fixed-point ribbons and layered Sprite composition,
including Stop/cleanup. Godot 4.7.2 has real-render coverage for opaque root/child
batching, CPU Sprite, fixed-point ribbons, held owner transforms/visibility,
world changes and Stop. These checks do not certify every transparent scene,
history mode, engine version, graphics backend or shipped player.

Unreal source has UE 5.7.4 Clang compile-only and MSVC linking coverage, plus
D3D11/SM5 rendered-pixel acceptance for opaque instancing, CPU Sprite RGB/half-alpha,
fixed-up ribbon vertex colors and clear targets after Stop. A one-sided plane
also passed three create/remove cycles, including a mirrored transform.
A NullRHI run alone does not establish rendered pixels. Other RHIs, packaged
games and console SDKs are not verified; consult the adapter's `USAGE.txt`.

Adapter statistics describe logical draws/particles, batches, native instances
and submitted/staged data; see each adapter's `USAGE.txt` for exact fields.
Allocated upload capacity can exceed the live particle count: three live
particles in a four-record MultiMesh buffer still submit four records.
Staged bytes are not a measurement of completed GPU transfers or frame time.

The update also has local real-GPU checks for [2D smoke/dye](fluid-2d.md),
and [nested CPU ownership](cpu-particles.md#spawnable-ownership) checks covering
particles surviving parent/child deadlines, final cleanup and restart in all
three engines. These are implementation acceptance, not certification of every
downloaded binary, platform, rendering backend or shipped player.

## Play a first effect

Start with a CPU-only effect package (`.lfxb`) without draw or GPU nodes.
This lets you verify loading and named Game I/O before setting up mesh or material
bindings. Save your `.lfxproj`, then compile it using the bundled `LiberaFxConverter`
from the `Converter` directory. For example, on Windows:

```powershell
.\LiberaFxConverter.exe convert C:\Effects\input.lfxproj C:\Effects\effect.lfxb
```

The editor's **Export VFX...** command creates the same `.lfxb` package. Give the graph
an observable output, such as a named Output Signal, so that a successful load
can be distinguished from an effect that never runs.

### Unity

Put the `.lfxb` under `Assets/`. Assign the imported asset to a
`LiberaFxSpawnableComponent`; enable **Play On Enable** and enter Play Mode,
or call `Play()` explicitly. Inspect `LastError` on failure and use
`GameIoEventReceived` to observe authored outputs.

### Unreal Engine

Build the project's Editor target after copying the plugin. Import the `.lfxb`
in the Content Browser and drag the imported asset into the level. This creates
an actor with a **Libra Effect** component. Enable **Auto Play** and start
Play In Editor, or call `Play()` explicitly. Use `OnGameIoEvent` to observe
authored outputs.

### Godot

Place the `.lfxb` under `res://`, add a `LibraEffectPlayer`, and call
`play_package(FileAccess.get_file_as_bytes("res://effect.lfxb"))` after the
player enters the scene tree. A result of `0` means the package loaded; wait
for the first process tick before firing a game input signal. Observe authored
outputs via `game_io_event`. Include `.lfxb` files in the export preset's
non-resource filter if you ship them with the game.

<a id="prepared-playback"></a>

## Prepare playback and share simulation work

Updated source integrations can reserve effect instances during loading and
reuse them at playback. Independent effects share a bounded executor instead
of each effect owning its own worker threads. Rendering and engine callbacks
remain at their engine-thread boundaries.

| Engine | Prepare during loading | Configure before preparation |
| --- | --- | --- |
| Unity | Assigned scene effects prepare in `Awake`; call `PrewarmEffect()` for dynamically assigned assets. | `usePreparedHosts`, `preparedHostCapacity`, `preparationWorkers`, `useSharedSimulation` on the Spawnable component. |
| Unreal | Assigned effects prepare in `BeginPlay`; call **Prewarm Effect** earlier for dynamic packages. | **Use Prepared Hosts**, **Prepared Host Capacity**, **Preparation Workers**, **Use Shared Simulation** under `Libra Runtime\|Performance`. |
| Godot | Call `prewarm_package(bytes, capacity, workers)` on an in-tree player. | Reserve the needed concurrent playback count before `play_package`. Normal processing shares simulation; explicit `advance(delta)` stays immediate. |

Capacity means simultaneous effect playbacks, not the number of particles.
The default reservation is **256 playbacks per package/options pool** with four
preparation workers. Existing saved component values and explicit arguments
keep their configured capacities; adjust those separately when needed.
For example, reserve eight hosts for eight copies of the same effect. Exhaustion
reports `InsufficientWork`; it does not silently allocate a ninth host. Return a
playback before reusing its capacity. Each new lease starts with reset clocks,
inputs, matrices, particle identities and events, so configure it again.

Nested Spawnables and [procedural mesh emitters](cpu-particles.md) are supported.
Parent/child Signal and retirement order is kept within each host. Memory also
scales with each template's configured child capacity. Animated mesh revisions
and hierarchical routing may allocate during simulation: preparation does not
make every possible frame allocation-free. Engine meshes, textures and materials
still require their normal resource setup, including Unreal's CPU Sprite materials.

### Native project callbacks

Custom native nodes can separately declare `reusable`, `workerSafe` and
`parallelPreparation` in `CustomNodePolicy`. Unity/Godot provide policies through
`host::ProjectNodeRegistry`; Unreal uses
`FLibraNativeNodes::Register(descriptors, policies)`. Rebuild project modules with
the matching SDK. The existing descriptor ABI-1 layout is unchanged.

With no declaration, a used custom callback cannot enter a prepared pool and
runs on the shared batch's calling thread, after its worker jobs join.
Declaring reuse alone does not permit worker execution or parallel initialization.
Only assert these permissions when the callback, its initialization/cleanup and
any shared external context satisfy the corresponding contract.

### What was checked

Windows tests covered fresh versus reused/shared particle state, child lifetime
and Signal order, callback state/cleanup and thread affinity, and memory checks.
Actual engine checks used Unity 7000.0.0a16/D3D11 with an isolated unlit SRP,
Godot 4.7.2/OpenGL and Unreal 5.7. Procedural mesh and nested CPU effects used
prepared storage and shared frames. Unity/Godot also verified visible particles,
Stop clearing and replay; Unreal verified task/scene integration, without a
cooked-player or framebuffer-equivalence claim for this path.

Install matching editor/converter and
native integration revisions; older Preview binaries do not acquire them when
the documentation changes. The [release notes](release-notes.md) explains
how to keep the tools and SDK in sync.

## Extend an effect

- **C# or GDScript:** On the root graph, use an `action.*` Output Signal. Bind it
  with `BindScriptAction` in Unity or `bind_script_action` in Godot. The handler
  runs on the engine thread and can write a reply to a named Input Property
  for the next evaluation. Existing Model Draw and Sprite nodes can handle
  rendering; this path does not require C++ or a custom renderer.
- **Project C++ nodes:** Register the descriptor matching the node JSON's UUID,
  ABI, and ports. Unreal registers descriptors at module startup. Unity and
  Godot build project descriptors into their native bridge. This supports root
  and child programs of the `.lfxb` package. Use a native node when the graph needs the
  result within the current evaluation.

See the [SDK distribution reference](https://github.com/Libra-Works/LibraFX/blob/main/docs/sdk-distribution.md#project-defined-nodes-in-spawnable-packages)
for registration steps, working code examples, and the supported verification
matrix. The Preview's Windows bridge binaries do not certify other target
platforms, render backends, or console SDKs.
