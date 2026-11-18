using System.Buffers.Binary;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using Avalonia.Threading;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;

internal static class ExecutionDebugPaneTests
{
    public static void Run()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var model = new MainViewModel();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var node = model.Graph.CreateNode(GraphNodeKind.Float);
        var source = model.Graph.CreateNode(GraphNodeKind.Time);
        model.Graph.RenameNode(source, "Fixture signal origin");
        model.Graph.SetParameter(node, "value", JsonSerializer.SerializeToElement(4.5));
        model.Graph.SelectNode(node);
        var pane = model.ExecutionDebugger;
        var stepReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pollAfterStep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? runtime = null;
        try
        {
            pane.BreakpointCommand.Execute(null);
            var start = pane.StartCommand.ExecuteAsync(null);
            WaitUntil(() => start.IsCompleted, timeout.Token);
            start.GetAwaiter().GetResult();
            var invitation = pane.Invitation;
            Require(invitation.Length != 0, "Debugger invitation was not opened.");
            runtime = Task.Run(() => RunRuntime(invitation, node.Id, source.Id, stepReceived, releaseStep, pollAfterStep,
                timeout.Token));

            WaitUntil(() => pane.StepCommand.CanExecute(null) || runtime.IsCompleted, timeout.Token);
            if (runtime.IsCompleted)
            {
                runtime.GetAwaiter().GetResult();
            }
            Require(pane.CurrentSignalSource.Contains(source.Name, StringComparison.Ordinal), "Signal predecessor is not visible.");
            Require(pane.AuthoredParameters.Single().Contains("4.5", StringComparison.Ordinal) &&
                pane.Inputs.Single().Contains(7.25.ToString("G17", CultureInfo.CurrentCulture), StringComparison.Ordinal),
                "Saved node settings must remain distinguishable from runtime operands.");

            pane.StepCommand.Execute(null);
            WaitUntil(() => stepReceived.Task.IsCompleted || runtime.IsCompleted, timeout.Token);
            Require(stepReceived.Task.IsCompletedSuccessfully, "The runtime did not receive Step.");
            Require(!pane.StepCommand.CanExecute(null) && !pane.ResumeCommand.CanExecute(null),
                "Execution controls must stay disabled until Step is acknowledged.");
            pane.StepCommand.Execute(null);
            pane.ResumeCommand.Execute(null);
            releaseStep.SetResult();

            WaitUntil(() => runtime.IsCompleted ||
                (pollAfterStep.Task.IsCompleted && pane.StepCommand.CanExecute(null)), timeout.Token);
            if (runtime.IsCompleted)
            {
                runtime.GetAwaiter().GetResult();
            }
            Require(pollAfterStep.Task.IsCompletedSuccessfully && pane.StepCommand.CanExecute(null),
                "The next pause must become controllable without replaying duplicate control commands.");
        }
        finally
        {
            model.DisposeStorageFiles();
            timeout.Cancel();
            try { runtime?.GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    }

    private static async Task RunRuntime(string invitation, Guid node, Guid source, TaskCompletionSource stepReceived,
        TaskCompletionSource releaseStep, TaskCompletionSource pollAfterStep, CancellationToken cancellation)
    {
        using var pipe = new NamedPipeClientStream(".", "LiberaFx.ExecutionDebug." + invitation, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancellation);
        var command = new byte[32];
        async Task<ExecutionDebugCommand> ReadCommand()
        {
            await pipe.ReadExactlyAsync(command, cancellation);
            return (ExecutionDebugCommand)command[4];
        }

        Require(await ReadCommand() == ExecutionDebugCommand.Poll, "Initial poll was not sent.");
        await pipe.WriteAsync(Frame(0), cancellation);
        Require(await ReadCommand() == ExecutionDebugCommand.AddBreakpoint &&
            new Guid(command.AsSpan(16), bigEndian: true) == node, "Breakpoint must be installed before attach.");
        await pipe.WriteAsync(Frame(0), cancellation);
        Require(await ReadCommand() == ExecutionDebugCommand.Attach, "Runtime attach was not sent.");
        await pipe.WriteAsync(Frame(1, node, source), cancellation);

        var stepped = false;
        while (!cancellation.IsCancellationRequested)
        {
            var next = await ReadCommand();
            if (next == ExecutionDebugCommand.Step && !stepped)
            {
                stepReceived.SetResult();
                await releaseStep.Task.WaitAsync(cancellation);
                stepped = true;
            }
            else
            {
                Require(next == ExecutionDebugCommand.Poll, "A stale Step or Resume reached the next pause.");
                if (stepped)
                {
                    pollAfterStep.TrySetResult();
                }
            }
            await pipe.WriteAsync(Frame(stepped ? 2UL : 1UL, node, source), cancellation);
        }
    }

    private static byte[] Frame(ulong sequence, Guid node = default, Guid source = default)
    {
        var frame = new byte[984];
        "LFD2"u8.CopyTo(frame);
        if (sequence == 0)
        {
            return frame;
        }

        frame[4] = (byte)ExecutionDebugState.Paused;
        frame[6] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(8), sequence);
        node.TryWriteBytes(frame.AsSpan(16), bigEndian: true, out _);
        source.TryWriteBytes(frame.AsSpan(40), bigEndian: true, out _);
        frame[57] = 1;
        BinaryPrimitives.WriteDoubleLittleEndian(frame.AsSpan(76), 7.25);
        return frame;
    }

    private static void WaitUntil(Func<bool> ready, CancellationToken cancellation)
    {
        if (ready())
        {
            return;
        }
        var frame = new DispatcherFrame();
        using var registration = cancellation.Register(() => Dispatcher.UIThread.Post(() => frame.Continue = false));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => frame.Continue = !ready();
        timer.Start();
        try { Dispatcher.UIThread.PushFrame(frame); }
        finally { timer.Stop(); }
        cancellation.ThrowIfCancellationRequested();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
