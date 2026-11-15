using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace LiberaFx.Services;

public sealed record LiberaFxMcpPlanNode(string Key, string Kind, string Name, double X, double Y,
    bool? IsPolymorphic = null);
public sealed record LiberaFxMcpPlanConnection(
    string SourceKey, string TargetKey, string? TargetPortId = null, string? SourcePortId = null);

[McpServerToolType]
public sealed class LiberaFxMcpTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [McpServerTool(Name = "libera_preview_clients", ReadOnly = true, OpenWorld = false)]
    [Description("List connected preview peers, protocol, explicit engine/role/capabilities and latest telemetry metadata. A role does not imply capabilities. Read full samples with libera_preview_telemetry.")]
    public static string PreviewClients(PreviewTelemetryStore telemetry) =>
        JsonSerializer.Serialize(telemetry.Clients().Select(peer => new
        {
            peer.ClientId, peer.Protocol, peer.Engine, peer.Role, helloVersion = peer.Engine is null ? (int?)null : 1,
            peer.ViewerName, peer.EngineVersion, peer.Platform, peer.RemoteAddress, peer.ConnectedAt, peer.LastReceivedAt,
            peer.Capabilities, peer.ProfilePending, peer.RequestedProfile, peer.WatchedNodes,
            profile = peer.Profile is { } profile ? new
            { profile.ProgramSequence, profile.SnapshotId, profile.Dropped, metricCount = profile.Metrics.Count } : null,
            values = peer.Values is { } values ? new
            { values.ProgramSequence, values.EvaluationTime, valueCount = values.Values.Count } : null
        }), JsonOptions);

    [McpServerTool(Name = "libera_preview_telemetry", ReadOnly = true, OpenWorld = false)]
    [Description("Read complete profile snapshots and latest native values with exact program sequence/evaluation time. Ns and explicitly converted Ms are included. ProfilePending never marks a partial snapshot complete.")]
    public static string PreviewTelemetry(PreviewTelemetryStore telemetry, string? clientId = null)
    {
        try { return JsonSerializer.Serialize(telemetry.Clients(PreviewClientId(clientId)), JsonOptions); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
    }

    [McpServerTool(Name = "libera_preview_profile_control", Destructive = false, OpenWorld = false)]
    [Description("Send native profile capture control to a peer advertising profiling. intervalMs is 100..5000. Omit clientId only when exactly one peer is connected. Success means sent, not acknowledged or a completed capture.")]
    public static async Task<string> PreviewProfileControl(PreviewTelemetryStore telemetry, bool enabled,
        bool nodes = true, bool accessors = false, int intervalMs = 500, string? clientId = null)
    {
        try
        {
            await telemetry.ProfileControlAsync(enabled, nodes, accessors, intervalMs, PreviewClientId(clientId));
            return """{"sent":true}""";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
            OperationCanceledException or System.Net.WebSockets.WebSocketException)
        { throw new McpException(ex is ArgumentException or InvalidOperationException ? ex.Message : "Preview control delivery failed."); }
    }

    [McpServerTool(Name = "libera_preview_watch_values", Destructive = false, OpenWorld = false)]
    [Description("Watch up to 64 distinct node GUIDs on a peer advertising values; an empty list clears watches. Omit clientId only with exactly one connected peer. Success means sent, not acknowledged.")]
    public static async Task<string> PreviewWatchValues(PreviewTelemetryStore telemetry, string[] nodeIds, string? clientId = null)
    {
        try
        {
            if (nodeIds is null || nodeIds.Length > 64) throw new ArgumentException("Watch at most 64 nodes.");
            var ids = nodeIds.Select(value =>
                Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id :
                    throw new ArgumentException("Invalid node GUID.")).ToArray();
            await telemetry.WatchValuesAsync(ids, PreviewClientId(clientId));
            return """{"sent":true}""";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
            OperationCanceledException or System.Net.WebSockets.WebSocketException)
        { throw new McpException(ex is ArgumentException or InvalidOperationException ? ex.Message : "Preview control delivery failed."); }
    }

    private static Guid? PreviewClientId(string? value) => value is null ? null :
        Guid.TryParseExact(value, "D", out var id) ? id : throw new McpException("Invalid clientId GUID.");

    [McpServerTool(Name = "libera_graph_read", ReadOnly = true, OpenWorld = false)]
    [Description("Read the active graph, including stable node IDs, positions, types, ports, and connections.")]
    public static async Task<string> GetGraph(ActiveGraphProvider activeGraph) =>
        await Dispatcher.UIThread.InvokeAsync(
            () => JsonSerializer.Serialize(activeGraph.Graph.CreateCurrentSnapshot(), JsonOptions));

    [McpServerTool(Name = "libera_graph_enter", Destructive = false, OpenWorld = false)]
    [Description("Navigate into an existing editable Graph Group or Spawnable Group child graph. This changes editor navigation only; it creates no runtime instance.")]
    public static async Task<string> EnterGraph(ActiveGraphProvider activeGraph, string nodeId) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var graph = activeGraph.Graph;
            if (!graph.EnterGraph(FindNode(graph, nodeId)))
                throw new McpException("The selected child graph cannot be entered.");
            return JsonSerializer.Serialize(graph.CreateCurrentSnapshot(), JsonOptions);
        });

    [McpServerTool(Name = "libera_graph_up", Destructive = false, OpenWorld = false)]
    [Description("Navigate to the parent editable graph without creating or changing runtime instances.")]
    public static async Task<string> ParentGraph(ActiveGraphProvider activeGraph) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var graph = activeGraph.Graph;
            if (graph.Breadcrumbs.Count < 2) throw new McpException("Already at the root graph.");
            graph.NavigateToGraph(graph.Breadcrumbs.Count - 2);
            return JsonSerializer.Serialize(graph.CreateCurrentSnapshot(), JsonOptions);
        });

    [McpServerTool(Name = "libera_graph_compile", ReadOnly = true, OpenWorld = false)]
    [Description("Compile a numeric graph to VFX bytecode. Graphs containing Spawnable Group must use libera_graph_compile_spawnable so child templates are retained.")]
    public static async Task<string> CompileGraph(ActiveGraphProvider activeGraph)
    {
        var snapshot = await Dispatcher.UIThread.InvokeAsync(() => activeGraph.Graph.CreateCurrentSnapshot());
        try
        {
            var program = VfxProgram.Compile(snapshot);
            return JsonSerializer.Serialize(new
            {
                format = "LFXB", version = program.BinaryVersion, byteOrder = "little-endian",
                workBytes = program.WorkSizeBytes, bytecodeBase64 = Convert.ToBase64String(program.ToBinary())
            }, JsonOptions);
        }
        catch (InvalidDataException ex) { throw new McpException(ex.Message); }
    }

    [McpServerTool(Name = "libera_graph_list_node_types", ReadOnly = true, OpenWorld = false)]
    [Description("List authoring node types and ports. Spawnable Group is one editable child template; receiving a signal creates runtime instances, never editor nodes. Add Spawn Input nodes directly inside its child graph.")]
    public static async Task<string> ListNodeTypes(ActiveGraphProvider activeGraph) =>
        await Dispatcher.UIThread.InvokeAsync(() => JsonSerializer.Serialize(activeGraph.Graph.AvailableNodeDefinitions, JsonOptions));

    [McpServerTool(Name = "libera_graph_create_node", Destructive = false, OpenWorld = false)]
    [Description("Create one editable node in the active graph, including one Spawnable Group container. This never creates a runtime instance. Family base kinds are polymorphic by default.")]
    public static async Task<string> CreateNode(
        ActiveGraphProvider activeGraph,
        [Description("Node kind from libera_graph_list_node_types, for example Float or Signal.")] string kind,
        [Description("Optional horizontal position.")] double? x = null,
        [Description("Optional vertical position.")] double? y = null,
        [Description("Override the family default; false creates an explicitly typed legacy node.")] bool? isPolymorphic = null) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ValidatePosition(x, y);
            var graph = activeGraph.Graph;
            var parsed = ParseKind(kind, graph);
            var node = graph.CreateNode(parsed, x, y, isPolymorphic ?? GraphPolymorphism.TryFamily(parsed, out _));
            return SerializeNode(graph, node.Id);
        });

    [McpServerTool(Name = "libera_graph_compile_spawnable", ReadOnly = true, OpenWorld = false)]
    [Description("Compile an immutable parent LFXB program and separate Spawnable Group template programs, capture-at-spawn input mappings, render configurations and GPU container snapshots. Runtime signals enqueue instances, not editor graph copies. Engine adapters are responsible for consuming lifecycle events; no engine objects are created by this tool.")]
    public static async Task<string> CompileSpawnable(ActiveGraphProvider activeGraph)
    {
        var snapshot = await Dispatcher.UIThread.InvokeAsync(() => activeGraph.Graph.CreateCurrentSnapshot());
        try
        {
            var package = SpawnableGroupPackage.Compile(snapshot);
            return JsonSerializer.Serialize(new
            {
                format = "LiberaFxSpawnablePackage",
                version = SpawnableGroupPackage.FormatVersion,
                bytecodeFormat = "LFXB",
                signalPassthroughTypeId = SpawnableGroupPackage.SignalPassthroughTypeId,
                parentBytecodeBase64 = Convert.ToBase64String(package.ParentBytecode),
                templates = package.Templates.Select(template => new
                {
                    template.ContainerId,
                    template.SignalTypeId,
                    bytecodeBase64 = Convert.ToBase64String(template.Bytecode),
                    capturePolicy = "CaptureAtSpawn",
                    template.Inputs,
                    renderConfigurationBase64 = Convert.ToBase64String(template.RenderConfiguration),
                    template.GpuContainers
                }),
                lifecycle = new { stop = "manual", reset = "manual", instanceIdScope = "pool" }
            }, JsonOptions);
        }
        catch (InvalidDataException ex) { throw new McpException(ex.Message); }
    }

    [McpServerTool(Name = "libera_spawnable_runtime_capabilities", ReadOnly = true, OpenWorld = false)]
    [Description("Describe Spawnable Group runtime identity and capture semantics. Runtime instance value telemetry is not yet exposed to MCP; unscoped preview watches must not be interpreted as instance values.")]
    public static string SpawnableRuntimeCapabilities() => JsonSerializer.Serialize(new
    {
        instanceIdentity = new[] { "containerId", "instanceId" },
        valueIdentity = new[] { "containerId", "templateNodeId", "instanceId" },
        capturePolicy = "CaptureAtSpawn",
        lifecycle = new[] { "spawn", "manualStop", "reset" },
        runtimeValueTelemetryAvailable = false,
        editorNodesAreRuntimeInstances = false
    }, JsonOptions);

    [McpServerTool(Name = "libera_graph_describe_spawnable", ReadOnly = true, OpenWorld = false)]
    [Description("Describe an editable Spawnable Group's synchronized signal and typed capture-at-spawn ports and child template. No runtime instance is created.")]
    public static async Task<string> DescribeSpawnable(ActiveGraphProvider activeGraph, string nodeId) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var group = FindNode(activeGraph.Graph, nodeId);
            if (!group.IsSpawnableGroup || group.ChildGraph is null) throw new McpException("Node is not a Spawnable Group.");
            return JsonSerializer.Serialize(new
            {
                containerId = group.Id,
                capturePolicy = "CaptureAtSpawn",
                signalInput = "spawn",
                ports = group.ChildGraph.Nodes.Where(node => SpawnableGroupSchema.IsInput(node.Kind))
                    .Select(node => new { portId = SpawnableGroupSchema.PortId(node.Id), templateNodeId = node.Id,
                        node.Name, node.PortType }),
                maxInputs = SpawnableGroupSchema.MaxInputs,
                childGraph = group.ChildGraph
            }, JsonOptions);
        });
    [McpServerTool(Name = "libera_graph_connect_nodes", Destructive = false, OpenWorld = false)]
    [Description("Connect the output of the source node to the input of the target node.")]
    public static async Task<string> ConnectNodes(
        ActiveGraphProvider activeGraph,
        [Description("Source node ID.")] string sourceId,
        [Description("Target node ID.")] string targetId,
        [Description("Optional input port ID; defaults to the first input.")] string? targetPortId = null,
        [Description("Output port ID for multi-output nodes (for example BreakVector4.w).")] string? sourcePortId = null) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var graph = activeGraph.Graph;
            var source = FindNode(graph, sourceId);
            var target = FindNode(graph, targetId);
            if (!graph.TryConnectNodes(source, target, out var error, targetPortId, sourcePortId))
                throw new McpException(error ?? "The connection could not be created.");
            var connection = graph.Connections.Last();
            return JsonSerializer.Serialize(new GraphConnectionSnapshot(source.Id, target.Id,
                connection.TargetPortId, connection.SourcePortId), JsonOptions);
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

    [McpServerTool(Name = "libera_graph_set_parameter", Destructive = false, OpenWorld = false)]
    [Description("Set one existing node parameter as a JSON value using the editor's type validation and undo history. Does not replace the graph or change other nodes. Read parameter IDs/types/defaults with libera_graph_list_node_types. For Float, parameterId is value and value can be 7.")]
    public static async Task<string> SetParameter(
        ActiveGraphProvider activeGraph,
        [Description("Existing node GUID.")] string nodeId,
        [Description("Parameter ID from the node definition, for example value.")] string parameterId,
        [Description("Actual JSON value, not JSON encoded as a string: a number, boolean, string, or component array matching the parameter type.")] JsonElement value) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var graph = activeGraph.Graph;
            var node = FindNode(graph, nodeId);
            try { graph.SetParameter(node, parameterId, value); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            { throw new McpException(ex.Message); }
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
                nodes.Select(node =>
                {
                    var kind = ParseKind(node.Kind, activeGraph.Graph);
                    return new LiberaGraphNodeSpec(node.Key, kind, node.Name, node.X, node.Y,
                        IsPolymorphic: node.IsPolymorphic ?? GraphPolymorphism.TryFamily(kind, out _));
                }).ToArray(),
                connections.Select(connection => new LiberaGraphConnectionSpec(
                    connection.SourceKey, connection.TargetKey, connection.TargetPortId, connection.SourcePortId)).ToArray());
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

    private static GraphNodeKind ParseKind(string kind, GraphWorkspaceViewModel graph)
    {
        return GraphNodeDefinition.TryFind(kind, out var definition, graph.AvailableNodeDefinitions)
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
