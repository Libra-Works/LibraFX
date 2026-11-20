using System.Text.Json;

namespace LiberaFx.Models.Graph;

/// <summary>CPU stages are ordinary graphs, but are execution scopes, never parent values.</summary>
public static class CpuParticleSchema
{
    public const string Prefix = "CpuParticle";
    public static bool IsContainer(GraphNodeKind kind) => Name(kind) == "CpuParticle";
    public static bool IsStage(GraphNodeKind kind) => Name(kind) is "CpuParticleSpawn" or "CpuParticleUpdate";
    public static bool HasChild(GraphNodeKind kind) => IsContainer(kind) || IsStage(kind);
    public static string Name(GraphNodeKind kind) =>
        GraphNodeDefinition.Find(kind)?.TypeName ?? kind.Value;
    public static bool IsBoundary(GraphNodeKind kind) => Name(kind).StartsWith("CpuParticleInput", StringComparison.Ordinal) ||
        Name(kind).StartsWith("CpuParticleOutput", StringComparison.Ordinal);
    public static bool IsInput(GraphNodeKind kind) => Name(kind).StartsWith("CpuParticleInput", StringComparison.Ordinal);
    public static bool IsOutput(GraphNodeKind kind) => Name(kind).StartsWith("CpuParticleOutput", StringComparison.Ordinal);
    public static bool IsStageOnly(GraphNodeKind kind) => Name(kind).StartsWith(Prefix, StringComparison.Ordinal) &&
        !HasChild(kind) && Name(kind) != "CpuParticleSpriteDraw";
    public static string PortId(Guid id) => "cpu:" + id.ToString("D");
    public static IEnumerable<GraphNodeSnapshot> Boundaries(GraphSnapshot? child) =>
        child?.Nodes.Where(node => IsStage(node.Kind)).SelectMany(node =>
            node.ChildGraph?.Nodes.Where(item => IsBoundary(item.Kind)) ?? []) ?? [];

    public static GraphNodeDefinition Effective(GraphNodeDefinition definition, GraphSnapshot? child)
    {
        if (!IsContainer(definition.Kind))
        {
            return definition;
        }
        var boundaries = Boundaries(child).ToArray();
        return definition with
        {
            InputPorts = [.. definition.InputPorts ?? [], .. boundaries.Where(node => IsInput(node.Kind))
                .Select(node => new GraphInputPortDefinition(PortId(node.Id), node.Name, node.PortType, node.PortCategory))],
            OutputPorts = [.. boundaries.Where(node => IsOutput(node.Kind))
                .Select(node => new GraphOutputPortDefinition(PortId(node.Id), node.Name, node.PortType, node.PortCategory))]
        };
    }

    public static GraphSnapshot CreateChild(GraphNodeKind kind)
    {
        GraphNodeSnapshot Node(string name, double x, double y, GraphSnapshot? child = null)
        {
            var definition = GraphNodeDefinition.All.Single(item => item.TypeName == name);
            return new(Guid.NewGuid(), definition.Kind, definition.Name, x, y, definition.PortType,
                definition.PortCategory, ChildGraph: child);
        }
        if (IsContainer(kind))
        {
            return new([Node("CpuParticleSpawn", 80, 80, CreateChild(new("CpuParticleSpawn"))),
                Node("CpuParticleUpdate", 400, 80, CreateChild(new("CpuParticleUpdate"))),
                Node("CpuParticleSpriteDraw", 720, 80)], []);
        }
        var entry = Node("CpuParticleStageEvent", 80, 100);
        var operation = Node(Name(kind) == "CpuParticleSpawn" ? "CpuParticleSetVelocity" : "CpuParticleMove", 400, 100);
        return new([entry, operation], [new(entry.Id, operation.Id, "signal", "then")]);
    }

    public static void ValidateContext(GraphSnapshot graph, GraphNodeKind? parent = null, bool insideSpawnable = false,
        GraphNodeKind? stageContext = null)
    {
        foreach (var node in graph.Nodes)
        {
            if (IsContainer(node.Kind))
            {
                if (stageContext is not null || parent is { } owner && HasChild(owner))
                {
                    throw new InvalidDataException("CPU Particle containers cannot be nested inside another CPU Particle container or kernel.");
                }
                var stages = node.ChildGraph?.Nodes.Where(item => IsStage(item.Kind)).ToArray() ?? [];
                if (stages.Count(item => Name(item.Kind) == "CpuParticleSpawn") != 1 ||
                    stages.Count(item => Name(item.Kind) == "CpuParticleUpdate") != 1 ||
                    node.ChildGraph!.Nodes.Any(item => !IsStage(item.Kind) &&
                        Name(item.Kind) != "CpuParticleSpriteDraw" && item.Kind != GraphNodeKind.Comment) ||
                    node.ChildGraph.Nodes.Count(item => Name(item.Kind) == "CpuParticleSpriteDraw") != 1 ||
                    node.ChildGraph.Connections.Count != 0)
                {
                    throw new InvalidDataException("CPU Particle requires exactly one Spawn stage, Update stage and CpuParticleSpriteDraw. Edit numeric/Signal work inside its stages.");
                }
            }
            if (IsStage(node.Kind) || Name(node.Kind) == "CpuParticleSpriteDraw")
            {
                if (parent is null || !IsContainer(parent.Value))
                {
                    throw new InvalidDataException("CPU Particle stages and Sprite Draw must be directly inside CpuParticle.");
                }
            }
            if (IsStageOnly(node.Kind) && stageContext is null && (parent is null || !IsStage(parent.Value)))
            {
                throw new InvalidDataException($"'{node.Name}' is only valid directly inside a CPU Spawn/Update stage.");
            }
            if ((IsBoundary(node.Kind) || Name(node.Kind) == "CpuParticleStageEvent") &&
                (parent is null || !IsStage(parent.Value)))
            {
                throw new InvalidDataException("CPU boundary nodes and Stage Event must be directly in Spawn/Update, not inside a nested Graph Group.");
            }
            if (parent is { } stage && IsStage(stage) &&
                (IsContainer(node.Kind) || node.Kind == GraphNodeKind.SpawnableGroup || node.Kind.Value == "GpuParticle"))
            {
                throw new InvalidDataException("Particle kernels cannot contain simulation containers.");
            }
            if (node.ChildGraph is { } child)
            {
                ValidateContext(child, node.Kind, insideSpawnable || node.Kind == GraphNodeKind.SpawnableGroup,
                    IsStage(node.Kind) ? node.Kind : stageContext);
            }
        }
    }
}
