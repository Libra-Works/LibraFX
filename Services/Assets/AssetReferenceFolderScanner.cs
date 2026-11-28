namespace LiberaFx.Services;

public sealed record ReferenceFolderScan(IReadOnlyList<ReferenceSource> Sources,
    IReadOnlyList<ReferenceResource> Files, IReadOnlyList<string> Errors, bool Truncated);

public static class AssetReferenceFolderScanner
{
    public static async Task<ReferenceFolderScan> ReadAsync(IReadOnlyList<string> roots, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var files = new Dictionary<string, ReferenceResource>();
        var sources = new List<ReferenceSource>();
        var errors = new List<string>();
        var truncated = false;
        long remaining = 64 * 1024 * 1024;
        var projects = 0;
        foreach (var root in roots)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var page = LocalResourceCatalog.Read(root, recursive: true, cancellation);
                truncated |= page.Truncated;
                foreach (var file in page.Assets)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var resource = AssetReferenceIndex.File(file.Path, file.Kind == LocalResourceKind.Effect);
                    if (files.ContainsKey(resource.Key)) continue;
                    if (files.Count >= LocalResourceCatalog.Limit) { truncated = true; break; }
                    files.Add(resource.Key, resource);
                    if (file.Kind != LocalResourceKind.Effect) continue;
                    if (++projects > 256 || file.Bytes > 16 * 1024 * 1024 || file.Bytes > remaining)
                    { truncated = true; continue; }
                    try
                    {
                        await using var stream = System.IO.File.OpenRead(file.Path);
                        if (stream.Length > remaining || stream.Length > 16 * 1024 * 1024)
                        { truncated = true; continue; }
                        var bytes = new byte[checked((int)stream.Length)];
                        remaining -= bytes.Length;
                        await stream.ReadExactlyAsync(bytes, cancellation);
                        if (stream.ReadByte() != -1) throw new IOException("File changed during indexing.");
                        using var data = new MemoryStream(bytes, writable: false);
                        var project = await ProjectDocumentReader.LoadAsync(data);
                        cancellation.ThrowIfCancellationRequested();
                        sources.Add(new(resource, project.Graph, LocalPath: file.Path));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                                   System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
                    { errors.Add($"{file.Name}: {ex.Message}"); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { errors.Add($"{root}: {ex.Message}"); }
        }
        return new(sources, files.Values.ToArray(), errors, truncated);
    }
}
