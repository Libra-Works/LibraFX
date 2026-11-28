using System.Text.Json;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels.Graph;

internal static class LiveGameSessionFixtures
{
    // Both packages are compiler output from the SAME authored graph. The second
    // adds Float + Add nodes and rewires position; it is not a parameter poke.
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager()) { AutoDestroyWhenIdle = false };
        var scalar = graph.CreateNode(new("Float"));
        graph.SetParameter(scalar, "value", JsonSerializer.SerializeToElement(1.0));
        var position = graph.CreateNode(new("ScalarToVector3"));
        Connect(scalar, position, "a");
        var draw = graph.CreateNode(new("ModelDraw"));
        graph.SetParameter(draw, "assetPath", JsonSerializer.SerializeToElement("/Engine/BasicShapes/Cube.Cube"));
        Connect(position, draw, "position");
        Save("Live.before.lfxp");
        var second = graph.CreateNode(new("Float"));
        graph.SetParameter(second, "value", JsonSerializer.SerializeToElement(2.0));
        var add = graph.CreateNode(new("Add"));
        Connect(scalar, add, "a");
        Connect(second, add, "b");
        graph.DisconnectConnection(graph.Connections.Single(edge => edge.Target.Id == position.Id && edge.TargetPortId == "a"));
        Connect(add, position, "a");
        Save("Live.after.lfxp");
        graph.CreateNode(new("OnDestroy")); // explicitly unsupported side effects
        Save("Live.unsupported.lfxp");
        File.WriteAllText(Path.Combine(output, "Live.identities.json"),
            JsonSerializer.Serialize(new { position = position.Id, draw = draw.Id, before = 1, after = 3 }));
        Console.WriteLine("Live game component graph-replacement compiler fixtures: " + output);
        return;
        void Connect(GraphNodeViewModel source, GraphNodeViewModel target, string port)
        {
            if (!graph.TryConnectNodes(graph.GetNode(source.Id), graph.GetNode(target.Id), out var error, port))
                throw new InvalidOperationException(error);
        }
        void Save(string name) => File.WriteAllBytes(Path.Combine(output, name),
            SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(graph.CreateSnapshot())));
    }
}
