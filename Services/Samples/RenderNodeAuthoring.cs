using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Project;
using LiberaFx.ViewModels.Graph;

namespace LiberaFx.Services.Samples;

public static class RenderNodeAuthoring
{
    public static GraphNodeViewModel CreateModel(
        GraphWorkspaceViewModel graph,
        EngineAsset asset,
        double? x = null,
        double? y = null)
    {
        ValidateAssetClass(asset, "model");
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
        ValidateAssetClass(asset, "texture");
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

    public static void ValidateAssetClass(EngineAsset asset, string expected)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.AssetClass != expected)
            throw new ArgumentException($"This target requires a {expected} asset.", nameof(asset));
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
        var defaultX = graph.Nodes.Count == 0
            ? 80
            : graph.Nodes.Select(existing => GraphFlowLayout.Bounds(existing).Right + 120).Max();
        graph.ApplyPlan(new LiberaGraphEditPlan(
            [new LiberaGraphNodeSpec("render", definition.Kind, asset.Name,
                x ?? defaultX, y ?? 80, parameters)],
            []));
        return graph.SelectedNode
            ?? throw new InvalidOperationException("The render node was not added.");
    }
}
