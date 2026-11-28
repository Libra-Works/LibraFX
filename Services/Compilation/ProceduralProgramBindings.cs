using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Gpu;

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
        foreach (var draw in nodes.Values.Where(n =>
            GraphNodeDefinition.All.Single(d => d.Kind == n.Kind).TypeName == "ModelDraw" ||
            FlexiblePlaneDrawSchema.IsNode(n.Kind) ||
            CpuParticleSchema.IsContainer(n.Kind) ||
            GpuParticleEditorService.IsGpu(n.Kind)))
        {
            var isModel = GraphNodeDefinition.All.Single(d => d.Kind == draw.Kind).TypeName == "ModelDraw";
            var model = isModel ? Source(draw, "proceduralModel", "ProceduralModel", nodes, incoming, scopes) : null;
            var directTexture = isModel ? null : Source(draw, "proceduralTexture", "ProceduralTexture", nodes, incoming, scopes);
            var material = Source(draw, "proceduralMaterial", "ProceduralMaterial", nodes, incoming, scopes);
            if (model is { } modelId) used.Add(modelId);
            if (directTexture is { } directTextureId) used.Add(directTextureId);
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
            var effective = ProceduralResourceSchema.EffectiveBlocks(definition, node.Parameters);
            var inputs = ResourceInputs(node, kind, recipe, effective, nodes, incoming);
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

    private static object[] ResourceInputs(GraphNodeSnapshot node, string kind, ProceduralRecipe recipe,
        GraphNodeDefinition effective, IReadOnlyDictionary<Guid, GraphNodeSnapshot> nodes, ILookup<Guid, GraphConnectionSnapshot> incoming)
    {
        var active = Inputs(node, effective, nodes, incoming)
            .ToDictionary(item => (string)item.GetType().GetProperty("id")!.GetValue(item)!, StringComparer.Ordinal);
        var result = new List<object>();
        if (kind == "ProceduralModel")
        {
            foreach (var modifier in recipe.Modifiers)
            {
                for (var block = 0; block < 4; block++)
                {
                    var id = ProceduralResourceSchema.Port(modifier.Id, block);
                    if (active.TryGetValue(id, out var existing))
                        result.Add(existing);
                    else
                        result.Add(DummyInput(id, modifier.Values, block));
                }
            }
        }
        else
        {
            foreach (var op in recipe.Operations)
            {
                for (var block = 0; block < 3; block++)
                {
                    var id = ProceduralResourceSchema.Port(op.Id, block);
                    if (active.TryGetValue(id, out var existing))
                        result.Add(existing);
                    else
                        result.Add(DummyInput(id, op.Values, block));
                }
            }
        }
        return result.ToArray();
    }

    private static object DummyInput(string id, float[] values, int block)
    {
        var offset = block * 4;
        var defaults = new double[4];
        for (var c = 0; c < 4 && offset + c < values.Length; c++)
            defaults[c] = values[offset + c];
        return new { id, width = 4, sourceNodeId = (Guid?)null, sourceComponent = 0, defaults, materialParameter = (object?)null };
    }

    internal static object[] Inputs(GraphNodeSnapshot node, GraphNodeDefinition definition,
        IReadOnlyDictionary<Guid, GraphNodeSnapshot> nodes, ILookup<Guid, GraphConnectionSnapshot> incoming)
    {
        var inputs = new List<object>();
        foreach (var port in definition.InputPorts ?? [])
        {
            var width = port.PortType switch { "Float" => 1, "FloatX2" => 2, "FloatX3" => 3, "FloatX4" or "Color" => 4, _ => 0 };
            if (width == 0) continue;
            var parameter = definition.Parameters.Single(p => p.Id == port.Id);
            var value = node.Parameters?.FirstOrDefault(p => p.Id == port.Id)?.Value ?? parameter.DefaultValue;
            if (!NodeParameter.IsValidValue(parameter.Type, value))
                throw new InvalidDataException("Invalid procedural input default.");
            var defaults = value.ValueKind == JsonValueKind.Number
                ? [value.GetDouble()]
                : value.EnumerateArray().Take(width).Select(v => v.GetDouble()).ToArray();
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
