---
title: Flexible Plane Draw
---

[Home](index.md) · [Editor reference](editor-reference.md)

# Flexible Plane Draw

Flexible Plane Draw turns ordered points into a textured ribbon, not a flat
rectangle or a particle simulation. Unity, Unreal and Godot package renderers
have root and spawned-child wiring. The updated Preview uses engine-native rendering
without a per-Draw scene component. Fixed-point ribbons have rendered-pixel
coverage in Unity 7 Built-in/URP and Godot 4.7.2; this does not certify every
history, camera or target-platform combination.
The earlier **2026.11.8 SDK does not include** these new symbols;
use matching updated [Preview packages](https://github.com/Libra-Works/LibraFX/releases).
The published 2026.11.22 archives include the original ribbon implementation,
not the later renderer migration. Use the [release notes](release-notes.md)
and see [rendering boundaries](engine-integration.md#rendering).

## Authoring

Add **Flexible Plane Draw** (Rendering) and either **Flexible Plane Point
List** or **Flexible Plane Point History** (Flexible Plane). Connect the
source's output to Draw's **Points**. Supply an explicit Texture resource; no
placeholder is substituted. To make a trail, connect an animated FloatX3
position to History's Position; sampling starts with the instance and requires
at least two distinct sampled positions before a ribbon appears. To create a
free shape, edit Point List's Points JSON, and optionally connect FloatX3
values to Position 0–3. Each connection replaces that list entry's position
at runtime; later positions are authored only in JSON.

The Texture property uses the existing texture-filtered resource browser.
Assignment is undoable and changes only the primary texture; engine ribbon
materials are configured by the host rather than imported as graph material
slots.

A standalone root ribbon or history does not itself keep an idle effect alive.
`AutoDestroyWhenIdle` may end a root when its Spawnable templates become idle;
a root-only package without templates is not auto-ended by that rule. To keep
the ribbon until an explicit stop or finite duration, disable root
`AutoDestroyWhenIdle` and manage the effect lifetime explicitly.

Points JSON is a string containing an array of 2–64 objects, at most 4096
characters total. Every object has exactly `position` (three numbers), `width`
(nonnegative number), and `color` (four numbers); numbers must fit finite
32-bit floats. Example:

```json
[{"position":[0,0,0],"width":1,"color":[1,1,1,1]},
 {"position":[1,0,0],"width":0.5,"color":[1,0.5,0,1]}]
```

Colors are linear, unpremultiplied RGBA; width is **full** width in the same
coordinate units as positions. Each point supplies its own width and color,
forming the width/color profile. Interpolation across each triangle is done by
the renderer. A connected Position 0–3 must correspond to an existing point.

| Node | Input/parameter | Default and limit |
|---|---|---|
| Draw | Points | required Point List or Point History connection |
| Draw | Texture | empty; explicit resource required |
| Draw | Width multiplier | 1; finite, nonnegative; numeric pin supported |
| Draw | Tint | `[1,1,1,1]`; finite RGBA multiplier; numeric pin supported |
| Draw | Camera facing | true; false uses Fixed up |
| Draw | Fixed up | `[0,1,0]`; finite vector in point coordinate space |
| Draw | Repeat UV | false: stretch along total length; true: repeat |
| Draw | Repeat length | 1; positive finite coordinate units per U repeat |
| Point List | Points JSON | two white, width-1 points at `[0,0,0]` and `[1,0,0]` |
| Point List | Position 0–3 | unconnected: matching JSON position; FloatX3 pins |
| Point History | Position | `[0,0,0]`; finite FloatX3 pin |
| Point History | Width | 1; finite nonnegative full width; Float pin |
| Point History | Color | `[1,1,1,1]`; finite RGBA; FloatX4 pin |
| Point History | Capacity | 64; integer 2–4096 samples |
| Point History | Maximum age | 1 second; positive finite seconds |

Fixed-up orientation uses the tangent crossed with the supplied up vector.
Camera-facing uses tangent crossed with the vector from the point to the
camera position; the host must supply a camera **in the same coordinate
space**, not a camera forward vector. A deterministic perpendicular fallback
is used when the directions are parallel or zero. Consecutive points closer
than or equal to `1e-6` coordinate units collapse; fewer than two distinct
positions produce an empty draw. Zero width is valid. U spans 0–1 by arc length
in stretch mode or arc length / Repeat length in repeat mode; V spans 0–1.
The host controls texture wrap mode, blend/cull settings, transforms, and any
conversion between engine coordinate conventions.

## Host integration and ownership

`FlexiblePlaneDrawSchema.CreateDraw` returns an additive `render_config.nodes`
entry with kind `flexiblePlane`, `nodeId`, `assetPath`, existing-format
`inputs`, and `flexiblePlane` version 1. Its source is `list` or `history`;
list `points` have `position`, `width`, `color`; history has `capacity` and
`maxAge`. `facing`, `up`, `uvMode`, and `repeatLength` match the table.
Binding IDs are Draw `width`, `tint`; list `position0`–`position3`; history
`position`, `pointWidth`, `pointColor`. Source IDs and component indices
refer to ordinary VM values. Metadata helpers must not become VM instructions,
but their connected numeric sources must remain evaluated and bindable.
`assetPath` carries the resolved primary texture; `materials` and `textures`
are empty arrays and do not select a material. An integrated renderer must
provide a texture-compatible default ribbon material, or report its absence;
this metadata alone does not provide one.

The native C ABI is `Runtime/include/liberafx/LbFlexiblePlaneDraw.h`; implementation
is `Runtime/Core/LbFlexiblePlaneDraw.cpp`. Build it into the native plugin with
`LIBRA_INTEROP_BUILD`, or into a static host with `LIBRA_FLEXIBLE_STATIC` for
both its compilation and consumers. A host whose core is a separate shared
module can instead define `LIBRA_FLEXIBLE_API` to that module's export/import
macro for both core and consumers. `libra_flexible_plane_generate` accepts
caller-owned point, vertex and 32-bit index buffers. Call with empty buffers
to obtain counts (`LIBRA_INSUFFICIENT_WORK` for nonempty output), reserve and
retry. On failure it does not write vertex/index buffers; check the result,
including invalid inputs and arithmetic overflow. The maximum is 4096 input
points. Input/output buffers must not overlap. It emits two vertices per
distinct point and six indices per segment, without allocation.

History state and timed storage belong to each **effect instance and point
source**, including each Spawnable child instance; never share them between
instances. Initialize/reset on creation and reuse. Update once per source per
instance update, even if several draws reference it. Before processing all
draws in a scope, call `BeginUpdate()` (Unity) or `beginUpdate()` (C++) on
that scope's shared history. Conflicting samples for one source in the same
update are rejected. Use finite instance time
in seconds, sampled position/width/color in the chosen coordinate space.
Capacity drops the oldest sample, age removes samples strictly older than
`maxAge`; an equal timestamp replaces the newest sample, and backwards time
clears the history and inserts the new sample. A discontinuous forward seek
must explicitly reset as well: there is no interpolation or historical replay.
Copy history to a separate caller-owned point buffer, then multiply width and
color by the Draw bindings **on the copy** before generating; never repeatedly
multiply the stored history. Apply the same multipliers to list points.

The portable API alone does not submit meshes, manage texture assets, resolve
instance transforms, or render pixels. The migrated source renderers own
reusable geometry per draw/instance without creating a matching scene component:

* Unity requires an explicit texture binding with exactly one material in
  `defaultMaterials[0]`. Its shader must sample `_MainTex` and multiply by
  vertex color. Camera-facing draws use `Camera.main`. Each renderer owns its
  mesh and material clone, but no rendering GameObject or MeshRenderer.
* Unreal requires the explicitly bound authored texture and a material bound
  to `builtin:flexible-plane`. The material must expose a global `Texture`
  parameter and use vertex color. Camera-facing draws use the camera override
  or player camera fallback. Native primitive/proxy geometry uses 8-bit vertex
  colors and rejects values outside 0–1; no FlexiblePlane Component is created.
* Godot's package renderer resolves the explicitly bound texture and uses a
  vertex-colored, unshaded, transparent `StandardMaterial3D`. Camera-facing
  draws require the player camera or current viewport camera. Root and each
  spawned child own separate histories. Backward time clears history; a host implementing an
  explicit forward seek must call `resetFlexibleHistories()` before updating.
  RenderingServer RIDs and reusable dynamic ArrayMesh surfaces replace
  MeshInstance3D nodes. Surface updates, visibility, owner transforms and Stop
  have real-render coverage; other backends and exported players remain separate checks.

All engine objects are owner-thread only. Camera-facing draws need a camera
position converted from world to their own local space; fixed-up draws do not
need a camera. A singular camera-facing transform is rejected. Hosts must stop or report an update error rather than
continue displaying a stale mesh. These adapters require matching new
plugin/SDK builds; older SDKs must report the unsupported draw instead of
silently omitting it. `.lfxb` package
metadata remains in the existing LFXP container; no new compatibility is
implied until each consumer supports this descriptor.
