using System.Text.Json;
using Avalonia.Threading;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;

internal static class AuthoredQualityTests
{
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager()) { AutoDestroyWhenIdle = false };
        var entry = graph.CreateNode(new("OnStart"));
        var sequence = graph.CreateNode(GraphNodeKind.Sequence);
        var counter = graph.CreateNode(GraphNodeDefinition.All.Single(definition =>
            definition.TypeId == GraphLogicalSchema.SignalToNumberTypeId).Kind);
        Set(sequence, "outputs", 4);
        Set(counter, "mode", 2);
        var compose = graph.CreateNode(new("ComposeVector4"));
        var capture = graph.CreateNode(new("SignalCaptureVector4"));
        var quality = graph.CreateNode(new("QualityBranch"));
        var whenTrue = graph.CreateNode(GraphNodeKind.Signal);
        var whenFalse = graph.CreateNode(GraphNodeKind.Signal);
        var split = graph.CreateNode(new("BreakVector4"));
        var result = graph.CreateNode(GraphNodeKind.Add);
        Connect(entry, sequence, "signal");
        Connect(sequence, counter, "signal", "then_0");
        Connect(sequence, capture, "signal", "then_1");
        Connect(sequence, counter, "signal", "then_2");
        Connect(sequence, capture, "signal", "then_3");
        Connect(counter, compose, "r", "value");
        Connect(compose, capture, "value", "value");
        Connect(capture, quality, "signal", "signal");
        Connect(capture, split, "a", "value");
        Connect(split, result, "a", "x");
        Connect(quality, whenTrue, null, "whenTrue");
        Connect(quality, whenFalse, null, "whenFalse");
        if (!graph.History.Undo() || !graph.History.Redo())
            throw new Exception("Quality branch authoring did not survive Undo/Redo.");
        var snapshot = graph.CreateSnapshot();
        GraphValidator.Validate(snapshot);
        var clone = new GraphWorkspaceViewModel(new UndoRedoManager());
        clone.PasteSnapshot(snapshot, 1000, 1000);
        if (clone.Connections.Count != snapshot.Connections.Count)
            throw new Exception("Copying a quality/value Signal lost its port identities.");
        using var stream = new MemoryStream();
        new ProjectFileService().SaveAsync(stream, graph).GetAwaiter().GetResult();
        stream.Position = 0;
        if (!GraphSnapshot.ContentEquals(snapshot,
            ProjectDocumentReader.LoadAsync(stream).GetAwaiter().GetResult().Graph))
            throw new Exception("Quality/value Signals did not survive persistence.");
        var package = SpawnableGroupPackage.Compile(snapshot);
        File.WriteAllBytes(Path.Combine(directory, "AuthoredQuality.lfxb"), package.ParentBytecode);
        File.WriteAllBytes(Path.Combine(directory, "AuthoredQuality.lfxp"),
            SpawnableGroupPackageBinary.Serialize(package));
        File.WriteAllBytes(Path.Combine(directory, "AuthoredQuality.ids"),
            new[] { entry.Id, capture.Id, quality.Id, whenTrue.Id, whenFalse.Id, result.Id }
                .SelectMany(id => id.ToByteArray(bigEndian: true)).ToArray());
        File.WriteAllText(Path.Combine(directory, "AuthoredQuality.json"), JsonSerializer.Serialize(new
        {
            entry = entry.Id, capture = capture.Id, quality = quality.Id,
            whenTrue = whenTrue.Id, whenFalse = whenFalse.Id, result = result.Id
        }));
        File.WriteAllBytes(Path.Combine(directory, "AuthoredQuality.lfxproj"), stream.ToArray());

        void Set(GraphNodeViewModel node, string parameter, int value)
        {
            var task = LiberaFxMcpTools.SetParameter(new ActiveGraphProvider(graph), node.Id.ToString(),
                parameter, JsonSerializer.SerializeToElement(value));
            Dispatcher.UIThread.RunJobs();
            task.GetAwaiter().GetResult();
        }

        void Connect(GraphNodeViewModel source, GraphNodeViewModel target, string? port, string? output = null)
        {
            if (!graph.TryConnectNodes(source, target, out var error, targetPortId: port,
                sourcePortId: output ?? (source.OutputPorts.Count == 1 ? source.OutputPorts[0].Id : null)))
                throw new Exception(error);
        }
    }
}
