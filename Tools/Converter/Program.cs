using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.Models.Graph;

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
            var libraries = new List<GroupAssetLibrary>();
            for (var index = 3; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--force": force = true; break;
                    case "--embedded-groups": embedded = true; break;
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
            var resolver = new GroupCompilationResolver(libraries, embedded);
            if (args[0] == "convert")
                return await ConvertAsync(input, output, force, resolver) ? 0 : 1;
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
                var target = Path.ChangeExtension(Path.Combine(output, Path.GetRelativePath(input, file)), ".lfxb");
                if (!await ConvertAsync(file, target, force, resolver)) failed = true;
            }
            return failed ? 1 : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidDataException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void Usage(TextWriter writer)
    {
        writer.WriteLine("Usage: LiberaFxConverter convert <input.lfxproj> <output.lfxb> [options]");
        writer.WriteLine("       LiberaFxConverter batch <input-directory> <output-directory> [options]");
        writer.WriteLine("Options: --force  --groups <directory> (repeatable)  --nodes <directory> (repeatable)  --embedded-groups");
        writer.WriteLine("Linked groups use the latest library versions by default. --embedded-groups explicitly uses frozen embedded versions.");
        writer.WriteLine("Source projects and group libraries are never modified.");
    }

    private static async Task<bool> ConvertAsync(string input, string output, bool force, GroupCompilationResolver resolver)
    {
        string? temporary = null;
        try
        {
            if (string.Equals(input, output, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new IOException("Input and output must be different files.");
            await using var stream = File.OpenRead(input);
            var project = await ProjectDocumentReader.LoadAsync(stream);
            var bytes = VfxProgram.Compile(resolver.Resolve(project.Graph)).ToBinary();
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
