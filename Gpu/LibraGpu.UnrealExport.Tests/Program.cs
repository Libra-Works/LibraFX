using System.Text.Json.Nodes;
using LibraGpu.Core;
using LibraGpu.UnrealExport;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static void Reject(Action action)
{
    try { action(); }
    catch (ArgumentException) { return; }
    throw new Exception("Invalid binding accepted.");
}

var program = GpuSamples.CreateEmitter();
var paths = new Dictionary<string, string> { ["billboardTexture"] = "/Game/FX/T_Spark.T_Spark" };
var bundle = UnrealGpuExporter.Compile(program, paths);
var compiled = GpuCompiler.Compile(program);
Check(bundle.Metadata.ContentHash == compiled.ContentHash, "IR hash");
Check(bundle.Metadata.SourceHash == compiled.EmitUsf().ContentHash, "USF hash");
Check(bundle.Kernels.Count == compiled.Kernels.Count, "kernel count");
for (var i = 0; i < bundle.Kernels.Count; i++)
    Check(bundle.Kernels[i].Packet.SequenceEqual(compiled.RegisterKernels[i].SerializeLittleEndian()), "packet");
var json = JsonNode.Parse(UnrealGpuExporter.Serialize(bundle))!;
Check(json["purpose"]!.GetValue<string>() == "SourceExport", "purpose");
Check(!json["runtimeExecuted"]!.GetValue<bool>(), "runtime execution");
Check(json["textures"]![0]!["objectPath"]!.GetValue<string>() == paths["billboardTexture"], "path");
Check(Convert.FromBase64String(json["kernels"]![0]!["packet"]!.GetValue<string>()).SequenceEqual(bundle.Kernels[0].Packet), "packet JSON");
Reject(() => UnrealGpuExporter.Compile(program, new Dictionary<string, string>()));
Reject(() => UnrealGpuExporter.Compile(program, new Dictionary<string, string> { ["wrong"] = paths["billboardTexture"] }));
Reject(() => UnrealGpuExporter.Compile(program, new Dictionary<string, string> { ["billboardTexture"] = "not-a-path" }));
Reject(() => UnrealGpuExporter.Compile(program, new Dictionary<string, string> { ["billboardTexture"] = "/Game/FX/T.T:Subobject" }));
var edited = program with { Capacity = program.Capacity / 2 };
Check(UnrealGpuExporter.Compile(edited, paths).Metadata.ContentHash != bundle.Metadata.ContentHash, "edited IR hash");
var tooManyRegisters = new GpuProgram
{
    Id = "registerLimit",
    Capacity = 1,
    Kernels = [new() {
        Id = "spawn", Stage = KernelStage.Spawn,
        Nodes = Enumerable.Range(0, 257).Select(i => new GpuNode
        { Id = $"n{i}", Op = GpuOp.Constant, Type = GpuType.Float, Value = [0] }).ToList()
    }],
    Flow = [new() { Id = "spawnStep", Kind = FlowKind.Spawn, Kernel = "spawn" }]
};
try
{
    UnrealGpuExporter.Compile(tooManyRegisters, new Dictionary<string, string>());
    throw new Exception("Unreal register limit was not enforced.");
}
catch (NotSupportedException) { }
Console.WriteLine("PASS Unreal source bundle");

if (args.Length == 4 && args[0] == "--ue-fixtures")
{
    var sourceDirectory = args[1];
    var destinationDirectory = args[2];
    Directory.CreateDirectory(destinationDirectory);
    foreach (var name in new[] { "math", "noise", "noiseguards" })
    {
        var fixtureProgram = GpuCompiler.Parse(File.ReadAllText(Path.Combine(sourceDirectory, name + ".json")));
        var fixture = UnrealGpuExporter.Compile(fixtureProgram, new Dictionary<string, string>());
        File.WriteAllText(Path.Combine(destinationDirectory, name + ".lfxgpu"), UnrealGpuExporter.Serialize(fixture));
        var reference = Path.Combine(sourceDirectory, name + ".reference.json");
        if (File.Exists(reference))
            File.Copy(reference, Path.Combine(destinationDirectory, name + ".reference.json"), overwrite: true);
    }
    var support = GpuShaderSupport.EmitHlslInclude();
    File.WriteAllText(args[3], support, new System.Text.UTF8Encoding(false));
    File.WriteAllText(Path.Combine(destinationDirectory, "LibraGpuSupport.hlsl"), support, new System.Text.UTF8Encoding(false));
    Check(File.ReadAllText(args[3]) == support, "Owned UE helper must match canonical Core helper.");
    Console.WriteLine("PASS Generated canonical UE new-op fixtures and exact shared helper");
}
