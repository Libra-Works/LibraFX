using LiberaFx.Models.Graph;

namespace LiberaFx.Services.Compilation;

internal static class ProceduralPixelCompilation
{
    internal static ProceduralRecipe Prune(ProceduralRecipe recipe)
    {
        var used = new bool[recipe.Operations.Length + 1];
        void Visit(uint register)
        {
            if (register == 0 || used[register]) return;
            used[register] = true;
            var op = recipe.Operations[register - 1];
            if (op.Kind is not (0 or 1 or 2)) Visit(op.InputA);
            if (Binary(op.Kind)) Visit(op.InputB);
        }
        Visit(recipe.OutputRegister);
        var mapping = new uint[used.Length]; var count = 0u;
        for (var i = 1; i < used.Length; ++i) if (used[i]) mapping[i] = ++count;
        return recipe with
        {
            Operations = recipe.Operations.Where((_, i) => used[i + 1]).Select(op => op with
            {
                InputA = op.Kind is 0 or 1 or 2 ? 0 : mapping[op.InputA],
                InputB = Binary(op.Kind) ? mapping[op.InputB] : 0
            }).ToArray(),
            OutputRegister = mapping[recipe.OutputRegister]
        };
    }
    private static bool Binary(uint kind) => kind is 16 or 17 or 18 or 19 or 20 or 21 or 22 or 29;
    internal static HashSet<int> TextureSlots(GraphNodeSnapshot node, ProceduralRecipe recipe,
        ILookup<Guid, GraphConnectionSnapshot> incoming)
    {
        var result = new HashSet<int>();
        foreach (var op in recipe.Operations.Where(op => op.Kind == 30))
        {
            if (incoming[node.Id].Any(edge => edge.TargetPortId == ProceduralResourceSchema.Port(op.Id, 0)))
                result.UnionWith([0, 1, 2, 3]);
            else result.Add((int)op.Values[0]);
        }
        return result;
    }
}
