using System.Buffers.Binary;
using System.IO.Pipes;
using System.Threading.Channels;

namespace LiberaFx.Services;

internal enum ExecutionDebugState : byte { Detached, Running, Paused, Aborted, Complete }
internal enum ExecutionDebugCommand : byte { Poll, Attach, AddBreakpoint, RemoveBreakpoint, Pause, Step, Resume }
internal sealed record ExecutionDebugInput(int Slot, int Kind, bool Connected, Guid Source, double[] Values);
internal sealed record ExecutionDebugFrame(ExecutionDebugState State, byte Result, ulong Sequence, Guid Node,
    double Time, Guid SignalSource, ExecutionDebugInput[] Inputs, ulong Generation, ulong Instance, Guid Container);

internal sealed class ExecutionDebugConnection : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<(ExecutionDebugCommand, Guid)> _commands = Channel.CreateBounded<(ExecutionDebugCommand, Guid)>(32);
    private readonly ulong _program;
    public string Invitation { get; } = Guid.NewGuid().ToString("N");

    public ExecutionDebugConnection(byte[] program)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        _program = ProgramIdentity(program);
        _pipe = new NamedPipeServerStream("LiberaFx.ExecutionDebug." + Invitation, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    internal static ulong ProgramIdentity(ReadOnlySpan<byte> bytes)
    {
        var hash = 14695981039346656037UL;
        foreach (var value in bytes) hash = unchecked((hash ^ value) * 1099511628211UL);
        return hash;
    }

    public bool Send(ExecutionDebugCommand command, Guid id = default) => _commands.Writer.TryWrite((command, id));

    public async Task RunAsync(IReadOnlyCollection<Guid> breakpoints, Action<ExecutionDebugFrame> report)
    {
        var cancellation = _stop.Token;
        try
        {
            using (var inviteTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                inviteTimeout.CancelAfter(TimeSpan.FromMinutes(2));
                await _pipe.WaitForConnectionAsync(inviteTimeout.Token).ConfigureAwait(false);
            }
            var command = new byte[32];
            var frame = new byte[984];
            "LFD2"u8.CopyTo(command);
            BinaryPrimitives.WriteUInt64LittleEndian(command.AsSpan(8), _program);

            async Task<ExecutionDebugFrame> Exchange(ExecutionDebugCommand operation, Guid id = default)
            {
                command[4] = (byte)operation;
                id.TryWriteBytes(command.AsSpan(16), bigEndian: true, out _);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                await _pipe.WriteAsync(command, deadline.Token).ConfigureAwait(false);
                await _pipe.ReadExactlyAsync(frame, deadline.Token).ConfigureAwait(false);
                return Decode(frame);
            }

            var hello = await Exchange(ExecutionDebugCommand.Poll).ConfigureAwait(false);
            if (hello.Result != 0) { report(hello); return; }
            foreach (var id in breakpoints)
            {
                var added = await Exchange(ExecutionDebugCommand.AddBreakpoint, id).ConfigureAwait(false);
                if (added.Result != 0) { report(added); return; }
            }
            var attached = await Exchange(ExecutionDebugCommand.Attach).ConfigureAwait(false);
            report(attached);
            if (attached.Result != 0) return;
            while (!cancellation.IsCancellationRequested)
            {
                var next = _commands.Reader.TryRead(out var item) ? item : (ExecutionDebugCommand.Poll, Guid.Empty);
                report(await Exchange(next.Item1, next.Item2).ConfigureAwait(false));
                if (next.Item1 == ExecutionDebugCommand.Poll)
                    await Task.Delay(50, cancellation).ConfigureAwait(false);
            }
        }
        finally { _pipe.Dispose(); }
    }

    internal static ExecutionDebugFrame Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != 984 || !frame[..4].SequenceEqual("LFD2"u8) ||
            frame[4] > 4 || frame[6] > 16 || frame[7] != 0)
            throw new InvalidDataException("Invalid execution debugger frame.");
        var state = (ExecutionDebugState)frame[4];
        var time = BinaryPrimitives.ReadDoubleLittleEndian(frame[32..]);
        if (!double.IsFinite(time)) throw new InvalidDataException("Invalid debugger time.");
        var inputs = new ExecutionDebugInput[frame[6]];
        for (var index = 0; index < inputs.Length; index++)
        {
            var input = frame.Slice(56 + index * 56, 56);
            if (input[1] > 4 || input[2] > 3 || input[3] > 1 ||
                (state != ExecutionDebugState.Paused && input[1] != 0) ||
                (input[1] == 0) != (input[2] == 3))
                throw new InvalidDataException("Invalid debugger operand.");
            var values = new double[input[1]];
            for (var lane = 0; lane < values.Length; lane++)
            {
                values[lane] = BinaryPrimitives.ReadDoubleLittleEndian(input[(20 + lane * 8)..]);
                if (!double.IsFinite(values[lane])) throw new InvalidDataException("Invalid debugger operand.");
            }
            inputs[index] = new(input[0], input[2], input[3] == 1, new Guid(input.Slice(4, 16), true), values);
        }
        var generation = BinaryPrimitives.ReadUInt64LittleEndian(frame[952..]);
        var instance = BinaryPrimitives.ReadUInt64LittleEndian(frame[960..]);
        var container = new Guid(frame.Slice(968, 16), true);
        if ((instance != 0) != (container != Guid.Empty) || (instance != 0 && generation == 0))
            throw new InvalidDataException("Invalid debugger scope.");
        return new(state, frame[5], BinaryPrimitives.ReadUInt64LittleEndian(frame[8..]),
            new Guid(frame.Slice(16, 16), true), time, new Guid(frame.Slice(40, 16), true), inputs, generation, instance, container);
    }

    public void Dispose()
    {
        _commands.Writer.TryComplete();
        _stop.Cancel();
        _pipe.Dispose();
        _stop.Dispose();
    }
}
