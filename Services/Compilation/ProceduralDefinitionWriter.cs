using System.Text;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Compilation;

/// <summary>Little-endian PFX1 definition consumed identically by native engine adapters.</summary>
internal static class ProceduralDefinitionWriter
{
    internal static string Encode(string kind, ProceduralRecipe recipe, GraphNodeSnapshot node,
        ILookup<Guid, GraphConnectionSnapshot> incoming)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(0x31584650u); // PFX1
        writer.Write(1u);
        writer.Write(kind == "ProceduralModel" ? 0u : kind == "ProceduralTexture" ? 1u : 2u);
        writer.Write((uint)recipe.Resolution);
        writer.Write(recipe.Blend == "additive" ? 1u : 0u);
        writer.Write(recipe.DoubleSided ? 1u : 0u);
        writer.Write(recipe.Emission);
        writer.Write(0u);
        float[] Values(Guid id, float[] defaults)
        {
            var result = (float[])defaults.Clone();
            for (var block = 0; block < result.Length / 4; ++block)
            {
                var port = ProceduralResourceSchema.Port(id, block);
                var saved = node.Parameters?.FirstOrDefault(p => p.Id == port);
                if (saved is null) continue;
                if (saved.Type != "vector4" || !NodeParameter.IsValidValue("vector4", saved.Value))
                    throw new InvalidDataException("Invalid procedural default block.");
                var values = saved.Value.EnumerateArray().Select(v => v.GetSingle()).ToArray();
                values.CopyTo(result, block * 4);
            }
            return result;
        }
        if (kind == "ProceduralModel")
        {
            bool Connected(ProceduralModifier m) => Enumerable.Range(0, 4).Any(block => incoming[node.Id]
                .Any(edge => edge.TargetPortId == ProceduralResourceSchema.Port(m.Id, block)));
            var effective = recipe with { Modifiers = recipe.Modifiers.Select(m => m with { Values = Values(m.Id, m.Values) }).ToArray() };
            ProceduralResourceSchema.Validate(effective, kind);
            ProceduralResourceSchema.ValidateModelBudget(effective, recipe.Modifiers.Any(Connected));
            var animated = recipe.AnimatedBase || recipe.Modifiers.Any(m => Connected(m) || (m.Flags & 16) != 0 ||
                m.Kind is 3 or 5 && Values(m.Id, m.Values)[3] != 0);
            writer.Write(1u); writer.Write((uint)recipe.Shape); writer.Write(recipe.SegmentsU); writer.Write(recipe.SegmentsV);
            foreach (var value in recipe.Size) writer.Write(value);
            writer.Write(recipe.InnerRadius); writer.Write(recipe.SweepDegrees); writer.Write(recipe.Turns); writer.Write(recipe.TubeRadius);
            writer.Write(recipe.PivotMode);
            writer.Write((recipe.Capped ? 1u : 0u) | 2u | (animated ? 4u : 0u) | (recipe.AnimatedBase ? 8u : 0u));
            foreach (var value in recipe.Pivot) writer.Write(value);
            writer.Write(0u); writer.Write(0u); writer.Write(recipe.PivotVertex); writer.Write(0u);
            writer.Write((uint)recipe.Modifiers.Length);
            foreach (var m in recipe.Modifiers)
            {
                writer.Write((uint)m.Kind); writer.Write((uint)m.Axis); writer.Write(m.Flags | (Connected(m) ? 16u : 0u)); writer.Write(m.Count);
                writer.Write(m.Seed); writer.Write(0u); writer.Write(0u); writer.Write(0u);
                foreach (var value in Values(m.Id, m.Values)) writer.Write(value);
            }
        }
        else
        {
            writer.Write(1u); writer.Write((uint)recipe.Operations.Length); writer.Write(recipe.OutputRegister);
            writer.Write(kind == "ProceduralMaterial" ? 1u : 0u);
            foreach (var op in recipe.Operations)
            {
                writer.Write(op.Kind); writer.Write(op.InputA); writer.Write(op.InputB); writer.Write(op.Seed);
                foreach (var value in Values(op.Id, op.Values)) writer.Write(value);
            }
        }
        return Convert.ToBase64String(stream.ToArray());
    }
}
