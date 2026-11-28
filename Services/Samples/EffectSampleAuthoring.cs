using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.ViewModels;

namespace LiberaFx.Services;

public static class EffectSampleAuthoring
{
    public static GraphSnapshot Create(GraphWorkspaceViewModel graph, EngineAsset asset, AssetMetadata metadata,
        string style, double duration = 0.5)
    {
        if (style is not ("color-box" or "sprite" or "sprite-animation" or "hitmark") ||
            !double.IsFinite(duration) || duration is < 0.05 or > 30)
            throw new ArgumentException("Choose color-box, sprite, sprite-animation or hitmark and a valid duration.");
        if (asset.AssetClass != (style == "color-box" ? "model" : "texture"))
            throw new ArgumentException("The sample requires the corresponding model or texture asset.");
        var schema = RenderNodeSchema.Encode(asset);
        var specifications = new List<LiberaGraphNodeSpec>();
        var connections = new List<LiberaGraphConnectionSpec>();
        GraphNodeKind Kind(string name) => graph.AvailableNodeDefinitions.Single(definition => definition.TypeName == name).Kind;
        static NodeParameterSnapshot Parameter(string id, string type, object value) =>
            new(id, type, JsonSerializer.SerializeToElement(value));
        void Add(string key, string kind, double x, double y, params NodeParameterSnapshot[] parameters) =>
            specifications.Add(new(key, Kind(kind), $"Sample {style}: {key}", x, y, parameters.Length == 0 ? null : parameters));
        void Connect(string source, string target, string port, string? output = null) =>
            connections.Add(new(source, target, port, output));

        var drawParameters = new List<NodeParameterSnapshot>
        {
            Parameter("assetPath", "string", asset.Path),
            Parameter("materialSchema", "string", schema)
        };
        if (style == "color-box")
        {
            var material = RenderNodeSchema.Describe(schema).Where(parameter => parameter.Kind == "vector")
                .OrderByDescending(parameter => parameter.Name.Contains("color", StringComparison.OrdinalIgnoreCase) ||
                    parameter.Name.Contains("tint", StringComparison.OrdinalIgnoreCase)).FirstOrDefault()
                ?? throw new InvalidOperationException("This model has no exposed vector material parameter for the color sample.");
            Add("time", "Time", 20, 30);
            Add("speed", "Multiply", 300, 30, Parameter("b", "float", 3.0));
            Add("wave", "Sin", 580, 30);
            Add("amplitude", "Multiply", 860, 30, Parameter("b", "float", 0.5));
            Add("bias", "Add", 1140, 30, Parameter("b", "float", 0.5));
            Add("color", "ComposeVector4", 1420, 30, Parameter("g", "float", 0.2),
                Parameter("b", "float", 0.85), Parameter("a", "float", 1.0));
            Connect("time", "speed", "a");
            Connect("speed", "wave", "a");
            Connect("wave", "amplitude", "a", "value");
            Connect("amplitude", "bias", "a");
            Connect("bias", "color", "r");
            Connect("color", "draw", material.Id, "value");
            specifications.Add(new("draw", Kind("ModelDraw"), "Sample: time-driven material color", 1700, 30, drawParameters));
        }
        else
        {
            drawParameters.Add(Parameter("columns", "int", metadata.AtlasColumns));
            drawParameters.Add(Parameter("rows", "int", metadata.AtlasRows));
            drawParameters.Add(Parameter("fps", "float", style == "hitmark" ?
                metadata.AtlasColumns * metadata.AtlasRows / duration : metadata.FramesPerSecond));
            drawParameters.Add(Parameter("playing", "bool", style != "sprite"));
            drawParameters.Add(Parameter("frame", "float", style == "sprite" ? 0d : -1d));
            if (style == "hitmark")
            {
                Add("time", "Time", 20, 30);
                Add("progress", "Multiply", 300, 30, Parameter("b", "float", 1 / duration));
                Add("clamp", "Clamp", 580, 30);
                Add("fade", "Subtract", 860, 30, Parameter("a", "float", 1.0));
                Add("flash", "ComposeVector4", 1140, 30, Parameter("r", "float", 2.0),
                    Parameter("g", "float", 1.4), Parameter("b", "float", 0.6));
                Connect("time", "progress", "a");
                Connect("progress", "clamp", "a");
                Connect("clamp", "fade", "b", "value");
                Connect("fade", "flash", "a");
                Connect("flash", "draw", "tint", "value");
            }
            specifications.Add(new("draw", Kind(style == "sprite" ? "SpriteDraw" : "SpriteAnimation"),
                $"Sample: {style}", style == "hitmark" ? 1420 : 40, 30, drawParameters));
        }
        return graph.ApplyPlan(new(specifications, connections));
    }
}
