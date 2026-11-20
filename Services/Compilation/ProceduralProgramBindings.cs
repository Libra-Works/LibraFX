using System.Text.Json;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Compilation;

/// <summary>Resources are scoped package metadata; only continuous inputs enter the numeric program.</summary>
internal static class ProceduralProgramBindings
{
    internal static Guid? Source(GraphNodeSnapshot consumer, string port, string expected,
        IReadOnlyDictionary<Guid, GraphNodeSnapshot> nodes, ILookup<Guid, GraphConnectionSnapshot> incoming,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> scopes)
    {
        var edges = incoming[consumer.Id].Where(e => e.TargetPortId == port).ToArray();
        if (edges.Length == 0) return null;
        if (edges.Length != 1 || !nodes.TryGetValue(edges[0].SourceNodeId, out var source) ||
            ProceduralResourceSchema.Name(source.Kind) != expected || edges[0].SourcePortId != "value" ||
            !(scopes.GetValueOrDefault(consumer.Id) ?? []).SequenceEqual(scopes.GetValueOrDefault(source.Id) ?? []))
        {
            throw new InvalidDataException($"Procedural input '{port}' requires one {expected} output in the same instance scope.");
        }
        return source.Id;
    }

    internal static object[] Resources(IReadOnlyDictionary<Guid, GraphNodeSnapshot> nodes,
        ILookup<Guid, GraphConnectionSnapshot> incoming, IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> scopes)
    {
        var result = new List<object>();
        ProceduralRecipe Recipe(GraphNodeSnapshot node)
        {
            var r = ProceduralResourceSchema.ReadEffective(GraphNodeDefinition.All.Single(d => d.Kind == node.Kind), node.Parameters);
            return ProceduralResourceSchema.Name(node.Kind) == "ProceduralModel" ? r : ProceduralPixelCompilation.Prune(r);
        }
        var used = new HashSet<Guid>();
        foreach (var draw in nodes.Values.Where(n => GraphNodeDefinition.All.Single(d => d.Kind == n.Kind).TypeName == "ModelDraw"))
        {
            var model = Source(draw, "proceduralModel", "ProceduralModel", nodes, incoming, scopes);
            var material = Source(draw, "proceduralMaterial", "ProceduralMaterial", nodes, incoming, scopes);
            if (model is { } modelId) used.Add(modelId);
            if (material is { } materialId)
            {
                used.Add(materialId);
                foreach (var slot in ProceduralPixelCompilation.TextureSlots(nodes[materialId], Recipe(nodes[materialId]), incoming))
                {
                    var texture = Source(nodes[materialId], "texture" + slot, "ProceduralTexture", nodes, incoming, scopes);
                    if (texture is { } textureId) used.Add(textureId);
                }
            }
        }
        foreach (var node in nodes.Values.Where(n => used.Contains(n.Id)))
        {
            if (result.Count >= 64) throw new InvalidDataException("A scope supports at most 64 procedural resources.");
            var definition = GraphNodeDefinition.All.Single(d => d.Kind == node.Kind);
            var kind = ProceduralResourceSchema.Name(node.Kind)!;
            var recipe = Recipe(node);
            var effective = ProceduralResourceSchema.Effective(definition, node.Parameters);
            var ports = recipe.Modifiers.SelectMany(m => Enumerable.Range(0, 4).Select(b => ProceduralResourceSchema.Port(m.Id, b)))
                .Concat(recipe.Operations.SelectMany(op => Enumerable.Range(0, 3).Select(b => ProceduralResourceSchema.Port(op.Id, b)))).ToHashSet(StringComparer.Ordinal);
            var inputs = Inputs(node, effective with { InputPorts = effective.InputPorts!.Where(p => ports.Contains(p.Id)).ToArray() }, nodes, incoming);
            var slots = ProceduralPixelCompilation.TextureSlots(node, recipe, incoming);
            var textures = kind == "ProceduralMaterial" ? slots.Order().Select(slot => new
            {
                slot, producerNodeId = Source(node, "texture" + slot, "ProceduralTexture", nodes, incoming, scopes)
            }).Where(t => t.producerNodeId is not null).ToArray() : [];
            if (kind == "ProceduralMaterial" && recipe.Operations.Any(op => op.Kind == 30 &&
                !textures.Any(t => t.slot == (int)op.Values[0])))
            {
                throw new InvalidDataException("Procedural Material samples an unconnected texture slot.");
            }
            result.Add(new { nodeId = node.Id, kind, recipe, inputs, textures,
                definition = ProceduralDefinitionWriter.Encode(kind, recipe, node, incoming) });
        }
        return result.ToArray();
    }

    internal static object[] Inputs(GraphNodeSnapshot node, GraphNodeDefinition definition,
        IReadOnlyDictionary<Guid, GraphNodeSnapshot> nodes, ILookup<Guid, GraphConnectionSnapshot> incoming)
    {
        var inputs = new List<object>();
        foreach (var port in definition.InputPorts ?? [])
        {
            var width = port.PortType switch { "Float" => 1, "FloatX3" => 3, "FloatX4" => 4, _ => 0 };
            if (width == 0) continue;
            var parameter = definition.Parameters.Single(p => p.Id == port.Id);
            var value = node.Parameters?.FirstOrDefault(p => p.Id == port.Id)?.Value ?? parameter.DefaultValue;
            if (!NodeParameter.IsValidValue(parameter.Type, value))
                throw new InvalidDataException("Invalid procedural input default.");
            var defaults = width == 1 ? [value.GetDouble()] : value.EnumerateArray().Select(v => v.GetDouble()).ToArray();
            var edges = incoming[node.Id].Where(e => e.TargetPortId == port.Id).ToArray();
            if (edges.Length > 1) throw new InvalidDataException("Multiple procedural input sources.");
            Guid? sourceNodeId = null;
            var sourceComponent = 0;
            if (edges.Length == 1)
            {
                if (!nodes.TryGetValue(edges[0].SourceNodeId, out var source) ||
                    ProceduralResourceSchema.IsMetadata(source.Kind) || RenderProgramBindings.IsRenderNode(source.Kind))
                    throw new InvalidDataException("Procedural continuous values require numeric sources.");
                sourceNodeId = source.Id;
                var sourceDefinition = GraphNodeDefinition.All.Single(d => d.Kind == source.Kind);
                if (sourceDefinition.TypeName.StartsWith("BreakVector", StringComparison.Ordinal))
                {
                    sourceComponent = sourceDefinition.OutputPorts!.ToList().FindIndex(p => p.Id == edges[0].SourcePortId);
                    if (sourceComponent < 0) throw new InvalidDataException("Invalid procedural source component.");
                }
            }
            inputs.Add(new { id = port.Id, width, sourceNodeId, sourceComponent, defaults, materialParameter = (object?)null });
        }
        return inputs.ToArray();
    }

    internal static object Draw(GraphNodeSnapshot node, Guid source, Guid? material,
        IReadOnlyDictionary<Guid, GraphNodeSnapshot> nodes, ILookup<Guid, GraphConnectionSnapshot> incoming)
    {
        var definition = GraphNodeDefinition.All.Single(d => d.Kind == node.Kind);
        return new { nodeId = node.Id, kind = "proceduralModel", assetPath = "builtin:procedural-model",
            proceduralModel = source, proceduralMaterial = material,
            inputs = Inputs(node, definition, nodes, incoming) };
    }
}
