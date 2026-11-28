using System.Text;
using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Assets;

namespace LiberaFx.Services.Compilation;

/// <summary>Never inferred from Debug/Release, editor presence, or timeline keys.</summary>
public sealed record VfxCompileOptions
{
    public bool IncludePreviewDebugDraw { get; init; }
    public bool OptimizeCpuParticleKernels { get; init; } = true;
    public static VfxCompileOptions Distribution { get; } = new();
    public static VfxCompileOptions LiveTransfer { get; } = new() { IncludePreviewDebugDraw = true };
}

public static class PreviewDebugDraw
{
    public static bool IsDebugNode(GraphNodeKind kind) => kind.Value is "DebugText" or "DebugModel";

    // Debug draws are terminal observers, never Signal relays or value producers.
    // Keep ALL ordinary nodes and their evaluation/lifetime side effects, even
    // when their only numeric consumer was a stripped draw.
    public static GraphSnapshot Strip(GraphSnapshot graph)
    {
        static bool ContainsDebug(GraphSnapshot current) => current.Nodes.Any(node =>
            IsDebugNode(node.Kind) || node.ChildGraph is { } child && ContainsDebug(child));
        if (!ContainsDebug(graph)) return graph;
        // Resolve instance overrides before removing their terminal targets.
        return StripMaterialized(GroupExposedItems.Materialize(graph));
    }

    private static GraphSnapshot StripMaterialized(GraphSnapshot graph)
    {
        var ids = graph.Nodes.Where(node => !IsDebugNode(node.Kind)).Select(node => node.Id).ToHashSet();
        return graph with
        {
            Nodes = graph.Nodes.Where(node => ids.Contains(node.Id)).Select(node => node.ChildGraph is { } child
                ? node with { ChildGraph = StripMaterialized(child) } : node).ToArray(),
            Connections = graph.Connections.Where(edge =>
                ids.Contains(edge.SourceNodeId) && ids.Contains(edge.TargetNodeId)).ToArray(),
            ExposedItems = graph.ExposedItems?.Where(item => item.Targets.All(target => ids.Contains(target.NodeId))).ToArray()
        };
    }

    internal static object CreateDraw(GraphNodeSnapshot node,
        IReadOnlyDictionary<Guid, GraphNodeSnapshot> nodes,
        ILookup<Guid, GraphConnectionSnapshot> connections,
        IRenderResourceBindingContext? resourceContext = null, IReadOnlyList<Guid>? instancePath = null)
    {
        var definition = GraphNodeDefinition.All.Single(item => item.Kind == node.Kind);
        JsonElement Parameter(string id) => node.Parameters?.SingleOrDefault(item => item.Id == id)?.Value ??
            definition.Parameters.Single(item => item.Id == id).DefaultValue;
        var text = node.Kind.Value == "DebugText" ? Parameter("text").GetString() ?? "" : "";
        var path = node.Kind.Value == "DebugModel" ? Parameter("assetPath").GetString() ?? "" : "";
        if (Encoding.UTF8.GetByteCount(text) > 1024 || text.Contains('\0'))
            throw new InvalidDataException("Debug Text supports at most 1024 UTF-8 bytes and no NUL.");
        if (node.Kind.Value == "DebugModel")
        {
            path = resourceContext?.ResolvePath(node.Id, "assetPath", "model", path, instancePath ?? []) ?? path;
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Debug Model requires an explicit model asset.");
            if (Encoding.UTF8.GetByteCount(path) > 512 || path.Contains('\0'))
                throw new InvalidDataException("Debug model bindings support at most 512 UTF-8 bytes and no NUL.");
            if (resourceContext?.HasResourceUse(node.Id, "assetPath", instancePath ?? []) != true)
                EngineAssetPath.Validate(path);
        }
        object Input(string id, int width, double[] defaults)
        {
            var matches = connections[node.Id].Where(edge => edge.TargetPortId == id).ToArray();
            if (matches.Length > 1) throw new InvalidDataException("A Debug draw input has multiple sources.");
            var edge = matches.SingleOrDefault();
            var component = 0;
            if (edge is not null)
            {
                var source = nodes[edge.SourceNodeId];
                var sourceDefinition = GraphNodeDefinition.All.Single(item => item.Kind == source.Kind);
                if (sourceDefinition.TypeName.StartsWith("BreakVector", StringComparison.Ordinal))
                {
                    component = sourceDefinition.OutputPorts!.ToList().FindIndex(port => port.Id == edge.SourcePortId);
                    if (component < 0) throw new InvalidDataException("Invalid Debug draw source component.");
                }
            }
            return new { id, width, sourceNodeId = edge?.SourceNodeId, sourceComponent = component,
                defaults, materialParameter = (object?)null };
        }
        var inputs = Enumerable.Range(0, 4).Select(row => Input($"row{row}", 4,
            Enumerable.Range(0, 4).Select(column => row == column ? 1d : 0d).ToArray())).ToList();
        inputs.Add(Input("visible", 1, [Parameter("visible").GetDouble()]));
        var size = Parameter("size").GetDouble();
        if (!double.IsFinite(size) || size <= 0 || size > 10000)
            throw new InvalidDataException("Debug draw size must be finite and in (0, 10000].");
        return new { nodeId = node.Id, kind = node.Kind.Value == "DebugText" ? "debugText" : "debugModel",
            previewOnly = true, debugVersion = 1, text, assetPath = path, size,
            inputs, materials = Array.Empty<object>(), textures = Array.Empty<object>(), billboard = false };
    }

    internal static bool Enabled(GraphNodeSnapshot node) =>
        node.Parameters?.SingleOrDefault(item => item.Id == "enabled")?.Value.GetBoolean() ?? true;
}
