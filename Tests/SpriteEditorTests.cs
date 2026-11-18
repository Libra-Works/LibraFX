using System.Text.Json;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        VerifyArbitrarySpriteAnimation(atlas, fixtureDirectory);
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

    private static void VerifyArbitrarySpriteAnimation(EngineAsset atlas, string? fixtureDirectory)
    {
        var cellA = new SpriteAnimationCell(Guid.NewGuid(), "head", 13, 9, 41, 27);
        var cellB = new SpriteAnimationCell(Guid.NewGuid(), "spark", 101, 17, 83, 55);
        var data = new SpriteAnimationData
        {
            TextureWidth = 256,
            TextureHeight = 128,
            Cells = [cellA, cellB],
            Frames =
            [
                new SpriteAnimationFrame(Guid.NewGuid(), cellA.Id, 1),
                new SpriteAnimationFrame(Guid.NewGuid(), cellB.Id, 3),
                new SpriteAnimationFrame(Guid.NewGuid(), cellA.Id, 0.5)
            ],
            Fps = 10,
            StartFrame = 1,
            EndFrame = 2,
            Loop = false
        };
        data.Validate();
        if (data.FrameAt(-1, 0.1, true) != data.Frames[1] ||
            data.FrameAt(-1, 0.4, true) != data.Frames[2] ||
            data.FrameAt(-1, 2, true) != data.Frames[2] ||
            data.FrameAt(0, 0, false) != data.Frames[1] ||
        data.FrameAt(2, 0, false) != data.Frames[2])
            throw new Exception("Arbitrary sprite sequence duration, range or frame-index sampling is incorrect.");
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var node = SpriteEditorAuthoring.Apply(graph, null, atlas, new(3, 1, 10, 1, false), data);
        var nodeId = node.Id;
        using (var scene = JsonDocument.Parse(RenderProgramBindings.CreateScene(graph.CreateSnapshot(), 9)))
        {
            var compiled = scene.RootElement.GetProperty("nodes")[0].GetProperty("spriteAnimation");
            var cells = compiled.GetProperty("cells").EnumerateArray().ToArray();
            if (cells.Length != 2 || cells[0].GetProperty("x").GetInt32() != 13 ||
                cells[0].GetProperty("width").GetInt32() != 41 ||
                Math.Abs(cells[0].GetProperty("u").GetDouble() - 13d / 256) > 1e-12 ||
                Math.Abs(cells[1].GetProperty("uvHeight").GetDouble() - 55d / 128) > 1e-12)
                throw new Exception("Compiler did not preserve arbitrary pixel rectangles and normalized UVs.");
            var frames = compiled.GetProperty("frames").EnumerateArray().ToArray();
            if (frames[1].GetProperty("cellIndex").GetInt32() != 1 ||
                frames[1].GetProperty("cellId").GetString() != cellB.Id.ToString("D") ||
                frames[1].GetProperty("durationFrames").GetDouble() != 3)
                throw new Exception("Compiled sprite sequence lost its stable cell reference or duration.");
        }
        var serialized = JsonSerializer.Serialize(graph.CreateSnapshot());
        var restored = new GraphWorkspaceViewModel(new UndoRedoManager());
        restored.InitializeFromSnapshot(JsonSerializer.Deserialize<GraphSnapshot>(serialized)!);
        var restoredNode = restored.GetNode(nodeId);
        var restoredData = SpriteAnimationData.Deserialize(restoredNode.GetParameterValue(
            restoredNode.Definition.Parameters.Single(parameter => parameter.Id == "spriteData")));
        if (restoredData is null || restoredData.Cells[1].Id != cellB.Id ||
            restoredData.Frames[1].CellId != cellB.Id || restoredData.Frames[1].DurationFrames != 3)
            throw new Exception("Named cells or sequence references were not retained by project serialization.");
        var before = graph.CreateSnapshot();
        SpriteEditorAuthoring.Apply(graph, node, atlas, new(3, 1, 10, 2, true), data with { Loop = true });
        graph.History.Undo();
        if (!GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()))
            throw new Exception("Undo did not restore arbitrary sprite animation metadata.");
        graph.History.Redo();
        graph.PasteSnapshot(graph.CreateSnapshot(), 1000, 1000);
        var copy = graph.Nodes.Single(candidate => candidate.Id != nodeId);
        var copiedData = SpriteAnimationData.Deserialize(copy.GetParameterValue(
            copy.Definition.Parameters.Single(parameter => parameter.Id == "spriteData")));
        if (copiedData?.Cells.Single(cell => cell.Name == "spark").Id != cellB.Id)
            throw new Exception("Copy/paste did not preserve stable sprite cell IDs.");
        try
        {
            SpriteEditorAuthoring.Apply(graph, null, atlas, new(1, 1, 10, 0, false),
                data with { Cells = [cellA with { X = 250, Width = 12 }] });
            throw new Exception("An out-of-bounds arbitrary sprite rectangle was accepted.");
        }
        catch (InvalidDataException) { }
        if (fixtureDirectory is not null)
        {
            var directory = Path.Combine(fixtureDirectory, "arbitrary");
            Directory.CreateDirectory(directory);
            var fixtureGraph = new GraphWorkspaceViewModel(new UndoRedoManager());
            var texture = new EngineAsset("/Engine/EngineResources/WhiteSquareTexture.WhiteSquareTexture",
                "Arbitrary atlas fixture", "texture", "Unreal");
            var animated = SpriteEditorAuthoring.Apply(fixtureGraph, null, texture, new(3, 1, 10, 1, true), data);
            var snapshot = fixtureGraph.CreateSnapshot();
            File.WriteAllBytes(Path.Combine(directory, "program.lfxb"), VfxProgram.Compile(snapshot).ToBinary());
            File.WriteAllBytes(Path.Combine(directory, "render-config.json"), RenderProgramBindings.CreateScene(snapshot, 1));
            var frames = new[] { 0d, 0.4, 2d, 0d }.Select(time =>
            {
                var cell = data.Cells.Single(item => item.Id == data.FrameAt(-1, time, true).CellId);
                return new
                {
                    timeSeconds = time,
                    nodes = new[] { new
                    {
                        nodeId = animated.Id, position = new[] { 0d, 0d, 0d }, scale = new[] { 1d, 1d, 1d },
                        rotation = new[] { 0d, 0d, 0d },
                        parameters = new[] {
                            new { name = "AtlasScale", slot = 0, value = new[] {
                                (double)cell.Width / data.TextureWidth, (double)cell.Height / data.TextureHeight, 0d, 0d } },
                            new { name = "AtlasOffset", slot = 0, value = new[] {
                                (double)cell.X / data.TextureWidth, (double)cell.Y / data.TextureHeight, 0d, 0d } }
                        }
                    } }
                };
            });
            File.WriteAllText(Path.Combine(directory, "expected.json"), JsonSerializer.Serialize(new { frames }));
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
        var graph = main.ActiveDocument!.Workspace;
        var cell = new SpriteAnimationCell(Guid.NewGuid(), "existing draft", 0, 0, 2, 2);
        var node = SpriteEditorAuthoring.Apply(graph, null,
            new EngineAsset("/Game/FX/T_Existing.T_Existing", "Existing", "texture", "Unreal"),
            new(1, 1, 12, 0, false), new SpriteAnimationData
            {
                TextureWidth = 2,
                TextureHeight = 2,
                Cells = [cell],
                Frames = [new SpriteAnimationFrame(Guid.NewGuid(), cell.Id, 1)],
                Fps = 12,
                StartFrame = 0,
                EndFrame = 0
            });
        graph.SelectNode(node);
        var before = graph.CreateSnapshot();
        using (var fresh = new SpriteEditorViewModel(main, loadSelected: false))
        {
            if (fresh.Cells.Count != 0 || fresh.Frames.Count != 0 ||
                !GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()))
                throw new Exception("Opening a fresh asset draft inherited or changed the selected graph animation.");
            fresh.Catalog.SelectedAsset = new EngineAsset("/Game/FX/T_New.T_New", "New", "texture", "Unreal");
            graph.CreateNode(GraphNodeKind.Float);
            var changed = graph.CreateSnapshot();
            fresh.Apply(false);
            if (!GraphSnapshot.ContentEquals(changed, graph.CreateSnapshot()))
                throw new Exception("A stale sprite draft was applied after graph history changed.");
        }
        graph.SelectNode(node);
        var window = new SpriteEditorWindow(main);
        window.Show();
        try
        {
            var model = (SpriteEditorViewModel)window.DataContext!;
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVQIHWP8DwQMQMD4HwgYgAAAV9UH+14vURUAAAAASUVORK5CYII=");
            using var stream = new MemoryStream(png);
            model.SetLocalPreview(new Bitmap(stream));
            model.SelectFrame(0);
            Dispatcher.UIThread.RunJobs();
            var atlas = window.FindControl<SpriteAtlasPreview>("Atlas")!;
            var frame = window.FindControl<SpriteAtlasPreview>("FramePreview")!;
            if (atlas.Bounds.Width <= 0 || frame.Bounds.Width <= 0 || model.PreviewFrame != 0 ||
                model.SourceBitmap?.PixelSize.Width != 2 || model.Cells.Count != 1 || model.Frames.Count != 1)
                throw new Exception("Sprite source image, arbitrary cell preview or frame selection was not available.");
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)atlas.Bounds.Width, (int)atlas.Bounds.Height));
            bitmap.Render(atlas);
            VerifyGridAndParts(window, model, graph);
        }
        finally
        {
            window.Close();
            main.DisposeStorageFiles();
        }
    }

    private static void VerifyGridAndParts(SpriteEditorWindow window, SpriteEditorViewModel model, GraphWorkspaceViewModel graph)
    {
        var source = new WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var buffer = source.Lock())
        {
            byte[] row = [0, 0, 255, 255, 255, 0, 0, 255];
            Marshal.Copy(row, 0, buffer.Address, row.Length);
            Marshal.Copy(row, 0, buffer.Address + buffer.RowBytes, row.Length);
        }
        model.SetLocalPreview(source);
        model.GridWidth = model.GridHeight = "1";
        if (!model.GenerateGridFlipbook() || model.Frames.Count != 4 ||
            model.Frames.Select(frame => model.Cells.Single(cell => cell.Id == frame.CellId))
                .Select(cell => (cell.X, cell.Y)).SequenceEqual([(0, 0), (1, 0), (0, 1), (1, 1)]) == false)
            throw new Exception("Grid flipbook did not populate row-major cells.");
        var stable = model.Frames.ToArray();
        if (!model.GenerateGridFlipbook() || !stable.SequenceEqual(model.Frames))
            throw new Exception("Regenerating the same grid lost stable cell/clip IDs.");
        model.GridWidth = "0";
        if (model.GenerateGridFlipbook() || !stable.SequenceEqual(model.Frames))
            throw new Exception("Invalid grid generation partially replaced the timeline.");
        model.GridWidth = "3";
        if (!model.GenerateGridFlipbook() || model.Frames.Count != 2 ||
            model.Frames.Any(frame => model.Cells.Single(cell => cell.Id == frame.CellId).Width != 2))
            throw new Exception("Grid generation did not clip partial edge cells to the source.");
        model.GridWidth = "1";
        model.GenerateGridFlipbook();
        if (model.SnapCoordinate(.6, true) != 1 || model.SnapCoordinate(9, false) != 2)
            throw new Exception("Grid snapping did not snap and clamp to the source edge.");
        var timeline = window.FindControl<SpriteTimeline>("Timeline")!;
        Dispatcher.UIThread.RunJobs();
        var original = model.Frames[0].Id;
        Drag(timeline, window, timeline.FrameBounds(0).Center, timeline.FrameBounds(2).Center);
        if (model.Frames[2].Id != original)
            throw new Exception("Dragging a timeline clip did not reorder the stable clip.");
        var rect = timeline.FrameBounds(0);
        Drag(timeline, window, new Point(rect.Right - 2, rect.Y + 50), new Point(rect.Right + 38, rect.Y + 50));
        if (Math.Abs(model.Frames[0].DurationFrames - 1.5) > .001 ||
            Math.Abs(timeline.FrameBounds(0).Width / timeline.FrameBounds(1).Width - 1.5) > .001)
            throw new Exception("Timeline edge resize or common linear time scale is incorrect.");
        model.Frames.Clear();
        model.SelectedCell = model.Cells.Single(cell => cell.Width == 1 && cell.Height == 1 && cell.X == 0 && cell.Y == 0);
        model.AddFrame();
        model.AddPart();
        var front = model.SelectedPart!;
        model.Frames.Clear();
        model.SelectedCell = model.Cells.Single(cell => cell.Width == 1 && cell.Height == 1 && cell.X == 1 && cell.Y == 0);
        model.AddFrame();
        model.ScrubAt(.5);
        model.Tick(4);
        if (model.Playhead != .5) throw new Exception("Paused playback overwrote the precise key time.");
        model.PositionX = "0"; model.Rotation = "0"; model.ScaleX = model.ScaleY = "1";
        model.SetTransformKey();
        model.ScrubAt(2.5);
        model.PositionX = "2"; model.Rotation = "90"; model.ScaleX = "2";
        model.SetTransformKey();
        model.ScrubAt(1.5);
        var pose = front.PoseAt(model.Playhead);
        if (pose.X != 1 || pose.RotationDegrees != 45 || pose.ScaleX != 1.5)
            throw new Exception("Part TRS keys did not interpolate at the common playhead.");
        var keyId = front.Keys[0].Id;
        model.MoveTransformKey(keyId, .25);
        if (front.Keys[0].Id != keyId || front.Keys[0].Frame != .25)
            throw new Exception("Retiming a transform key lost its identity.");
        front.Keys.Clear();
        model.ScrubAt(0);
        window.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        var stage = window.FindControl<SpriteCompositionPreview>("CompositionPreview")!;
        var pixel = CenterPixel(stage);
        if (pixel.B < 200 || pixel.R > 40) throw new Exception("Composite preview did not draw the front blue part.");
        pixel = CenterPixel(window.FindControl<SpriteAtlasPreview>("FramePreview")!);
        if (pixel.B < 200 || pixel.R > 40) throw new Exception("Frame preview did not crop the active cell.");
        model.MovePart(-1);
        pixel = CenterPixel(stage);
        if (pixel.R < 200 || pixel.B > 40) throw new Exception("Part reordering did not change composite pixels.");
        model.SelectedCell = model.Cells.Single(cell => cell.Width == 1 && cell.Height == 1 && cell.X == 1 && cell.Y == 0);
        model.RemoveCell();
        if (front.Frames.Count != 0) throw new Exception("Deleting a cell left dangling references on another part.");
        model.RemovePart();
        model.Catalog.SelectedAsset = new EngineAsset("/Game/FX/T_Existing.T_Existing", "Existing", "texture", "Unreal");
        model.SelectedPart = model.Parts[0];
        model.ScrubAt(0);
        model.SetTransformKey();
        model.TryBuildComposition(out var draft, false);
        draft.Validate();
        var before = graph.CreateSnapshot();
        model.Apply(true);
        if (!GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()))
            throw new Exception("Layered authoring silently overwrote an ordinary sprite node.");
        model.Apply(false);
        if (graph.Nodes.Count != before.Nodes.Count + 1 || graph.Nodes.All(node => node.Definition.TypeName != "SpriteComposition"))
            throw new Exception($"The editor could not create its authored composition: {model.Status}");
        graph.SelectNode(graph.Nodes.Single(node => node.Definition.TypeName == "SpriteComposition"));
        var properties = new NodePropertiesPaneViewModel(new ActiveGraphProvider(graph));
        if (properties.Parameters.Any(parameter => parameter.Id == "compositionData"))
            throw new Exception("The ordinary inspector exposed internal sprite UUID/JSON metadata.");
        var dimensionsRefreshed = false;
        model.PropertyChanged += (_, e) => dimensionsRefreshed |= e.PropertyName == nameof(model.TextureDimensions);
        model.LoadSelected();
        if (!dimensionsRefreshed || !model.IsComposition || model.SelectedCell is null ||
            model.Parts.Count != 1 || model.Parts[0].Keys.Count != 1 || !model.TryBuildComposition(out _))
            throw new Exception("Reloading a composition lost its draft or did not refresh the source UI.");
        graph.History.Undo();
        if (!GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()))
            throw new Exception("Composition apply was not one graph undo step.");
    }

    private static (byte B, byte G, byte R) CenterPixel(Control control)
    {
        using var target = new RenderTargetBitmap(new PixelSize((int)control.Bounds.Width, (int)control.Bounds.Height));
        target.Render(control);
        var pixel = Marshal.AllocHGlobal(4);
        try
        {
            target.CopyPixels(new PixelRect((int)control.Bounds.Width / 2, (int)control.Bounds.Height / 2, 1, 1), pixel, 4, 4);
            return (Marshal.ReadByte(pixel), Marshal.ReadByte(pixel, 1), Marshal.ReadByte(pixel, 2));
        }
        finally { Marshal.FreeHGlobal(pixel); }
    }

    private static void Drag(Control control, Window window, Point start, Point end)
    {
        var from = control.TranslatePoint(start, window)!.Value;
        var to = control.TranslatePoint(end, window)!.Value;
        var pointer = new Pointer(701, PointerType.Mouse, true);
        control.RaiseEvent(new PointerPressedEventArgs(control, pointer, window, from, 0,
            new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
        control.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, control, pointer, window, to, 1,
            new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
        control.RaiseEvent(new PointerReleasedEventArgs(control, pointer, window, to, 2,
            new(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
        Dispatcher.UIThread.RunJobs();
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
