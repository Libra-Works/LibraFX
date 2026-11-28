using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LiberaFx.Extensions.Abstractions;
using LiberaFx.Services.Settings;

namespace LiberaFx.Services.Crash;

public sealed record CrashDraft(int Version, Guid Id, string Kind, string Title, string Body,
    string Diagnostics, string Delivery = "Unsent");

/// <summary>Minimal best-effort collection. Never inspects exception messages, stack traces or projects.</summary>
public sealed class CrashDraftStore : IDisposable
{
    private readonly string _root;
    private readonly string _session;
    private FileStream? _lease;
    private bool _managed;
    private readonly object _gate = new();
    public string Status { get; private set; } = "";

    public CrashDraftStore()
    {
        _root = Path.Combine(AppDataDirectory.Root, "LiberaFx", "CrashDrafts");
        _session = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".session");
    }

    // Call before installing exception handlers. Failures are visible, never fatal to editor startup.
    public void Start()
    {
        lock (_gate)
        {
            try
            {
                if (_lease is not null) return;
                Directory.CreateDirectory(_root);
                _lease = new FileStream(_session, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                _lease.WriteByte(1);
                _lease.Flush(true);
            }
            catch { Status = "Crash.StorageFailed"; }
        }
    }

    public void CaptureManagedException(Exception? exception)
    {
        // Even exception type names can be user-controlled; collect a fixed category only.
        lock (_gate)
        {
            try
            {
                if (_managed) return;
                var draft = NewDraft("ManagedException");
                Save(draft);
                _managed = true;
                // A saved managed draft supersedes this session's abnormal-termination marker.
                _lease?.Dispose();
                _lease = null;
                File.Delete(_session);
            }
            catch { Status = "Crash.StorageFailed"; }
        }
    }

    public IReadOnlyList<CrashDraft> Recover()
    {
        lock (_gate)
        {
            var result = new List<CrashDraft>();
            try
            {
                Directory.CreateDirectory(_root);
                foreach (var marker in Directory.EnumerateFiles(_root, "*.session").Take(100))
                {
                    if (marker == _session) continue;
                    try
                    {
                        // A live instance holds an exclusive lease. Never steal its session.
                        using (new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                        var id = Guid.ParseExact(Path.GetFileNameWithoutExtension(marker), "N");
                        var path = DraftPath(id);
                        if (!File.Exists(path)) Save(NewDraft("UncleanTermination") with { Id = id });
                        File.Delete(marker);
                    }

                    catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 or 11) { }
                    catch { Status = "Crash.StorageFailed"; }
                }
                foreach (var path in Directory.EnumerateFiles(_root, "*.json").Take(100))
                {
                    try
                    {
                        var id = Guid.ParseExact(Path.GetFileNameWithoutExtension(path), "N");
                        result.Add(Read(id));
                    }
                    catch { Status = "Crash.CorruptDraft"; } // Preserve, never overwrite corrupt data.
                }
            }
            catch { Status = "Crash.StorageFailed"; }
            return result;
        }
    }

    public bool TryClaimStartupPrompt()
    {
        var show = false;
        foreach (var draft in Recover().Where(draft => draft.Delivery != "Sent"))
        {
            var marker = DraftPath(draft.Id) + ".shown";
            try
            {
                using var claimed = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                show = true;
            }
            catch (IOException) when (File.Exists(marker)) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Status = "Crash.StorageFailed";
            }
        }
        return show;
    }

    private static CrashDraft NewDraft(string kind) => new(1, Guid.NewGuid(), kind,
        kind == "ManagedException" ? "LIBERA FX managed exception" : "LIBERA FX unclean termination",
        kind == "ManagedException"
            ? "A managed exception was observed. No exception message or stack was collected."
            : "The previous session did not finish cleanly. Cause is unknown: native crash, forced termination, power loss, or shutdown failure. This is not proof of a native crash.",
        $"Runtime major: {Environment.Version.Major}\nOS family: {(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Other")}\nArchitecture: {RuntimeInformation.ProcessArchitecture}\n");

    private string DraftPath(Guid id) => Path.Combine(_root, id.ToString("N") + ".json");

    public CrashDraft Read(Guid id)
    {
        lock (_gate)
        {
            var path = DraftPath(id);
            if (new FileInfo(path).Length > 256 * 1024) throw new InvalidDataException();
            var draft = JsonSerializer.Deserialize<CrashDraft>(File.ReadAllText(path));
            if (draft is null || draft.Version != 1 || id == Guid.Empty || draft.Id != id ||
                draft.Title is null || draft.Body is null || draft.Diagnostics is null ||
                draft.Kind is not ("ManagedException" or "UncleanTermination") ||
                draft.Delivery is not ("Unsent" or "Sending" or "Sent"))
                throw new InvalidDataException();
            return draft;
        }
    }

    public void Save(CrashDraft draft)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_root);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(draft);
            if (bytes.Length > 256 * 1024) throw new InvalidDataException("Draft exceeds local limit.");
            var temporary = Path.Combine(_root, $"{draft.Id:N}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes); stream.Flush(true); }
                File.Move(temporary, DraftPath(draft.Id), true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public void Delete(CrashDraft draft)
    {
        lock (_gate)
        {
            // Explicit deletion also removes interrupted atomic-write copies for this draft.
            if (Directory.Exists(_root))
                foreach (var temporary in Directory.EnumerateFiles(_root, $"{draft.Id:N}.*.tmp"))
                    File.Delete(temporary);
            File.Delete(DraftPath(draft.Id));
            File.Delete(DraftPath(draft.Id) + ".shown");
        }
    }

    /// <summary>Only call on normal lifecycle exit, not from fatal exception handlers.</summary>
    public void CompleteCleanExit()
    {
        lock (_gate)
        {
            _lease?.Dispose();
            _lease = null;
            try { File.Delete(_session); }
            catch { Status = "Crash.StorageFailed"; }
        }
    }

    // Dispose releases handles but deliberately does not claim a clean exit.
    public void Dispose() { lock (_gate) { _lease?.Dispose(); _lease = null; } }
}

public static class CrashReportSafety
{
    // Conservative line-based redaction: retaining less is safer than retaining a
    // fragment of a credential. Review is still required; this is not a PII scrubber.
    private const string SensitivePattern =
        @"(?i)((?<![a-z0-9])[a-z]:[\\/]|\\\\|file:(?:/{2,}|\\{2,})\S+|(?<![:\w/])/(?:[^\s/]+/)+[^\s]*|gh[pousr]_[a-z0-9]+|github_pat_|sk-[a-z0-9_-]+|AKIA[A-Z0-9]{16}|eyJ[a-z0-9_-]+\.[a-z0-9_-]+\.[a-z0-9_-]+|(?:api[_-]?key|token|password|passwd|secret|authorization|cookie)\s*[:=]\s*\S+|Bearer\s+\S+|-----BEGIN .*PRIVATE KEY)";

    public static string RedactLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var privateKey = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("-----BEGIN", StringComparison.OrdinalIgnoreCase) &&
                lines[i].Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
                privateKey = true;
            var endKey = privateKey && lines[i].Contains("-----END", StringComparison.OrdinalIgnoreCase);
            if (privateKey || System.Text.RegularExpressions.Regex.IsMatch(lines[i], SensitivePattern,
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                lines[i] = "[REDACTED]";
            if (endKey) privateKey = false;
        }
        return string.Join("\n", lines);
    }

    // Conservative tripwire, not a promise of general PII/secret detection.
    public static void Validate(CrashReport report)
    {
        if (string.IsNullOrWhiteSpace(report.Title) || report.Title.Length > 200 ||
            string.IsNullOrWhiteSpace(report.Body) || report.Body.Length > 48000)
            throw new InvalidDataException("Crash.InvalidReport");
        Check(report.Title);
        Check(report.Body);
        if (report.Attachments is { Count: > 1 })
            throw new InvalidDataException("Crash.InvalidReport");
        foreach (var attachment in report.Attachments ?? [])
        {
            if (attachment.Name != "diagnostics.txt" || attachment.MediaType != "text/plain" ||
                attachment.Content.Length > 16000)
                throw new InvalidDataException("Crash.InvalidReport");
            Check(new UTF8Encoding(false, true).GetString(attachment.Content.Span));
        }
    }

    private static void Check(string text)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(text,
            SensitivePattern,
            System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidDataException("Crash.SensitiveText");
    }
}
