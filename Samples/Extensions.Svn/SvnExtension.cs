using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using LiberaFx.Extensions.Abstractions;

namespace LiberaFx.Samples;

public sealed class SvnExtension : IEditorExtension
{
    public void Register(IExtensionRegistration registration) => registration.AddVersionControlProvider(new SvnProvider());
    public void Dispose() { }

    private sealed class SvnProvider : IVersionControlProvider
    {
        public string Id => "sample.svn";
        public string Name => "Subversion (svn CLI)";

        public async Task<IReadOnlyList<VcsRevision>> GetHistoryAsync(string localFile, CancellationToken cancellation)
        {
            var bytes = await RunAsync(["log", "--xml", "--limit", "100", "--non-interactive", "--", FileArgument(localFile)], cancellation);
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024
            });
            return XDocument.Load(reader).Root?.Elements("logentry").Take(100).Select(entry =>
            {
                var revision = entry.Attribute("revision")?.Value ?? "";
                ValidateRevision(revision);
                var timestamp = DateTimeOffset.TryParse(entry.Element("date")?.Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var time) ? time : (DateTimeOffset?)null;
                return new VcsRevision(revision, entry.Element("msg")?.Value ?? "", timestamp);
            }).ToArray() ?? throw new InvalidDataException("Subversion returned an invalid log.");
        }

        public Task<byte[]> ReadRevisionAsync(string localFile, string revision, CancellationToken cancellation)
        {
            ValidateRevision(revision);
            return RunAsync(["cat", "--revision", revision, "--non-interactive", "--", FileArgument(localFile)], cancellation);
        }

        private static string FileArgument(string path)
        {
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute working-copy file is required.");
            // An empty peg revision prevents '@' in file names being parsed as a revision.
            return path + "@";
        }

        private static void ValidateRevision(string revision)
        {
            if (string.IsNullOrEmpty(revision) || revision.Length > 20 || revision.Any(ch => ch is < '0' or > '9') ||
                !ulong.TryParse(revision, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                throw new InvalidDataException("Invalid Subversion revision.");
        }

        private static async Task<byte[]> RunAsync(string[] arguments, CancellationToken cancellation)
        {
            var start = new ProcessStartInfo("svn")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Could not start Subversion.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var stop = timeout.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            });
            try
            {
                var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
                using var output = new MemoryStream();
                var buffer = new byte[81920];
                int count;
                while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    if (output.Length + count > 16 * 1024 * 1024)
                        throw new InvalidDataException("Subversion output exceeds 16 MiB.");
                    output.Write(buffer, 0, count);
                }
                await process.WaitForExitAsync(timeout.Token);
                await errors;
                if (process.ExitCode != 0)
                    throw new IOException($"Subversion operation failed (exit {process.ExitCode}). Check the working copy and existing CLI authentication.");
                return output.ToArray();
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
    }
}
