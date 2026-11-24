---
name: liberafx-operator
description: Operate LIBERA FX through MCP to open and save effects, edit and optimize node graphs safely, and verify playback in connected engine previews. Use for effect authoring and troubleshooting, not for modifying LIBERA FX source code.
---

# LIBERA FX operator

These are product operation instructions. Respond in the user's language; the
skill's English text does not require English conversation.

## Connect and discover

1. Ask the user to start LIBERA FX and open its MCP connection panel.
2. Read the actual endpoint from that panel. The default is
   `http://127.0.0.1:8765/mcp`, but the configured port may differ.
3. Configure the MCP client's Streamable HTTP connection with the
   `X-LiberaFx-Token` header. Let the user enter the token through the client's
   protected configuration. Never include a real token in prompts, screenshots,
   skill files, logs, project files or commits.
4. Discover tools with `tools/list` and read their current argument schemas.
   The installed editor is authoritative. If a tool below is missing, report
   the compatibility gap instead of inventing a replacement endpoint or tool.

MCP is authenticated local editing access, not the engine Preview transport.
Do not expose it on a public interface, bypass authentication or enable
development/debug access just to operate an effect.

## Open and save without native dialogs

Use the project tools rather than keyboard-driven Windows Open/Save dialogs.
Arguments below are examples; obtain the actual path from the user.

```json
{
  "name": "libera_graph_open_project",
  "arguments": {"path": "C:\\Effects\\Example.lfxproj"}
}
```

Opening adds and activates a document without replacing other open documents.
If that path is already open, it activates the existing tab and preserves its
unsaved changes; it is **not** a reload-from-disk operation.

```json
{
  "name": "libera_graph_save_project",
  "arguments": {"path": "C:\\Effects\\Example-copy.lfxproj", "overwrite": false}
}
```

- Paths must be absolute local `.lfxproj` paths, not URIs or network shares.
  The parent directory must already exist.
- Saving targets the active document and includes its complete history tree.
  Check the returned document ID, path and dirty state.
- Replacing an existing file requires `overwrite: true` and the user's
  permission. Never overwrite another effect merely to get a successful save.
  A destination owned by another open document is rejected even with permission.
- These tools are unavailable inside graph-scoped in-editor chat. Report that
  restriction and use an authorized external MCP client or the editor's own UI.
- Never edit the project JSON directly to bypass validation or alter history.

## Inspect before editing

Read `libera_graph_read` and use `libera_graph_list_node_types` with `query` or
`group` before changing the
graph. Preserve unrelated nodes, connections, names, values and document tabs.
Use readable node names in explanations; UUIDs are tool identifiers, not useful
user-facing labels.

- Use `libera_graph_create_node`, `libera_graph_set_parameter`,
  `libera_graph_connect_nodes`, `libera_graph_move_node` and
  `libera_graph_rename_node` according to their discovered schemas.
- For an additive batch, prefer `libera_graph_apply_plan`: it produces one
  undoable edit, and connection keys refer only to nodes declared in that plan.
- Prefer `layout: "flow"` for generated plans and omit x/y for nodes that may be
  positioned automatically. This follows dependencies left to right without
  wrapping to fit the screen. Explicit x/y pairs are fixed; existing graph
  nodes do not move. `layout: "compact"` is an explicit four-column alternative;
  the legacy `manual` plan mode requires coordinates.
- **Lay out graphs strictly left-to-right (upstream on the left, downstream on the right).** Node outputs are on the right and inputs are on the left, so place source/producer nodes (e.g., `ProceduralTexture` -> `ProceduralMaterial`, math/emitter inputs, `OnStart`) to the left (`smaller X`) of the target nodes they feed (`CpuParticle`, `GpuParticle`, Draw nodes). Never place a source node to the right of its destination; use `libera_graph_preview_layout` + `libera_graph_apply_layout` (`flow` mode) when needed.
- **Route textures through materials.** Particle and Draw nodes take `ProceduralMaterial`; connect `ProceduralTexture` to `ProceduralMaterial.texture0..3` and connect `ProceduralMaterial` to the particle or draw node.
- For existing nodes, call `libera_graph_preview_layout` with explicit `nodeIds`
  from the active editing level. Inspect positions, deltas and warnings before
  calling `libera_graph_apply_layout` with its `previewId`. Include immutable
  targets in `fixedNodeIds`. Stale previews are rejected. Unselected nodes and
  comment frames stay fixed; comments are not automatically resized.
- Frame all / Frame selected and pan/zoom change the view, not node positions.
  Do not rearrange a wide graph just to fit one screen.
- Parameter values are actual JSON values, not JSON serialized into a string.
  Obtain parameter IDs, types, enum options and defaults from node definitions.
- Preserve `sourcePortId` and `targetPortId`. A multi-output node's outputs are
  distinct pins; never approximate them by selecting a component on the node.
- Insert visible conversion nodes when port types differ. Do not assume
  arbitrary implicit conversions; existing polymorphic scalar broadcast is a
  specific exception.
- Each Signal output has at most one destination. Use Sequence to branch.
  Signal inputs can receive independent arrivals and do not synchronize them.
- `libera_graph_enter` / `libera_graph_up` change the active editing level.
  Re-read after navigation. A Spawnable Group is an editable template; editing
  it does not itself create a runtime instance.
- Do not delete and reconstruct an existing graph to implement a small change.
  Inspect the result after each meaningful edit and stop on validation errors.

### Undo, Redo and history

Edits use the editor's history. Use dedicated history tools only if they appear
in `tools/list`; otherwise use the editor's Edit > Undo / Redo or History UI,
with user assistance when UI control is unavailable. Do not invent Undo/Redo
tool names, rewrite history JSON, or use
development-only key simulation as a normal product dependency.

History can branch after an edit at an older state. Keep branches and bookmarks.
Before undoing, verify that the active document and latest edit are still the
ones you intended; never undo an intervening user edit blindly.

### Explain and navigate

In editor chat, "Explain selected nodes" prepares a draft for up to 32 selected
nodes; it does not send anything until the user presses Send and permits project
sharing. Separate graph facts from inferred author intent.

Reference known nodes as `[readable name](liberafx-node:UUID)` in assistant text.
A single UUID refers to the current editing level. For an absolute path, use
`[name](liberafx-node:/GROUP-UUID/NODE-UUID)`; `/NODE-UUID` refers to a root node.
The editor exposes locally resolved references as readable navigation buttons.
They retain the originating document and group-instance path, never resolve by
name, and select/frame the target without editing history. Closed or deleted
targets are unavailable. Following a reference preserves the explanatory
conversation, but discards old tool results and selection instructions, revokes
old tool permissions, and refreshes the current graph context before the next
shared chat message.

## Optimize an effect without changing its design

Use this workflow when asked to make an existing effect lighter or simplify its
nodes. Separate runtime optimization from graph readability. Node count, file
size, MCP response time, loading/preparation, initial emission, regular Update
and drawing are different costs. A Group can improve readability without
reducing runtime work; a Spawnable Group changes runtime identity/lifetime.

- Read the graph and effective ports, then call
  `libera_graph_analyze_optimization` when available. It compiles the current
  graph and descendants twice and reports actual distribution-export byte
  savings without changing nodes or history. Call from effect scope, not an
  isolated CPU Spawn/Update workspace. A missing tool is a compatibility gap;
  use supported compile information and state the limitation.
- Preserve the user's visual contract: silhouette, timing, density, lifetime,
  seed/particle identity, motion, colour/alpha, material/blend, coordinate space
  and real/scaled clocks. Reducing burst/rate, lifetime, texture quality, noise
  detail or Fluid2D resolution is a quality tradeoff, not an equivalent rewrite.
  Do not introduce that tradeoff without authorization.
- Distribution export already fuses an eligible CPU lifetime-colour chain:
  `Age / Life -> Clamp(0,1) -> 1-x -> Compose RGBA` with constant RGB. Live debug
  retains source instructions. Keep the editable nodes; do not create an
  internal runtime-kernel node or hand-edit version fields. Check the analyzer
  before manually rewriting a chain the compiler already optimizes.
- Fewer source nodes can make execution slower. In particular, merging cheap
  Age/Life reads into a shared output can prevent the single-consumer fusion.
  Share an expensive pure expression only when its inputs, evaluation scope,
  clocks and seed/ID dependencies are truly identical. Do not hoist per-particle
  random/noise, age, velocity or texture samples to one effect-wide value.
- Preserve semantic boundaries: named Game/Instance IO, exposed group pins,
  Signal entries and timeline keys, stateful/custom nodes, Spawn/Update order,
  emitter/draw matrices and procedural resource producers/consumers. An unused
  output is not proof that a node is removable: pure evaluation can raise errors,
  and Signal or resource work may have other effects.
- Inspect literal-only arithmetic, identity copies and duplicate expensive pure
  work as candidates, not automatic deletions. Preserve types and each output
  pin. Do not assume algebraic reassociation is bit-exact; zero, signed zero,
  division by zero and nonfinite intermediates can change behavior. Reconnecting
  constants can also remove an exposed artist control.
- Make one bounded, undoable change at a time with the discovered edit tools.
  `libera_graph_apply_plan` only adds nodes/wires declared in its plan; it cannot
  transactionally rewrite existing nodes. Use exact pin IDs for
  `libera_graph_disconnect_nodes` / `libera_graph_connect_nodes`, re-read and
  compile before deleting a verified redundant node with
  `libera_graph_delete_node`. For GPU IR, use `gpu_read_program` and
  `gpu_apply_plan` with `requireValid=true` when those tools exist, preserving
  attribute writes, flow order and resource hazards.
- Compare the analyzer and compile results before/after, then repeat the Preview
  and evidence checks below on the same backend, inputs, particle count, camera,
  frame/time scale and playback phase. Check initial emission and the tail/Stop,
  not just one still image. If behavior regresses, undo only this operation's
  known edits after checking for intervening user changes.

A successful CPU/GPU export proves compilation, not a timing improvement or
visible equivalence. If runtime timings or frames are unavailable, report the
verified graph/binary change and leave speed/appearance unverified. Report source
node changes separately from compiler optimizations already applied. Core host
pool preparation is a loading/integration concern; reorganizing graph nodes
does not itself prepare an engine pool. Matching Unity/Unreal components expose
loading-time preparation and a shared simulation path; Godot exposes
`prewarm_package`. Check the actual installed SDK and preparation status before
claiming that normal playback used a pool or shared workers.

## Assets and material authoring

Select an explicit connected engine peer using `libera_preview_clients`.
Discover actual assets with `libera_assets_search` and read
`libera_asset_details` before choosing a mesh, texture or material. Engine
capabilities and project asset catalogs differ.

- Do not guess asset paths or treat an absolute filesystem path as a portable
  asset identity. A binding that works in a saved scene may not exist in live
  Preview. Resolve references in the actual target engine project.
- Prefer `libera_render_create_model` / `libera_render_create_sprite` when
  available; they obtain asset metadata without clearing the existing graph.
- Dynamic numeric material pins come from actual material metadata. After an
  assignment, re-read the graph for current pin IDs and types. Use the editor's
  per-slot material browser if the connected MCP schema has no slot-assignment
  operation; do not write internal material-schema JSON by hand.
- Scalar and vector values can be animated through graph connections. Texture
  properties are resources, not interchangeable numeric pins. Portable resource
  mappings must be edited through their supported identity/binding workflow.

## Preview and evidence

LIBERA FX authors effects; a connected engine executes and renders them.
A successful compile, send acknowledgement or graph screenshot does not prove
that a model is visible in the engine viewport.

1. Inspect `libera_preview_clients` and choose the intended peer by engine,
   project and advertised capabilities. If several peers are connected, pass
   `clientId` explicitly. Do not select the first peer arbitrarily.
2. Compile with the appropriate tool when needed: `libera_graph_compile` for
   supported numeric graphs; `libera_graph_compile_spawnable` for Signal,
   lifecycle and Spawnable graphs. Unsupported nodes are errors, not working
   runtime features.
3. Control the shared Timeline with `libera_preview_playback`. Its `frame`
   argument is in frames, not seconds, and is clamped to the existing range.
   Read the returned FPS and range. Set the intended loop range in the Timeline
   UI; for example, a four-second cycle at 60 FPS needs an end frame of 240.
   Do not silently change a user's playback settings or leave playback paused
   after a temporary inspection without explaining it.
4. Read `libera_render_status`. Check the expected node and asset, the current
   program sequence, evaluation time, `rendered` state, material parameter
   values and errors. Allow shaders to finish compiling, but do not wait
   indefinitely or claim that a send acknowledgement means rendering succeeded.
5. If required, use `libera_preview_watch_values` and
   `libera_preview_telemetry` to inspect native evaluated values. Correlate
   program sequence and evaluation time; stale telemetry is not fresh evidence.
   An unscoped watch is not proof of a particular Spawnable runtime instance.
6. Inspect the actual engine viewport or an authorized engine-side capture.
   Render feedback confirms application in the engine, not pixel visibility.
   If viewport inspection is unavailable, state that limitation and ask the user
   to confirm the visible result.

When Preview is blank, check connection and capabilities, compilation errors,
asset/material resolution, shader readiness, transforms and camera framing,
visibility/alpha, effect lifetime and the Timeline range. Verify the live
Preview path itself, not only a separately saved scene with extra asset bindings.

Report what changed, where it was saved and what evidence was obtained.
Distinguish compilation, transport, engine feedback and visual verification.
Never upload user projects, assets or logs to an external service for diagnosis.
