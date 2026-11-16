using System.Security.Cryptography;
using System.Text;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Compilation;

public sealed record SpawnableInput(string PortId, Guid TemplateNodeId, string PortType, int Width, bool IsColor);
public sealed record SpawnableOutput(string PortId, Guid TemplateNodeId, string PortType);
public sealed record SpawnableLifecycle(Guid? OnStartNodeId, Guid? LifeNodeId);
public sealed record SpawnableRouteCapture(Guid TargetNodeId, int Width, Guid? SourceContainerId,
    Guid SourceNodeId, int SourceComponent);
public sealed record SpawnableDestroyRoute(Guid SourceContainerId, Guid TargetContainerId,
    IReadOnlyList<SpawnableRouteCapture> Captures);
public sealed record SpawnableTemplate(Guid ContainerId, Guid SignalTypeId, byte[] Bytecode,
    IReadOnlyList<SpawnableInput> Inputs, byte[] RenderConfiguration, IReadOnlyList<GraphNodeSnapshot> GpuContainers,
    SpawnableLifecycle? Lifecycle = null, IReadOnlyList<SpawnableOutput>? Outputs = null);
public sealed record SpawnableGroupPackage(byte[] ParentBytecode, IReadOnlyList<SpawnableTemplate> Templates)
{
    public const int FormatVersion = 2;
    public int Version => FormatVersion;
    public Guid? OnStartNodeId { get; init; }
    public IReadOnlyList<SpawnableDestroyRoute> DestroyRoutes { get; init; } = [];
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
        GraphTypeDiagnostics.Validate(graph);
        var starts = graph.Nodes.Where(node => node.Kind.Value == "OnStart").ToArray();
        if (starts.Length > 1) throw new InvalidDataException("Only one direct On Start entry is supported.");
        var routes = CreateRoutes(graph);
        var templates = new List<SpawnableTemplate>();
        void Visit(GraphSnapshot current, bool withinTemplate, bool directTemplate)
        {
            foreach (var node in current.Nodes)
            {
                if (SpawnableGroupSchema.IsInput(node.Kind) && !directTemplate)
                    throw new InvalidDataException("Spawn input nodes must be directly inside a Spawnable Group.");
                if ((node.Kind.Value == "LifeTime" || SpawnableGroupSchema.IsOutput(node.Kind)) && !directTemplate)
                    throw new InvalidDataException("Life Time and Group Output must be directly inside a Spawnable Group.");
                if (node.Kind.Value == "OnStart" && !directTemplate && !ReferenceEquals(current, graph))
                    throw new InvalidDataException("On Start must be directly at the root or directly inside a Spawnable Group.");
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
                    if (life.Length > 1) throw new InvalidDataException($"Spawnable '{node.Name}' has multiple Life Times; exactly one or none is supported.");
                    if (onStart.Length > 1) throw new InvalidDataException($"Spawnable '{node.Name}' has multiple On Start entries.");
                    if (life.Length == 1 && !child.Connections.Any(edge => edge.TargetNodeId == life[0].Id))
                    {
                        var seconds = life[0].Parameters?.SingleOrDefault(parameter => parameter.Id == "seconds")?.Value.GetDouble() ?? 2;
                        if (!double.IsFinite(seconds) || seconds <= 0)
                            throw new InvalidDataException($"Spawnable '{node.Name}' Life Time must be positive seconds.");
                    }
                    var bytecode = VfxProgram.CompileForPackage(child).ToBinary();
                    var render = RenderProgramBindings.CreateScene(child, 0);
                    var gpu = CollectGpu(child).ToArray();
                    var outputs = child.Nodes.Where(item => SpawnableGroupSchema.IsOutput(item.Kind))
                        .Select(item => new SpawnableOutput(SpawnableGroupSchema.OutputPortId(item.Id), item.Id, item.PortType)).ToArray();
                    templates.Add(new(node.Id, SignalTypeId(node.Id, child), bytecode, inputs, render, gpu,
                        new(onStart.SingleOrDefault()?.Id, life.SingleOrDefault()?.Id), outputs));
                }
                else if (node.ChildGraph is { } child && !GpuParticleEditorService.IsGpu(node.Kind))
                    Visit(child, withinTemplate, false);
            }
        }
        Visit(graph, false, false);
        var parent = VfxProgram.CompileForPackage(RemoveLocalRoutes(graph)).ToBinary();
        return new(parent, templates.AsReadOnly()) { OnStartNodeId = starts.SingleOrDefault()?.Id, DestroyRoutes = routes };
    }

    // Local Destroy is not a signal branch evaluated at spawn. Remove only
    // explicitly represented event/value edges; the host routes frozen values.
    private static GraphSnapshot RemoveLocalRoutes(GraphSnapshot graph) => graph with
    {
        Connections = graph.Connections.Where(edge => !graph.Nodes.Any(node =>
            node.Id == edge.SourceNodeId && node.Kind == GraphNodeKind.SpawnableGroup &&
            edge.SourcePortId != "then")).ToArray(),
        Nodes = graph.Nodes.Select(node => node.ChildGraph is { } child && node.Kind != GraphNodeKind.SpawnableGroup
            ? node with { ChildGraph = RemoveLocalRoutes(child) } : node).ToArray()
    };

    private static IReadOnlyList<SpawnableDestroyRoute> CreateRoutes(GraphSnapshot graph)
    {
        var groups = graph.Nodes.Where(node => node.Kind == GraphNodeKind.SpawnableGroup).ToDictionary(node => node.Id);
        var routes = new List<SpawnableDestroyRoute>();
        foreach (var edge in graph.Connections.Where(edge => groups.ContainsKey(edge.SourceNodeId) && edge.SourcePortId == "destroy"))
        {
            if (!groups.TryGetValue(edge.TargetNodeId, out var target) || edge.TargetPortId != "spawn")
                throw new InvalidDataException("Local Destroy currently supports only a direct sibling Spawnable.Spawn target.");
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
            routes.Add(new(edge.SourceNodeId, target.Id, captures));
        }
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
