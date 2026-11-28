using System.Text.Json;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Gpu;
using LiberaFx.Services.Sprite;
using LiberaFx.ViewModels.Graph;
using LiberaFx.ViewModels.Main;
using LiberaFx.ViewModels.Panels;
using LiberaFx.Services.Compilation;
using ManagedSprite = LiberaFx.Unity.ParticleSpriteAnimation;

internal static class ParticleSpriteAnimationTests
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static void Run(string? directory)
    {
        if (directory is not null && (!Path.IsPathFullyQualified(directory) || Directory.Exists(directory)))
            throw new ArgumentException("Use a new absolute isolated fixture directory.");
        if (directory is not null) Directory.CreateDirectory(directory);
        var a = new SpriteAnimationCell(Guid.NewGuid(), "Red", 0, 0, 24, 16);
        var b = new SpriteAnimationCell(Guid.NewGuid(), "Blue", 24, 0, 16, 32);
        var c = new SpriteAnimationCell(Guid.NewGuid(), "Gold", 40, 0, 24, 24);
        SpriteAnimationFrame Frame(SpriteAnimationCell cell, double duration) => new(Guid.NewGuid(), cell.Id, duration);
        var sequence = new SpriteAnimationData
        {
            TextureWidth = 64,
            TextureHeight = 32,
            Fps = 4,
            Loop = false,
            Cells = [a, b, c],
            Frames = [Frame(c, .5), Frame(a, 1.5), Frame(b, 1), Frame(a, 1)],
            StartFrame = 1,
            EndFrame = 3
        };
        var composition = new SpriteCompositionData
        {
            TextureWidth = 64,
            TextureHeight = 32,
            Fps = 4,
            Loop = true,
            Cells = [a, b, c],
            Tracks = [new() { Id=Guid.NewGuid(),Name="Back",Frames=[Frame(a,1),Frame(b,2)] },
                new() {Id=Guid.NewGuid(),Name="Hidden",Visible=false},
                new() {Id=Guid.NewGuid(),Name="Front",Frames=[Frame(c,4)],Keys=[
                    new(Guid.NewGuid(),0,-16,8,0,1,1),new(Guid.NewGuid(),2,16,-8,90,-1,.5)]}]
        };
        var asset = new EngineAsset("/Game/LibraTests/ParticleSpriteAtlas.ParticleSpriteAtlas", "Particle sprite atlas", "texture", "Unreal");
        foreach (var layered in new[] { false, true })
        {
            var compiled = ParticleSpriteAnimation.Compile(layered ? null : sequence, layered ? composition : null)!;
            compiled.ValidateGpuPrecision();
            var managed = new ManagedSprite { version = compiled.Version, partCount = compiled.PartCount, values = compiled.Values };
            managed.Validate(); managed.ValidateGpuPrecision();
            Require(managed.partCount == (layered ? 2 : 1), "Hidden tracks changed part order/count.");
            for (var part = 0; part < managed.partCount; ++part)
                foreach (var time in new[] { 0, .0625, .125, .25, .375, .5, .75, 1, 1.25, 2, 8 })
                {
                    var expected = Expected(part, time, layered);
                    var actual = managed.Sample(part, time);
                    Require(actual.cell == expected.Cell && Math.Abs(actual.u - expected.Values[0]) < 1e-12 &&
                        Math.Abs(actual.x - expected.Values[4]) < 1e-12 && Math.Abs(actual.y - expected.Values[5]) < 1e-12 &&
                        Math.Abs(actual.rotation - expected.Values[6]) < 1e-12 && Math.Abs(actual.scaleX - expected.Values[7]) < 1e-12 &&
                        Math.Abs(actual.scaleY - expected.Values[8]) < 1e-12, "Authored frame/pose and prepared managed playback differ.");
                }
            if (directory is not null)
            {
                using var writer = new BinaryWriter(File.Create(Path.Combine(directory, layered ? "composition.bin" : "sequence.bin")));
                writer.Write((uint)compiled.PartCount); writer.Write((uint)compiled.Values.Length);
                foreach (var value in compiled.Values) writer.Write(value);
                var times = new[] { 0, .0625, .125, .25, .375, .5, .75, 1, 1.25, 2, 8 };
                writer.Write((uint)(managed.partCount * times.Length));
                for (var part = 0; part < managed.partCount; ++part)
                    foreach (var time in times)
                    {
                        var expected = Expected(part, time, layered);
                        writer.Write((uint)part); writer.Write(time); writer.Write(expected.Cell);
                        foreach (var value in expected.Values) writer.Write(value);
                    }
            }
            foreach (var gpu in new[] { false, true })
            {
                var graph = new GraphWorkspaceViewModel(new UndoRedoManager()) { AutoDestroyWhenIdle = false };
                var node = gpu ? GpuParticleEditorService.Create(graph) : graph.CreateNode(
                    graph.AvailableNodeDefinitions.Single(d => d.TypeName == "CpuParticle").Kind);
                if (gpu)
                {
                    var state = GpuParticleEditorService.ReadState(node);
                    var program = LibraGpu.Core.GpuSamples.CreateEmitter(32) with
                    {
                        SpawnRate = 1,
                        Kernels = [
                        LibraGpu.Core.GpuDsl.Compile("@Position=emitterPosition; @Velocity=float3(0,0,0); @Lifetime=5; @Size=float2(0.8,0.4); @Color=float4(1,1,1,1);","spawn",LibraGpu.Core.KernelStage.Spawn),
                        LibraGpu.Core.GpuDsl.Compile("@Position+=@Velocity*dt; @Color=float4(1,1,1,1);","update",LibraGpu.Core.KernelStage.Update)]
                    };
                    var parameters = new[] {new NodeParameterSnapshot(GpuParticleEditorService.ParameterId,"gpuState",
                        JsonSerializer.SerializeToElement(state with {Program=program},GpuParticleEditorService.Json))};
                    graph.History.Execute(new SetNodeParametersCommand(node, node.Parameters, parameters, "Probe program"));
                }
                else
                {
                    var snapshot = graph.CreateSnapshot();
                    var cpuRoot = snapshot.Nodes.Single();
                    var spawn = cpuRoot.ChildGraph!.Nodes.Single(n => CpuParticleSchema.Name(n.Kind) == "CpuParticleSpawn");
                    var velocity = spawn.ChildGraph!.Nodes.Single(n => CpuParticleSchema.Name(n.Kind) == "CpuParticleSetVelocity");
                    GraphNodeSnapshot Setter(string kind, string type, object value)
                    {
                        var d = graph.AvailableNodeDefinitions.Single(d => d.TypeName == kind);
                        return new(Guid.NewGuid(), d.Kind, kind, 0, 0, d.PortType, d.PortCategory,
                            Parameters: [new("value", type, JsonSerializer.SerializeToElement(value))]);
                    }
                    var life = Setter("CpuParticleSetLife", "float", 5d);
                    var size = Setter("CpuParticleSetSize", "vector2", new[] { .8, .4 });
                    var stage = spawn.ChildGraph with
                    {
                        Nodes = [..spawn.ChildGraph.Nodes.Select(n=>n.Id!=velocity.Id ? n : n with {
                        Parameters=[new("value","vector3",JsonSerializer.SerializeToElement(new[] {0d,0,0}))]}),life,size],
                        Connections = [.. spawn.ChildGraph.Connections, new(velocity.Id, life.Id, "signal", "then"), new(life.Id, size.Id, "signal", "then")]
                    };
                    var changed = cpuRoot with
                    {
                        Parameters = [new("capacity","int",JsonSerializer.SerializeToElement(32)),
                        new("rate","float",JsonSerializer.SerializeToElement(0d)),new("burst","int",JsonSerializer.SerializeToElement(1))],
                        ChildGraph = cpuRoot.ChildGraph with { Nodes = cpuRoot.ChildGraph.Nodes.Select(n => n.Id != spawn.Id ? n : n with { ChildGraph = stage }).ToArray() }
                    };
                    graph.History.Execute(new ReplaceGraphSnapshotCommand(graph, snapshot, snapshot with { Nodes = [changed] }));
                    node = graph.GetNode(cpuRoot.Id);
                }
                var before = graph.CreateSnapshot();
                if (layered) SpriteCompositionAuthoring.Apply(graph, node, asset, composition);
                else SpriteEditorAuthoring.Apply(graph, node, asset, new(4, 1, sequence.Fps, 0, true), sequence);
                var id = node.Id; node = graph.GetNode(id);
                var after = graph.CreateSnapshot();
                var restored = JsonSerializer.Deserialize<GraphSnapshot>(JsonSerializer.Serialize(after))!;
                Require(GraphSnapshot.ContentEquals(after, restored), "Particle sprite document did not roundtrip.");
                Require(graph.History.Undo() && GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()) &&
                    graph.History.Redo() && GraphSnapshot.ContentEquals(after, graph.CreateSnapshot()), "Particle sprite edit was not atomic/undoable.");
                var saved = ParticleSpriteAuthoring.Read(graph.GetNode(id));
                Require(saved.Path == asset.Path && (layered ? saved.Composition?.Tracks.Count == 3 : saved.Animation?.StartFrame == 1), "Particle Sprite editor reload lost its source settings.");
                var package = SpawnableGroupPackage.Compile(restored);
                using var json = JsonDocument.Parse(gpu ? package.RootGpuPrograms : package.RootRenderConfiguration);
                var root = json.RootElement;
                var animation = gpu ? root.GetProperty("programs")[0].GetProperty("unreal").GetProperty("rendererBindings")[0].GetProperty("spriteAnimation")
                    : root.GetProperty("cpuParticles")[0].GetProperty("spriteAnimation");
                Require((gpu ? root.GetProperty("version").GetInt32() : root.GetProperty("cpuParticleVersion").GetInt32()) == 4 &&
                    animation.GetProperty("partCount").GetInt32() == compiled.PartCount &&
                    animation.GetProperty("values").EnumerateArray().Select(x => x.GetDouble()).SequenceEqual(compiled.Values),
                    "CPU/GPU sprite package does not contain the same authored playback data.");
                if (directory is not null)
                {
                    var name = $"{(gpu ? "gpu" : "cpu")}-{(layered ? "composition" : "sequence")}";
                    File.WriteAllBytes(Path.Combine(directory, name + ".lfxp"), SpawnableGroupPackageBinary.Serialize(package));
                    File.WriteAllBytes(Path.Combine(directory, name + ".json"), gpu ? package.RootGpuPrograms : package.RootRenderConfiguration);
                }
                if (gpu)
                {
                    var flow = GpuParticleEditorService.ReadState(graph.GetNode(id)).Program.Flow.Single(f => f.Kind == LibraGpu.Core.FlowKind.RenderBillboard);
                    GpuParticleEditorService.Apply(graph, id, [new() { Action = "renderer", NodeId = flow.Id, SizeScale = 2, SpriteAnimationEnabled = false }]);
                    var disabled = GpuParticleEditorService.ReadState(graph.GetNode(id)).RendererSettings[flow.Id];
                    Require(!disabled.SpriteAnimationEnabled && (layered ? disabled.SpriteComposition is not null : disabled.SpriteAnimation is not null),
                        "Changing GPU renderer settings discarded the authored animation.");
                    var legacyPackage = SpawnableGroupPackage.Compile(graph.CreateSnapshot());
                    using (var legacy = JsonDocument.Parse(legacyPackage.RootGpuPrograms))
                    {
                        var manifest = legacy.RootElement;
                        var renderer = manifest.GetProperty("programs")[0].GetProperty("unreal").GetProperty("rendererBindings")[0];
                        Require(manifest.GetProperty("version").GetInt32() == 4 &&
                            !renderer.TryGetProperty("spriteAnimation", out _),
                            "Disabling Sprite animation must omit it from the current GPU manifest.");
                    }
                    if (directory is not null && !layered)
                    {
                        File.WriteAllBytes(Path.Combine(directory, "gpu-disabled.lfxp"), SpawnableGroupPackageBinary.Serialize(legacyPackage));
                        File.WriteAllBytes(Path.Combine(directory, "gpu-disabled.json"), legacyPackage.RootGpuPrograms);
                    }
                    Require(graph.History.Undo() && GraphSnapshot.ContentEquals(after, graph.CreateSnapshot()), "GPU animation toggle did not undo.");
                }
            }
        }
        var hidden = ParticleSpriteAnimation.Compile(null, composition with { Tracks = composition.Tracks.Select(t => t with { Visible = false }).ToList() })!;
        Require(hidden.PartCount == 0 && hidden.Values.Length == 0, "All-hidden composition must render nothing.");
        var tiny = ParticleSpriteAnimation.Compile(sequence with { Frames = [Frame(a, 1_000_000), Frame(b, 1e-9)], StartFrame = 0, EndFrame = 1 }, null)!;
        try { tiny.ValidateGpuPrecision(); throw new Exception("GPU collapsed frame times were accepted."); } catch (InvalidDataException) { }
        var main = new MainViewModel();
        try
        {
            var graph = main.ActiveDocument!.Workspace;
            var source = SpriteEditorAuthoring.Apply(graph, null, asset, new(4, 1, sequence.Fps, 0, true), sequence);
            graph.SelectNode(source);
            using var editor = new SpriteEditorViewModel(main);
            editor.Catalog.SelectedAsset = asset;
            var cpu = graph.CreateNode(graph.AvailableNodeDefinitions.Single(d => d.TypeName == "CpuParticle").Kind);
            graph.SelectNode(cpu);
            editor.Apply(true);
            Require(ParticleSpriteAuthoring.Read(graph.GetNode(cpu.Id)).Animation?.Frames.Count == 4,
                "Sprite editor retained draft could not apply to a selected CPU particle.");
            var gpu = GpuParticleEditorService.Create(graph); graph.SelectNode(gpu);
            editor.Apply(true);
            Require(ParticleSpriteAuthoring.Read(graph.GetNode(gpu.Id)).Animation?.StartFrame == 1,
                "Sprite editor retained draft could not apply to a selected GPU particle.");
            editor.LoadSelected();
            Require(editor.Frames.Count == 4 && editor.Cells.Count == 3, "Sprite editor could not load particle settings.");
        }
        finally { main.DisposeStorageFiles(); }
        if (directory is not null)
        {
            using var image = new WriteableBitmap(new PixelSize(64, 32), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var locked = image.Lock())
            {
                var pixels = new byte[locked.RowBytes * 32];
                foreach (var (cell, color) in new[] { (a, new byte[] { 0, 0, 255, 255 }), (b, new byte[] { 255, 0, 0, 255 }), (c, new byte[] { 0, 255, 255, 255 }) })
                    for (var y = cell.Y; y < cell.Y + cell.Height; ++y)
                        for (var x = cell.X; x < cell.X + cell.Width; ++x) Array.Copy(color, 0, pixels, y * locked.RowBytes + x * 4, 4);
                System.Runtime.InteropServices.Marshal.Copy(pixels, 0, locked.Address, pixels.Length);
            }
            image.Save(Path.Combine(directory, "atlas.png"), PngBitmapEncoderOptions.Default);
        }
        Console.WriteLine("CPU/GPU Sprite editor sequence/composition: timing, authored poses, atomic edits, reload and package parity passed.");

        (uint Cell, double[] Values) Expected(int part, double time, bool layered)
        {
            var cells = sequence.Cells;
            SpriteAnimationFrame frame; var pose = new SpritePose(0, 0, 0, 1, 1);
            if (!layered) frame = sequence.FrameAt(-1, time, true);
            else
            {
                var track = composition.Tracks.Where(t => t.Visible).ElementAt(part);
                var ticks = composition.SampleFrame(time); var cursor = ticks;
                frame = track.Frames[^1];
                foreach (var candidate in track.Frames) { if (cursor < candidate.DurationFrames) { frame = candidate; break; } cursor -= candidate.DurationFrames; }
                pose = track.PoseAt(ticks);
            }
            var cell = cells.Single(x => x.Id == frame.CellId);
            var uv = sequence.UvRect(cell);
            return ((uint)cells.IndexOf(cell), [..uv,layered ? pose.X/64 : 0,layered ? pose.Y/32 : 0,
                layered ? pose.RotationDegrees*Math.PI/180 : 0,layered ? uv[2]*pose.ScaleX : 1,layered ? uv[3]*pose.ScaleY : 1]);
        }
    }
}
