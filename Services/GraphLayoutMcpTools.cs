using System.ComponentModel;
using System.Text.Json;
using LiberaFx.ViewModels;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace LiberaFx.Services;

[McpServerToolType]
public sealed class GraphLayoutMcpTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "libera_graph_preview_layout", ReadOnly = true, OpenWorld = false)]
    [Description("Preview deterministic flow or compact layout for explicit nodeIds in the active editing level. Does not move nodes, change selection or zoom, or edit history. Flow never wraps at viewport width. Unselected nodes and comment frames remain fixed obstacles. Returns dimensions, old/new positions, deltas, warnings and a short-lived previewId for explicit application.")]
    public static Task<string> Preview(ActiveGraphProvider graphs,
        [Description("GUIDs of nodes to include in this layout preview.")] string[] nodeIds,
        [Description("Optional GUIDs of selected nodes that must remain fixed.")] string[]? fixedNodeIds = null,
        [Description("flow for dependency order, or compact for a grid.")] string mode = "flow",
        [Description("Optional horizontal origin; defaults to the current leftmost node position.")] double? originX = null,
        [Description("Optional vertical origin; defaults to the current topmost node position.")] double? originY = null) =>
        Execute(() => graphs.Graph.PreviewLayout(Ids(nodeIds), fixedNodeIds is null ? null : Ids(fixedNodeIds),
            mode, originX, originY));

    [McpServerTool(Name = "libera_graph_apply_layout", Destructive = false, OpenWorld = false)]
    [Description("Apply a previously inspected layout previewId to the same unchanged active graph as one undoable move. Rejects stale snapshots or changed node dimensions. Does not move unselected/fixed nodes, navigate groups, frame the viewport or zoom.")]
    public static Task<string> Apply(ActiveGraphProvider graphs,
        [Description("GUID returned by libera_graph_preview_layout for the current graph revision.")] string previewId) =>
        Execute(() => graphs.Graph.ApplyLayout(Id(previewId)));

    private static Guid Id(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty
        ? id : throw new ArgumentException("Expected a nonempty GUID.");
    private static Guid[] Ids(string[] values) => values is null
        ? throw new ArgumentException("nodeIds are required.") : values.Select(Id).ToArray();
    private static async Task<string> Execute(Func<GraphLayoutPreview> action) =>
        await ChatMcpExecution.InvokeAsync(() =>
        {
            try { return JsonSerializer.Serialize(action(), JsonOptions); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            { throw new McpException(ex.Message); }
        });
}
