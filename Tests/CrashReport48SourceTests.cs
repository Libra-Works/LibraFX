using System.Text;
using LiberaFx.Extensions.Abstractions;
using LiberaFx.Services.Crash;
using LiberaFx.Services.Settings;

internal static class CrashReport48SourceTests
{
    // Synthetic, offline checks only. Parent opts in after building, under an isolated data root.
    public static async Task RunAsync()
    {
        var original = Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT");
        if (string.IsNullOrEmpty(original) || !Path.IsPathFullyQualified(original))
            throw new InvalidOperationException("Set an absolute isolated LIBERAFX_DATA_ROOT before this check.");
        var isolated = Path.Combine(original, "crash-48-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("LIBERAFX_DATA_ROOT", isolated);
        try
        {
            var setup = new StartupSetupState();
            Require(setup.IsPending, "A fresh profile did not offer first-run setup.");
            setup.Complete();
            Require(!new StartupSetupState().IsPending, "Completed setup was shown on another launch.");

            CrashReportSafety.Validate(new("Safe link", "See https://example.invalid/issues/1"));
            using var store = new CrashDraftStore();
            store.Start();
            using (var observer = new CrashDraftStore())
                Require(observer.Recover().Count == 0, "A live session must not be reported.");
            store.CaptureManagedException(new Exception(
                @"ghp_SYNTHETICSECRET C:\Users\Private\source.cs project-confidential"));
            var managed = store.Recover().Single();
            Require(managed.Kind == "ManagedException", "Managed classification missing.");
            Require(store.TryClaimStartupPrompt(), "A new draft did not claim the startup prompt.");
            using (var restarted = new CrashDraftStore())
            {
                Require(!restarted.TryClaimStartupPrompt() && restarted.Recover().Single().Id == managed.Id,
                    "An acknowledged draft prompted again or was no longer readable.");
            }
            var allLocal = string.Join("\n", Directory.GetFiles(Path.Combine(isolated, "LiberaFx", "CrashDrafts"))
                .Select(File.ReadAllText));
            Require(!allLocal.Contains("SYNTHETICSECRET") && !allLocal.Contains("Private") &&
                !allLocal.Contains("project-confidential"), "Exception data was collected.");
            store.Delete(managed);

            using (var interrupted = new CrashDraftStore()) interrupted.Start();
            var unclean = store.Recover().Single();
            Require(unclean.Kind == "UncleanTermination" && unclean.Body.Contains("Cause is unknown"),
                "Unclean termination must not be diagnosed as native crash.");
            Require(store.TryClaimStartupPrompt(), "An earlier acknowledgement suppressed a different draft.");
            store.Delete(unclean);
            using (var clean = new CrashDraftStore()) { clean.Start(); clean.CompleteCleanExit(); }
            Require(store.Recover().Count == 0, "Clean exit generated a draft.");

            var draft = managed with { Title = "Synthetic crash", Body = "Safe report", Delivery = "Unsent" };
            store.Save(draft);
            var provider = new FakeProvider();
            var enabled = true;
            var session = new CrashReportSession(store, p => enabled && ReferenceEquals(p, provider));
            Require(provider.Checks == 0 && provider.Sends == 0, "Construction performed outbound work.");
            await RejectAsync(() => session.SendAsync(true, false, default));
            Require(provider.Sends == 0, "Send without preview was allowed.");
            await session.CheckAsync(provider, draft, new("Safe title", "Safe body"), default);
            await RejectAsync(() => session.SendAsync(false, false, default));
            Require(provider.Sends == 0, "No-consent send was allowed.");
            await session.SendAsync(true, false, default);
            Require(provider.Sends == 1 && store.Recover().Single().Delivery == "Sent", "Send not recorded.");
            await RejectAsync(() => session.SendAsync(true, false, default));
            Require(provider.Sends == 1, "Consent reused.");
            await session.CheckAsync(provider, draft, new("Safe", "Safe"), default);
            await RejectAsync(() => session.SendAsync(true, false, default));
            Require(provider.Sends == 1, "A stale draft snapshot bypassed sent state.");

            store.Delete(draft);
            draft = draft with { Id = Guid.NewGuid() };
            store.Save(draft);
            await session.CheckAsync(provider, draft, new("Safe", "Safe"), default);
            session.Invalidate();
            await RejectAsync(() => session.SendAsync(true, false, default));
            Require(provider.Sends == 1, "Edited review still authorized send.");
            await session.CheckAsync(provider, draft, new("Safe", "Safe"), default);
            enabled = false;
            await RejectAsync(() => session.SendAsync(true, false, default));
            Require(provider.Sends == 1, "Disabled provider used.");
            enabled = true;

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await session.CheckAsync(provider, draft, new("Safe", "Safe"), default);
                var previousSends = provider.Sends;
                try
                {
                    await session.SendAsync(true, false, cancelled.Token);
                    throw new Exception("Cancelled submission was accepted.");
                }
                catch (OperationCanceledException) { }
                Require(provider.Sends == previousSends && store.Read(draft.Id).Delivery == "Unsent",
                    "Pre-send cancellation performed outbound work or changed outcome.");
            }

            store.Delete(draft);
            draft = draft with { Id = Guid.NewGuid() };
            store.Save(draft);
            provider.Fail = true;
            await session.CheckAsync(provider, draft, new("Safe", "Safe"), default);
            await RejectAsync(() => session.SendAsync(true, false, default));
            var uncertain = store.Recover().Single();
            Require(uncertain.Delivery == "Sending", "Failed attempt must retain unknown outcome.");
            Require(CrashReportSession.SafeError(new Exception("secret details")) == "Crash.Failed",
                "Provider exception message exposed.");
            provider.Fail = false;
            await session.CheckAsync(provider, uncertain, new("Safe", "Safe"), default);
            var sendsBeforeRetry = provider.Sends;
            await RejectAsync(() => session.SendAsync(true, false, default));
            Require(provider.Sends == sendsBeforeRetry, "Possible duplicate sent without acknowledgment.");
            await session.SendAsync(true, true, default);

            store.Delete(draft);
            draft = draft with { Id = Guid.NewGuid() };
            store.Save(draft);
            var content = Encoding.UTF8.GetBytes("Explicit safe attachment");
            provider.OnCheck = () => content[0] = (byte)'X';
            await session.CheckAsync(provider, draft, new("Safe", "Safe",
                [new("diagnostics.txt", "text/plain", content)]), default);
            provider.OnCheck = null;
            await session.SendAsync(true, false, default);
            Require(provider.LastReport?.Attachments?.Count == 1 &&
                Encoding.UTF8.GetString(provider.LastReport.Attachments[0].Content.Span) == "Explicit safe attachment",
                "Selected attachment was lost or changed after preview.");
            store.Delete(draft);
            draft = draft with { Id = Guid.NewGuid() };
            store.Save(draft);
            await session.CheckAsync(provider, draft, new("Safe", "No attachment"), default);
            await session.SendAsync(true, false, default);
            Require(provider.LastReport?.Attachments is null, "Excluded attachment was sent.");

            foreach (var sensitive in new[] { @"C:\Users\someone\source.cs", "/home/person/project", "file:///home/person/project", "ghp_SYNTHETICSECRET", "token=synthetic" })
                await RejectAsync(() => session.CheckAsync(provider, draft, new("Safe", sensitive), default));
            await RejectAsync(() => session.CheckAsync(provider, draft, new("Safe", "Safe",
                [new("diagnostics.txt", "text/plain", Encoding.UTF8.GetBytes("password=synthetic"))]), default));

            var redacted = CrashReportSafety.RedactLines("safe\npassword=synthetic\nC:\\private\\file\n-----BEGIN PRIVATE KEY-----\nsynthetic\n-----END PRIVATE KEY-----");
            Require(!redacted.Contains("synthetic") && !redacted.Contains("private") && redacted.StartsWith("safe\n"),
                "Redaction retained synthetic sensitive content.");
            var inline = GitHubCrashReportProvider.FormatBody(new("Safe", "Safe",
                [new("diagnostics.txt", "text/plain", Encoding.UTF8.GetBytes("<script>unsafe & text"))]));
            Require(inline.Contains("&lt;script&gt;unsafe &amp; text") && !inline.Contains("<script>"),
                "GitHub inline text escaped incorrectly.");
            await RejectAsync(() => session.CheckAsync(provider, draft, new("Safe", "Safe",
                [new("C:\\private\\original.txt", "text/plain", Encoding.UTF8.GetBytes("safe"))]), default));
            provider.Support = CrashAttachmentSupport.None;
            var checksBefore = provider.Checks;
            await RejectAsync(() => session.CheckAsync(provider, draft, new("Safe", "Safe",
                [new("diagnostics.txt", "text/plain", Encoding.UTF8.GetBytes("safe"))]), default));
            Require(provider.Checks == checksBefore, "Unsupported attachment reached destination lookup.");
            provider.Support = CrashAttachmentSupport.InlineText;

            var corrupt = Path.Combine(isolated, "LiberaFx", "CrashDrafts", Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(corrupt, "{broken");
            store.Recover();
            Require(File.ReadAllText(corrupt) == "{broken" && store.Status == "Crash.CorruptDraft",
                "Corrupt draft was overwritten or hidden.");
            store.Delete(draft);
            Require(!store.Recover().Any(d => d.Id == draft.Id), "Draft deletion failed.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIBERAFX_DATA_ROOT", original);
            if (Directory.Exists(isolated)) Directory.Delete(isolated, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static async Task RejectAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or NotSupportedException or IOException)
        { return; }
        throw new Exception("Operation should have been rejected.");
    }

    private sealed class FakeProvider : ICrashReportProvider
    {
        public string Id => "synthetic.offline";
        public string Name => "Offline synthetic provider";
        public CrashAttachmentSupport Support = CrashAttachmentSupport.InlineText;
        public CrashAttachmentSupport AttachmentSupport => Support;
        public int Checks, Sends;
        public bool Fail;
        public Action? OnCheck;
        public CrashReport? LastReport;
        public Task<ReportDestination> GetDestinationAsync(CancellationToken cancellation)
        {
            Checks++;
            OnCheck?.Invoke();
            return Task.FromResult(new ReportDestination("Synthetic tracker",
                new Uri("https://example.invalid/issues"), "private"));
        }
        public Task<Uri> SubmitAsync(ReportDestination confirmedDestination, CrashReport report,
            CancellationToken cancellation)
        {
            Sends++;
            LastReport = report;
            if (Fail) throw new IOException("Synthetic credential detail must never be shown");
            return Task.FromResult(new Uri("https://example.invalid/issues/1"));
        }
    }
}
