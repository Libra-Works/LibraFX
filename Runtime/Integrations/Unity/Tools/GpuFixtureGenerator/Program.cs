using LibraGpu.Core;
using System.Text.Json;

if (args.Length < 1 || args.Length > 2) throw new ArgumentException("Supply an output directory and optionally a frontend bundle JSON.");
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
if (args.Length == 2)
{
    using var bundle = JsonDocument.Parse(File.ReadAllText(args[1]));
    string canonicalJson = bundle.RootElement.GetProperty("canonicalJson").GetString()
        ?? throw new ArgumentException("Bundle canonicalJson is missing.");
    var compiled = GpuCompiler.Compile(canonicalJson);
    string expectedHash = bundle.RootElement.GetProperty("metadata").GetProperty("contentHash").GetString()
        ?? throw new ArgumentException("Bundle contentHash is missing.");
    if (compiled.ContentHash != expectedHash) throw new ArgumentException("Bundle canonical IR hash does not match metadata.");
    Write("Frontend", compiled);
}

void Export(string name, GpuProgram program)
{
    var compiled = GpuCompiler.Compile(program);
    Write(name, compiled);
}

void Write(string name, CompiledGpuProgram compiled)
{
    File.WriteAllText(Path.Combine(args[0], name + ".compute"), compiled.EmitUnityCompute().Text);
    File.WriteAllText(Path.Combine(args[0], name + ".json"), compiled.ExportMetadataJson());
}
