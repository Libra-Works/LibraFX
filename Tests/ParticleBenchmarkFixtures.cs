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
        var referenceOptions = new VfxCompileOptions { OptimizeCpuParticleKernels = false };
        foreach (var count in new[] { 100, 512, 1000, 10000 })
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
                SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(graph, options: referenceOptions)));
            File.WriteAllBytes(Path.Combine(directory, "CpuFountain-aot-" + count + ".lfxb"),
                SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(graph)));
            File.WriteAllBytes(Path.Combine(directory, "CpuFountain-binary-" + count + ".lfxb"),
                CpuParticleBinaryOptimizer.OptimizePackage(File.ReadAllBytes(Path.Combine(directory, "CpuFountain-" + count + ".lfxb"))));
            var systemId = new byte[16];
            graph.Nodes.Single(node => CpuParticleSchema.IsContainer(node.Kind)).Id.TryWriteBytes(systemId, true, out _);
            File.WriteAllBytes(Path.Combine(directory, "CpuFountain-" + count + ".id"), systemId);
            if (count == 1000)
            {
                GraphNodeSnapshot Port(string kind, string name, int? valueType = null)
                {
                    var definition = GraphNodeDefinition.All.Single(item => item.TypeName == kind);
                    var parameters = new List<NodeParameterSnapshot>();
                    if (definition.Parameters.Any(item => item.Id == "name")) parameters.Add(new("name", "string", JsonSerializer.SerializeToElement(name)));
                    if (valueType is { } type) parameters.Add(new("valueType", definition.Parameters.Single(item => item.Id == "valueType").Type,
                        JsonSerializer.SerializeToElement(type)));
                    return new(Guid.NewGuid(), definition.Kind, name, 0, 0, definition.PortType, definition.PortCategory,
                        Parameters: parameters.Count == 0 ? null : parameters.ToArray());
                }
                var input = Port("InputProperty", "position", 2);
                var output = Port("OutputProperty", "echo", 2);
                var trigger = Port("InputSignal", "trigger");
                var done = Port("OutputSignal", "done");
                var owner = Port("OwnerMatrix", "owner");
                var withGameIo = graph with { Nodes = [.. graph.Nodes, input, output, trigger, done, owner],
                    Connections = [.. graph.Connections, new(input.Id, output.Id, "value", "value"), new(trigger.Id, done.Id, "signal", "then")] };
                File.WriteAllBytes(Path.Combine(directory, "CpuPool-gameio.lfxb"),
                    SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(withGameIo)));

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
                    SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(shortLived, options: referenceOptions)));
                File.WriteAllBytes(Path.Combine(directory, "CpuFountain-aot-retirement.lfxb"),
                    SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(shortLived)));
                File.WriteAllBytes(Path.Combine(directory, "CpuFountain-binary-retirement.lfxb"),
                    CpuParticleBinaryOptimizer.OptimizePackage(File.ReadAllBytes(Path.Combine(directory, "CpuFountain-retirement.lfxb"))));
                File.WriteAllBytes(Path.Combine(directory, "CpuBatch-math.lfxb"),
                    SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(MathVariant(graph), options: referenceOptions)));
                File.WriteAllBytes(Path.Combine(directory, "CpuBatch-divide-zero.lfxb"),
                    SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(MathVariant(graph, divideByZero: true), options: referenceOptions)));
                File.WriteAllBytes(Path.Combine(directory, "CpuBatch-error-order.lfxb"),
                    SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(MathVariant(graph, mixedError: true), options: referenceOptions)));
                foreach (var mode in new[] { "divide", "mixed", "zero-life", "unsupported" })
                    File.WriteAllBytes(Path.Combine(directory, "CpuBirth-" + mode + ".lfxb"),
                        SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(BirthVariant(graph, mode), options: referenceOptions)));
            }
        }
    }

    private static GraphSnapshot BirthVariant(GraphSnapshot graph, string mode)
    {
        var system = graph.Nodes.Single(node => CpuParticleSchema.IsContainer(node.Kind));
        GraphNodeSnapshot TransformStage(GraphNodeSnapshot stage)
        {
            var stageName = CpuParticleSchema.Name(stage.Kind);
            if (stageName is not ("CpuParticleSpawn" or "CpuParticleUpdate")) return stage;
            var spawn = stageName == "CpuParticleSpawn";
            var nodes = stage.ChildGraph!.Nodes.ToList();
            var edges = stage.ChildGraph.Connections.ToList();
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
            if (spawn && mode is "divide" or "mixed")
            {
                var id = Add("CpuParticleId");
                var denominator = Add("Subtract", ("b", "float", 2d)); Wire(id, denominator, "a");
                var division = Add("Divide", ("a", "float", 1d)); Wire(denominator, division, "b");
            }
            if (!spawn && mode == "mixed")
            {
                var command = Add("CpuParticleSetLife", ("value", "float", -1d));
                var previous = nodes.Single(node => CpuParticleSchema.Name(node.Kind) == "CpuParticleSetColor");
                edges.Add(new(previous.Id, command.Id, "signal", "then"));
            }
            if (mode == "zero-life")
            {
                if (spawn)
                    nodes = nodes.Select(node => CpuParticleSchema.Name(node.Kind) != "CpuParticleSetLife" ? node : node with
                    { Parameters = [new("value", "float", JsonSerializer.SerializeToElement(0d))] }).ToList();
                else Add("Divide", ("a", "float", 1d), ("b", "float", 0d));
            }
            if (spawn && mode == "unsupported") Add("Sin", ("value", "float", .3));
            return stage with { ChildGraph = stage.ChildGraph with { Nodes = nodes.ToArray(), Connections = edges.ToArray() } };
        }
        system = system with { ChildGraph = system.ChildGraph! with
            { Nodes = system.ChildGraph.Nodes.Select(TransformStage).ToArray() } };
        return graph with { Nodes = graph.Nodes.Select(node => node.Id == system.Id ? system : node).ToArray() };
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
        // Reuse the very same output in two expressions: separate attribute
        // nodes would not exercise the optimizer's shared-register boundary.
        var life = nodes.First(node => CpuParticleSchema.Name(node.Kind) == "CpuParticleLife");
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
