using System.Diagnostics;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;

internal static class EffectBinaryConverterTests
{
    public static void Run()
    {
        var executable = Environment.GetEnvironmentVariable("LIBERAFX_CONVERTER_EXE");
        if (string.IsNullOrEmpty(executable))
        {
            Console.WriteLine("Unified binary CLI checks skipped: set LIBERAFX_CONVERTER_EXE to the matching converter.");
            return;
        }
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
        {
            throw new InvalidOperationException("LIBERAFX_CONVERTER_EXE must identify a matching prebuilt executable.");
        }
        var directory = Path.Combine(Path.GetTempPath(), "liberafx-effect-binary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var inputDirectory = Path.Combine(directory, "input");
            Directory.CreateDirectory(inputDirectory);
            var input = Path.Combine(inputDirectory, "effect.lfxproj");
            var output = Path.Combine(directory, "effect.lfxb");
            var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
            workspace.CreateNode(GraphNodeKind.Float);
            using (var stream = new MemoryStream())
            {
                new ProjectFileService().SaveAsync(stream, workspace).GetAwaiter().GetResult();
                File.WriteAllBytes(input, stream.ToArray());
            }
            var source = File.ReadAllBytes(input);
            Invoke(executable, ["convert", input, output], expectedSuccess: true);
            var bytes = File.ReadAllBytes(output);
            var package = SpawnableGroupPackageBinary.Deserialize(bytes);
            Require(package.Templates.Count == 0, "Simple converter output was not an effect package.");
            Require(bytes.SequenceEqual(EffectBinary.Compile(workspace.CreateSnapshot())),
                "Editor and converter exports differ for the same graph.");
            var batchDirectory = Path.Combine(directory, "batch");
            Invoke(executable, ["batch", inputDirectory, batchDirectory], expectedSuccess: true);
            Require(bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(batchDirectory, "effect.lfxb"))),
                "Batch conversion used a different binary format.");
            var legacyOutput = Path.Combine(directory, "effect.lfxp");
            Invoke(executable, ["convert", input, legacyOutput], expectedSuccess: false);
            Require(!File.Exists(legacyOutput), "Converter still writes a second effect format.");
            Invoke(executable, ["convert", input, output], expectedSuccess: false);
            Require(bytes.SequenceEqual(File.ReadAllBytes(output)), "Rejected export damaged an existing binary.");
            Require(source.SequenceEqual(File.ReadAllBytes(input)), "Converter modified its source project.");
            var particle = new GraphWorkspaceViewModel(new UndoRedoManager());
            var spawnable = particle.CreateNode(GraphNodeKind.SpawnableGroup);
            Require(particle.EnterGraph(spawnable), "Cannot enter converter Spawnable fixture.");
            GpuParticleEditorService.Create(particle);
            particle.NavigateToGraph(0);
            var gpuInput = Path.Combine(inputDirectory, "gpu-spawnable.lfxproj");
            using (var stream = new MemoryStream())
            {
                new ProjectFileService().SaveAsync(stream, particle).GetAwaiter().GetResult();
                File.WriteAllBytes(gpuInput, stream.ToArray());
            }
            var gpuOutput = Path.Combine(directory, "gpu-spawnable.lfxb");
            Invoke(executable, ["convert", gpuInput, gpuOutput], expectedSuccess: true);
            Require(File.ReadAllBytes(gpuOutput).SequenceEqual(EffectBinary.Compile(particle.CreateSnapshot())),
                "Nested GPU/Spawnable conversion must match the editor's executable package.");
            var list = Path.Combine(directory, "effects.txt");
            File.WriteAllText(list, "# Relative input paths and renamed nested outputs\n\ninput/effect.lfxproj\n" +
                "input/gpu-spawnable.lfxproj\tparticles/nested.lfxb\n");
            var listed = Path.Combine(directory, "listed");
            Invoke(executable, ["batch", "--list", list, listed], expectedSuccess: true);
            Require(bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(listed, "effect.lfxb"))) &&
                File.ReadAllBytes(gpuOutput).SequenceEqual(File.ReadAllBytes(Path.Combine(listed, "particles", "nested.lfxb"))),
                "List conversion must preserve both simple and nested GPU effect packages.");
            File.WriteAllText(list, "input/effect.lfxproj\t../escaped.lfxb\n");
            Invoke(executable, ["batch-list", list, listed], expectedSuccess: false);
            Require(!File.Exists(Path.Combine(directory, "escaped.lfxb")), "List conversion must keep outputs inside its output directory.");
            File.WriteAllText(list, "input/effect.lfxproj\tsame.lfxb\ninput/gpu-spawnable.lfxproj\tsame.lfxb\n");
            Invoke(executable, ["batch-list", list, listed], expectedSuccess: false);
            Require(!File.Exists(Path.Combine(listed, "same.lfxb")), "Duplicate list outputs must be rejected before any conversion.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Invoke(string executable, string[] arguments, bool expectedSuccess)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start converter.");
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Converter timed out.");
        }
        Require((process.ExitCode == 0) == expectedSuccess, "Unexpected converter exit status.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
