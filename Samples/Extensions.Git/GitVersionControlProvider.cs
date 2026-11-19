using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LiberaFx.Extensions.Abstractions;

namespace LiberaFx.OfficialExtensions.Git;

public sealed class GitExtension : IEditorExtension
{
    public void Register(IExtensionRegistration registration) => registration.AddVersionControlProvider(new GitVersionControlProvider());
    public void Dispose() { }
}

internal sealed partial class GitVersionControlProvider : IVersionControlProvider, IVersionControlWorkspaceProvider
{
    public string Id => "liberafx.git";
    public string Name => "Git";
    [GeneratedRegex("^[a-fA-F0-9]{40}([a-fA-F0-9]{24})?$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitId();

    public async Task<VcsWorkspaceStatus> GetWorkspaceStatusAsync(string localFile, CancellationToken cancellation)
    {
        var (directory, _) = Split(localFile);
        var output = await RunAsync(directory,
            ["--no-optional-locks", "-c", "core.fsmonitor=false", "status", "--porcelain=v2", "-z",
                "--branch", "--untracked-files=all", "--ignore-submodules=all"], cancellation);
        var records = Encoding.UTF8.GetString(output).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var changes = new List<VcsFileChange>();
        string? branch = null;
        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            if (record.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                branch = record[14..];
            }
            else if (record.StartsWith("? ", StringComparison.Ordinal))
            {
                changes.Add(new(record[2..], VcsChangeKind.None, VcsChangeKind.Untracked));
            }
            else if (record is ['1' or '2' or 'u', ' ', ..])
            {
                var fields = record.Split(' ', record[0] switch { '1' => 9, '2' => 10, _ => 11 });
                if (fields.Length != (record[0] switch { '1' => 9, '2' => 10, _ => 11 }) || fields[1].Length != 2)
                {
                    throw new InvalidDataException("Invalid Git status record.");
                }
                string? previous = null;
                if (record[0] == '2')
                {
                    if (++index >= records.Length)
                    {
                        throw new InvalidDataException("Invalid Git rename record.");
                    }
                    previous = records[index];
                }
                var conflict = record[0] == 'u';
                changes.Add(new(fields[^1], conflict ? VcsChangeKind.Conflict : Change(fields[1][0]),
                    conflict ? VcsChangeKind.Conflict : Change(fields[1][1]), previous));
            }
        }
        return new(branch, changes);
    }

    private static VcsChangeKind Change(char value) => value switch
    {
        '.' => VcsChangeKind.None,
        'A' => VcsChangeKind.Added,
        'M' or 'T' => VcsChangeKind.Modified,
        'D' => VcsChangeKind.Deleted,
        'R' or 'C' => VcsChangeKind.Renamed,
        _ => VcsChangeKind.Other
    };

    public async Task<IReadOnlyList<VcsRevision>> GetHistoryAsync(string localFile, CancellationToken cancellation)
    {
        var (directory, name) = Split(localFile);
        var output = Encoding.UTF8.GetString(await RunAsync(directory,
            ["log", "-100", "--format=%H%x09%aI%x09%s", "--", name], cancellation));
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            var fields = line.TrimEnd('\r').Split('\t', 3);
            if (fields.Length != 3 || !CommitId().IsMatch(fields[0]))
                throw new InvalidDataException("Invalid Git revision.");
            return new VcsRevision(fields[0], fields[2],
                DateTimeOffset.TryParse(fields[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null);
        }).ToArray();
    }

    public async Task<byte[]> ReadRevisionAsync(string localFile, string revision, CancellationToken cancellation)
    {
        if (!CommitId().IsMatch(revision)) throw new ArgumentException("A full Git commit ID is required.");
        var (directory, _) = Split(localFile);
        var root = Encoding.UTF8.GetString(await RunAsync(directory, ["rev-parse", "--show-toplevel"], cancellation)).Trim();
        if (string.IsNullOrEmpty(root)) throw new InvalidDataException("Git did not identify a repository.");
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(localFile));
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new InvalidDataException("The selected file is outside the Git repository.");
        // Git tree object paths always use '/' even when the working tree is on Windows.
        return await RunAsync(directory, ["show", revision + ":" + relative.Replace('\\', '/')], cancellation);
    }

    private static (string Directory, string Name) Split(string path)
    {
        if (!Path.IsPathFullyQualified(path) || string.IsNullOrEmpty(Path.GetDirectoryName(path)) ||
            string.IsNullOrEmpty(Path.GetFileName(path)))
            throw new ArgumentException("An absolute local file is required.");
        return (Path.GetDirectoryName(path)!, Path.GetFileName(path));
    }

    private static async Task<byte[]> RunAsync(string directory, string[] arguments, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--no-pager");
        start.ArgumentList.Add("--literal-pathspecs");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(start) ?? throw new IOException("Could not start Git.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var stop = timeout.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        try
        {
            var errorTask = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (output.Length + count > 16 * 1024 * 1024)
                    throw new InvalidDataException("The revision exceeds the 16 MiB limit.");
                output.Write(buffer, 0, count);
            }
            await process.WaitForExitAsync(timeout.Token);
            await errorTask;
            if (process.ExitCode != 0) throw new IOException($"Git operation failed (exit {process.ExitCode}).");
            return output.ToArray();
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
