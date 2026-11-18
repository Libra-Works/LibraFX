using LiberaFx.Extensions.Abstractions;

namespace LiberaFx.Samples;

public sealed class DirectoryHistoryExtension : IEditorExtension
{
    public void Register(IExtensionRegistration registration) =>
        registration.AddVersionControlProvider(new DirectoryHistoryProvider());
    public void Dispose() { }

    private sealed class DirectoryHistoryProvider : IVersionControlProvider
    {
        public string Id => "sample.directory-history";
        public string Name => "Local revision archive (SDK sample)";
        public Task<IReadOnlyList<VcsRevision>> GetHistoryAsync(string localFile, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var directory = HistoryDirectory(localFile);
            IReadOnlyList<VcsRevision> revisions = !Directory.Exists(directory) ? [] :
                Directory.EnumerateFiles(directory, "*.lfxproj", SearchOption.TopDirectoryOnly).Take(100)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Select(file => new VcsRevision(Path.GetFileName(file), Path.GetFileNameWithoutExtension(file),
                        new DateTimeOffset(File.GetLastWriteTimeUtc(file)))).ToArray();
            return Task.FromResult(revisions);
        }
        public async Task<byte[]> ReadRevisionAsync(string localFile, string revision, CancellationToken cancellation)
        {
            if (string.IsNullOrEmpty(revision) || Path.GetFileName(revision) != revision ||
                revision.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !revision.EndsWith(".lfxproj", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Select a revision from the archive.");
            var path = Path.Combine(HistoryDirectory(localFile), revision);
            if (new FileInfo(path).Length > 16 * 1024 * 1024)
                throw new InvalidDataException("Archived revision exceeds 16 MiB.");
            return await File.ReadAllBytesAsync(path, cancellation);
        }
        private static string HistoryDirectory(string file)
        {
            if (!Path.IsPathFullyQualified(file)) throw new ArgumentException("An absolute local file is required.");
            return file + ".history";
        }
    }
}
