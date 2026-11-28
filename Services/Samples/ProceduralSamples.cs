using System.Text.Json;
using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Samples;

/// <summary>Synthetic source graphs: no imported model, image, or engine resource mapping.</summary>
public static class ProceduralSamples
{
    public static GraphSnapshot Create(string name)
    {
        if (name is not ("Slash" or "Shockwave" or "Beam")) throw new ArgumentException("Unknown procedural sample.", nameof(name));
        var modelRecipe = new ProceduralRecipe
        {
            Shape = name == "Slash" ? 1 : name == "Shockwave" ? 4 : 8,
            SegmentsU = name == "Shockwave" ? 64u : 32u, SegmentsV = 8,
            Size = name == "Slash" ? [2, 1, 1] : name == "Shockwave" ? [3, 1, 3] : [.25f, 3, .25f],
            PivotMode = name == "Slash" ? 5u : name == "Shockwave" ? 3u : 1u,
            Modifiers = name == "Beam"
                ? [Modifier(5, 1, [.03f, 5, 0, .8f, .5f], 17)]
                : [Modifier(0, 0, [20], 1), Modifier(3, name == "Shockwave" ? 1 : 2, [.06f, 3, 0, 2], 17),
                    Modifier(12, 1, [.02f, 0], 1)]
        };
        var textureRecipe = ProceduralResourceSchema.Default("ProceduralTexture") with
        {
            Operations = [Pixel(13, 0, 0, [8, 5, .15f, .08f, 4, .5f]),
                Pixel(27, 1, 0, [0, .05f, .2f, 0, .15f, .65f, 1, 1, .15f, .8f])], OutputRegister = 2
        };
        var materialRecipe = ProceduralResourceSchema.Default("ProceduralMaterial") with
        {
            Blend = "additive", Emission = 1.5f, Operations = [Pixel(30, 0, 0, [0])], OutputRegister = 1
        };
        GraphNodeSnapshot Producer(string kind, ProceduralRecipe recipe, double y)
        {
            var definition = GraphNodeDefinition.All.Single(d => d.TypeName == kind);
            return new(Guid.NewGuid(), definition.Kind, name + " " + definition.Name, 0, y,
                definition.PortType, definition.PortCategory, Parameters:
                [new("proceduralState", "proceduralState", JsonSerializer.SerializeToElement(ProceduralResourceSchema.Encode(recipe)))]);
        }
        var model = Producer("ProceduralModel", modelRecipe, 0);
        var texture = Producer("ProceduralTexture", textureRecipe, 250);
        var material = Producer("ProceduralMaterial", materialRecipe, 500);
        var drawDefinition = GraphNodeDefinition.All.Single(d => d.TypeName == "ModelDraw");
        var draw = new GraphNodeSnapshot(Guid.NewGuid(), drawDefinition.Kind, name + " Draw", 430, 100,
            drawDefinition.PortType, drawDefinition.PortCategory);
        return new([model, texture, material, draw],
        [new(model.Id, draw.Id, "proceduralModel", "value"), new(texture.Id, material.Id, "texture0", "value"),
            new(material.Id, draw.Id, "proceduralMaterial", "value")]) { AutoDestroyWhenIdle = false };
    }
    private static ProceduralModifier Modifier(int kind, int axis, float[] values, uint flags)
    {
        var packed = new float[16]; values.CopyTo(packed, 0);
        return new() { Kind = kind, Axis = axis, Values = packed, Flags = flags };
    }
    private static ProceduralPixelOperation Pixel(uint kind, uint a, uint b, float[] values)
    {
        var packed = new float[12]; values.CopyTo(packed, 0);
        return new() { Kind = kind, InputA = a, InputB = b, Values = packed };
    }
}
