using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Resources;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Gpu;

namespace LiberaFx.Services.Compilation;

public sealed record SpawnableInput(string PortId, Guid TemplateNodeId, string PortType, int Width, bool IsColor, bool IsLive = false);
public sealed record SpawnableOutput(string PortId, Guid TemplateNodeId, string PortType);
public sealed record SpawnableSignalPort(string PortId, Guid TemplateNodeId, Guid ProxyNodeId = default);
public sealed record SpawnableSignalRoute(Guid SourceContainerId, Guid SourceNodeId, Guid TargetNodeId);
public sealed record SpawnableLifecycle(Guid? OnStartNodeId, Guid? LifeNodeId, Guid? OwnerDestroyNodeId = null);
public sealed record SpawnableRouteCapture(Guid TargetNodeId, int Width, Guid? SourceContainerId,
    Guid SourceNodeId, int SourceComponent);
public sealed record SpawnableDestroyRoute(Guid SourceContainerId, Guid TargetContainerId,
    IReadOnlyList<SpawnableRouteCapture> Captures, Guid? DelayNodeId = null);
public sealed record SpawnableTemplate(Guid ContainerId, Guid SignalTypeId, byte[] Bytecode,
    IReadOnlyList<SpawnableInput> Inputs, byte[] RenderConfiguration, IReadOnlyList<GraphNodeSnapshot> GpuContainers,
    SpawnableLifecycle? Lifecycle = null, IReadOnlyList<SpawnableOutput>? Outputs = null,
    IReadOnlyList<SpawnableRouteCapture>? LiveInputs = null,
    IReadOnlyList<SpawnableRouteCapture>? CapturedInputs = null,
    IReadOnlyList<SpawnableSignalPort>? SignalInputs = null,
    IReadOnlyList<SpawnableSignalPort>? SignalOutputs = null)
{
    public byte[] GpuPrograms { get; init; } = [];
    public Guid? ParentContainerId { get; init; }
    public byte[] CpuPrograms { get; init; } = [];
    public bool UsesExtendedCaptureInputs => Inputs.Count > 4 || Inputs.Sum(input => input.Width) > 16;
}
public sealed record SpawnableGroupPackage(byte[] ParentBytecode, IReadOnlyList<SpawnableTemplate> Templates)
{
    public const int FormatVersion = 13;
    public int Version => FormatVersion;
    public byte[] CpuPrograms { get; init; } = [];
    public bool AutoDestroyWhenIdle { get; init; } = true;
    public bool HasLiveInputs => Templates.Any(template => template.Inputs.Any(input => input.IsLive));
    public bool HasSignalPorts => Templates.Any(template =>
        template.SignalInputs is { Count: > 0 } || template.SignalOutputs is { Count: > 0 });
    public bool HasDeferredSignals { get; init; }
    public bool HasChildDeferredSignals { get; init; }
    public Guid? OnStartNodeId { get; init; }
    public Guid? OnDestroyNodeId { get; init; }
    public IReadOnlyList<SpawnableDestroyRoute> DestroyRoutes { get; init; } = [];
    public IReadOnlyList<SpawnableSignalRoute> SignalRoutes { get; init; } = [];
    public IReadOnlyList<GameIoPort> GameIoPorts { get; init; } = [];
    public byte[] RootRenderConfiguration { get; init; } = [];
    public IReadOnlyList<GraphNodeSnapshot> RootGpuContainers { get; init; } = [];
    public byte[] RootGpuPrograms { get; init; } = [];
    public static readonly Guid SignalPassthroughTypeId = Guid.Parse("989fa5b5-485f-4e75-8542-9c5500000001");

    public static Guid SignalTypeId(Guid containerId, GraphSnapshot child)
    {
        var shape = string.Join(";", child.Nodes.Where(node => SpawnableGroupSchema.IsInput(node.Kind) ||
                SpawnableGroupSchema.IsSignalInput(node.Kind))
            .Select(node => $"{node.Id:D}:{node.PortType}"));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"LiberaFx.SpawnableGroup.Signal.v1:{containerId:D}:{shape}"));
        return new Guid(digest.AsSpan(0, 16), bigEndian: true);
    }

    public static Guid SignalInputTypeId(Guid containerId, Guid inputNodeId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"LiberaFx.SpawnableGroup.SignalInput.v1:{containerId:D}:{inputNodeId:D}"));
        return new Guid(digest.AsSpan(0, 16), bigEndian: true);
    }

    public static Guid SignalInputProxyNodeId(Guid containerId, Guid inputNodeId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"LiberaFx.SpawnableGroup.SignalInputProxy.v1:{containerId:D}:{inputNodeId:D}"));
        return new Guid(digest.AsSpan(0, 16), bigEndian: true);
    }

    public static SpawnableGroupPackage Compile(GraphSnapshot graph, bool includePreviewSignalKeys = false,
        IRenderResourceBindingContext? resourceContext = null, VfxCompileOptions? options = null)
    {
        VfxProgram.ValidateForCompilation(graph);
        graph = RerouteLowering.Lower(graph);
        CpuParticleSchema.ValidateContext(graph);
        if (options?.IncludePreviewDebugDraw != true) graph = PreviewDebugDraw.Strip(graph);
        RejectTemplateGameIo(graph, withinTemplate: false);
        VfxProgram.ValidateForCompilation(graph);
        graph = GroupExposedItems.Materialize(graph);
        VfxProgram.ValidateForCompilation(graph);
        graph = SignalTimelineLowering.Lower(graph, includePreviewSignalKeys);
        VfxProgram.ValidateForCompilation(graph);
        GraphTypeDiagnostics.Validate(graph);
        graph = ProceduralLowering.Lower(graph);
        graph = CpuParticleCompilation.LowerEmitters(graph);
        var templates = new List<SpawnableTemplate>();
        var destroyRoutes = new List<SpawnableDestroyRoute>();
        var signalRoutes = new List<SpawnableSignalRoute>();
        var hasDeferredSignals = false;
        var hasChildDeferredSignals = false;

        ScopeBuild BuildScope(GraphSnapshot source, Guid? ownerContainerId, IRenderResourceBindingContext? scopeResources)
        {
            ValidateScope(source, ownerContainerId);
            var cpuSource = source;
            var cpu = CpuParticleCompilation.Lower(source, scopeResources, options);
            var scope = cpu.Graph;
            var flat = GroupBoundaryLowering.Lower(scope);
            if (flat.Nodes.Count(node => node.Kind.Value == "LifeTime") > 1)
                throw new InvalidDataException("An execution scope can contain at most one Life Time.");

            var routes = CreateRoutes(flat);
            destroyRoutes.AddRange(routes);
            var routedDelays = routes.Where(route => route.DelayNodeId is not null)
                .Select(route => route.DelayNodeId!.Value).ToHashSet();
            var delays = flat.Nodes.Where(node => node.Kind.Value == "Delay").ToArray();
            if (ownerContainerId is null)
                hasDeferredSignals |= delays.Any(node => !routedDelays.Contains(node.Id));
            else
                hasChildDeferredSignals |= delays.Length > 0;

            var parentWithSignalInputs = LowerSignalInputs(flat, out var signalTypes, out var signalProxies);
            foreach (var pair in cpu.SignalTypes) signalTypes = AddSignalType(signalTypes, pair.Key, pair.Value);
            signalRoutes.AddRange(CreateSignalRoutes(flat, signalProxies));
            var program = VfxProgram.CompileForPackage(RemoveLocalRoutes(parentWithSignalInputs, routedDelays),
                out var render, signalTypes, scopeResources, options);
            render = CpuParticleCompilation.AddDraws(render, cpuSource, scopeResources);

            var nodes = flat.Nodes.ToDictionary(node => node.Id);
            foreach (var node in flat.Nodes.Where(item => item.Kind == GraphNodeKind.SpawnableGroup))
            {
                var child = node.ChildGraph!;
                var inputs = child.Nodes.Where(item => SpawnableGroupSchema.IsInput(item.Kind))
                    .Select(item => new SpawnableInput(SpawnableGroupSchema.PortId(item.Id), item.Id,
                        item.PortType, SpawnableGroupSchema.InputWidth(item.PortType),
                        item.PortType == "Color", SpawnableGroupSchema.IsLive(item))).ToArray();
                var usesExtendedCaptureInputs = inputs.Length > 4 || inputs.Sum(input => input.Width) > 16;
                var capturedInputs = new List<SpawnableRouteCapture>();
                foreach (var input in inputs.Where(input => usesExtendedCaptureInputs && !input.IsLive))
                {
                    var edge = flat.Connections.SingleOrDefault(item =>
                        item.TargetNodeId == node.Id && item.TargetPortId == input.PortId);
                    if (edge is null) continue;
                    var binding = nodes[edge.SourceNodeId];
                    if (binding.Kind != GraphNodeKind.SpawnableGroup)
                        capturedInputs.Add(new(input.TemplateNodeId, input.Width, null, binding.Id,
                            ParameterSourceComponent(binding, edge.SourcePortId)));
                }

                var childResources = ScopedRenderResourceBindingContext.UnderGroup(scopeResources, node.Id);
                var childBuild = BuildScope(child, node.Id, childResources);
                var life = childBuild.Graph.Nodes.Where(item => item.Kind.Value == "LifeTime").ToArray();
                var onStart = childBuild.Graph.Nodes.Where(item => item.Kind.Value == "OnStart").ToArray();
                var ownerDestroy = childBuild.Graph.Nodes.Where(item => item.Kind.Value == "OwnerDestroy").ToArray();
                if (life.Length > 1) throw new InvalidDataException($"Spawnable '{node.Name}' has multiple Life Times; exactly one or none is supported.");
                if (onStart.Length > 1) throw new InvalidDataException($"Spawnable '{node.Name}' has multiple On Start entries.");
                if (ownerDestroy.Length > 1) throw new InvalidDataException($"Spawnable '{node.Name}' has multiple Owner Destroy entries.");
                if (life.Length == 1 && !childBuild.Graph.Connections.Any(edge => edge.TargetNodeId == life[0].Id))
                {
                    var seconds = life[0].Parameters?.SingleOrDefault(parameter => parameter.Id == "seconds")?.Value.GetDouble() ?? 2;
                    if (!double.IsFinite(seconds) || seconds < 0)
                        throw new InvalidDataException($"Spawnable '{node.Name}' Life Time must be nonnegative finite seconds.");
                }
                var outputs = child.Nodes.Where(item => SpawnableGroupSchema.IsOutput(item.Kind))
                    .Select(item => new SpawnableOutput(SpawnableGroupSchema.OutputPortId(item.Id), item.Id, item.PortType)).ToArray();
                var signalInputs = child.Nodes.Where(item => SpawnableGroupSchema.IsSignalInput(item.Kind))
                    .Select(item => new SpawnableSignalPort(SpawnableGroupSchema.PortId(item.Id), item.Id,
                        SignalInputProxyNodeId(node.Id, item.Id))).ToArray();
                var signalOutputs = child.Nodes.Where(item => SpawnableGroupSchema.IsSignalOutput(item.Kind))
                    .Select(item => new SpawnableSignalPort(SpawnableGroupSchema.OutputPortId(item.Id), item.Id)).ToArray();
                var liveInputs = new List<SpawnableRouteCapture>();
                foreach (var input in inputs.Where(input => input.IsLive))
                {
                    var edge = flat.Connections.SingleOrDefault(item =>
                        item.TargetNodeId == node.Id && item.TargetPortId == input.PortId);
                    if (edge is null) continue;
                    var binding = nodes[edge.SourceNodeId];
                    if (binding.Kind == GraphNodeKind.SpawnableGroup)
                        throw new InvalidDataException("Live cannot read a completed Spawnable's local output. Use CaptureAtSpawn on Destroy routes.");
                    liveInputs.Add(new(input.TemplateNodeId, input.Width, null, binding.Id,
                        ParameterSourceComponent(binding, edge.SourcePortId)));
                }
                templates.Add(new(node.Id, SignalTypeId(node.Id, child), childBuild.Bytecode, inputs,
                    childBuild.RenderConfiguration, CollectGpu(child).ToArray(),
                    new(onStart.SingleOrDefault()?.Id, life.SingleOrDefault()?.Id, ownerDestroy.SingleOrDefault()?.Id),
                    outputs, liveInputs.AsReadOnly(), capturedInputs.AsReadOnly(), signalInputs, signalOutputs)
                {
                    GpuPrograms = CreateGpuPrograms(child, childBuild.RenderConfiguration, scopeResources),
                    ParentContainerId = ownerContainerId,
                    CpuPrograms = childBuild.CpuPrograms
                });
            }

            return new(program.ToPackageBinary(), render, cpu.Payload, flat, program.GameIoPorts);
        }

        var rootBuild = BuildScope(graph, null, resourceContext);
        var starts = graph.Nodes.Where(node => node.Kind.Value == "OnStart").ToArray();
        if (starts.Length > 1) throw new InvalidDataException("Only one direct On Start entry is supported.");
        var destroys = graph.Nodes.Where(node => node.Kind.Value == "OnDestroy").ToArray();
        if (destroys.Length > 1) throw new InvalidDataException("Only one root On Destroy entry is supported.");
        return new(rootBuild.Bytecode, templates.AsReadOnly())
        {
            AutoDestroyWhenIdle = graph.AutoDestroyWhenIdle,
            OnStartNodeId = starts.SingleOrDefault()?.Id,
            OnDestroyNodeId = destroys.SingleOrDefault()?.Id,
            HasDeferredSignals = hasDeferredSignals,
            HasChildDeferredSignals = hasChildDeferredSignals,
            DestroyRoutes = destroyRoutes.AsReadOnly(),
            SignalRoutes = signalRoutes,
            GameIoPorts = rootBuild.GameIoPorts,
            RootRenderConfiguration = rootBuild.RenderConfiguration,
            CpuPrograms = rootBuild.CpuPrograms,
            RootGpuContainers = CollectGpu(rootBuild.Graph).ToArray(),
            RootGpuPrograms = CreateGpuPrograms(rootBuild.Graph, rootBuild.RenderConfiguration, resourceContext)
        };
    }

    private sealed record ScopeBuild(byte[] Bytecode, byte[] RenderConfiguration, byte[] CpuPrograms,
        GraphSnapshot Graph, IReadOnlyList<GameIoPort> GameIoPorts);

    private static IReadOnlyDictionary<Guid, Guid> AddSignalType(IReadOnlyDictionary<Guid, Guid> source,
        Guid nodeId, Guid typeId)
    {
        var result = source.ToDictionary(pair => pair.Key, pair => pair.Value);
        result.Add(nodeId, typeId);
        return result;
    }

    private static void ValidateScope(GraphSnapshot graph, Guid? ownerContainerId)
    {
        var root = ownerContainerId is null;
        void Visit(GraphSnapshot current, bool insideOrdinaryGroup)
        {
            foreach (var node in current.Nodes)
            {
                if (node.Kind == GraphNodeKind.GraphGroup && node.ChildGraph is { } groupChild)
                {
                    Visit(groupChild, true);
                    continue;
                }
                if (insideOrdinaryGroup &&
                    (GameIoSchema.IsInstanceNode(node.Kind) || SpawnableGroupSchema.IsInput(node.Kind) ||
                     SpawnableGroupSchema.IsSignalInput(node.Kind) || SpawnableGroupSchema.IsSignalOutput(node.Kind) ||
                     SpawnableGroupSchema.IsOutput(node.Kind) ||
                     node.Kind.Value is "LifeTime" or "OwnerDestroy" or "OnDestroy"))
                    throw new InvalidDataException("Spawnable boundary and lifecycle nodes must be directly inside their execution scope, not inside an ordinary Graph Group.");
                if (insideOrdinaryGroup && root && node.Kind.Value == "OnStart")
                    throw new InvalidDataException("Root On Start must be directly in the effect graph.");
                if (GameIoSchema.IsInstanceNode(node.Kind) && root)
                    throw new InvalidDataException("Instance Game I/O must be directly inside a Spawnable Group.");
                if ((SpawnableGroupSchema.IsInput(node.Kind) || SpawnableGroupSchema.IsSignalInput(node.Kind) ||
                     SpawnableGroupSchema.IsSignalOutput(node.Kind)) && root)
                    throw new InvalidDataException("Spawnable boundary nodes must be directly inside a Spawnable Group.");
                if (SpawnableGroupSchema.IsOutput(node.Kind) && root)
                    throw new InvalidDataException("Group Output must be directly inside a Spawnable Group.");
                if (node.Kind.Value == "OwnerDestroy" && root)
                    throw new InvalidDataException("Owner Destroy must be directly inside a Spawnable Group.");
                if (node.Kind.Value == "OnDestroy" && !root)
                    throw new InvalidDataException("On Destroy must be directly at the effect root.");
                if (root && node.Kind.Value == "LifeTime")
                    throw new InvalidDataException("Life Time is only valid directly inside a Spawnable Group; the effect root has no Life Time.");
            }
        }
        Visit(graph, false);
    }

    private static void RejectTemplateGameIo(GraphSnapshot graph, bool withinTemplate)
    {
        if (graph?.Nodes is null) return;
        foreach (var node in graph.Nodes)
        {
            if (node is null) continue;
            if (withinTemplate && SkeletalPoseSchema.IsSource(node.Kind))
                throw new InvalidDataException(
                    $"SOURCE node '{node.Name}' is not supported inside a Spawnable Group template. " +
                    "SOURCE adapters currently publish effect-root inputs only; template pose capture requires instance-scoped support.");
            if (withinTemplate && GameIoSchema.IsNode(node.Kind) && !GameIoSchema.IsInstanceNode(node.Kind))
                throw new InvalidDataException(
                    $"Spawnable Group template '{node.Name}' cannot contain named Game I/O. " +
                    "Use Spawn Input/Output for template data, or place named Game I/O at the effect root.");
            if (node.ChildGraph is { } child)
                RejectTemplateGameIo(child, withinTemplate || node.Kind == GraphNodeKind.SpawnableGroup);
        }
    }

    private static byte[] CreateGpuPrograms(GraphSnapshot graph, byte[] renderConfiguration,
        IRenderResourceBindingContext? resources)
    {
        var bytes = LiberaFx.Services.Gpu.GpuProgramDeliveryService.CreateExecutablePayload(graph, resources: resources);
        if (renderConfiguration.Length == 0) return bytes;
        var render = JsonNode.Parse(renderConfiguration)!;
        var bindings = render["nodes"]!.AsArray().Where(node => node?["generatedTextures"] is JsonArray)
            .SelectMany(node => node!["generatedTextures"]!.AsArray()).Select(binding => binding!.DeepClone()).ToArray();
        if (bindings.Length == 0) return bytes;
        var payload = JsonNode.Parse(bytes)!;
        payload["generatedDrawBindings"] = new JsonArray(bindings);
        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    // Local Destroy is not a signal branch evaluated at spawn. Remove only
    // explicitly represented event/value edges; the host routes frozen values.
    private static GraphSnapshot RemoveLocalRoutes(GraphSnapshot graph, IReadOnlySet<Guid> delays)
    {
        return graph with
        {
            Connections = graph.Connections.Where(edge =>
                !delays.Contains(edge.SourceNodeId) &&
                !(delays.Contains(edge.TargetNodeId) && edge.TargetPortId == "signal") &&
                !graph.Nodes.Any(node => node.Id == edge.SourceNodeId &&
                    node.Kind == GraphNodeKind.SpawnableGroup && edge.SourcePortId != "then"))
                .Select(edge => delays.Contains(edge.TargetNodeId) ? edge with { TargetPortId = null } : edge).ToArray(),
            Nodes = graph.Nodes.Select(node => delays.Contains(node.Id)
                ? node with { Kind = GraphNodeKind.Float, PortType = "Float", PortCategory = GraphPortCategory.Parameter,
                    Parameters = [new("value", "float", node.Parameters?.SingleOrDefault(p => p.Id == "seconds")?.Value
                        ?? JsonSerializer.SerializeToElement(1d))] }
                : node.ChildGraph is { } child && node.Kind != GraphNodeKind.SpawnableGroup
                    ? node with { ChildGraph = RemoveLocalRoutes(child, delays) } : node).ToArray()
        };
    }

    private static GraphSnapshot LowerSignalInputs(GraphSnapshot graph,
        out IReadOnlyDictionary<Guid, Guid> signalTypeIds,
        out IReadOnlyDictionary<(Guid ContainerId, string PortId), Guid> proxyNodes)
    {
        var types = new Dictionary<Guid, Guid>();
        var proxies = new Dictionary<(Guid, string), Guid>();
        var nodes = graph.Nodes.ToList();
        var connections = graph.Connections.ToArray();
        foreach (var group in graph.Nodes.Where(node => node.Kind == GraphNodeKind.SpawnableGroup))
        {
            foreach (var input in group.ChildGraph!.Nodes.Where(node => SpawnableGroupSchema.IsSignalInput(node.Kind)))
            {
                var portId = SpawnableGroupSchema.PortId(input.Id);
                var proxyId = SignalInputProxyNodeId(group.Id, input.Id);
                if (nodes.Any(node => node.Id == proxyId))
                    throw new InvalidDataException("A Spawnable Signal input proxy ID collides with a graph node.");
                nodes.Add(new GraphNodeSnapshot(proxyId, GraphNodeKind.Signal, input.Name,
                    group.X, group.Y, "Signal", GraphPortCategory.Signal));
                types.Add(proxyId, SignalInputTypeId(group.Id, input.Id));
                proxies.Add((group.Id, portId), proxyId);
                connections = connections.Select(edge =>
                    edge.TargetNodeId == group.Id && edge.TargetPortId == portId
                        ? edge with { TargetNodeId = proxyId, TargetPortId = null } : edge).ToArray();
            }
        }
        signalTypeIds = types;
        proxyNodes = proxies;
        return graph with { Nodes = nodes.AsReadOnly(), Connections = connections };
    }

    private static IReadOnlyList<SpawnableSignalRoute> CreateSignalRoutes(GraphSnapshot graph,
        IReadOnlyDictionary<(Guid ContainerId, string PortId), Guid> proxyNodes)
    {
        var groups = graph.Nodes.Where(node => node.Kind == GraphNodeKind.SpawnableGroup)
            .ToDictionary(node => node.Id);
        var routes = new List<SpawnableSignalRoute>();
        foreach (var edge in graph.Connections)
        {
            if (!groups.TryGetValue(edge.SourceNodeId, out var source)) continue;
            var output = source.ChildGraph!.Nodes.SingleOrDefault(node =>
                SpawnableGroupSchema.IsSignalOutput(node.Kind) &&
                SpawnableGroupSchema.OutputPortId(node.Id) == edge.SourcePortId);
            if (output is null) continue;
            var targetId = edge.TargetNodeId;
            if (groups.TryGetValue(targetId, out var target) &&
                proxyNodes.TryGetValue((targetId, edge.TargetPortId ?? string.Empty), out var proxy))
                targetId = proxy;
            routes.Add(new(source.Id, output.Id, targetId));
        }
        return routes.AsReadOnly();
    }

    private static IReadOnlyList<SpawnableDestroyRoute> CreateRoutes(GraphSnapshot graph)
    {
        var groups = graph.Nodes.Where(node => node.Kind == GraphNodeKind.SpawnableGroup).ToDictionary(node => node.Id);
        var delays = graph.Nodes.Where(node => node.Kind.Value == "Delay").ToDictionary(node => node.Id);
        var sequences = graph.Nodes.Where(node => SequenceSchema.IsSequence(node.Kind)).ToDictionary(node => node.Id);
        var routes = new List<SpawnableDestroyRoute>();
        foreach (var edge in graph.Connections.Where(edge => groups.ContainsKey(edge.SourceNodeId) && edge.SourcePortId == "destroy"))
        {
            var targets = new List<(GraphConnectionSnapshot Edge, Guid? Delay)>();
            var visiting = new HashSet<Guid>();
            var steps = 0;
            void VisitTarget(GraphConnectionSnapshot current, Guid? delayId)
            {
                if (++steps > 10000 || !visiting.Add(current.TargetNodeId))
                    throw new InvalidDataException("Destroy routing contains a cycle or too many steps.");
                if (sequences.TryGetValue(current.TargetNodeId, out var sequence))
                {
                    if (current.TargetPortId != "signal")
                        throw new InvalidDataException("Sequence requires a Signal input.");
                    for (var index = 0; index < SequenceSchema.OutputCount(sequence.Parameters); index++)
                        foreach (var next in graph.Connections.Where(other =>
                            other.SourceNodeId == sequence.Id && other.SourcePortId == SequenceSchema.OutputId(index)))
                            VisitTarget(next, delayId);
                }
                else if (delays.ContainsKey(current.TargetNodeId))
                {
                    if (delayId is not null || current.TargetPortId != "signal" ||
                        graph.Connections.Count(other => other.TargetNodeId == current.TargetNodeId &&
                            other.TargetPortId == "signal") != 1)
                        throw new InvalidDataException("A Destroy route supports one Delay with a single Signal source.");
                    foreach (var next in graph.Connections.Where(other =>
                        other.SourceNodeId == current.TargetNodeId && other.SourcePortId == "then"))
                        VisitTarget(next, current.TargetNodeId);
                }
                else targets.Add((current, delayId));
                visiting.Remove(current.TargetNodeId);
            }
            VisitTarget(edge, null);
            foreach (var (targetEdge, delayId) in targets)
            {
                if (!groups.TryGetValue(targetEdge.TargetNodeId, out var target) || targetEdge.TargetPortId != "spawn")
                    throw new InvalidDataException("Local Destroy currently supports a sibling Spawnable directly or through Delay.");
                if (target.Id == edge.SourceNodeId) throw new InvalidDataException("Self-spawning Destroy routes are unsupported.");
                var captures = new List<SpawnableRouteCapture>();
                foreach (var input in target.ChildGraph!.Nodes.Where(node => SpawnableGroupSchema.IsInput(node.Kind)))
                {
                    var sourceEdge = graph.Connections.SingleOrDefault(other => other.TargetNodeId == target.Id &&
                        other.TargetPortId == SpawnableGroupSchema.PortId(input.Id));
                    if (sourceEdge is null) continue; // Authored template defaults.
                    var source = graph.Nodes.Single(node => node.Id == sourceEdge.SourceNodeId);
                    Guid? container = null;
                    var sourceId = source.Id;
                    var component = 0;
                    if (source.Kind == GraphNodeKind.SpawnableGroup)
                    {
                        if (source.Id != edge.SourceNodeId)
                            throw new InvalidDataException("Destroy capture must read the expiring source instance, not an ambiguous sibling.");
                        var output = source.ChildGraph!.Nodes.SingleOrDefault(node =>
                            SpawnableGroupSchema.IsOutput(node.Kind) && SpawnableGroupSchema.OutputPortId(node.Id) == sourceEdge.SourcePortId)
                            ?? throw new InvalidDataException("Invalid local Group Output capture.");
                        container = source.Id;
                        sourceId = output.Id;
                    }
                    else component = ParameterSourceComponent(source, sourceEdge.SourcePortId);
                    var width = SpawnableGroupSchema.InputWidth(input.PortType);
                    captures.Add(new(input.Id, width, container, sourceId, component));
                }
                routes.Add(new(edge.SourceNodeId, target.Id, captures, delayId));
            }
        }
        foreach (var edge in graph.Connections.Where(edge => groups.ContainsKey(edge.SourceNodeId) &&
            edge.SourcePortId is not ("then" or "destroy")))
            if (!groups[edge.SourceNodeId].ChildGraph!.Nodes.Any(node =>
                    SpawnableGroupSchema.IsSignalOutput(node.Kind) &&
                    SpawnableGroupSchema.OutputPortId(node.Id) == edge.SourcePortId) &&
                !routes.Any(route => route.SourceContainerId == edge.SourceNodeId && route.TargetContainerId == edge.TargetNodeId))
                throw new InvalidDataException("Local Group Output is readable only on its explicitly connected Destroy -> sibling Spawn route.");
        foreach (var node in graph.Nodes.Where(node => node.ChildGraph is not null && node.Kind != GraphNodeKind.SpawnableGroup))
            if (CreateRoutes(node.ChildGraph!).Count != 0)
                throw new InvalidDataException("Lifecycle routing currently requires root sibling Spawnable Groups.");
        return routes.AsReadOnly();
    }

    private static int ParameterSourceComponent(GraphNodeSnapshot source, string? portId)
    {
        if (portId is null) return 0;
        var definition = GraphNodeDefinition.All.Single(item => item.Kind == source.Kind);
        var index = definition.OutputPorts?.Select((port, index) => (port, index))
            .Single(item => item.port.Id == portId).index
            ?? throw new InvalidDataException("Invalid named root input binding.");
        if (source.Kind.Value.StartsWith("BreakVector", StringComparison.Ordinal)) return index;
        if (index != 0) throw new InvalidDataException("Named root input binding is unsupported for this output.");
        return 0;
    }

    private static IEnumerable<GraphNodeSnapshot> CollectGpu(GraphSnapshot graph)
    {
        foreach (var node in graph.Nodes)
        {
            if (GpuParticleEditorService.IsGpu(node.Kind)) yield return node;
            else if (node.ChildGraph is { } child && node.Kind != GraphNodeKind.SpawnableGroup)
                foreach (var nested in CollectGpu(child)) yield return nested;
        }
    }

    public static class ScopedRenderResourceBindingContext
    {
        public static IRenderResourceBindingContext? UnderGroup(
            IRenderResourceBindingContext? context, Guid groupNodeId)
        {
            if (context is null) return null;
            if (groupNodeId == Guid.Empty) throw new ArgumentException("Group ID must not be empty.", nameof(groupNodeId));
            return new Scope(context, [groupNodeId]);
        }

        private sealed class Scope(IRenderResourceBindingContext inner, IReadOnlyList<Guid> prefix)
            : IRenderResourceBindingContext
        {
            public IReadOnlyList<Guid> GetInstancePath(Guid nodeId)
            {
                var innerPath = inner.GetInstancePath(nodeId);
                return innerPath.Count > 0 ? innerPath : Combine(Array.Empty<Guid>());
            }

            public IReadOnlyList<Guid> GetInstancePath(Guid nodeId, IReadOnlyList<Guid> localPath)
            {
                var innerPath = inner.GetInstancePath(nodeId);
                return innerPath.Count > 0 ? innerPath : Combine(nodeId, localPath);
            }

            public bool HasResourceUse(Guid nodeId, string location) =>
                HasResourceUse(nodeId, location, Array.Empty<Guid>());

            public bool HasResourceUse(Guid nodeId, string location, IReadOnlyList<Guid> instancePath) =>
                inner.HasResourceUse(nodeId, location, Combine(nodeId, instancePath));

            public void ValidateRenderNode(Guid nodeId, IEnumerable<(string Id, string Kind, int Slot)> parameters) =>
                ValidateRenderNode(nodeId, parameters, Array.Empty<Guid>());

            public void ValidateRenderNode(Guid nodeId, IEnumerable<(string Id, string Kind, int Slot)> parameters,
                IReadOnlyList<Guid> instancePath) =>
                inner.ValidateRenderNode(nodeId, parameters, Combine(nodeId, instancePath));

            public void ValidateRenderNode(Guid nodeId, IEnumerable<(string Id, string Kind, int Slot)> parameters,
                IReadOnlyList<Guid> instancePath, IReadOnlyList<int> materialSlots) =>
                inner.ValidateRenderNode(nodeId, parameters, Combine(nodeId, instancePath), materialSlots);

            public string ResolvePath(Guid nodeId, string location, string kind, string legacyValue) =>
                ResolvePath(nodeId, location, kind, legacyValue, Array.Empty<Guid>());

            public string ResolvePath(Guid nodeId, string location, string kind, string legacyValue,
                IReadOnlyList<Guid> instancePath) =>
                inner.ResolvePath(nodeId, location, kind, legacyValue, Combine(nodeId, instancePath));

            public (string Name, int Association, int Index)? ResolveParameter(
                Guid nodeId, string pinId, int slot, string kind) =>
                ResolveParameter(nodeId, pinId, slot, kind, Array.Empty<Guid>());

            public (string Name, int Association, int Index)? ResolveParameter(
                Guid nodeId, string pinId, int slot, string kind, IReadOnlyList<Guid> instancePath) =>
                inner.ResolveParameter(nodeId, pinId, slot, kind, Combine(nodeId, instancePath));

            private IReadOnlyList<Guid> Combine(Guid nodeId, IReadOnlyList<Guid>? path)
            {
                var boundPath = inner.GetInstancePath(nodeId);
                if (boundPath.Count > 0) return boundPath;
                var combined = prefix.Concat(path ?? Array.Empty<Guid>()).ToArray();
                ResourceIdentityScope.Validate(combined);
                return combined;
            }

            private IReadOnlyList<Guid> Combine(IReadOnlyList<Guid>? path)
            {
                var combined = prefix.Concat(path ?? Array.Empty<Guid>()).ToArray();
                ResourceIdentityScope.Validate(combined);
                return combined;
            }
        }
    }
}
