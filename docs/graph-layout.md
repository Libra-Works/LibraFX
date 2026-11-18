# Readable graph layout through MCP

Generated graphs can use a dependency-driven left-to-right layout without
wrapping at the viewport edge. The editor remains a wide pan/zoom canvas.
Frame all and Frame selected change the view, never graph coordinates.

## New nodes

Discover current schemas with `tools/list`. For `libera_graph_apply_plan`,
pass `layout: "flow"` and omit x/y on nodes that may be placed automatically.
All additions and positioning form one Undo step. Only new nodes are arranged;
existing manually positioned nodes stay unchanged and are treated as obstacles.
Both explicit x/y coordinates fix that node's position.

`layout: "compact"` explicitly requests four-column placement, with node-aware
row sizes. The default `manual` plan mode retains the original coordinate-based
contract and requires x/y. `libera_graph_create_node` also accepts `layout:
"flow"`: without coordinates, it appends to the right of the existing graph.
Its default compact mode preserves legacy creation behavior.

## Existing nodes

1. Read the active graph. For a nested Group, navigate into it first.
2. Call `libera_graph_preview_layout` with explicit `nodeIds`. Optional
   `fixedNodeIds` must be a subset of that scope. Optional originX/originY
   support negative coordinates and origins far outside the viewport.
3. Inspect the returned old/new coordinates, deltas, dimensions and warnings.
4. Call `libera_graph_apply_layout` with the returned `previewId`.

Preview does not edit nodes, selection, zoom or history. Apply is one undoable
move and rejects previews after graph edits or node dimension changes. A
preview belongs to one workspace/editing level; previews are session-only and
only a small recent set is retained. At most 256 nodes are arranged per request.

The flow ordering includes both Parameter and Signal connections. Actual
measured heights and port anchors are used when available; conservative
definition-based sizes cover new or off-screen nodes. Expanded Groups are
atomic rectangles: this operation does not rearrange their child graphs.
Node names keep their complete saved values; long headers are ellipsized with
the full name available in a tooltip.

Unselected and explicitly fixed nodes never move. Existing comment frames
remain fixed and are not resized. Review annotation coverage before applying;
connections to fixed or out-of-scope targets may still run backwards.
