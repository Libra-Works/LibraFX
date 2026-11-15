using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;
using LiberaFx.Views;

internal static class SpriteEditorTests
{
    public static void Run(string? fixtureDirectory = null)
    {
        var atlas = new EngineAsset("/Game/FX/T_Atlas.T_Atlas", "Atlas", "texture", "Unreal");
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var settings = new SpriteEditorSettings(4, 2, 6, 3, false);
        var sprite = SpriteEditorAuthoring.Apply(graph, null, atlas, settings);
        var id = sprite.Id;
        var before = graph.CreateSnapshot();
        graph.History.Undo();
        if (graph.Nodes.Count != 0) throw new Exception("Creating a sprite was not one undo step.");
        graph.History.Redo();
        sprite = graph.GetNode(id);
        var restored = new GraphWorkspaceViewModel(new UndoRedoManager());
        var json = JsonSerializer.Serialize(graph.CreateSnapshot());
        restored.InitializeFromSnapshot(JsonSerializer.Deserialize<GraphSnapshot>(json)!);
        VerifyScene(restored.CreateSnapshot(), 4, 2, 6, 3, false);

        SpriteEditorAuthoring.Apply(graph, sprite, atlas, new(2, 3, 10, 5, true));
        VerifyScene(graph.CreateSnapshot(), 2, 3, 10, -1, true);
        graph.History.Undo();
        if (!GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()))
            throw new Exception("Applying sprite settings was not one undo step.");
        graph.History.Redo();
        VerifyScene(graph.CreateSnapshot(), 2, 3, 10, -1, true);
        if (new SpriteEditorSettings(4, 2, 2, 3, true).FrameAt(4.5) != 1 ||
            new SpriteEditorSettings(4, 2, 0, 3, true).FrameAt(1) != 0 ||
            settings.FrameAt(5) != 3)
            throw new Exception("Sprite playback does not match the runtime frame calculation.");
        try { SpriteEditorAuthoring.Apply(graph, null, atlas, new(2, 2, 1, 4, false)); throw new Exception("Invalid frame accepted."); }
        catch (ArgumentException) { }
        try { SpriteEditorAuthoring.Apply(graph, null, atlas with { AssetClass = "model" }, settings); throw new Exception("Non-texture accepted."); }
        catch (ArgumentException) { }
        VerifyMaterialHistory(atlas);
        VerifyWindow();
        if (fixtureDirectory is not null)
        {
            var fixtureGraph = new GraphWorkspaceViewModel(new UndoRedoManager());
            var texture = new EngineAsset("/Engine/EngineResources/WhiteSquareTexture.WhiteSquareTexture",
                "Builtin atlas fixture", "texture", "Unreal");
            var animated = SpriteEditorAuthoring.Apply(fixtureGraph, null, texture, new(4, 2, 6, 0, true));
            var snapshot = fixtureGraph.CreateSnapshot();
            Directory.CreateDirectory(fixtureDirectory);
            File.WriteAllBytes(Path.Combine(fixtureDirectory, "program.lfxb"), VfxProgram.Compile(snapshot).ToBinary());
            File.WriteAllBytes(Path.Combine(fixtureDirectory, "render-config.json"), RenderProgramBindings.CreateScene(snapshot, 1));
            var frames = new[] { 0d, 0.5, 1d, 0d }.Select(time =>
            {
                var frame = new SpriteEditorSettings(4, 2, 6, 0, true).FrameAt(time);
                return new
                {
                    timeSeconds = time,
                    nodes = new[] { new
                    {
                        nodeId = animated.Id, position = new[] { 0d, 0d, 0d }, scale = new[] { 1d, 1d, 1d },
                        rotation = new[] { 0d, 0d, 0d },
                        parameters = new[] {
                            new { name = "AtlasScale", slot = 0, value = new[] { 0.25, 0.5, 0d, 0d } },
                            new { name = "AtlasOffset", slot = 0, value = new[] { frame % 4 / 4d, frame / 4 / 2d, 0d, 0d } }
                        }
                    } }
                };
            });
            File.WriteAllText(Path.Combine(fixtureDirectory, "expected.json"), JsonSerializer.Serialize(new { frames }));
        }
    }

    private static void VerifyMaterialHistory(EngineAsset atlas)
    {
        var rich = atlas with
        {
            Materials = [new EngineMaterial(0, "/Game/FX/M_Test.M_Test", "M_Test",
                [new EngineMaterialParameter("Intensity", "scalar", JsonSerializer.SerializeToElement(0.5))])]
        };
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var sprite = SpriteEditorAuthoring.Apply(graph, null, rich, new(1, 1, 0, 0, false));
        var pin = sprite.InputPorts.Single(port => port.Name == "Intensity");
        var driver = graph.CreateNode(GraphNodeKind.Float);
        if (!graph.TryConnectNodes(driver, sprite, out _, pin.Id))
            throw new Exception("Could not connect material input.");
        SpriteEditorAuthoring.Apply(graph, sprite, atlas, new(2, 1, 3, 0, true));
        if (sprite.InputPorts.All(port => port.Id != pin.Id) || graph.Connections.Count != 1)
            throw new Exception("Texture selection discarded the saved material schema or link.");
        using var scene = JsonDocument.Parse(RenderProgramBindings.CreateScene(graph.CreateSnapshot(), 1));
        var inputs = scene.RootElement.GetProperty("nodes")[0].GetProperty("inputs");
        if (!inputs.EnumerateArray().Any(item => item.GetProperty("id").GetString() == pin.Id &&
            item.GetProperty("materialParameter").GetProperty("name").GetString() == "Intensity"))
            throw new Exception("Saved material parameter did not reach the preview scene.");
        var before = graph.CreateSnapshot();
        var replacement = atlas with
        {
            Materials = [new EngineMaterial(0, "/Game/FX/M_Other.M_Other", "M_Other",
                [new EngineMaterialParameter("Other", "scalar", JsonSerializer.SerializeToElement(1d))])]
        };
        SpriteEditorAuthoring.Apply(graph, sprite, replacement, new(2, 2, 4, 1, false));
        if (graph.Connections.Count != 0) throw new Exception("Obsolete material input link was retained.");
        graph.History.Undo();
        if (!GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()))
            throw new Exception("Undo failed to restore material schema and connection.");
    }

    private static void VerifyWindow()
    {
        var main = new MainViewModel();
        var window = new SpriteEditorWindow(main);
        window.Show();
        try
        {
            var model = (SpriteEditorViewModel)window.DataContext!;
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVQIHWP8DwQMQMD4HwgYgAAAV9UH+14vURUAAAAASUVORK5CYII=");
            using var stream = new MemoryStream(png);
            model.SetLocalPreview(new Bitmap(stream));
            model.Catalog.Columns = "2";
            model.Catalog.Rows = "2";
            model.SelectFrame(2);
            Dispatcher.UIThread.RunJobs();
            var atlas = window.FindControl<SpriteAtlasPreview>("Atlas")!;
            var frame = window.FindControl<SpriteAtlasPreview>("FramePreview")!;
            if (atlas.Bounds.Width <= 0 || frame.Bounds.Width <= 0 || model.PreviewFrame != 2)
                throw new Exception("Sprite editor previews were not laid out or frame selection failed.");
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)atlas.Bounds.Width, (int)atlas.Bounds.Height));
            bitmap.Render(atlas);
        }
        finally
        {
            window.Close();
            main.DisposeStorageFiles();
        }
    }

    private static void VerifyScene(GraphSnapshot graph, int columns, int rows, double fps, double frame, bool playing)
    {
        using var scene = JsonDocument.Parse(RenderProgramBindings.CreateScene(graph, 1));
        var node = scene.RootElement.GetProperty("nodes")[0];
        if (node.GetProperty("assetPath").GetString() != "/Game/FX/T_Atlas.T_Atlas")
            throw new Exception("Texture path did not reach preview scene.");
        var inputs = node.GetProperty("inputs").EnumerateArray().ToDictionary(
            item => item.GetProperty("id").GetString()!, item => item.GetProperty("defaults")[0].GetDouble());
        if (inputs["columns"] != columns || inputs["rows"] != rows || inputs["fps"] != fps ||
            inputs["frame"] != frame || inputs["playing"] != (playing ? 1 : 0))
            throw new Exception("Atlas settings did not reach preview scene.");
    }
}
