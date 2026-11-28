using LiberaFx.Extensions.Abstractions;

namespace LiberaFx.Services.Crash;

/// <summary>One UI review. Consent is never persisted. No retries or alternate providers.</summary>
public sealed class CrashReportSession
{
    private readonly CrashDraftStore _store;
    private readonly Func<ICrashReportProvider, bool> _isEnabled;
    private ICrashReportProvider? _provider;
    private CrashReport? _report;
    private CrashDraft? _draft;
    private ReportDestination? _destination;
    private bool _busy;

    public CrashReportSession(CrashDraftStore store, Func<ICrashReportProvider, bool> isEnabled)
    { _store = store; _isEnabled = isEnabled; }

    public void Invalidate()
    {
        _provider = null;
        _report = null;
        _draft = null;
        _destination = null;
    }

    public async Task<ReportDestination> CheckAsync(ICrashReportProvider provider, CrashDraft draft,
        CrashReport report, CancellationToken cancellation)
    {
        if (_busy) throw new InvalidOperationException("Crash.Busy");
        Invalidate();
        _busy = true;
        try
        {
            if (!_isEnabled(provider)) throw new InvalidOperationException("Crash.ProviderDisabled");
            // Copy before validation and before any provider await. Callers may retain
            // and mutate the source buffers while destination lookup is in flight.
            if (report.Attachments is { Count: > 1 } ||
                report.Attachments?.Any(a => a.Content.Length > 16000) == true)
                throw new InvalidDataException("Crash.InvalidReport");
            var snapshot = new CrashReport(report.Title, report.Body, report.Attachments?
                .Select(a => new CrashAttachment(a.Name, a.MediaType, a.Content.ToArray())).ToArray());
            CrashReportSafety.Validate(snapshot);
            if (snapshot.Attachments is { Count: > 0 } &&
                provider.AttachmentSupport != CrashAttachmentSupport.InlineText)
                throw new NotSupportedException("Crash.AttachmentsUnsupported");
            var destination = await provider.GetDestinationAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (!_isEnabled(provider)) throw new InvalidOperationException("Crash.ProviderDisabled");
            ValidateDestination(destination);
            _report = snapshot;
            _provider = provider;
            _draft = draft;
            return _destination = destination;
        }
        finally { _busy = false; }
    }

    public async Task<Uri> SendAsync(bool explicitConsent, bool acknowledgePossibleDuplicate,
        CancellationToken cancellation)
    {
        if (_busy) throw new InvalidOperationException("Crash.Busy");
        if (!explicitConsent || _provider is null || _report is null || _draft is null || _destination is null)
            throw new InvalidOperationException("Crash.ConsentRequired");
        var provider = _provider;
        var report = _report;
        var draft = _draft;
        var destination = _destination;
        var delivery = _store.Read(draft.Id).Delivery;
        if (delivery == "Sent") throw new InvalidOperationException("Crash.AlreadySent");
        if (delivery == "Sending" && !acknowledgePossibleDuplicate)
            throw new InvalidOperationException("Crash.DuplicateWarning");
        if (!_isEnabled(provider)) throw new InvalidOperationException("Crash.ProviderDisabled");
        if (report.Attachments is { Count: > 0 } &&
            provider.AttachmentSupport != CrashAttachmentSupport.InlineText)
            throw new NotSupportedException("Crash.AttachmentsUnsupported");
        _busy = true;
        Invalidate(); // One consent authorizes one attempt, including failures and cancellation.
        try
        {
            cancellation.ThrowIfCancellationRequested();
            // Write-ahead intent: any interruption after this point has an unknown remote outcome.
            _store.Save(draft with { Delivery = "Sending" });
            var result = await provider.SubmitAsync(destination, report, cancellation);
            if (!result.IsAbsoluteUri || result.Scheme != "https" || !string.IsNullOrEmpty(result.UserInfo))
                throw new InvalidDataException("Crash.ResultUnknown");
            _store.Save(draft with { Delivery = "Sent" });
            return result;
        }
        finally { _busy = false; }
    }

    private static void ValidateDestination(ReportDestination destination)
    {
        if (destination is null || destination.Address is null || !destination.Address.IsAbsoluteUri ||
            destination.Address.Scheme != "https" || !string.IsNullOrEmpty(destination.Address.UserInfo) ||
            string.IsNullOrWhiteSpace(destination.Label) || string.IsNullOrWhiteSpace(destination.Visibility))
            throw new InvalidDataException("Crash.InvalidDestination");
        CrashReportSafety.Validate(new CrashReport(destination.Label,
            destination.Address.AbsoluteUri + "\n" + destination.Visibility));
    }

    public static string SafeError(Exception exception)
    {
        if (exception is OperationCanceledException) return "Crash.Cancelled";
        // Only our fixed keys may be shown. Provider exception text is untrusted/confidential.
        string[] allowed = ["Crash.InvalidReport", "Crash.SensitiveText", "Crash.AttachmentsUnsupported",
            "Crash.DestinationChanged", "Crash.CredentialMissing", "Crash.AuthenticationFailed",
            "Crash.ConnectionFailed", "Crash.SecretStoreUnsupported", "Crash.InvalidRepository",
            "Crash.RepositoryUnavailable", "Crash.ResultUnknown", "Crash.ProviderDisabled",
            "Crash.ConsentRequired", "Crash.DuplicateWarning", "Crash.AlreadySent", "Crash.InvalidDestination"];
        return allowed.Contains(exception.Message, StringComparer.Ordinal) ? exception.Message : "Crash.Failed";
    }
}
