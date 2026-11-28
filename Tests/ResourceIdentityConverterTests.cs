using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Project;
using LiberaFx.Models.Resources;
using LiberaFx.Services.Assets;

// Set LIBERAFX_CONVERTER_EXE to an isolated, prebuilt converter executable to run this suite.
internal static class ResourceIdentityConverterTests
{
    public static void Run()
    {
        var executable = Environment.GetEnvironmentVariable("LIBERAFX_CONVERTER_EXE");
        if (string.IsNullOrEmpty(executable)) { Console.WriteLine("Resource converter CLI check skipped: no executable supplied."); return; }
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new InvalidOperationException("LIBERAFX_CONVERTER_EXE must identify a prebuilt executable.");
        var directory = Path.Combine(Path.GetTempPath(), "liberafx-resource-converter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "input.lfxproj");
            var output = Path.Combine(directory, "output.lfxrm");
            var mapPath = Path.Combine(directory, "mapping.json");
            var snapshotPath = Path.Combine(directory, "snapshot.json");
            var target = new EngineProjectIdentity("Unreal", Guid.NewGuid());
            var other = new EngineProjectIdentity("Unreal", Guid.NewGuid());
            var resourceId = Guid.NewGuid();
            var nodeId = Guid.NewGuid();
            var binding = new OpaqueBinding("unreal.object-path", "/Game/Mesh/Mesh.Mesh");
            var project = new LiberaFxProjectDocument
            {
                FormatVersion = 1,
                Nodes = [new(nodeId, "Model", "ModelDraw", 0, 0, Parameters:
                    [
                        new("assetPath", "string", JsonSerializer.SerializeToElement("/Game/Old/Old.Old")),
                        new(ResourceIdentityExport.EmbeddedParameterId, ResourceIdentityExport.EmbeddedParameterType,
                            JsonSerializer.SerializeToElement(new EmbeddedResourceIdentities(1, [new("assetPath", resourceId)], [])))
                    ]),
                    new(Guid.NewGuid(), "Value", "Float", 100, 0)],
                Connections = []
            };
            File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(project));
            var inputBefore = File.ReadAllBytes(input);
            var map = new ResourceIdentityDocument(1, [new(resourceId, "model", "Mesh")],
                [new(target, resourceId, "model", binding)], [],
                [new(nodeId, "assetPath", resourceId)], []);
            using (var stream = File.Create(mapPath)) ResourceIdentityStore.Write(stream, map);
            var snapshot = new ResourceBindingSnapshot(1, target, true,
                [new(binding, ResourceResolutionStatus.Resolved, "model")], []);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new JsonStringEnumConverter() }
            };
            File.WriteAllBytes(snapshotPath, JsonSerializer.SerializeToUtf8Bytes(snapshot, options));
            string[] Arguments(string destination, EngineProjectIdentity projectIdentity) =>
            [
                "convert", input, destination, "--resource-map", mapPath, "--binding-snapshot", snapshotPath,
                "--target-engine", projectIdentity.Engine, "--target-project", projectIdentity.ProjectId.ToString("D")
            ];
            Require(Invoke(executable, directory, Arguments(output, target)) == 0, "Mapped converter export failed.");
            using var result = JsonDocument.Parse(File.ReadAllBytes(output));
            Require(result.RootElement.GetProperty("uses")[0].GetProperty("resourceId").GetGuid() == resourceId &&
                result.RootElement.GetProperty("renderConfiguration").GetProperty("nodes")[0].GetProperty("assetPath").GetString() == binding.Value,
                "Converter output lost identity or resolved binding.");
            Require(inputBefore.SequenceEqual(File.ReadAllBytes(input)), "Converter rewrote its source project.");
            var rejected = Path.Combine(directory, "rejected.lfxrm");
            Require(Invoke(executable, directory, Arguments(rejected, other)) != 0 && !File.Exists(rejected),
                "Converter reused another project's mapping.");
            var package = Path.Combine(directory, "rejected.lfxp");
            Require(Invoke(executable, directory, Arguments(package, target)) != 0 && !File.Exists(package),
                "Converter silently bypassed mapping for a package.");
            var unsupported = Path.Combine(directory, "rejected.bin");
            Require(Invoke(executable, directory, ["convert", input, unsupported]) != 0 && !File.Exists(unsupported),
                "Converter silently discarded embedded portable identities.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static int Invoke(string executable, string directory, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.Environment["LIBERAFX_DATA_ROOT"] = Path.Combine(directory, "data");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start converter.");
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new InvalidOperationException("Converter timed out.");
        }
        return process.ExitCode;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
