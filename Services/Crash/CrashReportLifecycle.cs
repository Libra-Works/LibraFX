namespace LiberaFx.Services.Crash;

/// <summary>Owned by application lifetime, not a document or extension. No network operations.</summary>
public sealed class CrashReportLifecycle : IDisposable
{
    private volatile bool _fatal;
    public CrashDraftStore Drafts { get; } = new();

    public CrashReportLifecycle()
    {
        Drafts.Start();
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
    }

    private void OnUnhandled(object sender, UnhandledExceptionEventArgs args)
    {
        if (!args.IsTerminating) return;
        CaptureFatalManagedException(args.ExceptionObject as Exception);
    }

    // For the UI dispatcher fatal-exception event. Do NOT mark the exception handled here.
    public void CaptureFatalManagedException(Exception? exception)
    {
        _fatal = true;
        Drafts.CaptureManagedException(exception);
    }

    public void CompleteCleanExit()
    {
        if (!_fatal) Drafts.CompleteCleanExit();
    }

    public void Dispose()
    {
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
        Drafts.Dispose();
    }
}
