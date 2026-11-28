using Avalonia;
using Avalonia.Threading;
using LiberaFx;
using LiberaFx.Services.Crash;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var root = Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new InvalidOperationException("An isolated absolute data root is required.");
        AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
        try
        {
            var task = ChatSourceTests.RunAsync();
            var frame = new DispatcherFrame();
            var timedOut = false;
            using var timeout = DispatcherTimer.RunOnce(() =>
            {
                timedOut = true;
                frame.Continue = false;
            }, TimeSpan.FromSeconds(90));
            _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false),
                TaskScheduler.Default);
            if (!task.IsCompleted) Dispatcher.UIThread.PushFrame(frame);
            if (timedOut) throw new TimeoutException("Provider UI checks timed out.");
            task.GetAwaiter().GetResult();
            Console.WriteLine("Synthetic provider UI checks passed.");
        }
        finally
        {
            var field = typeof(App).GetField("_crashReports", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field?.GetValue(Application.Current) is CrashReportLifecycle reports)
            {
                reports.CompleteCleanExit();
                reports.Dispose();
                field.SetValue(Application.Current, null);
            }
        }
    }
}
