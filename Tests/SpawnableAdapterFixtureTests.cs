using System.Buffers.Binary;
using System.Text.Json;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;

internal static class SpawnableAdapterFixtureTests
{
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager()) { AutoDestroyWhenIdle = false };
        var property = graph.CreateNode(GraphNodeKind.InputProperty);
        graph.SetParameter(property, "name", JsonSerializer.SerializeToElement("position"));
        var signal = graph.CreateNode(GraphNodeKind.InputSignal);
        graph.SetParameter(signal, "name", JsonSerializer.SerializeToElement("spawn"));
        var echo = graph.CreateNode(GraphNodeKind.OutputProperty);
        graph.SetParameter(echo, "name", JsonSerializer.SerializeToElement("echo"));
        Connect(property, echo, "value", "value");
        var rootPosition = graph.CreateNode(new("ScalarToVector3"));
        Connect(property, rootPosition, "a", "value");
        var rootDraw = Draw(rootPosition);
        var template = graph.CreateNode(GraphNodeKind.SpawnableGroup);
        graph.EnterGraph(template);
        var capture = graph.CreateNode(new("SpawnInputFloat"));
        var live = graph.CreateNode(new("SpawnInputFloat"));
        graph.SetParameter(live, "bindingMode", JsonSerializer.SerializeToElement(1));
        var capturedPosition = graph.CreateNode(new("ScalarToVector3"));
        var livePosition = graph.CreateNode(new("ScalarToVector3"));
        Connect(capture, capturedPosition, "a");
        Connect(live, livePosition, "a");
        var captureDraw = Draw(capturedPosition);
        var liveDraw = Draw(livePosition);
        graph.CreateNode(new("LifeTime"));
        graph.NavigateToGraph(0);
        template = graph.GetNode(template.Id);
        Connect(signal, template, "spawn", "then");
        Connect(property, template, SpawnableGroupSchema.PortId(capture.Id), "value");
        Connect(property, template, SpawnableGroupSchema.PortId(live.Id), "value");
        var package = SpawnableGroupPackage.Compile(graph.CreateSnapshot());
        File.WriteAllBytes(Path.Combine(output, "GameIo.lfxp"), SpawnableGroupPackageBinary.Serialize(package));
        File.WriteAllText(Path.Combine(output, "GameIo.identities.json"), JsonSerializer.Serialize(new
        {
            template = template.Id, rootDraw = rootDraw.Id, captureDraw = captureDraw.Id, liveDraw = liveDraw.Id
        }));
        var rootGpuGraph = new GraphWorkspaceViewModel(new UndoRedoManager()) { AutoDestroyWhenIdle = false };
        rootGpuGraph.CreateNode(new("OnStart"));
        rootGpuGraph.CreateNode(new("OnDestroy"));
        rootGpuGraph.CreateNode(new("GpuParticle"));
        var rootGpuPackage = SpawnableGroupPackage.Compile(rootGpuGraph.CreateSnapshot());
        var rootGpuBytes = SpawnableGroupPackageBinary.Serialize(rootGpuPackage);
        File.WriteAllBytes(Path.Combine(output, "RootGpu.lfxp"), rootGpuBytes);
        var legacyRootGpu = rootGpuBytes[..^(sizeof(int) + rootGpuPackage.RootGpuPrograms.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(legacyRootGpu.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(legacyRootGpu.AsSpan(8), (ulong)legacyRootGpu.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(legacyRootGpu.AsSpan(16), 10);
        File.WriteAllBytes(Path.Combine(output, "RootGpu.legacy.lfxp"), legacyRootGpu);
        Console.WriteLine("Compiler-produced game I/O, Capture/Live, renderer and root GPU adapter fixtures: " + output);
        return;

        void Connect(GraphNodeViewModel source, GraphNodeViewModel target, string? port = null, string? outputPort = null)
        {
            if (!graph.TryConnectNodes(graph.GetNode(source.Id), graph.GetNode(target.Id), out var error, port, outputPort))
                throw new InvalidOperationException(error);
        }
        GraphNodeViewModel Draw(GraphNodeViewModel position)
        {
            var draw = graph.CreateNode(new("ModelDraw"));
            graph.SetParameter(draw, "assetPath", JsonSerializer.SerializeToElement("/Engine/BasicShapes/Cube.Cube"));
            Connect(position, draw, "position");
            return draw;
        }
    }
}
