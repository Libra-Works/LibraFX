---
name: liberafx-game-effects
description: Create or tune editable LIBERA FX game-effect prototypes through its MCP tools, using gameplay briefs such as impact, slash, elemental spells, healing or magic circles.
---

# LIBERA FX game effects

Turn the brief into an editable source graph and verify its appearance in a connected preview. Prefer existing CPU/GPU particle stages and reusable groups; a requested spell is usually a composition of ordinary nodes, not a reason to add a new runtime node.

Discover available MCP tool schemas before editing. Read `libera_graph_read` and `libera_preview_clients` so the edit targets the intended document, graph scope and backend. Use one undoable template insertion, preserving existing nodes. If the selected scope cannot accept the template, navigate to the appropriate effect scope using the available navigation tools.

Use `libera_create_game_effect` for `HitImpact`, `HitSlash`, `FireMagic`, `MagicCircle`, `DarkMagic`, `LightMagic`, `HealingMagic`, `DustPuff`, `Explosion`, `Rain`, `Snow` and `Lightning`. Themes are `auto`, `fire`, `dark`, `light`, `heal`, `ice`, `earth`; duration is seconds, with a per-template default when omitted. Use `libera_create_particle_sample` for `CpuRadial`, `CpuFountain`, `CpuMeshSurface`, `Fireworks`, `GpuCurl100k` and the Fluid2D starters. These are starting graphs to tune, not finished production assets. CPU starters include a generated radial glow; they do not need a texture import.

Interpret references to FF, Dragon Quest or similar games as a brief about rhythm, silhouette, colour and gameplay readability. Extract the useful decisions: contact point versus area effect, anticipation/impact/decay, one-shot versus looping, ground alignment, scale and element. For an unspecified hit mark, clarify slash versus blunt impact when that distinction is needed; otherwise use a compact blunt-impact prototype and state that assumption. Do not make the user choose every numerical parameter before showing a useful first result.

Useful compositions:

- Blunt hit: a short bright contact flash, outward sparks and a quick decay; keep the contact point readable.
- Slash: a directional arc/ribbon, a narrow bright edge and a shorter spark tail. Orient the arc to the attack plane.
- Fire spell: upward motion, warm core and orange decay; add smoke separately if the brief needs it.
- Dark spell: violet/blue rim, inward or orbiting motion, short high-contrast release.
- Light spell: pale gold/white core, rays or outward sparkles, restrained lingering particles.
- Heal: upward green/gold motes, a soft pulse and a ground ring; keep the character readable.
- Magic circle: a ground-aligned ring structure with layered rotation, then a separate activation pulse.
- Dust: a low, wide puff with alpha blending, slow expansion and a soft brown/grey fade. Avoid the luminous additive look used for sparks.
- Explosion: fast outward motion and a short bright flash, followed by a slower smoke/dust layer when requested. Time the layers separately.
- Rain: a wide emission region above the current camera, narrow downward streaks and continuous emission. The emitter origin follows camera position; existing particles remain in world space. Use a translation matrix for emission and an identity world draw matrix, rather than parenting all live rain to the moving camera.
- Snow: the same camera-centred, world-space policy as rain, with slower fall, longer life, smaller rounded flakes and lateral wind. Keep density and coverage adjustable.
- Lightning: a world-space strike between explicit endpoints or at a chosen impact location, with a narrow irregular bolt and a very short flash/afterglow. Lightning does not follow the camera by default; only rain and snow use that weather convention.

Author in metres with X right, Y up, Z forward. Ground is XZ; upward acceleration is Y. Engine adapters absorb axis, handedness and unit differences. Colours and arbitrary numeric vectors are not spatial values. Use Bool pins and checkboxes for switches. Use emitter matrices to place new particles and draw matrices to transform the rendered set; inspect whether the intended space is local or world before connecting both.

Inspect `libera_graph_list_node_types`, `libera_graph_describe_node_ports`, `libera_graph_describe_group` or `gpu_describe` before constructing unfamiliar connections. Reuse discoverable groups rather than rebuilding common motion/fade/orientation logic. Do not hand-edit version fields to enable capabilities: export through the current compiler. CPU lifecycle graphs compile with `libera_graph_compile_spawnable`; GPU source compilation alone is not proof of rendering.

Preview at normal speed, then use `libera_preview_playback` with `timeScale` for timing inspection. Check `libera_render_status` / `gpu_runtime_status` and actual frames. Distinguish capacity from live count and exported shader source from GPU execution. Match the preview to the brief, adjust the graph, and report what was created plus any unverified appearance. Save to the user-authorized destination with the available project tools.

## Hands-on authoring review

When asked to improve usability, exercise complete artist tasks instead of waiting for the user to identify every problem. Work in an isolated document and preview connection. Include an empty-graph path as well as template insertion; bulk MCP edits can bypass the very controls whose usability needs review.

- Build a simple fountain, change emission/lifetime/colour, select a glow texture, and inspect the result.
- Move the emitter while existing particles remain in world space; check the coordinate preset and unit scale.
- Enter and leave CPU/GPU/procedural workspaces, rename nodes, and inspect labels and pin connections at reduced zoom.
- Preview, slow, stop, switch documents, reconnect, Undo/Redo, save and reopen. Check that stale particles and resources disappear.
- Find an asset in a large catalog, change folders during loading, and verify that old results do not replace the current selection.

For each finding, record the task, reproducible steps, expected/actual result, engine/backend and evidence. Inspect actual UI controls and captures where tools permit; successful MCP calls do not establish that controls are discoverable or readable. Report unavailable UI/graphics checks as unverified. Treat crashes, lost edits, coordinate errors and missing/stale output as blockers; then address confusing defaults, excessive steps and unclear labels. Fix small reproducible problems within the authorized scope, add a meaningful regression check, and repeat the same task before claiming it improved. Keep commits focused and distinguish starting-point visuals from polished game effects.
