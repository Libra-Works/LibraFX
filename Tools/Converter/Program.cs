using LiberaFx.Services;
using LiberaFx.Services.Compilation;

return await Converter.RunAsync(args);

internal static class Converter
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            Console.WriteLine("Usage: LiberaFxConverter convert <input.lfxproj> <output.lfxb> [--force]");
            Console.WriteLine("       LiberaFxConverter batch <input-directory> <output-directory> [--force]");
            return 0;
        }
        if (args.Length is < 3 or > 4 || (args.Length == 4 && args[3] != "--force") ||
            args[0] is not ("convert" or "batch"))
        {
            Console.Error.WriteLine("Usage: LiberaFxConverter convert <input.lfxproj> <output.lfxb> [--force]");
            Console.Error.WriteLine("       LiberaFxConverter batch <input-directory> <output-directory> [--force]");
            return 2;
        }

        try
        {
            var input = Path.GetFullPath(args[1]);
            var output = Path.GetFullPath(args[2]);
            var force = args.Length == 4;
            if (args[0] == "convert")
                return await ConvertAsync(input, output, force) ? 0 : 1;
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
                if (!await ConvertAsync(file, target, force)) failed = true;
            }
            return failed ? 1 : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<bool> ConvertAsync(string input, string output, bool force)
    {
        string? temporary = null;
        try
        {
            if (string.Equals(input, output, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new IOException("Input and output must be different files.");
            await using var stream = File.OpenRead(input);
            var project = await ProjectDocumentReader.LoadAsync(stream);
            var bytes = VfxProgram.Compile(project.Graph).ToBinary();
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
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
