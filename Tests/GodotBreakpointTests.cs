using System.Diagnostics;
using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;

internal static class GodotBreakpointTests
{
    public static async Task Run(string executable, string project)
    {
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(executable) ||
            !Path.IsPathFullyQualified(project) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT")))
            throw new ArgumentException("Use an isolated Godot project and LIBERAFX_DATA_ROOT on Windows.");

        var definition = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.Time);
        var time = new GraphNodeSnapshot(Guid.NewGuid(), definition.Kind, definition.Name, 0, 0,
            definition.PortType, definition.PortCategory);
        var drawDefinition = GraphNodeDefinition.All.Single(item => item.Kind.Value == "ModelDraw");
        var draw = new GraphNodeSnapshot(Guid.NewGuid(), drawDefinition.Kind, drawDefinition.Name, 0, 0,
            drawDefinition.PortType, drawDefinition.PortCategory, Parameters:
            [new("assetPath", "string", JsonSerializer.SerializeToElement("/Engine/BasicShapes/Cube.Cube"))]);
        var graph = new GraphSnapshot([time, draw], []) { AutoDestroyWhenIdle = false };
        var package = SpawnableGroupPackageBinary.Serialize(SpawnableGroupPackage.Compile(graph));
        var file = Path.Combine(project, "breakpoint.lfxp");
        var resumedFile = Path.Combine(project, "breakpoint-resumed.flag");
        var visualFile = Path.Combine(project, "breakpoint-visual.flag");
        if (File.Exists(file) || File.Exists(resumedFile) || File.Exists(visualFile))
            throw new IOException("Breakpoint scratch files already exist.");

        var connection = new ExecutionDebugConnection(package);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var paused = new TaskCompletionSource<ExecutionDebugFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = connection.RunAsync([time.Id], (_, frame) =>
        {
            if (frame.Result != 0)
                paused.TrySetException(new InvalidOperationException($"Godot debugger rejected command: {frame.Result}."));
            if (frame.State == ExecutionDebugState.Paused)
                paused.TrySetResult(frame);
        });
        Process? godot = null;
        try
        {
            File.WriteAllBytes(file, package);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            start.Environment["LIBERAFX_GODOT_TEST_DEBUG_INVITATION"] = connection.Invitation;
            foreach (var argument in new[] { "--headless", "--path", project, "--script", "res://breakpoint_smoke.gd" })
                start.ArgumentList.Add(argument);
            godot = Process.Start(start) ?? throw new InvalidOperationException("Could not start Godot.");
            var exited = godot.WaitForExitAsync(timeout.Token);
            var first = await Task.WhenAny(paused.Task, exited, server);
            if (first != paused.Task)
            {
                await first;
                throw new InvalidOperationException("Godot exited or disconnected before hitting the breakpoint.");
            }
            var stop = await paused.Task;
            if (stop.Node != time.Id || stop.Generation == 0 || stop.Instance != 0)
                throw new InvalidOperationException("Godot did not pause at the authored root Time node.");
            while (!File.Exists(visualFile) && !godot.HasExited)
                await Task.Delay(20, timeout.Token);
            if (!File.Exists(visualFile) || !connection.Send(ExecutionDebugCommand.Resume))
                throw new InvalidOperationException("Godot did not keep its scene visible during the breakpoint.");
            File.WriteAllText(resumedFile, "");
            await exited;
            if (godot.ExitCode != 0)
                throw new InvalidOperationException($"Godot breakpoint smoke exited with {godot.ExitCode}.");
            Console.WriteLine("PASS: Editor debugger stopped and resumed a real Godot package worker");
        }
        finally
        {
            if (godot is { HasExited: false })
                godot.Kill();
            godot?.Dispose();
            connection.Dispose();
            try { await server; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            File.Delete(resumedFile);
            File.Delete(visualFile);
            File.Delete(file);
        }
    }
}
