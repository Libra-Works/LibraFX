using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Compilation;

public sealed record SpawnableInput(string PortId, Guid TemplateNodeId, string PortType, int Width, bool IsColor);
public sealed record SpawnableOutput(string PortId, Guid TemplateNodeId, string PortType);
public sealed record SpawnableLifecycle(Guid? OnStartNodeId, Guid? LifeNodeId, Guid? OwnerDestroyNodeId = null);
public sealed record SpawnableRouteCapture(Guid TargetNodeId, int Width, Guid? SourceContainerId,
    Guid SourceNodeId, int SourceComponent);
public sealed record SpawnableDestroyRoute(Guid SourceContainerId, Guid TargetContainerId,
    IReadOnlyList<SpawnableRouteCapture> Captures, Guid? DelayNodeId = null);
public sealed record SpawnableTemplate(Guid ContainerId, Guid SignalTypeId, byte[] Bytecode,
    IReadOnlyList<SpawnableInput> Inputs, byte[] RenderConfiguration, IReadOnlyList<GraphNodeSnapshot> GpuContainers,
    SpawnableLifecycle? Lifecycle = null, IReadOnlyList<SpawnableOutput>? Outputs = null);
public sealed record SpawnableGroupPackage(byte[] ParentBytecode, IReadOnlyList<SpawnableTemplate> Templates)
{
    public const int FormatVersion = 2;
    public int Version => OnDestroyNodeId is not null || DestroyRoutes.Any(route => route.DelayNodeId is not null) ||
        Templates.Any(template => template.Lifecycle?.OwnerDestroyNodeId is not null) ? 3 : FormatVersion;
    public Guid? OnStartNodeId { get; init; }
    public Guid? OnDestroyNodeId { get; init; }
    public Guid? LifeNodeId { get; init; }
    public IReadOnlyList<SpawnableDestroyRoute> DestroyRoutes { get; init; } = [];
    public IReadOnlyList<GameIoPort> GameIoPorts { get; init; } = [];
    public static readonly Guid SignalPassthroughTypeId = Guid.Parse("989fa5b5-485f-4e75-8542-9c5500000001");

    public static Guid SignalTypeId(Guid containerId, GraphSnapshot child)
    {
        var shape = string.Join(";", child.Nodes.Where(node => SpawnableGroupSchema.IsInput(node.Kind))
            .Select(node => $"{node.Id:D}:{node.PortType}"));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"LiberaFx.SpawnableGroup.Signal.v1:{containerId:D}:{shape}"));
        return new Guid(digest.AsSpan(0, 16), bigEndian: true);
    }

    public static SpawnableGroupPackage Compile(GraphSnapshot graph)
    {
        VfxProgram.ValidateForCompilation(graph);
        graph = GroupExposedItems.Materialize(graph);
        VfxProgram.ValidateForCompilation(graph);
        GraphTypeDiagnostics.Validate(graph);
        var starts = graph.Nodes.Where(node => node.Kind.Value == "OnStart").ToArray();
        if (starts.Length > 1) throw new InvalidDataException("Only one direct On Start entry is supported.");
        var destroys = graph.Nodes.Where(node => node.Kind.Value == "OnDestroy").ToArray();
        if (destroys.Length > 1) throw new InvalidDataException("Only one root On Destroy entry is supported.");
        var rootLives = graph.Nodes.Where(node => node.Kind.Value == "LifeTime").ToArray();
        if (rootLives.Length > 1) throw new InvalidDataException("Only one root Life Time is supported.");
        if (rootLives.Length == 1 && !graph.Connections.Any(edge => edge.TargetNodeId == rootLives[0].Id))
        {
            var seconds = rootLives[0].Parameters?.SingleOrDefault(parameter => parameter.Id == "seconds")?.Value.GetDouble() ?? 2;
            if (!double.IsFinite(seconds) || seconds < 0)
                throw new InvalidDataException("Root Life Time must be nonnegative finite seconds.");
        }
        var routes = CreateRoutes(graph);
        var templates = new List<SpawnableTemplate>();
        void Visit(GraphSnapshot current, bool withinTemplate, bool directTemplate)
        {
            foreach (var node in current.Nodes)
            {
                if (SpawnableGroupSchema.IsInput(node.Kind) && !directTemplate)
                    throw new InvalidDataException("Spawn input nodes must be directly inside a Spawnable Group.");
                if ((node.Kind.Value == "LifeTime" && !directTemplate && !ReferenceEquals(current, graph)) ||
                    (SpawnableGroupSchema.IsOutput(node.Kind) && !directTemplate))
                    throw new InvalidDataException("Life Time must be at the root or directly inside a Spawnable Group; Group Output requires a Spawnable Group.");
                if (node.Kind.Value == "OnStart" && !directTemplate && !ReferenceEquals(current, graph))
                    throw new InvalidDataException("On Start must be directly at the root or directly inside a Spawnable Group.");
                if (node.Kind.Value == "OwnerDestroy" && !directTemplate)
                    throw new InvalidDataException("Owner Destroy must be directly inside a Spawnable Group.");
                if (node.Kind.Value == "OnDestroy" && !ReferenceEquals(current, graph))
                    throw new InvalidDataException("On Destroy must be directly at the effect root.");
                if (node.Kind.Value == "Delay" && !ReferenceEquals(current, graph))
                    throw new InvalidDataException("Delay currently requires a root Destroy-to-Spawn route.");
                if (node.Kind == GraphNodeKind.SpawnableGroup)
                {
                    if (withinTemplate) throw new InvalidDataException("Nested Spawnable Groups are not supported.");
                    var child = node.ChildGraph!;
                    var inputs = child.Nodes.Where(item => SpawnableGroupSchema.IsInput(item.Kind))
                        .Select(item => new SpawnableInput(SpawnableGroupSchema.PortId(item.Id), item.Id,
                            item.PortType, item.PortType switch
                            {
                                "Float" => 1, "FloatX2" => 2, "FloatX3" => 3,
                                "FloatX4" or "Color" => 4, _ => throw new InvalidDataException("Invalid spawn input.")
                            }, item.PortType == "Color")).ToArray();
                    Visit(child, true, true);
                    var life = child.Nodes.Where(item => item.Kind.Value == "LifeTime").ToArray();
                    var onStart = child.Nodes.Where(item => item.Kind.Value == "OnStart").ToArray();
                    var ownerDestroy = child.Nodes.Where(item => item.Kind.Value == "OwnerDestroy").ToArray();
                    if (life.Length > 1) throw new InvalidDataException($"Spawnable '{node.Name}' has multiple Life Times; exactly one or none is supported.");
                    if (onStart.Length > 1) throw new InvalidDataException($"Spawnable '{node.Name}' has multiple On Start entries.");
                    if (ownerDestroy.Length > 1) throw new InvalidDataException($"Spawnable '{node.Name}' has multiple Owner Destroy entries.");
                    if (life.Length == 1 && !child.Connections.Any(edge => edge.TargetNodeId == life[0].Id))
                    {
                        var seconds = life[0].Parameters?.SingleOrDefault(parameter => parameter.Id == "seconds")?.Value.GetDouble() ?? 2;
                        if (!double.IsFinite(seconds) || seconds < 0)
                            throw new InvalidDataException($"Spawnable '{node.Name}' Life Time must be nonnegative finite seconds.");
                    }
                    var bytecode = VfxProgram.CompileForPackage(child).ToBinary();
                    var render = RenderProgramBindings.CreateScene(child, 0);
                    var gpu = CollectGpu(child).ToArray();
                    var outputs = child.Nodes.Where(item => SpawnableGroupSchema.IsOutput(item.Kind))
                        .Select(item => new SpawnableOutput(SpawnableGroupSchema.OutputPortId(item.Id), item.Id, item.PortType)).ToArray();
                    templates.Add(new(node.Id, SignalTypeId(node.Id, child), bytecode, inputs, render, gpu,
                        new(onStart.SingleOrDefault()?.Id, life.SingleOrDefault()?.Id, ownerDestroy.SingleOrDefault()?.Id), outputs));
                }
                else if (node.ChildGraph is { } child && !GpuParticleEditorService.IsGpu(node.Kind))
                    Visit(child, withinTemplate, false);
            }
        }
        Visit(graph, false, false);
        var parentProgram = VfxProgram.CompileForPackage(RemoveLocalRoutes(graph));
        return new(parentProgram.ToBinary(), templates.AsReadOnly())
        {
            OnStartNodeId = starts.SingleOrDefault()?.Id,
            OnDestroyNodeId = destroys.SingleOrDefault()?.Id,
            LifeNodeId = rootLives.SingleOrDefault()?.Id,
            DestroyRoutes = routes,
            GameIoPorts = parentProgram.GameIoPorts
        };
    }

    // Local Destroy is not a signal branch evaluated at spawn. Remove only
    // explicitly represented event/value edges; the host routes frozen values.
    private static GraphSnapshot RemoveLocalRoutes(GraphSnapshot graph)
    {
        var delays = graph.Nodes.Where(node => node.Kind.Value == "Delay").Select(node => node.Id).ToHashSet();
        return graph with
        {
            Connections = graph.Connections.Where(edge =>
                !delays.Contains(edge.SourceNodeId) &&
                !(delays.Contains(edge.TargetNodeId) && edge.TargetPortId == "signal") &&
                !graph.Nodes.Any(node => node.Id == edge.SourceNodeId &&
                    node.Kind == GraphNodeKind.SpawnableGroup && edge.SourcePortId != "then"))
                .Select(edge => delays.Contains(edge.TargetNodeId) ? edge with { TargetPortId = null } : edge).ToArray(),
            Nodes = graph.Nodes.Select(node => node.Kind.Value == "Delay"
                ? node with { Kind = GraphNodeKind.Float, PortType = "Float", PortCategory = GraphPortCategory.Parameter,
                    Parameters = [new("value", "float", node.Parameters?.SingleOrDefault(p => p.Id == "seconds")?.Value
                        ?? JsonSerializer.SerializeToElement(1d))] }
                : node.ChildGraph is { } child && node.Kind != GraphNodeKind.SpawnableGroup
                    ? node with { ChildGraph = RemoveLocalRoutes(child) } : node).ToArray()
        };
    }

    private static IReadOnlyList<SpawnableDestroyRoute> CreateRoutes(GraphSnapshot graph)
    {
        var groups = graph.Nodes.Where(node => node.Kind == GraphNodeKind.SpawnableGroup).ToDictionary(node => node.Id);
        var delays = graph.Nodes.Where(node => node.Kind.Value == "Delay").ToDictionary(node => node.Id);
        var routes = new List<SpawnableDestroyRoute>();
        foreach (var edge in graph.Connections.Where(edge => groups.ContainsKey(edge.SourceNodeId) && edge.SourcePortId == "destroy"))
        {
            var targets = new[] { edge };
            Guid? delayId = null;
            if (delays.ContainsKey(edge.TargetNodeId))
            {
                if (edge.TargetPortId != "signal" ||
                    graph.Connections.Count(other => other.TargetNodeId == edge.TargetNodeId && other.TargetPortId == "signal") != 1)
                    throw new InvalidDataException("Delay requires one direct Spawnable Destroy signal.");
                targets = graph.Connections.Where(other => other.SourceNodeId == edge.TargetNodeId && other.SourcePortId == "then").ToArray();
                if (targets.Length == 0 || targets.Any(target => target.TargetPortId != "spawn"))
                    throw new InvalidDataException("Delay currently requires sibling Spawnable.Spawn targets.");
                delayId = edge.TargetNodeId;
            }
            foreach (var targetEdge in targets)
            {
                if (!groups.TryGetValue(targetEdge.TargetNodeId, out var target) || targetEdge.TargetPortId != "spawn")
                    throw new InvalidDataException("Local Destroy currently supports a sibling Spawnable directly or through Delay.");
                if (target.Id == edge.SourceNodeId) throw new InvalidDataException("Self-spawning Destroy routes are unsupported.");
                if (graph.Connections.Count(other => other.TargetNodeId == target.Id && other.TargetPortId == "spawn") != 1)
                    throw new InvalidDataException("A Destroy-routed Spawnable must have exactly one Spawn signal source.");
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
                    else if (sourceEdge.SourcePortId is not null)
                    {
                        var definition = GraphNodeDefinition.All.Single(item => item.Kind == source.Kind);
                        if (!source.Kind.Value.StartsWith("BreakVector", StringComparison.Ordinal))
                            throw new InvalidDataException("Named root output capture is unsupported except BreakVector components.");
                        component = definition.OutputPorts!.Select((port, index) => (port, index))
                            .Single(item => item.port.Id == sourceEdge.SourcePortId).index;
                    }
                    var width = input.PortType switch { "Float" => 1, "FloatX2" => 2, "FloatX3" => 3, _ => 4 };
                    captures.Add(new(input.Id, width, container, sourceId, component));
                }
                routes.Add(new(edge.SourceNodeId, target.Id, captures, delayId));
            }
        }
        if (delays.Keys.Any(id => !routes.Any(route => route.DelayNodeId == id)))
            throw new InvalidDataException("Delay currently requires a root Spawnable Destroy-to-Spawn route.");
        foreach (var edge in graph.Connections.Where(edge => groups.ContainsKey(edge.SourceNodeId) &&
            edge.SourcePortId is not ("then" or "destroy")))
            if (!routes.Any(route => route.SourceContainerId == edge.SourceNodeId && route.TargetContainerId == edge.TargetNodeId))
                throw new InvalidDataException("Local Group Output is readable only on its explicitly connected Destroy -> sibling Spawn route.");
        foreach (var node in graph.Nodes.Where(node => node.ChildGraph is not null && node.Kind != GraphNodeKind.SpawnableGroup))
            if (CreateRoutes(node.ChildGraph!).Count != 0)
                throw new InvalidDataException("Lifecycle routing currently requires root sibling Spawnable Groups.");
        return routes.AsReadOnly();
    }

    private static IEnumerable<GraphNodeSnapshot> CollectGpu(GraphSnapshot graph)
    {
        foreach (var node in graph.Nodes)
        {
            if (GpuParticleEditorService.IsGpu(node.Kind)) yield return node;
            else if (node.ChildGraph is { } child)
                foreach (var nested in CollectGpu(child)) yield return nested;
        }
    }
}
