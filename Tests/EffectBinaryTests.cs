using System.Text.Json;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Samples;
using LiberaFx.ViewModels.Graph;
using LiberaFx.Services.Compilation;

internal static class EffectBinaryTests
{
    public static void Run()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        graph.CreateNode(GraphNodeKind.Float);
        var simple = SpawnableGroupPackageBinary.Deserialize(EffectBinary.Compile(graph.CreateSnapshot()));
        Require(simple.Templates.Count == 0 && simple.ParentBytecode.AsSpan(0, 4).SequenceEqual("LFXB"u8),
            "Simple effects must use the same outer package as lifecycle effects.");

        var source = FireworksSample.Create();
        var expected = SpawnableGroupPackage.Compile(source);
        var fireworks = SpawnableGroupPackageBinary.Deserialize(EffectBinary.Compile(source));
        Require(fireworks.Templates.Count == 2 && fireworks.OnStartNodeId is not null &&
                fireworks.DestroyRoutes.Count != 0,
            "Unified export must preserve child templates, the entry signal and destroy routing.");
        Require(fireworks.Templates.Zip(expected.Templates).All(pair =>
                pair.First.Lifecycle == pair.Second.Lifecycle &&
                pair.First.RenderConfiguration.SequenceEqual(pair.Second.RenderConfiguration)),
            "Unified export lost child lifetimes or render bindings.");

        var timed = new GraphWorkspaceViewModel(new UndoRedoManager()) { AutoDestroyWhenIdle = false };
        var input = timed.CreateNode(new("InputSignal"));
        var output = timed.CreateNode(new("OutputSignal"));
        timed.SetParameter(input, "name", JsonSerializer.SerializeToElement("trigger"));
        timed.SetParameter(output, "name", JsonSerializer.SerializeToElement("echo"));
        Require(timed.TryConnectNodes(input, output, out _, "signal", "then"),
            "Could not connect the runtime Signal fixture.");
        timed.SetSignalKeys(input, [new(Guid.NewGuid(), 0.25, SignalTimelineMode.Runtime)], "Runtime key");
        var scheduled = SpawnableGroupPackageBinary.Deserialize(EffectBinary.Compile(timed.CreateSnapshot()));
        Require(scheduled.HasDeferredSignals && !scheduled.AutoDestroyWhenIdle &&
                scheduled.GameIoPorts.Any(port => port.Name == "echo"),
            "Unified export lost scheduled Signal execution or root Game I/O.");

        var snapshot = graph.CreateSnapshot();
        var withIdentity = snapshot with
        {
            Nodes = snapshot.Nodes.Select(item => item with
            {
                Parameters = (item.Parameters ?? []).Append(new NodeParameterSnapshot(
                    ResourceIdentityExport.EmbeddedParameterId, ResourceIdentityExport.EmbeddedParameterType,
                    JsonSerializer.SerializeToElement(new EmbeddedResourceIdentities(1, [], [])))).ToArray()
            }).ToArray()
        };
        try
        {
            EffectBinary.Compile(withIdentity);
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException("Unified export silently discarded unresolved portable resource identities.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
