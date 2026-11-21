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
        }
    }
}
