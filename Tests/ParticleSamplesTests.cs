using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;

internal static class ParticleSamplesTests
{
    public static void Run()
    {
        foreach (var name in ParticleSamples.Names)
        {
            Console.WriteLine("Validating particle sample: " + name);
            var graph = ParticleSamples.Create(name);
            GraphValidator.ValidateDocument(graph);
            var package = SpawnableGroupPackage.Compile(graph);
            var bytes = SpawnableGroupPackageBinary.Serialize(package);
            if (bytes.Length == 0) throw new InvalidOperationException(name + " did not compile.");
            if (name.StartsWith("Cpu", StringComparison.Ordinal))
            {
                using var render = JsonDocument.Parse(package.RootRenderConfiguration);
                if (render.RootElement.GetProperty("cpuParticles")[0].GetProperty("assetPath").GetString() != "builtin:radial-glow")
                    throw new InvalidOperationException(name + " requires its generated glow texture.");
            }
            if (name == "Fireworks" && (package.Templates.Count != 2 || package.DestroyRoutes.Count != 1 ||
                package.Templates.Any(template => template.CpuPrograms.Length == 0)))
                throw new InvalidOperationException("Fireworks must execute a launch-to-burst CPU particle lifecycle.");
            var path = Environment.GetEnvironmentVariable("LIBERAFX_AUTHORING_SAMPLE_FIXTURES");
            if (!string.IsNullOrWhiteSpace(path))
            {
                Directory.CreateDirectory(path);
                File.WriteAllBytes(Path.Combine(path, name + ".lfxp"), bytes);
            }
        }
        MeshEmissionCompilationTests.Run();
        foreach (var name in GameEffectTemplates.Names)
        {
            Console.WriteLine("Validating game effect template: " + name);
            var graph = GameEffectTemplates.Create(name, duration: GameEffectTemplates.DefaultDuration(name));
            GraphValidator.ValidateDocument(graph);
            _ = SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(graph));
        }
    }
}
