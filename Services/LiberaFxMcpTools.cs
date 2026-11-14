using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using LiberaFx.Models.Graph;
using LiberaFx.ViewModels;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace LiberaFx.Services;

public sealed record LiberaFxMcpPlanNode(string Key, string Kind, string Name, double X, double Y);
public sealed record LiberaFxMcpPlanConnection(string SourceKey, string TargetKey);

[McpServerToolType]
public sealed class LiberaFxMcpTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [McpServerTool(Name = "libera_graph_read", ReadOnly = true, OpenWorld = false)]
    [Description("Read the active graph, including stable node IDs, positions, types, ports, and connections.")]
    public static async Task<string> GetGraph(ActiveGraphProvider activeGraph) =>
        await Dispatcher.UIThread.InvokeAsync(
            () => JsonSerializer.Serialize(activeGraph.Graph.CreateCurrentSnapshot(), JsonOptions));

    [McpServerTool(Name = "libera_graph_list_node_types", ReadOnly = true, OpenWorld = false)]
    [Description("List node types and their compatible port types and categories.")]
    public static async Task<string> ListNodeTypes() =>
        await Dispatcher.UIThread.InvokeAsync(() => JsonSerializer.Serialize(GraphNodeDefinition.All, JsonOptions));

    [McpServerTool(Name = "libera_graph_create_node", Destructive = false, OpenWorld = false)]
    [Description("Create one node in the active graph. Omit x and y to use automatic positions.")]
    public static async Task<string> CreateNode(
        ActiveGraphProvider activeGraph,
        [Description("Node kind from libera_graph_list_node_types, for example Float or Signal.")] string kind,
        [Description("Optional horizontal position.")] double? x = null,
        [Description("Optional vertical position.")] double? y = null) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ValidatePosition(x, y);
            var graph = activeGraph.Graph;
            var node = graph.CreateNode(ParseKind(kind), x, y);
            return SerializeNode(graph, node.Id);
        });

    [McpServerTool(Name = "libera_graph_connect_nodes", Destructive = false, OpenWorld = false)]
    [Description("Connect the output of the source node to the input of the target node.")]
    public static async Task<string> ConnectNodes(
        ActiveGraphProvider activeGraph,
        [Description("Source node ID.")] string sourceId,
        [Description("Target node ID.")] string targetId) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var graph = activeGraph.Graph;
            var source = FindNode(graph, sourceId);
            var target = FindNode(graph, targetId);
            if (!graph.TryConnectNodes(source, target, out var error))
                throw new McpException(error ?? "The connection could not be created.");
            return JsonSerializer.Serialize(new GraphConnectionSnapshot(source.Id, target.Id), JsonOptions);
        });

    [McpServerTool(Name = "libera_graph_rename_node", OpenWorld = false)]
    [Description("Rename a node in the active graph.")]
    public static async Task<string> RenameNode(
        ActiveGraphProvider activeGraph,
        [Description("Node ID.")] string nodeId,
        [Description("New non-empty node name.")] string name) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new McpException("The node name must not be empty.");
            var graph = activeGraph.Graph;
            var node = FindNode(graph, nodeId);
            graph.RenameNode(node, name);
            return SerializeNode(graph, node.Id);
        });

    [McpServerTool(Name = "libera_graph_move_node", OpenWorld = false)]
    [Description("Move a node to an absolute position in the active graph.")]
    public static async Task<string> MoveNode(
        ActiveGraphProvider activeGraph,
        [Description("Node ID.")] string nodeId,
        [Description("Horizontal position.")] double x,
        [Description("Vertical position.")] double y) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ValidatePosition(x, y);
            var graph = activeGraph.Graph;
            var node = FindNode(graph, nodeId);
            graph.MoveNode(node, x, y);
            return SerializeNode(graph, node.Id);
        });

    [McpServerTool(Name = "libera_graph_delete_node", OpenWorld = false)]
    [Description("Delete a node and its connections from the active graph.")]
    public static async Task<string> DeleteNode(
        ActiveGraphProvider activeGraph,
        [Description("Node ID.")] string nodeId) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var graph = activeGraph.Graph;
            var node = FindNode(graph, nodeId);
            var id = node.Id;
            graph.DeleteNode(node);
            return JsonSerializer.Serialize(new { deletedNodeId = id }, JsonOptions);
        });

    [McpServerTool(Name = "libera_graph_apply_plan", Destructive = false, OpenWorld = false)]
    [Description("Add a set of nodes and connections in one undoable edit. Connection keys refer only to nodes in this plan; existing nodes are not changed.")]
    public static async Task<string> ApplyGraphPlan(
        ActiveGraphProvider activeGraph,
        [Description("Nodes with unique keys, a kind from libera_graph_list_node_types, name, and x/y coordinates.")] IReadOnlyList<LiberaFxMcpPlanNode> nodes,
        [Description("Connections between sourceKey and targetKey from this plan; use an empty array for none.")] IReadOnlyList<LiberaFxMcpPlanConnection> connections) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (nodes is null || connections is null)
                throw new McpException("Both nodes and connections arrays are required.");
            if (nodes.Any(node => node is null) || connections.Any(connection => connection is null))
                throw new McpException("Plan arrays must not contain null entries.");

            var plan = new LiberaGraphEditPlan(
                nodes.Select(node => new LiberaGraphNodeSpec(
                    node.Key, ParseKind(node.Kind), node.Name, node.X, node.Y)).ToArray(),
                connections.Select(connection => new LiberaGraphConnectionSpec(
                    connection.SourceKey, connection.TargetKey)).ToArray());
            try
            {
                var graph = activeGraph.Graph;
                return JsonSerializer.Serialize(graph.ApplyPlan(plan), JsonOptions);
            }
            catch (ArgumentException ex)
            {
                throw new McpException(ex.Message);
            }
        });

    private static GraphNodeKind ParseKind(string kind)
    {
        return GraphNodeDefinition.TryFind(kind, out var definition)
            ? definition!.Kind
            : throw new McpException($"Unknown node kind '{kind}'. Use libera_graph_list_node_types for available kinds.");
    }

    private static GraphNodeViewModel FindNode(GraphWorkspaceViewModel graph, string id)
    {
        if (!Guid.TryParse(id, out var parsed))
            throw new McpException($"Invalid node ID '{id}'; expected a GUID.");
        try
        {
            return graph.GetNode(parsed);
        }
        catch (KeyNotFoundException)
        {
            throw new McpException($"Graph node '{id}' was not found.");
        }
    }

    private static void ValidatePosition(double? x, double? y)
    {
        if ((x.HasValue && !double.IsFinite(x.Value)) || (y.HasValue && !double.IsFinite(y.Value)))
            throw new McpException("Node coordinates must be finite numbers.");
    }

    private static string SerializeNode(GraphWorkspaceViewModel graph, Guid id) =>
        JsonSerializer.Serialize(graph.CreateCurrentSnapshot().Nodes.Single(node => node.Id == id), JsonOptions);
}
