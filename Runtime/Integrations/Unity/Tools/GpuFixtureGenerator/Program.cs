using LibraGpu.Core;

if (args.Length != 1) throw new ArgumentException("Supply an output directory.");
Directory.CreateDirectory(args[0]);
var sample = GpuSamples.CreateEmitter(128);
Export("Emitter", sample);
var blue = sample with
{
    Id = "blueEmitter",
    Kernels = sample.Kernels.Select(k => k.Stage == KernelStage.Spawn
        ? GpuDsl.Compile("@Position = emitterPosition; @Velocity = float3(0, 3, 0); @Lifetime = 2; @Size = float2(0.5, 0.5); @Color = float4(0, 0, 1, 1);", "spawn", KernelStage.Spawn)
        : k).ToList()
};
Export("BlueEmitter", blue);

void Export(string name, GpuProgram program)
{
    var compiled = GpuCompiler.Compile(program);
    File.WriteAllText(Path.Combine(args[0], name + ".compute"), compiled.EmitUnityCompute().Text);
    File.WriteAllText(Path.Combine(args[0], name + ".json"), compiled.ExportMetadataJson());
}
