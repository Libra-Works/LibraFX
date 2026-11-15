using System.Text.Json;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.ViewModels;

namespace LiberaFx.Services;

public sealed record SpriteEditorSettings(int Columns, int Rows, double Fps, int Frame, bool Playing)
{
    public void Validate()
    {
        if (Columns is < 1 or > 4096 || Rows is < 1 or > 4096 ||
            !double.IsFinite(Fps) || Fps is < 0 or > 10000 ||
            Frame < 0 || Frame >= (long)Columns * Rows)
            throw new ArgumentException("Invalid sprite atlas settings.");
    }

    public int FrameAt(double seconds) =>
        Playing
            ? double.IsFinite(seconds) && seconds >= 0
                ? (int)(Math.Floor(seconds * Fps) % ((long)Columns * Rows))
                : 0
            : Frame;
}

public static class SpriteEditorAuthoring
{
    public static GraphNodeViewModel Apply(GraphWorkspaceViewModel graph, GraphNodeViewModel? target,
        EngineAsset asset, SpriteEditorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(asset);
        settings.Validate();
        if (!string.Equals(asset.AssetClass, "texture", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select an engine texture.", nameof(asset));
        var schema = RenderNodeSchema.Encode(asset);
        var values = new List<NodeParameterSnapshot>
        {
            Value("assetPath", "string", asset.Path), Value("materialSchema", "string", schema),
            Value("columns", "int", settings.Columns), Value("rows", "int", settings.Rows),
            Value("fps", "float", settings.Fps), Value("frame", "float", settings.Playing ? -1d : settings.Frame),
            Value("playing", "bool", settings.Playing)
        };
        if (target is null)
        {
            var definition = graph.AvailableNodeDefinitions.Single(item =>
                item.TypeName == (settings.Playing ? "SpriteAnimation" : "SpriteDraw"));
            var index = graph.Nodes.Count;
            graph.ApplyPlan(new LiberaGraphEditPlan(
                [new("sprite", definition.Kind, asset.Name, 80 + index % 4 * 280, 80 + index / 4 * 160, values)], []));
            return graph.SelectedNode ?? throw new InvalidOperationException("Sprite node was not selected.");
        }
        if (!graph.Nodes.Contains(target) || target.Definition.TypeName is not ("SpriteDraw" or "SpriteAnimation"))
            throw new InvalidOperationException("Select a sprite node in the active graph.");

        var previousSchema = RenderNodeSchema.Read(
            GraphNodeDefinition.All.Single(item => item.Kind == target.Definition.Kind), target.Parameters);
        if (asset.Materials is not { Count: > 0 })
        {
            var previous = target.GetParameterValue(target.Definition.Parameters.Single(item => item.Id == "materialSchema"));
            schema = previous.GetString() ?? "[]";
            values[1] = Value("materialSchema", "string", schema);
        }
        var newIds = RenderNodeSchema.Describe(schema).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var obsolete = previousSchema.Select(item => item.Id).Where(id => !newIds.Contains(id)).ToHashSet(StringComparer.Ordinal);
        var updated = (target.Parameters ?? []).Where(item => !obsolete.Contains(item.Id) &&
            values.All(value => value.Id != item.Id)).Concat(values).ToArray();
        var effective = RenderNodeSchema.Effective(
            GraphNodeDefinition.All.Single(item => item.Kind == target.Definition.Kind), updated);
        RenderNodeSchema.ValidateNodeParameters(effective, updated);
        var commands = new List<IUndoableCommand> { new SetNodeParametersCommand(target, target.Parameters, updated, "sprite atlas") };
        foreach (var connection in graph.Connections.Where(item => item.Target == target).ToArray())
            if (effective.InputPorts?.Any(port => port.Id == connection.TargetPortId) != true ||
                connection.Source.Definition.GetOutputPortCategory(connection.SourcePortId) !=
                effective.GetInputPortCategory(connection.TargetPortId))
                commands.Add(new RemoveGraphConnectionCommand(graph, connection));
        graph.History.Execute(new CompositeUndoableCommand("Edit sprite atlas", commands));
        return target;
    }

    private static NodeParameterSnapshot Value<T>(string id, string type, T value) =>
        new(id, type, JsonSerializer.SerializeToElement(value));
}
