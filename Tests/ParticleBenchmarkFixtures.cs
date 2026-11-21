using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;

internal static class ParticleBenchmarkFixtures
{
    public static void Write(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || Directory.Exists(directory))
            throw new ArgumentException("Use an absolute new isolated benchmark fixture directory.");
        Directory.CreateDirectory(directory);
        foreach (var count in new[] { 1000, 10000 })
        {
            var graph = ParticleSamples.Create("CpuFountain");
            GraphNodeSnapshot Transform(GraphNodeSnapshot node)
            {
                var parameters = node.Parameters?.ToList() ?? [];
                void Set<T>(string id, string type, T value)
                {
                    parameters.RemoveAll(parameter => parameter.Id == id);
                    parameters.Add(new(id, type, JsonSerializer.SerializeToElement(value)));
                }
                if (CpuParticleSchema.IsContainer(node.Kind))
                { Set("capacity", "int", count); Set("burst", "int", count); Set("rate", "float", 0d); }
                if (CpuParticleSchema.Name(node.Kind) == "CpuParticleSetLife") Set("value", "float", 10000d);
                return node with { Parameters = parameters.ToArray(), ChildGraph = node.ChildGraph is not { } child ? null :
                    child with { Nodes = child.Nodes.Select(Transform).ToArray() } };
            }
            graph = graph with { Nodes = graph.Nodes.Select(Transform).ToArray() };
            File.WriteAllBytes(Path.Combine(directory, "CpuFountain-" + count + ".lfxb"),
                SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(graph)));
            var systemId = new byte[16];
            graph.Nodes.Single(node => CpuParticleSchema.IsContainer(node.Kind)).Id.TryWriteBytes(systemId, true, out _);
            File.WriteAllBytes(Path.Combine(directory, "CpuFountain-" + count + ".id"), systemId);
            if (count == 1000)
            {
                GraphNodeSnapshot ShortLived(GraphNodeSnapshot node)
                {
                    var parameters = node.Parameters?.ToList() ?? [];
                    void Set<T>(string id, string type, T value)
                    {
                        parameters.RemoveAll(parameter => parameter.Id == id);
                        parameters.Add(new(id, type, JsonSerializer.SerializeToElement(value)));
                    }
                    if (CpuParticleSchema.IsContainer(node.Kind)) { Set("burst", "int", 61); Set("rate", "float", 97d); }
                    if (CpuParticleSchema.Name(node.Kind) == "CpuParticleSetLife") Set("value", "float", .75);
                    return node with { Parameters = parameters.ToArray(), ChildGraph = node.ChildGraph is not { } child ? null :
                        child with { Nodes = child.Nodes.Select(ShortLived).ToArray() } };
                }
                var shortLived = graph with { Nodes = graph.Nodes.Select(ShortLived).ToArray() };
                File.WriteAllBytes(Path.Combine(directory, "CpuFountain-retirement.lfxb"),
                    SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(shortLived)));
                File.WriteAllBytes(Path.Combine(directory, "CpuBatch-math.lfxb"),
                    SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(MathVariant(graph))));
                File.WriteAllBytes(Path.Combine(directory, "CpuBatch-divide-zero.lfxb"),
                    SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(MathVariant(graph, divideByZero: true))));
                File.WriteAllBytes(Path.Combine(directory, "CpuBatch-error-order.lfxb"),
                    SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(MathVariant(graph, mixedError: true))));
            }
        }
    }

    private static GraphSnapshot MathVariant(GraphSnapshot graph, bool divideByZero = false, bool mixedError = false)
    {
        var system = graph.Nodes.Single(node => CpuParticleSchema.IsContainer(node.Kind));
        var update = system.ChildGraph!.Nodes.Single(node => CpuParticleSchema.Name(node.Kind) == "CpuParticleUpdate");
        var nodes = update.ChildGraph!.Nodes.ToList();
        var edges = update.ChildGraph.Connections.ToList();
        GraphNodeSnapshot Add(string kind, params (string Id, string Type, object Value)[] parameters)
        {
            var definition = GraphNodeDefinition.All.Single(item => item.TypeName == kind);
            var node = new GraphNodeSnapshot(Guid.NewGuid(), definition.Kind, definition.Name, 0, 0,
                definition.PortType, definition.PortCategory, Parameters: parameters.Select(parameter =>
                    new NodeParameterSnapshot(parameter.Id, parameter.Type, JsonSerializer.SerializeToElement(parameter.Value))).ToArray());
            nodes.Add(node); return node;
        }
        void Wire(GraphNodeSnapshot source, GraphNodeSnapshot target, string input) => edges.Add(new(source.Id, target.Id, input,
            GraphNodeDefinition.All.Single(item => item.Kind == source.Kind).OutputPorts?.FirstOrDefault()?.Id));
        var position = Add("CpuParticlePosition");
        var velocity = Add("CpuParticleVelocity");
        var size = Add("CpuParticleSize");
        var color = Add("CpuParticleColor");
        var life = Add("CpuParticleLife");
        var offset = Add("FloatX3", ("value", "vector3", new double[] { .25, -.1, .4 }));
        var moved = Add("AddVector3"); Wire(position, moved, "a"); Wire(offset, moved, "b");
        var accelerated = Add("MultiplyVector3", ("b", "vector3", new double[] { .99, 1.01, .98 })); Wire(velocity, accelerated, "a");
        var resized = Add("MultiplyVector2", ("b", "vector2", new double[] { .999, 1.001 })); Wire(size, resized, "a");
        var shortened = Add("Subtract", ("b", "float", .001)); Wire(life, shortened, "a");
        if (divideByZero)
        {
            var denominator = Add("Subtract", ("b", "float", .25));
            Wire(nodes.First(node => CpuParticleSchema.Name(node.Kind) == "CpuParticleAge"), denominator, "a");
            var division = Add("Divide", ("a", "float", 1d)); Wire(denominator, division, "b");
        }
        if (mixedError)
        {
            var id = Add("CpuParticleId");
            var age = nodes.First(node => CpuParticleSchema.Name(node.Kind) == "CpuParticleAge");
            var offsetId = Add("Add", ("b", "float", 999d)); Wire(id, offsetId, "a");
            var ageFactor = Add("Multiply", ("b", "float", 4004d)); Wire(age, ageFactor, "a");
            var denominator = Add("Subtract"); Wire(offsetId, denominator, "a"); Wire(ageFactor, denominator, "b");
            var division = Add("Divide", ("a", "float", 1d)); Wire(denominator, division, "b");
            var initialLife = Add("Add", ("b", "float", 1d)); Wire(id, initialLife, "a");
            var lifeLoss = Add("Multiply", ("b", "float", 10d)); Wire(age, lifeLoss, "a");
            shortened = Add("Subtract"); Wire(initialLife, shortened, "a"); Wire(lifeLoss, shortened, "b");
        }
        var previous = nodes.Single(node => CpuParticleSchema.Name(node.Kind) == "CpuParticleSetColor");
        foreach (var (kind, value) in new[] { ("CpuParticleSetPosition", moved), ("CpuParticleSetVelocity", accelerated),
                     ("CpuParticleSetSize", resized), ("CpuParticleSetLife", shortened), ("CpuParticleSetColor", color) })
        {
            var command = Add(kind);
            Wire(value, command, "value");
            edges.Add(new(previous.Id, command.Id, "signal", "then")); previous = command;
        }
        update = update with { ChildGraph = update.ChildGraph with { Nodes = nodes.ToArray(), Connections = edges.ToArray() } };
        system = system with { ChildGraph = system.ChildGraph with
            { Nodes = system.ChildGraph.Nodes.Select(node => node.Id == update.Id ? update : node).ToArray() } };
        return graph with { Nodes = graph.Nodes.Select(node => node.Id == system.Id ? system : node).ToArray() };
    }
}
