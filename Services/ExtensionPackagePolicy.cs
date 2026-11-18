using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LiberaFx.Services;

internal static partial class ExtensionPackagePolicy
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex StableId();

    internal static bool ValidId(string? id) => id is not null && StableId().IsMatch(id);

    internal static string Fingerprint(string directory, bool recursive = true, bool legacy = false)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var length = new byte[sizeof(int)];
        var contentLength = new byte[sizeof(long)];
        var files = PackageFiles(directory, recursive, 0).Take(4097).ToArray();
        if (files.Length > 4096) throw new InvalidDataException("Extension package exceeds the file limit.");
        long totalBytes = 0;
        foreach (var path in files
                     .OrderBy(path => Path.GetRelativePath(directory, path), StringComparer.Ordinal))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Extension packages must not contain symbolic links.");
            var name = Encoding.UTF8.GetBytes(Path.GetRelativePath(directory, path));
            BinaryPrimitives.WriteInt32LittleEndian(length, name.Length);
            hash.AppendData(length);
            hash.AppendData(name);
            using var stream = File.OpenRead(path);
            if (!legacy)
            {
                BinaryPrimitives.WriteInt64LittleEndian(contentLength, stream.Length);
                hash.AppendData(contentLength);
            }
            totalBytes += stream.Length;
            if (totalBytes > 512L * 1024 * 1024)
                throw new InvalidDataException("Extension package exceeds 512 MiB.");
            var buffer = new byte[81920];
            int count;
            while ((count = stream.Read(buffer)) != 0) hash.AppendData(buffer.AsSpan(0, count));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static IEnumerable<string> PackageFiles(string directory, bool recursive, int depth)
    {
        if (depth > 16 || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Extension packages must not contain links or exceed 16 directory levels.");
        foreach (var path in Directory.EnumerateFiles(directory)) yield return path;
        if (!recursive) yield break;
        foreach (var child in Directory.EnumerateDirectories(directory))
        foreach (var path in PackageFiles(child, true, depth + 1))
            yield return path;
    }

    internal static string PackageFile(string directory, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidDataException("A package-relative file name is required.");
        var root = Path.GetFullPath(directory);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The file is outside the extension package.");
        var current = path;
        while (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Extension packages must not contain symbolic links.");
            current = Path.GetDirectoryName(current)!;
        }
        return path;
    }
}
