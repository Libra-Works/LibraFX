using System.Diagnostics;
using System.Runtime.InteropServices;
using LiberaFx.Services;

if (args is ["managed"])
{
    if (OperatingSystem.IsWindows()) SetErrorMode(0x0002);
    using var lifecycle = new CrashReportLifecycle();
    var fault = new Thread(() => throw new Exception("synthetic-secret-never-collect"));
    fault.Start();
    fault.Join();
    return;
}
if (args is ["unclean"])
{
    using var lifecycle = new CrashReportLifecycle();
    Console.WriteLine("ready");
    Console.Out.Flush();
    await Task.Delay(Timeout.Infinite);
    return;
}

var root = Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT");
if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
    throw new InvalidOperationException("An isolated absolute data root is required.");
var testRoot = Path.Combine(root, "recovery-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("LIBERAFX_DATA_ROOT", testRoot);
try
{
    var child = StartChild("managed");
    using (child)
    {
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            if (child.ExitCode == 0) throw new Exception("Managed exception unexpectedly succeeded.");
        }
        finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
    }
    using var store = new CrashDraftStore();
    var managed = store.Recover().Single();
    if (managed.Kind != "ManagedException" ||
        File.ReadAllText(Directory.GetFiles(Path.Combine(testRoot, "LiberaFx", "CrashDrafts"), "*.json").Single())
            .Contains("synthetic-secret", StringComparison.Ordinal))
        throw new Exception("Managed recovery or privacy failed.");
    store.Delete(managed);

    using (var interrupted = StartChild("unclean"))
    {
        try
        {
            if (await interrupted.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) != "ready")
                throw new Exception("Child did not start.");
            interrupted.Kill();
            await interrupted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally { if (!interrupted.HasExited) { interrupted.Kill(); await interrupted.WaitForExitAsync(); } }
    }
    CrashDraft? unclean = null;
    for (var attempt = 0; attempt < 30 && unclean is null; attempt++)
    {
        unclean = store.Recover().SingleOrDefault();
        if (unclean is null) await Task.Delay(100);
    }
    if (unclean is null) throw new Exception("Unclean marker was not recovered: " + store.Status);
    if (unclean.Kind != "UncleanTermination") throw new Exception("Unclean recovery failed.");
    store.Delete(unclean);
    Console.WriteLine("Isolated process recovery checks passed.");
}
finally
{
    Environment.SetEnvironmentVariable("LIBERAFX_DATA_ROOT", root);
    if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
}

static Process StartChild(string mode)
{
    var start = new ProcessStartInfo("dotnet")
    {
        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        CreateNoWindow = true
    };
    start.ArgumentList.Add(typeof(CrashRecoveryProgramMarker).Assembly.Location);
    start.ArgumentList.Add(mode);
    start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
    return Process.Start(start) ?? throw new InvalidOperationException("Child start failed.");
}

[DllImport("kernel32.dll")]
static extern uint SetErrorMode(uint mode);

sealed class CrashRecoveryProgramMarker;
