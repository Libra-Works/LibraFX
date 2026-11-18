using Avalonia;
using Avalonia.Threading;
using LiberaFx;
using LiberaFx.Services;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var root = Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || args.Length != 1)
            throw new InvalidOperationException("Supply isolated absolute LIBERAFX_DATA_ROOT and one isolated test artifacts directory.");
        AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
        try
        {
            var task = LiveGameEditorUiTests.RunAsync(args[0]);
            var frame = new DispatcherFrame();
            var timedOut = false;
            using var timeout = DispatcherTimer.RunOnce(() => { timedOut = true; frame.Continue = false; }, TimeSpan.FromSeconds(45));
            _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
            if (!task.IsCompleted) Dispatcher.UIThread.PushFrame(frame);
            if (timedOut) throw new TimeoutException("Live game editor UI checks timed out.");
            task.GetAwaiter().GetResult();
        }
        finally
        {
            var field = typeof(App).GetField("_crashReports", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field?.GetValue(Application.Current) is CrashReportLifecycle reports)
            { reports.CompleteCleanExit(); reports.Dispose(); field.SetValue(Application.Current, null); }
        }
    }
}
