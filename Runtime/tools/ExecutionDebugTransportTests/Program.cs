using System.Buffers.Binary;
using System.IO.Pipes;
using System.Threading.Channels;
using LiberaFx.Services;

if (!OperatingSystem.IsWindows())
{
    Console.WriteLine("Skipped: Windows named pipe transport");
    return 77;
}
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
var token = deadline.Token;
var node = new Guid("8086dc70-ce9f-4ae7-8a87-57f3f10921e7");
var source = new Guid("39f155e6-3878-4406-ab26-de2983510548");
var reports = Channel.CreateUnbounded<ExecutionDebugFrame>();
using var server = new ExecutionDebugConnection([1, 2, 3]);
using var client = new NamedPipeClientStream(".", "LiberaFx.ExecutionDebug." + server.Invitation,
    PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
var serverTask = server.RunAsync([node], frame => reports.Writer.TryWrite(frame));
await client.ConnectAsync(token);

async Task Exchange(ExecutionDebugCommand expected, ExecutionDebugState state, Guid expectedId = default, bool values = false)
{
    var command = new byte[32];
    await client.ReadExactlyAsync(command, token);
    Check(command.AsSpan(0, 4).SequenceEqual("LFD2"u8));
    Check(command[4] == (byte)expected);
    Check(new Guid(command.AsSpan(16), bigEndian: true) == expectedId);
    Check(BinaryPrimitives.ReadUInt64LittleEndian(command.AsSpan(8)) == 0xd0aa6218672cf5abUL);
    var frame = new byte[984];
    "LFD2"u8.CopyTo(frame);
    frame[4] = (byte)state;
    if (values)
    {
        frame[6] = 1;
        frame[8] = 2;
        node.TryWriteBytes(frame.AsSpan(16), true, out _);
        source.TryWriteBytes(frame.AsSpan(40), true, out _);
        frame[57] = 1;
        frame[58] = 2;
        frame[59] = 1;
        source.TryWriteBytes(frame.AsSpan(60), true, out _);
        BinaryPrimitives.WriteDoubleLittleEndian(frame.AsSpan(76), 2147483647);
    }
    // A pipe frame is allowed to arrive as multiple reads.
    await client.WriteAsync(frame.AsMemory(0, 11), token);
    await client.WriteAsync(frame.AsMemory(11), token);
}

await Exchange(ExecutionDebugCommand.Poll, ExecutionDebugState.Detached);
await Exchange(ExecutionDebugCommand.AddBreakpoint, ExecutionDebugState.Detached, node);
await Exchange(ExecutionDebugCommand.Attach, ExecutionDebugState.Running);
Check((await reports.Reader.ReadAsync(token)).State == ExecutionDebugState.Running);
await Exchange(ExecutionDebugCommand.Poll, ExecutionDebugState.Paused, values: true);
var pause = await reports.Reader.ReadAsync(token);
Check(pause.State == ExecutionDebugState.Paused && pause.Sequence == 2 && pause.Node == node &&
      pause.SignalSource == source && pause.Inputs is [{ Kind: 2, Connected: true, Values: [2147483647] }] &&
      pause.Inputs[0].Source == source);
Check(server.Send(ExecutionDebugCommand.Step));
await Exchange(ExecutionDebugCommand.Step, ExecutionDebugState.Paused, values: true);
Check((await reports.Reader.ReadAsync(token)).State == ExecutionDebugState.Paused);
// Let the server attempt the next poll, then simulate losing its client.
client.Dispose();
try { await serverTask.WaitAsync(token); }
catch (IOException) { }
Console.WriteLine("Execution debugger named pipe: handshake, breakpoint, fragmented frames, paused Int32 and disconnect passed.");
return 0;

static void Check(bool condition)
{
    if (!condition) throw new InvalidOperationException("Execution debugger transport contract failed.");
}
