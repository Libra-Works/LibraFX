using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Gpu;
using LiberaFx.Services.Samples;
using LiberaFx.Services.Compilation;
using LibraGpu.Core;

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
                var cpuDraw = render.RootElement.GetProperty("cpuParticles")[0];
                if (cpuDraw.GetProperty("assetPath").GetString() != "builtin:procedural-material" ||
                    cpuDraw.GetProperty("proceduralMaterial").GetGuid() == Guid.Empty)
                    throw new InvalidOperationException(name + " requires its procedural glow material.");
            }
            if (name == "Fireworks" && (package.Templates.Count != 2 || package.DestroyRoutes.Count != 1 ||
                package.Templates.Any(template => template.CpuPrograms.Length == 0)))
                throw new InvalidOperationException("Fireworks must execute a launch-to-burst CPU particle lifecycle.");
            if (name == "GpuCurlTurbulence")
            {
                var node = graph.Nodes.Single(item => item.Kind.Value == "GpuParticle");
                var update = GpuParticleEditorService.ReadState(node).Program.Kernels.Single(kernel => kernel.Stage == KernelStage.Update);
                if (!update.Nodes.Any(item => item.Op == GpuOp.CurlNoise) ||
                    !update.Nodes.Any(item => item.Op == GpuOp.Turbulence) ||
                    !update.Writes.Any(item => item.Attribute == "Velocity") ||
                    !update.Writes.Any(item => item.Attribute == "Position"))
                {
                    throw new InvalidOperationException("GpuCurlTurbulence must use both noise operations to update particle motion.");
                }
            }
            if (name == "GpuFluidVelocity")
            {
                var producer = graph.Nodes.Single(node => node.Name == "Fluid2D velocity source");
                var consumer = graph.Nodes.Single(node => node.Name == "Particles following fluid");
                var state = GpuParticleEditorService.ReadState(consumer);
                var binding = state.GeneratedResources!["fluidVelocity"];
                var update = state.Program.Kernels.Single(kernel => kernel.Stage == KernelStage.Update);
                if (GpuParticleEditorService.ReadState(producer).Fluid2D is null ||
                    binding.ProducerNodeId != producer.Id || binding.OutputId != "VelocityOutput" ||
                    !update.Nodes.Any(node => node.Op == GpuOp.SampleFlowMap && node.Resource == "fluidVelocity") ||
                    !update.Writes.Any(write => write.Attribute == "Position"))
                {
                    throw new InvalidOperationException("GpuFluidVelocity must move particles using the fluid velocity output.");
                }
            }
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
