using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using Avalonia.Threading;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Preview;
using LiberaFx.Services.Project;
using LiberaFx.Services.Samples;
using LiberaFx.ViewModels.Graph;
using LiberaFx.ViewModels.Panels;
using LiberaFx.Views.Timeline;
using LiberaFx.Services.Compilation;

internal static class TimelineTests
{
    public static void Run()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var node = graph.CreateNode(GraphNodeKind.FloatFCurve);
        var original = new FCurveSnapshot([
            new(Guid.NewGuid(), 0, 0, 0, 0.02, CurveInterpolation.Cubic),
            new(Guid.NewGuid(), 1, 1, 0.01, 0, CurveInterpolation.Linear)
        ]);
        graph.SetCurve(node, original, "Test curve");
        VerifyCurveCompilation(graph);
        if (Environment.GetEnvironmentVariable("LIBERAFX_UNREAL_FCURVE_FIXTURE") is { Length: > 0 } fixtureDirectory)
            ExportUnrealCurveRenderFixture(fixtureDirectory);
        VerifyLegacyFrameCurveMigration();
        using var playback = new EditorPlayback();
        var active = new ActiveGraphProvider(graph);
        var timeline = new TimelineViewModel(active, playback);
        var curves = new CurveEditorViewModel(active, playback);
        var track = timeline.Tracks.Single();
        timeline.Retime(track, 0.5, 2);
        var retimed = node.Curve!;
        for (var step = 0; step <= 4; step++)
        {
            var time = step * 0.25;
            if (Math.Abs(FCurve.Evaluate(original, time) - FCurve.Evaluate(retimed, 0.5 + time * 2)) > 1e-9)
                throw new InvalidOperationException("Timeline duration edits did not preserve the curve's interpolation shape.");
        }
        if (!graph.History.Undo() || !graph.GetNode(node.Id).Curve!.Keys.SequenceEqual(original.Keys) ||
            !graph.History.Redo() || !graph.GetNode(node.Id).Curve!.Keys.SequenceEqual(retimed.Keys))
            throw new InvalidOperationException("Timeline edits were not part of graph history.");
        var before = graph.CreateSnapshot();
        timeline.Retime(timeline.Tracks.Single(), 0, 0);
        if (!GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()))
            throw new InvalidOperationException("An invalid timeline duration changed graph data.");
        var publishedPlayback = new List<PreviewPlaybackState>();
        playback.StateChanged += (_, _) => publishedPlayback.Add(playback.Snapshot);
        playback.Play();
        playback.Advance(1);
        if (playback.Frame != 60 || curves.PlayheadFrame != 60 ||
            publishedPlayback.Count != 2 || publishedPlayback[^1].Frame != 60 || !publishedPlayback[^1].Playing)
            throw new InvalidOperationException("Timeline and curve editor do not share their playhead.");
        playback.Advance(0);
        if (publishedPlayback.Count != 2)
            throw new InvalidOperationException("An unchanged playhead generated a playback update.");
        playback.FpsText = "30";
        playback.Advance(1);
        if (playback.Frame != 90) throw new InvalidOperationException("Playback did not use its selected FPS.");
        playback.Advance(2);
        if (playback.Frame != 30 || publishedPlayback[^1].Frame != 30 || !publishedPlayback[^1].Playing)
            throw new InvalidOperationException("Playback did not publish its wrapped loop position.");
        playback.ShowSeconds = true;
        playback.FrameText = playback.FormatTime(1.5);
        if (playback.Frame != 45 || playback.TimeSeconds != 1.5 || curves.SnapDisplayTime(0.51) != 0.5)
            throw new InvalidOperationException("Seconds editing did not convert to the shared playback clock.");
        playback.ShowSeconds = false;
        if (playback.FrameText != "45" || curves.SnapDisplayTime(1.6) != 2)
            throw new InvalidOperationException("Switching the time display did not preserve the playhead.");
        playback.Loop = false;
        var beforeStop = publishedPlayback.Count;
        playback.Advance(10);
        if (playback.Frame != 120 || playback.IsPlaying || publishedPlayback.Count != beforeStop + 1 ||
            publishedPlayback[^1].Frame != 120 || publishedPlayback[^1].Playing)
            throw new InvalidOperationException("Non-looping playback did not stop at the range end.");
        playback.StopCommand.Execute(null);
        if (playback.Frame != 0 || !GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()))
            throw new InvalidOperationException("Playback controls changed authored graph data.");
        var playbackState = playback.Snapshot;
        playback.StartText = "120";
        playback.EndText = "NaN";
        playback.FpsText = "0";
        playback.Frame = double.NaN;
        if (playback.Snapshot != playbackState)
            throw new InvalidOperationException("Invalid playback settings changed the effective clock.");
        playback.StepForwardCommand.Execute(null);
        playback.StepBackCommand.Execute(null);
        if (playback.Frame != 0)
            throw new InvalidOperationException("Single-frame stepping did not return to the original playhead.");
        using var project = new MemoryStream();
        new ProjectFileService().SaveAsync(project, graph).GetAwaiter().GetResult();
        project.Position = 0;
        var loaded = ProjectDocumentReader.LoadAsync(project).GetAwaiter().GetResult();
        if (!loaded.Graph.Nodes.Single().Curve!.Keys.SequenceEqual(retimed.Keys))
            throw new InvalidOperationException("Retimed graph curves did not persist.");
        active.SetGraph(new GraphWorkspaceViewModel(new UndoRedoManager()));
        if (timeline.Tracks.Count != 0)
            throw new InvalidOperationException("Switching documents retained the previous timeline tracks.");
        active.SetGraph(graph);
        if (timeline.Tracks.Count != 1)
            throw new InvalidOperationException("Returning to a document did not restore its timeline tracks.");
        var view = new TimelineView { DataContext = timeline };
        var window = new Window { Content = view, Width = 1100, Height = 350 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            if (view.FindControl<TimelineCanvas>("Canvas")!.Bounds.Width <= 0)
                throw new InvalidOperationException("The Timeline workspace did not create its editable canvas.");
        }
        finally { window.Close(); }
    }

    private static void VerifyCurveCompilation(GraphWorkspaceViewModel graph)
    {
        var binary = VfxProgram.Compile(graph.CreateSnapshot()).ToBinary();
        var instructionOffset = 16;
        var curveOffset = BinaryPrimitives.ReadInt32LittleEndian(binary.AsSpan(instructionOffset + 224));
        if (BinaryPrimitives.ReadUInt16LittleEndian(binary.AsSpan(4)) != 4 ||
            BinaryPrimitives.ReadUInt16LittleEndian(binary.AsSpan(12)) != 256 ||
            binary[instructionOffset + 16] != (byte)VfxOpcode.FCurve ||
            BinaryPrimitives.ReadInt32LittleEndian(binary.AsSpan(instructionOffset + 228)) != 2 ||
            curveOffset != 0 ||
            Math.Abs(BinaryPrimitives.ReadDoubleLittleEndian(binary.AsSpan(272 + 40)) - 1) > 1e-9)
            throw new InvalidOperationException("F-Curve compilation did not emit seconds-based v4 curve data.");
        if (Environment.GetEnvironmentVariable("LIBERAFX_FCURVE_TEST_BYTECODE") is { Length: > 0 } path)
            File.WriteAllBytes(path, binary);
    }

    public static void ExportUnrealCurveRenderFixture(string directory)
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var curve = graph.CreateNode(GraphNodeKind.FloatFCurve);
        graph.SetCurve(curve, new FCurveSnapshot([
            new(Guid.NewGuid(), 0, 0.2, 0, 0, CurveInterpolation.Linear),
            new(Guid.NewGuid(), 1, 0.8, 0, 0, CurveInterpolation.Linear)
        ]), "Unreal material color curve");
        GraphNodeViewModel Create(string typeName) =>
            graph.CreateNode(GraphNodeDefinition.All.Single(item => item.TypeName == typeName).Kind);
        var vector3 = Create("ScalarToVector3");
        var color = Create("Vector3ToVector4");
        if (!graph.TryConnectNodes(curve, vector3, out _, "a") ||
            !graph.TryConnectNodes(vector3, color, out _, "a"))
            throw new InvalidOperationException("The Unreal fixture could not connect its FCurve conversion path.");
        graph.SetParameter(color, "fill", JsonSerializer.SerializeToElement(1d));
        var initialColor = new[] { 0.2d, 0.2d, 0.2d, 1d };
        var asset = new EngineAsset(SolarSystemSample.SphereAsset, "FCurve material target", "model", "Unreal",
            Materials: [new(0, "builtin:unlit-color", "Unlit color", [
                new("Color", "vector", JsonSerializer.SerializeToElement(initialColor))
            ])]);
        var draw = RenderNodeAuthoring.CreateModel(graph, asset);
        var colorPort = draw.InputPorts.Single(port => port.Name == "Color");
        if (!graph.TryConnectNodes(color, draw, out _, colorPort.Id))
            throw new InvalidOperationException("The Unreal fixture could not connect the material color input.");

        var snapshot = graph.CreateSnapshot();
        var program = VfxProgram.Compile(snapshot);
        var colorInstruction = program.TypedInstructions.Single(item => item.NodeId == color.Id);
        if (program.BinaryVersion != 4 || colorInstruction.Opcode != VfxOpcode.Vector3ToVector4)
            throw new InvalidOperationException("The Unreal render fixture did not compile the FCurve value path.");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "program.lfxp"), EffectBinary.Compile(snapshot));
        File.WriteAllBytes(Path.Combine(directory, "render-config.json"),
            RenderProgramBindings.CreateScene(snapshot, 1));
        File.WriteAllText(Path.Combine(directory, "expected.json"), JsonSerializer.Serialize(new
        {
            frames = new[]
            {
                new
                {
                    timeSeconds = 0.5,
                    nodes = new[]
                    {
                        new
                        {
                            nodeId = draw.Id,
                            position = new[] { 0d, 0d, 0d },
                            scale = new[] { 1d, 1d, 1d },
                            rotation = new[] { 0d, 0d, 0d },
                            parameters = new[]
                            {
                                new { name = "Color", slot = 0, value = new[] { 0.5d, 0.5d, 0.5d, 1d } }
                            }
                        }
                    }
                }
            }
        }));
    }

    private static void VerifyLegacyFrameCurveMigration()
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var key = JsonSerializer.Deserialize<FCurveKey>(
            """{"Id":"00000000-0000-0000-0000-000000000001","Frame":60,"Value":1,"InSlope":0.1,"OutSlope":0.2,"Interpolation":"Cubic"}""",
            options)!;
        var saved = JsonSerializer.Serialize(key, options);
        if (key.TimeSeconds != 1 || key.InSlope != 6 || key.OutSlope != 12 ||
            !saved.Contains("TimeSeconds", StringComparison.Ordinal) ||
            saved.Contains("\"Frame\"", StringComparison.Ordinal))
            throw new InvalidOperationException("Legacy frame-based F-Curve keys did not migrate to seconds.");
    }
}
