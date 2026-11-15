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
Console.WriteLine("PASS Unreal source bundle");
