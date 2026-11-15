using System.Text.Json;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;

internal static class EffectSampleAuthoringTests
{
    public static void Run()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var existing = graph.CreateNode(GraphNodeKind.Float);
        var model = new EngineAsset("/Engine/BasicShapes/Cube.Cube", "Cube", "model", "Unreal",
            Materials: [new(0, "/Engine/BasicShapes/BasicShapeMaterial.BasicShapeMaterial", "Basic",
                [new("Color", "vector", JsonSerializer.SerializeToElement(new[] { 1, 1, 1, 1 }))])]);
        _ = EffectSampleAuthoring.Create(graph, model, new("Unreal", model.Path), "color-box");
        var draw = graph.Nodes.Single(node => node.Definition.TypeName == "ModelDraw");
        var pin = RenderNodeSchema.Read(draw.Definition, draw.Parameters).Single().Id;
        Require(graph.Connections.Any(connection => connection.Target.Id == draw.Id && connection.TargetPortId == pin),
            "The time-driven color must connect to the actual material pin.");
        var program = VfxProgram.Compile(graph.CreateSnapshot());
        Require(program.TypedInstructions.All(instruction => instruction.NodeId != draw.Id) &&
            program.TypedInstructions.Any(instruction => instruction.NodeId == existing.Id),
            "Draw bindings must be separate from the portable CPU bytecode, preserving existing nodes.");
        using (var scene = JsonDocument.Parse(RenderProgramBindings.CreateScene(graph.CreateSnapshot(), 42)))
        {
            var inputs = scene.RootElement.GetProperty("nodes")[0].GetProperty("inputs");
            Require(inputs.EnumerateArray().Any(input => input.GetProperty("id").GetString() == pin &&
                input.GetProperty("sourceNodeId").ValueKind == JsonValueKind.String),
                "Compiled scene bindings must bind the material's computed source, not its default.");
        }
        Require(graph.History.Undo() && graph.Nodes.Count == 1 && graph.Nodes.Single().Id == existing.Id,
            "Sample creation must be one undoable edit without removing pre-existing nodes.");
        Require(graph.History.Redo(), "Sample redo must restore the full graph.");
        _ = VfxProgram.Compile(graph.CreateSnapshot());

        var sprite = new EngineAsset("/Game/Effects/Flash.Flash", "Flash", "texture", "Unreal", 256, 256);
        _ = EffectSampleAuthoring.Create(graph, sprite,
            new("Unreal", sprite.Path, AtlasColumns: 4, AtlasRows: 4, FramesPerSecond: 16), "hitmark", 0.5);
        var hit = graph.Nodes.Single(node => node.Definition.TypeName == "SpriteAnimation");
        using var hitScene = JsonDocument.Parse(RenderProgramBindings.CreateScene(graph.CreateSnapshot(), 43));
        var command = hitScene.RootElement.GetProperty("nodes").EnumerateArray()
            .Single(node => node.GetProperty("nodeId").GetGuid() == hit.Id);
        Require(command.GetProperty("inputs").EnumerateArray().Any(input => input.GetProperty("id").GetString() == "tint" &&
            input.GetProperty("sourceNodeId").ValueKind == JsonValueKind.String),
            "Hitmark alpha fade must reach the sprite renderer.");
        _ = VfxProgram.Compile(graph.CreateSnapshot());
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
