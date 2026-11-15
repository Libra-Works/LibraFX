using System.Text.Json;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Compilation;

public static class RenderProgramBindings
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static bool IsRenderNode(GraphNodeKind kind) => GraphNodeDefinition.All.Any(definition =>
        definition.Kind == kind && definition.TypeName is "ModelDraw" or "SpriteDraw" or "SpriteAnimation");

    public static byte[] CreateScene(GraphSnapshot graph, long programSequence)
    {
        var nodes = Flatten(graph).ToDictionary(node => node.Id);
        var connections = Connections(graph).ToLookup(connection => connection.TargetNodeId);
        var renderNodes = new List<object>();
        foreach (var node in nodes.Values.Where(node => IsRenderNode(node.Kind)))
        {
            if (renderNodes.Count >= 128) throw new InvalidDataException("Preview supports at most 128 draw nodes.");
            var definition = RenderNodeSchema.Effective(GraphNodeDefinition.All.Single(item => item.Kind == node.Kind), node.Parameters);
            JsonElement Parameter(string id)
            {
                var declared = definition.Parameters.Single(item => item.Id == id);
                return node.Parameters?.FirstOrDefault(item => item.Id == id && item.Type == declared.Type)?.Value
                    ?? declared.DefaultValue;
            }
            var path = Parameter("assetPath").GetString() ?? "";
            if (path.Length == 0) continue;
            PreviewAssetStore.ValidateAssetPath(path);
            var materialEntries = RenderNodeSchema.Read(definition, node.Parameters);
            object? MaterialParameter(string id)
            {
                var entry = materialEntries.FirstOrDefault(value => value.Id == id);
                if (entry is null) return null;
                return new
                {
                    name = entry.Name, kind = entry.Kind, slot = entry.Slot,
                    association = entry.Association, index = entry.Index
                };
            }
            var inputs = new List<object>();
            foreach (var port in definition.InputPorts ?? [])
            {
                var type = port.PortType ?? definition.PortType;
                var width = type switch { "Float" => 1, "FloatX2" => 2, "FloatX3" => 3, "FloatX4" or "Color" => 4, _ => 0 };
                if (width == 0) continue;
                var matches = connections[node.Id].Where(connection => connection.TargetPortId == port.Id).ToArray();
                if (matches.Length > 1) throw new InvalidDataException("A render pin has multiple sources.");
                Guid? sourceId = null;
                var component = 0;
                if (matches.Length == 1)
                {
                    var connection = matches[0];
                    if (!nodes.TryGetValue(connection.SourceNodeId, out var source) || IsRenderNode(source.Kind))
                        throw new InvalidDataException("Render inputs require numeric runtime sources.");
                    sourceId = source.Id;
                    var sourceDefinition = GraphNodeDefinition.All.Single(item => item.Kind == source.Kind);
                    if (sourceDefinition.TypeName.StartsWith("BreakVector", StringComparison.Ordinal))
                    {
                        component = sourceDefinition.OutputPorts!.ToList().FindIndex(output => output.Id == connection.SourcePortId);
                        if (component < 0) throw new InvalidDataException("Invalid render source component.");
                    }
                }
                var value = Parameter(port.Id);
                var defaults = width == 1 ? new[] { value.GetDouble() } : value.EnumerateArray().Select(lane => lane.GetDouble()).ToArray();
                inputs.Add(new { id = port.Id, width, sourceNodeId = sourceId, sourceComponent = component,
                    defaults, materialParameter = MaterialParameter(port.Id) });
            }
            var sprite = definition.TypeName is "SpriteDraw" or "SpriteAnimation";
            if (sprite)
            {
                foreach (var id in new[] { "columns", "rows", "fps", "playing" })
                {
                    var value = Parameter(id);
                    inputs.Add(new { id, width = 1, sourceNodeId = (Guid?)null, sourceComponent = 0,
                        defaults = new[] { value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() ? 1d : 0d : value.GetDouble() },
                        materialParameter = (object?)null });
                }
                if (Parameter("columns").GetInt32() < 1 || Parameter("rows").GetInt32() < 1 ||
                    Parameter("fps").GetDouble() < 0) throw new InvalidDataException("Invalid sprite atlas settings.");
            }
            var textures = materialEntries.Where(entry => entry.Kind == "texture")
                .Select(entry => new { name = entry.Name, slot = entry.Slot,
                    path = Parameter(entry.Id).GetString(), association = entry.Association, index = entry.Index }).ToArray();
            renderNodes.Add(new { nodeId = node.Id, kind = sprite ? "sprite" : "model", assetPath = path,
                materials = Array.Empty<object>(), inputs, textures, billboard = sprite && Parameter("billboard").GetBoolean() });
        }
        return JsonSerializer.SerializeToUtf8Bytes(new { type = "render_config", programSequence, nodes = renderNodes }, JsonOptions);
    }

    private static IEnumerable<GraphNodeSnapshot> Flatten(GraphSnapshot graph) => graph.Nodes.SelectMany(node =>
        node.ChildGraph is { } child ? Flatten(child) : new[] { node });
    private static IEnumerable<GraphConnectionSnapshot> Connections(GraphSnapshot graph) =>
        graph.Connections.Concat(graph.Nodes.Where(node => node.ChildGraph is not null).SelectMany(node => Connections(node.ChildGraph!)));
}
