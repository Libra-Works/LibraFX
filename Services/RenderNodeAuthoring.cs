using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.ViewModels;

namespace LiberaFx.Services;

public static class RenderNodeAuthoring
{
    public static GraphNodeViewModel CreateModel(
        GraphWorkspaceViewModel graph,
        EngineAsset asset,
        double? x = null,
        double? y = null)
    {
        return CreateNode(graph, "ModelDraw", asset, [], x, y);
    }

    public static GraphNodeViewModel CreateSprite(
        GraphWorkspaceViewModel graph,
        EngineAsset asset,
        int columns = 1,
        int rows = 1,
        double fps = 12,
        double? x = null,
        double? y = null)
    {
        if (columns is < 1 or > 4096 || rows is < 1 or > 4096 ||
            !double.IsFinite(fps) || fps is < 0 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(columns), "Sprite atlas settings are invalid.");
        var values = new[]
        {
            new NodeParameterSnapshot("columns", "int", JsonSerializer.SerializeToElement(columns)),
            new NodeParameterSnapshot("rows", "int", JsonSerializer.SerializeToElement(rows)),
            new NodeParameterSnapshot("fps", "float", JsonSerializer.SerializeToElement(fps))
        };
        return CreateNode(graph, fps > 0 ? "SpriteAnimation" : "SpriteDraw", asset, values, x, y);
    }

    public static void AssignAsset(GraphWorkspaceViewModel graph, GraphNodeViewModel node, EngineAsset asset)
    {
        ArgumentNullException.ThrowIfNull(graph);
        graph.AssignRenderAsset(node, asset);
    }

    private static GraphNodeViewModel CreateNode(
        GraphWorkspaceViewModel graph,
        string typeName,
        EngineAsset asset,
        IReadOnlyList<NodeParameterSnapshot> extraParameters,
        double? x,
        double? y)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var schema = RenderNodeSchema.Encode(asset);
        var definition = graph.AvailableNodeDefinitions.FirstOrDefault(item =>
            string.Equals(item.TypeName, typeName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"The {typeName} node definition is unavailable.");
        var parameters = new[]
        {
            new NodeParameterSnapshot("assetPath", "string", JsonSerializer.SerializeToElement(asset.Path)),
            new NodeParameterSnapshot("materialSchema", "string", JsonSerializer.SerializeToElement(schema))
        }.Concat(extraParameters).ToArray();
        var index = graph.Nodes.Count;
        graph.ApplyPlan(new LiberaGraphEditPlan(
            [new LiberaGraphNodeSpec("render", definition.Kind, asset.Name,
                x ?? 80 + index % 4 * 280, y ?? 80 + index / 4 * 160, parameters)],
            []));
        return graph.SelectedNode
            ?? throw new InvalidOperationException("The render node was not added.");
    }
}
