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

public sealed record LiberaFxMcpPlanNode(string Key, string Kind, string Name, double? X = null, double? Y = null,
    bool? IsPolymorphic = null);
public sealed record LiberaFxMcpPlanConnection(
    string SourceKey, string TargetKey, string? TargetPortId = null, string? SourcePortId = null);

[McpServerToolType]
public sealed partial class LiberaFxMcpTools
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
    public static string PreviewTelemetry(PreviewTelemetryStore telemetry,
        [Description("Optional peer GUID; omit to read every peer.")] string? clientId = null)
    {
        try { return JsonSerializer.Serialize(telemetry.Clients(PreviewClientId(clientId)), JsonOptions); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
    }

    [McpServerTool(Name = "libera_preview_profile_control", Destructive = false, OpenWorld = false)]
    [Description("Send native profile capture control to a peer advertising profiling. intervalMs is 100..5000. Omit clientId only when exactly one peer is connected. Success means sent, not acknowledged or a completed capture.")]
    public static async Task<string> PreviewProfileControl(PreviewTelemetryStore telemetry,
        [Description("True enables capture and false disables it.")] bool enabled,
        [Description("Record per-node timing.")] bool nodes = true,
        [Description("Record native accessor timing.")] bool accessors = false,
        [Description("Minimum interval between snapshots, 100–5000 milliseconds.")] int intervalMs = 500,
        [Description("Peer GUID; omit only if exactly one peer is connected.")] string? clientId = null)
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
    public static async Task<string> PreviewWatchValues(PreviewTelemetryStore telemetry,
        [Description("Up to 64 node GUIDs, or an empty array to clear watches.")] string[] nodeIds,
        [Description("Peer GUID; omit only if exactly one peer is connected.")] string? clientId = null)
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
    [Description("Read the active graph, including stable node IDs, typeName, positions, parameters and connections. Use libera_graph_status for document, graph path and diagnostics. Each Signal output has at most one destination; Signal inputs execute on every arrival, without synchronization. Sequence executes outputs top to bottom, without waiting for Delay or Spawnable completion.")]
    public static async Task<string> GetGraph(ActiveGraphProvider activeGraph) =>
        await ChatMcpExecution.InvokeAsync(
            () => EncodeGraph(activeGraph.Graph.CreateCurrentSnapshot()).ToJsonString(JsonOptions));

    [McpServerTool(Name = "libera_graph_enter", Destructive = false, OpenWorld = false)]
    [Description("Navigate into an existing editable Graph Group or Spawnable Group child graph. This changes editor navigation only; it creates no runtime instance.")]
    public static async Task<string> EnterGraph(ActiveGraphProvider activeGraph,
        [Description("Group or Spawnable Group node GUID in the current graph.")] string nodeId) =>
        await ChatMcpExecution.InvokeAsync(() =>
        {
            var graph = activeGraph.Graph;
            if (!graph.EnterGraph(FindNode(graph, nodeId)))
                throw new McpException("The selected child graph cannot be entered.");
            return EncodeGraph(graph.CreateCurrentSnapshot()).ToJsonString(JsonOptions);
        });

    [McpServerTool(Name = "libera_graph_up", Destructive = false, OpenWorld = false)]
    [Description("Navigate to the parent editable graph without creating or changing runtime instances.")]
    public static async Task<string> ParentGraph(ActiveGraphProvider activeGraph) =>
        await ChatMcpExecution.InvokeAsync(() =>
        {
            var graph = activeGraph.Graph;
            if (graph.Breadcrumbs.Count < 2) throw new McpException("Already at the root graph.");
            graph.NavigateToGraph(graph.Breadcrumbs.Count - 2);
            return EncodeGraph(graph.CreateCurrentSnapshot()).ToJsonString(JsonOptions);
        });

    [McpServerTool(Name = "libera_graph_compile", ReadOnly = true, OpenWorld = false)]
    [Description("Compile a numeric graph to VFX bytecode. Graphs containing Signal, lifecycle, Spawn boundaries, or Spawnable Group must use libera_graph_compile_spawnable.")]
    public static async Task<string> CompileGraph(ActiveGraphProvider activeGraph)
    {
        var snapshot = await ChatMcpExecution.InvokeAsync(() => activeGraph.Graph.CreateCurrentSnapshot());
        try
        {
            var program = VfxProgram.Compile(snapshot);
            return JsonSerializer.Serialize(new
            {
                format = "LFXB", version = program.BinaryVersion, byteOrder = "little-endian",
                gameIoPorts = program.GameIoPorts,
                workBytes = program.WorkSizeBytes, bytecodeBase64 = Convert.ToBase64String(program.ToBinary())
            }, JsonOptions);
        }
        catch (InvalidDataException ex) { throw new McpException(ex.Message); }
    }

    [McpServerTool(Name = "libera_graph_create_node", Destructive = false, OpenWorld = false)]
    [Description("Create one editable node. Default layout='flow' appends to the right without viewport wrapping when coordinates are omitted; compact or manual preserves legacy/explicit placement. Explicit coordinates are always retained. For connected batches prefer apply_plan with layout='flow'. This never creates a runtime instance.")]
    public static async Task<string> CreateNode(
        ActiveGraphProvider activeGraph,
        [Description("Copy the exact kind field from libera_graph_list_node_types (either a name like Float or a UUID). The qualifiedName is also accepted; typeName alone is not a general identifier.")] string kind,
        [Description("Optional horizontal position.")] double? x = null,
        [Description("Optional vertical position.")] double? y = null,
        [Description("Override the family default; false creates an explicitly typed legacy node.")] bool? isPolymorphic = null,
        [Description("flow (default), compact, or manual.")] string layout = "flow") =>
        await ChatMcpExecution.InvokeAsync(() =>
        {
            ValidatePosition(x, y);
            var graph = activeGraph.Graph;
            GraphLayoutMode mode;
            try { mode = GraphFlowLayout.ParseMode(layout); }
            catch (ArgumentException ex) { throw new McpException(ex.Message); }
            if (mode == GraphLayoutMode.Flow && x is null && y is null)
            {
                x = graph.Nodes.Select(node => GraphFlowLayout.Bounds(node).Right + 160).DefaultIfEmpty(80).Max();
                y = graph.Nodes.Select(node => node.Y).DefaultIfEmpty(80).Min();
            }
            var parsed = ParseKind(kind, graph);
            var node = graph.CreateNode(parsed, x, y, isPolymorphic ?? GraphPolymorphism.TryFamily(parsed, out _));
            return SerializeNode(graph, node.Id);
        });

    [McpServerTool(Name = "libera_graph_compile_spawnable", ReadOnly = true, OpenWorld = false)]
    [Description("Compile Signal or Spawnable graphs to an immutable parent LFXB program and separate Spawnable template programs, per-pin CaptureAtSpawn/Live mappings, render configurations, GPU snapshots and executable GPU payloads. This also supports Signal graphs without Spawnable Groups. Signals connected to a Spawnable enqueue runtime instances, not editor graph copies. Engine adapters consume lifecycle events; no engine objects are created by this tool.")]
    public static async Task<string> CompileSpawnable(ActiveGraphProvider activeGraph)
    {
        var snapshot = await ChatMcpExecution.InvokeAsync(() =>
            activeGraph.Graph.CreateCurrentSnapshot() with
            { AutoDestroyWhenIdle = activeGraph.Graph.AutoDestroyWhenIdle });
        try
        {
            var package = SpawnableGroupPackage.Compile(snapshot);
            return JsonSerializer.Serialize(new
            {
                format = "LiberaFxSpawnablePackage",
                version = package.Version,
                bytecodeFormat = "LFXB",
                signalPassthroughTypeId = SpawnableGroupPackage.SignalPassthroughTypeId,
                package.AutoDestroyWhenIdle,
                gameIoPorts = package.GameIoPorts,
                parentBytecodeBase64 = Convert.ToBase64String(package.ParentBytecode),
                rootRenderConfigurationBase64 = Convert.ToBase64String(package.RootRenderConfiguration),
                rootGpuProgramsBase64 = Convert.ToBase64String(package.RootGpuPrograms),
                cpuProgramsBase64 = Convert.ToBase64String(package.CpuPrograms),
                package.RootGpuContainers,
                package.OnStartNodeId,
                package.OnDestroyNodeId,
                package.HasDeferredSignals,
                package.HasChildDeferredSignals,
                package.HasLiveInputs,
                package.DestroyRoutes,
                package.SignalRoutes,
                templates = package.Templates.Select(template => new
                {
                    template.ContainerId,
                    template.SignalTypeId,
                    bytecodeBase64 = Convert.ToBase64String(template.Bytecode),
                    gpuProgramsBase64 = Convert.ToBase64String(template.GpuPrograms),
                    cpuProgramsBase64 = Convert.ToBase64String(template.CpuPrograms),
                    capturePolicy = template.Inputs.Any(input => input.IsLive) ? "PerPin" : "CaptureAtSpawn",
                    template.Inputs,
                    template.CapturedInputs,
                    template.LiveInputs,
                    template.Outputs,
                    template.SignalInputs,
                    template.SignalOutputs,
                    template.Lifecycle,
                    renderConfigurationBase64 = Convert.ToBase64String(template.RenderConfiguration),
                    template.GpuContainers
                }),
                lifecycle = new { life = "valid only inside a Spawnable Group; absent means that instance is infinite",
                    root = "no Life Time; AutoDestroyWhenIdle ends after root-local work drains when Spawnables exist; otherwise the host must End",
                    destroy = "natural expiry only; frozen outputs routed before finalize",
                    stop = "no Destroy", reset = "no Destroy", instanceIdScope = "pool" }
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
        capturePolicy = "PerPin",
        defaultCapturePolicy = "CaptureAtSpawn",
        supportedCapturePolicies = new[] { "CaptureAtSpawn", "Live" },
        lifecycle = new[] { "onStartOnce", "spawn", "capturedLifeTime", "naturalDestroy", "manualStop", "reset" },
        localDestroyRouting = "root sibling Spawnables directly or via one Delay; frozen child outputs captured before finalize",
        localSignalRouting = "incoming local signals broadcast to active child instances; Spawn creates a new instance and natural Destroy remains per-instance; output signals preserve their source instance",
        deferredSignals = "Signal Delay uses the owning root or Spawnable local time; pending continuations retain the owner past Life Time",
        engineLifecyclePreviewAvailable = false,
        editorNativeLifecyclePreviewAvailable = true,
        previewExecutionOwner = "editor-native (requires matching LIBERAFX_PREVIEW_NATIVE_LIBRARY); UE renders evaluated defaults only",
        runtimeValueTelemetryAvailable = false,
        editorNodesAreRuntimeInstances = false
    }, JsonOptions);

    [McpServerTool(Name = "libera_graph_describe_runtime_outputs", ReadOnly = true, OpenWorld = false)]
    [Description("Describe deterministic runtime value bindings for an authoring node's output pins. A Matrix maps to four FloatX4 rows, never one four-component value. Use runtimeNodeId with value watching and sourceComponent for scalar Break outputs. This does not evaluate the graph or create runtime instances.")]
    public static async Task<string> DescribeRuntimeOutputs(ActiveGraphProvider activeGraph,
        [Description("GUID of the node whose runtime outputs should be described.")] string nodeId) =>
        await ChatMcpExecution.InvokeAsync(() =>
        {
            var workspace = activeGraph.Graph;
            var node = FindNode(workspace, nodeId);
            var snapshot = workspace.CreateCurrentSnapshot();
            GraphValidator.Validate(snapshot);
            var problems = GraphTypeDiagnostics.Analyze(snapshot);
            if (problems.Count != 0) throw new McpException("Fix graph type mismatches before resolving runtime outputs.");
            if (node.Definition.Kind == GraphNodeKind.GraphGroup && node.ChildGraph is { } child)
            {
                var outputs = child.Nodes.Where(item => GroupBoundarySchema.IsOutput(item.Kind) &&
                    item.PortCategory == GraphPortCategory.Parameter).ToArray();
                if (outputs.Length == 0) throw new McpException("This group has no numeric output pins.");
                var matrixOutputs = outputs.Any(item => item.PortType == "Matrix")
                    ? GraphPolymorphism.LowerWithOutputs(GroupBoundaryLowering.LowerGroupContents(snapshot)) : null;
                return JsonSerializer.Serialize(new
                {
                    authoringNodeId = node.Id,
                    outputs = outputs.Select(item => new
                    {
                        portId = GroupBoundarySchema.OutputPortId(item.Id),
                        type = item.PortType,
                        bindings = item.PortType == "Matrix"
                            ? matrixOutputs!.Outputs[(item.Id, "value")].Select((binding, index) =>
                                new { row = (int?)index, runtimeNodeId = binding.NodeId,
                                    runtimePortId = binding.PortId, sourceComponent = 0 }).ToArray()
                            : new[] { new { row = (int?)null, runtimeNodeId = item.Id,
                                runtimePortId = (string?)null, sourceComponent = 0 } }
                    })
                }, JsonOptions);
            }
            var result = GraphPolymorphism.LowerWithOutputs(GroupBoundaryLowering.LowerGroupContents(snapshot));
            var lowered = result.Graph.Nodes.ToDictionary(item => item.Id);
            var definition = GraphPolymorphism.Resolve(snapshot)[node.Id];
            var ports = definition.OutputPorts?.Select(port => (string?)port.Id).ToArray() ?? [null];
            if (ports.Any(port => definition.GetOutputPortType(port) is not ("Float" or "FloatX2" or "FloatX3" or "FloatX4" or "Color" or "Matrix")))
                throw new McpException("Runtime value bindings are available only for numeric and Matrix output pins.");
            return JsonSerializer.Serialize(new
            {
                authoringNodeId = node.Id,
                outputs = ports.Select(port => new
                {
                    portId = port,
                    type = definition.GetOutputPortType(port),
                    bindings = result.Outputs[(node.Id,
                        GroupBoundarySchema.IsInput(node.Definition.Kind) && node.PortType == "Matrix" ? "value" : port)].Select((binding, index) =>
                    {
                        var runtime = GraphNodeDefinition.All.Single(item => item.Kind == lowered[binding.NodeId].Kind);
                        var component = runtime.TypeName.StartsWith("BreakVector", StringComparison.Ordinal)
                            ? runtime.OutputPorts!.ToList().FindIndex(output => output.Id == binding.PortId) : 0;
                        return new { row = definition.GetOutputPortType(port) == "Matrix" ? (int?)index : null,
                            runtimeNodeId = binding.NodeId, runtimePortId = binding.PortId, sourceComponent = component };
                    })
                })
            }, JsonOptions);
        });

    [McpServerTool(Name = "libera_graph_describe_spawnable", ReadOnly = true, OpenWorld = false)]
    [Description("Describe an editable Spawnable Group's synchronized signal and typed capture-at-spawn ports and child template. No runtime instance is created.")]
    public static async Task<string> DescribeSpawnable(ActiveGraphProvider activeGraph,
        [Description("Spawnable Group node GUID.")] string nodeId) =>
        await ChatMcpExecution.InvokeAsync(() =>
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
                maxInputLanes = SpawnableGroupSchema.MaxInputLanes,
                childGraph = group.ChildGraph
            }, JsonOptions);
        });

    [McpServerTool(Name = "libera_graph_describe_group", ReadOnly = true, OpenWorld = false)]
    [Description("Describe an ordinary Group's scoped Property and Signal pins. Port IDs are stable within this instance and are used by libera_graph_connect_nodes. Name is the boundary node's authored name; displayName is the label shown on the group pin.")]
    public static async Task<string> DescribeGroup(ActiveGraphProvider activeGraph,
        [Description("Group node GUID in the current graph.")] string nodeId) =>
        await ChatMcpExecution.InvokeAsync(() =>
        {
            var group = FindNode(activeGraph.Graph, nodeId);
            if (group.Definition.Kind != GraphNodeKind.GraphGroup || group.ChildGraph is null)
                throw new McpException("Node is not an ordinary Graph Group.");
            return JsonSerializer.Serialize(new
            {
                containerId = group.Id,
                inputs = group.ChildGraph.Nodes.Where(node => GroupBoundarySchema.IsInput(node.Kind))
                    .Select(node => new { portId = GroupBoundarySchema.InputPortId(node.Id),
                        boundaryNodeId = node.Id, node.Name,
                        displayName = group.InputPorts.Single(port => port.Id == GroupBoundarySchema.InputPortId(node.Id)).Name,
                        node.PortType, node.PortCategory }),
                outputs = group.ChildGraph.Nodes.Where(node => GroupBoundarySchema.IsOutput(node.Kind))
                    .Select(node => new { portId = GroupBoundarySchema.OutputPortId(node.Id),
                        boundaryNodeId = node.Id, node.Name,
                        displayName = group.OutputPorts.Single(port => port.Id == GroupBoundarySchema.OutputPortId(node.Id)).Name,
                        node.PortType, node.PortCategory })
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
        await ChatMcpExecution.InvokeAsync(() =>
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
        await ChatMcpExecution.InvokeAsync(() =>
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
        await ChatMcpExecution.InvokeAsync(() =>
        {
            ValidatePosition(x, y);
            var graph = activeGraph.Graph;
            var node = FindNode(graph, nodeId);
            graph.MoveNode(node, x, y);
            return SerializeNode(graph, node.Id);
        });

    [McpServerTool(Name = "libera_graph_set_parameter", Destructive = false, OpenWorld = false)]
    [Description("Set one existing node parameter as a JSON value using undo history. Read IDs/types/defaults with libera_graph_list_node_types using summaryOnly=false. Use libera_graph_list_enums for enum values. For Sequence, parameterId='outputs', value=1..32; shrinking removes trailing pins and their connections in the same undo step. Sequence output IDs then_0, then_1, etc. remain stable.")]
    public static async Task<string> SetParameter(
        ActiveGraphProvider activeGraph,
        [Description("Existing node GUID.")] string nodeId,
        [Description("Parameter ID from the node definition, for example value.")] string parameterId,
        [Description("Actual JSON value, not JSON encoded as a string: a number, boolean, string, or component array matching the parameter type.")] JsonElement value) =>
        await ChatMcpExecution.InvokeAsync(() =>
        {
            var graph = activeGraph.Graph;
            var node = FindNode(graph, nodeId);
            try { graph.SetParameter(node, parameterId, value); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException)
            { throw new McpException(ex.Message); }
            return SerializeNode(graph, node.Id);
        });

    [McpServerTool(Name = "libera_graph_expose_parameter", Destructive = false, OpenWorld = false)]
    [Description("Expose a direct child node parameter on its containing group. The active graph must be inside an editable group. Uses the same validation and undo history as the inspector.")]
    public static async Task<string> ExposeParameter(ActiveGraphProvider activeGraph,
        [Description("GUID of a direct child node in the group being edited.")] string nodeId,
        [Description("Parameter ID from the node's parameters.")] string parameterId) =>
        await ChatMcpExecution.InvokeAsync(() =>
        {
            var graph = activeGraph.Graph;
            try
            {
                var id = graph.PublishGroupParameter(FindNode(graph, nodeId), parameterId);
                return JsonSerializer.Serialize(new { exposedItemId = id }, JsonOptions);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                       InvalidOperationException or ArgumentException)
            { throw new McpException(ex.Message); }
        });

    [McpServerTool(Name = "libera_graph_expose_curves", Destructive = false, OpenWorld = false)]
    [Description("Expose one to four Float F-Curves in component order on the containing group. The active graph must be inside an editable group.")]
    public static async Task<string> ExposeCurves(ActiveGraphProvider activeGraph,
        [Description("One to four FloatFCurve GUIDs in X/Y/Z/W order.")] string[] nodeIds) =>
        await ChatMcpExecution.InvokeAsync(() =>
        {
            var graph = activeGraph.Graph;
            try
            {
                if (nodeIds is null || nodeIds.Length is < 1 or > 4)
                {
                    throw new ArgumentException("Supply one to four curve node IDs.");
                }
                var id = graph.PublishGroupCurves(nodeIds.Select(nodeId => FindNode(graph, nodeId)).ToArray());
                return JsonSerializer.Serialize(new { exposedItemId = id }, JsonOptions);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                       InvalidOperationException or ArgumentException)
            { throw new McpException(ex.Message); }
        });

    [McpServerTool(Name = "libera_graph_set_group_instance_value", Destructive = false, OpenWorld = false)]
    [Description("Set an exposed group item on this instance without changing the shared group. Pass the item GUID and a JSON parameter value, one F-Curve object, or a component-ordered array of F-Curve objects.")]
    public static async Task<string> SetGroupInstanceValue(
        ActiveGraphProvider activeGraph,
        [Description("GUID of the group instance in the active graph.")] string nodeId,
        [Description("Stable exposed-item GUID from the child graph's exposedItems.")] string exposedItemId,
        [Description("JSON parameter value, an object with a keys array for a curve, or a matching array for vector and color curves.")] JsonElement value) =>
        await ChatMcpExecution.InvokeAsync(() =>
        {
            var graph = activeGraph.Graph;
            var node = FindNode(graph, nodeId);
            try
            {
                if (!Guid.TryParse(exposedItemId, out var itemId) || itemId == Guid.Empty)
                    throw new ArgumentException("Invalid exposed item GUID.");
                var item = node.ChildGraph?.ExposedItems?.SingleOrDefault(candidate => candidate.Id == itemId)
                    ?? throw new InvalidOperationException("The exposed item no longer exists.");
                GroupInstanceComponent[] components = item.Editor switch
                {
                    "parameter" => [new(value.Clone())],
                    "curve" => [new(Curve: JsonSerializer.Deserialize<FCurveSnapshot>(value.GetRawText(), JsonOptions)
                        ?? throw new ArgumentException("An F-Curve is required."))],
                    "vector2Curve" or "vector3Curve" or "vector4Curve" or "colorCurve" =>
                        (JsonSerializer.Deserialize<FCurveSnapshot[]>(value.GetRawText(), JsonOptions)
                        ?? throw new ArgumentException("An array of F-Curves is required."))
                        .Select(curve => new GroupInstanceComponent(Curve: curve)).ToArray(),
                    _ => throw new InvalidOperationException("The exposed editor type is unsupported.")
                };
                graph.SetGroupInstanceValue(node, new(itemId, components));
                return SerializeNode(graph, node.Id);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or ArgumentException)
            { throw new McpException(ex.Message); }
        });

    [McpServerTool(Name = "libera_graph_configure_group_item", Destructive = false, OpenWorld = false)]
    [Description("Configure an exposed item on an editable group. Supply the complete item from the graph snapshot; its ID, editor type and targets cannot be changed. Uses the same validation, asset lock and undo history as the inspector.")]
    public static async Task<string> ConfigureGroupItem(
        ActiveGraphProvider activeGraph,
        [Description("Group node GUID in the current graph.")] string nodeId,
        [Description("Complete exposed item from childGraph.exposedItems; keep the id, editor and targets.")] GroupExposedItem item) =>
        await ChatMcpExecution.InvokeAsync(() =>
        {
            var graph = activeGraph.Graph;
            var node = FindNode(graph, nodeId);
            try { graph.UpdateGroupExposedItem(node, item); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                       InvalidOperationException or ArgumentException)
            { throw new McpException(ex.Message); }
            return SerializeNode(graph, node.Id);
        });

    [McpServerTool(Name = "libera_graph_reset_group_instance_value", Destructive = false, OpenWorld = false)]
    [Description("Remove one exposed-item instance override, restoring the shared group default. Uses the editor's undo history.")]
    public static async Task<string> ResetGroupInstanceValue(ActiveGraphProvider activeGraph,
        [Description("Group instance GUID in the current graph.")] string nodeId,
        [Description("Stable exposed-item GUID from the child graph's exposedItems.")] string exposedItemId) =>
        await ChatMcpExecution.InvokeAsync(() =>
        {
            var graph = activeGraph.Graph;
            var node = FindNode(graph, nodeId);
            if (!Guid.TryParse(exposedItemId, out var itemId) || itemId == Guid.Empty)
                throw new McpException("Invalid exposed item GUID.");
            try { graph.ResetGroupInstanceValue(node, itemId); }
            catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
            return SerializeNode(graph, node.Id);
        });

    [McpServerTool(Name = "libera_graph_delete_node", OpenWorld = false)]
    [Description("Delete a node and its connections from the active graph.")]
    public static async Task<string> DeleteNode(
        ActiveGraphProvider activeGraph,
        [Description("Node ID.")] string nodeId) =>
        await ChatMcpExecution.InvokeAsync(() =>
        {
            var graph = activeGraph.Graph;
            var node = FindNode(graph, nodeId);
            var id = node.Id;
            graph.DeleteNode(node);
            return JsonSerializer.Serialize(new { deletedNodeId = id }, JsonOptions);
        });

    [McpServerTool(Name = "libera_graph_apply_plan", Destructive = false, OpenWorld = false)]
    [Description("Add nodes and connections in one undoable edit. layout='flow' uses dependency order, node dimensions and ports without viewport wrapping; omit x/y for automatic positions. Explicit x/y pairs are fixed. compact is an explicit four-column alternative. Default manual requires x/y and preserves existing behavior. Existing nodes never move; connection keys refer only to this plan.")]
    public static async Task<string> ApplyGraphPlan(
        ActiveGraphProvider activeGraph,
        [Description("Nodes with unique keys, kind and name. Omit x/y for automatic flow/compact layout; provide both to fix a position.")] IReadOnlyList<LiberaFxMcpPlanNode> nodes,
        [Description("Connections between sourceKey and targetKey from this plan; use an empty array for none.")] IReadOnlyList<LiberaFxMcpPlanConnection> connections,
        [Description("manual, flow, or compact. Manual requires an explicit x/y pair for every node.")] string layout = "manual") =>
        await ChatMcpExecution.InvokeAsync(() =>
        {
            if (nodes is null || connections is null)
                throw new McpException("Both nodes and connections arrays are required.");
            if (nodes.Any(node => node is null) || connections.Any(connection => connection is null))
                throw new McpException("Plan arrays must not contain null entries.");

            GraphLayoutMode mode;
            try { mode = GraphFlowLayout.ParseMode(layout); }
            catch (ArgumentException ex) { throw new McpException(ex.Message); }
            var plan = new LiberaGraphEditPlan(
                nodes.Select(node =>
                {
                    var kind = ParseKind(node.Kind, activeGraph.Graph);
                    if (node.X.HasValue != node.Y.HasValue ||
                        mode == GraphLayoutMode.Manual && node.X is null)
                        throw new McpException("Provide both x/y, or omit both with layout='flow' or 'compact'.");
                    return new LiberaGraphNodeSpec(node.Key, kind, node.Name, node.X ?? 0, node.Y ?? 0,
                        IsPolymorphic: node.IsPolymorphic ?? GraphPolymorphism.TryFamily(kind, out _),
                        IsPositionFixed: node.X is not null);
                }).ToArray(),
                connections.Select(connection => new LiberaGraphConnectionSpec(
                    connection.SourceKey, connection.TargetKey, connection.TargetPortId, connection.SourcePortId)).ToArray(), mode);
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
        EncodeNode(graph.CreateCurrentSnapshot().Nodes.Single(node => node.Id == id)).ToJsonString(JsonOptions);
}
