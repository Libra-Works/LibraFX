using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Resources;

return await Converter.RunAsync(args);

internal static class Converter
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            Usage(Console.Out);
            return 0;
        }
        if (args.Length < 3 || args[0] is not ("convert" or "batch"))
        {
            Usage(Console.Error);
            return 2;
        }

        try
        {
            var input = Path.GetFullPath(args[1]);
            var output = Path.GetFullPath(args[2]);
            var force = false;
            var embedded = false;
            string? resourceMap = null, bindingSnapshot = null, targetEngine = null, targetProject = null;
            var libraries = new List<GroupAssetLibrary>();
            for (var index = 3; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--force": force = true; break;
                    case "--embedded-groups": embedded = true; break;
                    case "--resource-map" when index + 1 < args.Length && resourceMap is null:
                        resourceMap = Path.GetFullPath(args[++index]); break;
                    case "--binding-snapshot" when index + 1 < args.Length && bindingSnapshot is null:
                        bindingSnapshot = Path.GetFullPath(args[++index]); break;
                    case "--target-engine" when index + 1 < args.Length && targetEngine is null:
                        targetEngine = args[++index]; break;
                    case "--target-project" when index + 1 < args.Length && targetProject is null:
                        targetProject = args[++index]; break;
                    case "--groups" when index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal):
                        libraries.Add(new GroupAssetLibrary(Path.GetFullPath(args[++index])));
                        break;
                    case "--nodes" when index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal):
                        GraphNodeDefinition.LoadDirectory(Path.GetFullPath(args[++index]), out var warnings);
                        if (warnings.Count != 0) throw new InvalidDataException(string.Join(Environment.NewLine, warnings));
                        break;
                    default: Usage(Console.Error); return 2;
                }
            }
            ExportOptions? export = null;
            if (resourceMap is not null || bindingSnapshot is not null || targetEngine is not null || targetProject is not null)
            {
                if (args[0] != "convert" || bindingSnapshot is null ||
                    string.IsNullOrWhiteSpace(targetEngine) || !Guid.TryParse(targetProject, out var projectId) ||
                    projectId == Guid.Empty || !Path.GetExtension(output).Equals(".lfxrm", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Resource manifest export requires convert, .lfxrm, --binding-snapshot, --target-engine and --target-project; --resource-map is optional when the project stores its identity document.");
                export = new(resourceMap, bindingSnapshot, new(targetEngine, projectId));
            }
            else if (Path.GetExtension(output).Equals(".lfxrm", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Resource manifest export requires explicit mapping, snapshot and target project options.");
            }
            if (args[0] == "convert" && export is null &&
                !Path.GetExtension(output).Equals(EffectBinary.FileExtension, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Compiled effects use .lfxb. Legacy .lfxp files remain readable; choose a .lfxb output path.");
            }
            var resolver = new GroupCompilationResolver(libraries, embedded);
            if (args[0] == "convert")
                return await ConvertAsync(input, output, force, resolver, export) ? 0 : 1;
            if (!Directory.Exists(input))
            {
                Console.Error.WriteLine($"Input directory not found: {input}");
                return 1;
            }
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false
            };
            var files = Directory.EnumerateFiles(input, "*.lfxproj", options)
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (files.Length == 0)
            {
                Console.Error.WriteLine("No .lfxproj files found.");
                return 1;
            }
            var failed = false;
            foreach (var file in files)
            {
                var target = Path.ChangeExtension(Path.Combine(output, Path.GetRelativePath(input, file)), EffectBinary.FileExtension);
                if (!await ConvertAsync(file, target, force, resolver)) failed = true;
            }
            return failed ? 1 : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidDataException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void Usage(TextWriter writer)
    {
        writer.WriteLine("Usage: LiberaFxConverter convert <input.lfxproj> <output.lfxb|output.lfxrm> [options]");
        writer.WriteLine("       LiberaFxConverter batch <input-directory> <output-directory> [options]");
        writer.WriteLine("Options: --force  --groups <directory> (repeatable)  --nodes <directory> (repeatable)  --embedded-groups");
        writer.WriteLine("Linked groups use the latest library versions by default. --embedded-groups explicitly uses frozen embedded versions.");
        writer.WriteLine("Source projects and group libraries are never modified.");
        writer.WriteLine("All effect exports use .lfxb packages, including simple graphs, lifecycle and runtime Signal timeline keys.");
        writer.WriteLine("Legacy .lfxp and raw .lfxb remain runtime inputs; raw bytecode is no longer a converter output.");
        writer.WriteLine("Mapped .lfxrm: [--resource-map <file>] --binding-snapshot <file> --target-engine <engine> --target-project <guid>");
        writer.WriteLine("The project identity document is used unless an identical --resource-map is supplied. Snapshot assertions are not live verification.");
    }

    private sealed record ExportOptions(string? ResourceMap, string Snapshot, EngineProjectIdentity Project);

    private static async Task<bool> ConvertAsync(string input, string output, bool force, GroupCompilationResolver resolver,
        ExportOptions? export = null)
    {
        string? temporary = null;
        try
        {
            if (string.Equals(input, output, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new IOException("Input and output must be different files.");
            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (export is not null && (string.Equals(output, export.ResourceMap, pathComparison) ||
                string.Equals(output, export.Snapshot, pathComparison)))
                throw new IOException("Output must not overwrite the resource map or binding snapshot.");
            await using var stream = File.OpenRead(input);
            var project = await ProjectDocumentReader.LoadAsync(stream);
            GpuParticleEditorService.RejectGpu(project.Graph);
            if (export is null && (project.Graph.ResourceIdentities is not null ||
                                   ResourceIdentityExport.HasEmbeddedMetadata(project.Graph)))
                throw new InvalidDataException("This project contains portable resource identities; use explicit .lfxrm mapping export. Mapped .lfxb export is not implemented.");
            var graph = resolver.Resolve(project.Graph);
            if (export is null && (graph.ResourceIdentities is not null ||
                                   ResourceIdentityExport.HasEmbeddedMetadata(graph)))
                throw new InvalidDataException("This project contains portable resource identities; use explicit .lfxrm mapping export. Mapped .lfxb export is not implemented.");
            byte[] bytes;
            if (export is not null)
            {
                var map = project.Graph.ResourceIdentities;
                if (export.ResourceMap is { } resourceMapPath)
                {
                    using var mapStream = File.OpenRead(resourceMapPath);
                    var supplied = ResourceIdentityStore.Read(mapStream);
                    if (map is not null && !ResourceIdentityStore.ContentEquals(map, supplied))
                        throw new InvalidDataException("The external resource map conflicts with the identity document saved in the project.");
                    map = supplied;
                }
                if (map is null)
                    throw new InvalidDataException("The project has no registered resource identity document; register explicit IDs and project bindings in the editor or supply --resource-map.");
                using var snapshotStream = File.OpenRead(export.Snapshot);
                var probe = ResourceIdentitySnapshotProbe.Read(snapshotStream);
                bytes = ResourceIdentityExport.CreateManifest(graph, map, export.Project, probe);
            }
            else
            {
                bytes = EffectBinary.Compile(graph);
            }
            var directory = Path.GetDirectoryName(output)!;
            Directory.CreateDirectory(directory);
            if (!force && File.Exists(output)) throw new IOException($"Output already exists: {output}");
            temporary = Path.Combine(directory, $".liberafx-{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(temporary, bytes);
            File.Move(temporary, output, force);
            temporary = null;
            Console.WriteLine($"{input} -> {output}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or
                                   System.Text.Json.JsonException or InvalidOperationException)
        {
            Console.Error.WriteLine($"{input}: {ex.Message}");
            return false;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
