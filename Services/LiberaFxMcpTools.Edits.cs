using System.ComponentModel;
using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.ViewModels;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace LiberaFx.Services;

public sealed record McpCurveKey(double TimeSeconds, double Value, string Interpolation,
    double InSlope = 0, double OutSlope = 0, Guid? Id = null);

public sealed record McpSignalKey(double TimeSeconds, string Mode, Guid? Id = null);

public sealed partial class LiberaFxMcpTools
{
    [McpServerTool(Name = "libera_graph_move_into_group", Destructive = false, OpenWorld = false)]
    [Description("Move existing nodes and their internal wires into a child graph as one undoable edit. IDs are retained. Rejects outside wires, exposed parameters and invalid/locked destinations. This is the same action as dropping nodes onto a group.")]
    public static Task<string> MoveIntoGroup(ActiveGraphProvider activeGraph,
        [Description("Node GUIDs in the current graph.")] string[] nodeIds,
        [Description("Destination group node GUID in the current graph.")] string groupId) =>
        EditGraph(activeGraph, graph =>
        {
            if (nodeIds is null || nodeIds.Length == 0) throw new McpException("Select nodes to move.");
            if (!graph.MoveNodesIntoGraph(nodeIds.Select(id => FindNode(graph, id)), FindNode(graph, groupId)))
                throw new McpException("The nodes cannot be moved into this child graph.");
            return EncodeGraph(graph.CreateCurrentSnapshot()).ToJsonString(JsonOptions);
        });

    [McpServerTool(Name = "libera_graph_set_curve", Destructive = false, OpenWorld = false)]
    [Description("Replace the keys of a FloatFCurve in one undoable edit. Times are seconds, slopes are value per second. Interpolation is per key: Constant, Linear or Cubic. Read back the node for assigned key IDs.")]
    public static Task<string> SetCurve(ActiveGraphProvider activeGraph,
        [Description("FloatFCurve node GUID in the current graph.")] string nodeId,
        [Description("Complete replacement: each key has timeSeconds, value and interpolation; optional inSlope, outSlope and id. Omit id to generate one. Pass [] to clear the keys.")] McpCurveKey[] keys) =>
        EditGraph(activeGraph, graph =>
        {
            if (keys is null || keys.Any(key => key is null))
            {
                throw new McpException("keys must be an array of non-null curve keys.");
            }

            var node = FindNode(graph, nodeId);
            var curve = new FCurveSnapshot(keys.Select(key => new FCurveKey(key.Id ?? Guid.NewGuid(),
                key.TimeSeconds, key.Value, key.InSlope, key.OutSlope,
                ParseChoice<CurveInterpolation>(key.Interpolation, "interpolation"))).ToArray());
            graph.SetCurve(node, curve, LocalizationService.Shared.Get("Timeline.EditCurveKeysHistory"));
            return SerializeNode(graph, node.Id);
        });

    [McpServerTool(Name = "libera_graph_set_signal_keys", Destructive = false, OpenWorld = false)]
    [Description("Replace the timeline keys on a compatible Signal entry node in one undoable edit. Preview keys are editor-only. Runtime keys are relative to the owning scope's start.")]
    public static Task<string> SetSignalKeys(ActiveGraphProvider activeGraph,
        [Description("GUID of a Signal, InputSignal, GroupInputSignal or SpawnInputSignal in the current graph.")] string nodeId,
        [Description("Complete replacement: each key has timeSeconds and mode (Preview or Runtime), plus optional id. Omit id to generate one. Pass [] to clear the keys.")] McpSignalKey[] keys) =>
        EditGraph(activeGraph, graph =>
        {
            if (keys is null || keys.Any(key => key is null))
            {
                throw new McpException("keys must be an array of non-null Signal keys.");
            }

            var node = FindNode(graph, nodeId);
            graph.SetSignalKeys(node, keys.Select(key => new SignalTimelineKey(key.Id ?? Guid.NewGuid(),
                key.TimeSeconds, ParseChoice<SignalTimelineMode>(key.Mode, "mode"))).ToArray(),
                LocalizationService.Shared.Get("Timeline.EditSignalKeysHistory"));
            return SerializeNode(graph, node.Id);
        });

    [McpServerTool(Name = "libera_graph_disconnect_nodes", Destructive = false, OpenWorld = false)]
    [Description("Remove exactly one existing wire without deleting nodes. If more than one wire matches, specify its port IDs. Uses document undo history.")]
    public static Task<string> DisconnectNodes(ActiveGraphProvider activeGraph,
        [Description("Source node GUID.")] string sourceId,
        [Description("Target node GUID.")] string targetId,
        [Description("Optional input port ID from the wire in libera_graph_read. Omit to match any input.")] string? targetPortId = null,
        [Description("Optional output port ID from the wire in libera_graph_read. Omit to match any output.")] string? sourcePortId = null) =>
        EditGraph(activeGraph, graph =>
        {
            var source = FindNode(graph, sourceId);
            var target = FindNode(graph, targetId);
            var matches = graph.Connections.Where(edge => edge.Source == source && edge.Target == target &&
                (targetPortId is null || edge.TargetPortId == targetPortId) &&
                (sourcePortId is null || edge.SourcePortId == sourcePortId)).Take(2).ToArray();
            if (matches.Length != 1)
            {
                throw new McpException(matches.Length == 0 ? "No wire matches those nodes and pins." :
                    "Multiple wires match. Specify the input or output port ID from libera_graph_read.");
            }

            var edge = matches[0];
            graph.DisconnectConnection(edge);
            return JsonSerializer.Serialize(new GraphConnectionSnapshot(source.Id, target.Id,
                edge.TargetPortId, edge.SourcePortId), JsonOptions);
        });

    [McpServerTool(Name = "libera_graph_undo", Destructive = false, OpenWorld = false)]
    [Description("Undo one edit in the active document, including edits made at another graph level. Returns changed=false at the beginning of history.")]
    public static Task<string> UndoGraph(ActiveGraphProvider activeGraph) => ChangeHistory(activeGraph, true);

    [McpServerTool(Name = "libera_graph_redo", Destructive = false, OpenWorld = false)]
    [Description("Redo one edit in the active document using the editor's preferred history branch. Returns changed=false when no redo is available.")]
    public static Task<string> RedoGraph(ActiveGraphProvider activeGraph) => ChangeHistory(activeGraph, false);

    [McpServerTool(Name = "libera_graph_list_modules", ReadOnly = true, OpenWorld = false)]
    [Description("List available standard group modules. Copy an id from this list to libera_graph_insert_module.")]
    public static Task<string> ListModules(ActiveGraphProvider activeGraph) =>
        ChatMcpExecution.InvokeAsync(() =>
        {
            var catalog = activeGraph.Graph.GroupAssets;
            var modules = (catalog?.CreatableAssets ?? []).Where(asset => asset.Origin == GroupAssetOrigin.Standard)
                .OrderBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
                .Select(asset =>
                {
                    var entry = new NodeCatalogEntryViewModel(asset, catalog);
                    return new { asset.Id, asset.Name, displayName = entry.Name, entry.Group, asset.Revision };
                });
            return JsonSerializer.Serialize(modules, JsonOptions);
        });

    [McpServerTool(Name = "libera_graph_insert_module", Destructive = false, OpenWorld = false)]
    [Description("Insert a linked instance of a standard module as one undoable edit. Does not alter the standard asset.")]
    public static Task<string> InsertModule(ActiveGraphProvider activeGraph,
        [Description("Standard module GUID returned by libera_graph_list_modules.")] Guid moduleId,
        [Description("Horizontal graph coordinate.")] double x,
        [Description("Vertical graph coordinate.")] double y) =>
        EditGraph(activeGraph, graph =>
        {
            ValidatePosition(x, y);
            if (graph.GroupAssets?.CreatableAssets.Any(asset => asset.Id == moduleId && asset.Origin == GroupAssetOrigin.Standard) != true)
            {
                throw new McpException("Standard module not found. Use libera_graph_list_modules.");
            }

            return SerializeNode(graph, graph.CreateGroupAssetNode(moduleId, x, y).Id);
        });

    private static Task<string> ChangeHistory(ActiveGraphProvider activeGraph, bool undo) =>
        EditGraph(activeGraph, graph =>
        {
            var history = graph.History;
            var changed = undo ? history.Undo() : history.Redo();
            return JsonSerializer.Serialize(new
            {
                changed, historyNodeId = history.CurrentNodeId, history.CanUndo, history.CanRedo
            }, JsonOptions);
        });

    private static Task<string> EditGraph(ActiveGraphProvider activeGraph, Func<GraphWorkspaceViewModel, string> action) =>
        ChatMcpExecution.InvokeAsync(() =>
        {
            try
            {
                return action(activeGraph.Graph);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                InvalidDataException or IOException or UnauthorizedAccessException)
            {
                throw new McpException(ex.Message);
            }
        });

    private static T ParseChoice<T>(string value, string field) where T : struct, Enum
    {
        var choices = Enum.GetNames<T>();
        var name = choices.FirstOrDefault(name => string.Equals(name, value, StringComparison.OrdinalIgnoreCase));
        return name is not null ? Enum.Parse<T>(name) :
            throw new McpException($"{field} must be one of: {string.Join(", ", choices)}.");
    }
}
