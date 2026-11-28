using System.Text.Json;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Assets;
using LiberaFx.ViewModels.Graph;

namespace LiberaFx.Services.Sprite;

public static class SpriteCompositionAuthoring
{
    public static GraphNodeViewModel Apply(GraphWorkspaceViewModel graph, GraphNodeViewModel? target,
        EngineAsset asset, SpriteCompositionData data)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(data);
        if (target is not null && ParticleSpriteAuthoring.Supports(target))
        {
            return ParticleSpriteAuthoring.Apply(graph,target,asset,null,data);
        }
        if (!string.Equals(asset.AssetClass, "texture", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(asset.Path))
            throw new ArgumentException("Select an engine texture.", nameof(asset));
        EngineAssetPath.Validate(asset.Path);
        if (target is not null && (!graph.Nodes.Contains(target) || target.Definition.TypeName != "SpriteComposition"))
            throw new InvalidOperationException("Select a sprite composition in the active graph, or create a new node.");

        var json = data.Serialize();
        var definition = graph.AvailableNodeDefinitions.Single(item => item.TypeName == "SpriteComposition");
        var schema = target is not null && asset.Materials is not { Count: > 0 }
            ? target.GetParameterValue(definition.Parameters.Single(item => item.Id == "materialSchema")).GetString() ?? "[]"
            : RenderNodeSchema.Encode(asset);
        var values = new[] { Value("assetPath", asset.Path), Value("materialSchema", schema), Value("compositionData", json) };
        if (target is null)
        {
            RenderNodeSchema.ValidateNodeParameters(RenderNodeSchema.Effective(definition, values), values);
            var index = graph.Nodes.Count;
            graph.ApplyPlan(new LiberaGraphEditPlan(
                [new("composition", definition.Kind, asset.Name, 80 + index % 4 * 280, 80 + index / 4 * 160, values)], []));
            return graph.SelectedNode ?? throw new InvalidOperationException("Sprite composition was not selected.");
        }

        var newIds = RenderNodeSchema.Describe(schema).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var obsolete = RenderNodeSchema.Read(definition, target.Parameters).Select(item => item.Id)
            .Where(id => !newIds.Contains(id)).ToHashSet(StringComparer.Ordinal);
        var updated = (target.Parameters ?? []).Where(item => !obsolete.Contains(item.Id) &&
            values.All(value => value.Id != item.Id)).Concat(values).ToArray();
        var effective = RenderNodeSchema.Effective(definition, updated);
        RenderNodeSchema.ValidateNodeParameters(effective, updated);
        var commands = new List<IUndoableCommand> { new SetNodeParametersCommand(target, target.Parameters, updated, "sprite composition") };
        foreach (var connection in graph.Connections.Where(item => item.Target == target).ToArray())
            if (effective.InputPorts?.Any(port => port.Id == connection.TargetPortId) != true ||
                connection.Source.Definition.GetOutputPortCategory(connection.SourcePortId) !=
                effective.GetInputPortCategory(connection.TargetPortId))
                commands.Add(new RemoveGraphConnectionCommand(graph, connection));
        graph.History.Execute(new CompositeUndoableCommand("Edit sprite composition", commands));
        return target;
    }

    private static NodeParameterSnapshot Value(string id, string value) =>
        new(id, "string", JsonSerializer.SerializeToElement(value));
}
