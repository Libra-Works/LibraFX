using Avalonia;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace LiberaFx;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        var debugPort = Environment.GetEnvironmentVariable("LIBERAFX_MCP_DEBUG_PORT");
        var port = 0;
        var isolatedDebug = Environment.GetEnvironmentVariable("LIBERAFX_MCP_DEBUG") == "1" &&
            Environment.GetEnvironmentVariable("LIBERAFX_DEBUG_ISOLATED_INSTANCE") == "1" &&
            int.TryParse(debugPort, out port) && port is >= 1024 and <= 65535 &&
            Path.IsPathFullyQualified(Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT") ?? "");
        using var mutex = new Mutex(false, isolatedDebug
            ? $"LiberaFx.Editor.Debug.{port}" : "LiberaFx.Editor.Singleton");
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                if (OperatingSystem.IsWindows())
                    MessageBoxW(IntPtr.Zero, "LIBERA FX is already running.", "LIBERA FX", 0x40);
                else
                    Console.Error.WriteLine("LIBERA FX is already running.");
                return;
            }
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
