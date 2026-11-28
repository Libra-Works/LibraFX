using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LiberaFx.Services.Preview;

internal static class ViewerDiscoveryTests
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("Viewer discovery checks require Windows.");
            return;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var cancellation = timeout.Token;
        var name = ViewerDiscovery.Prefix + Environment.ProcessId;
        var id = Guid.NewGuid();
        var received = new List<JsonElement>();
        var invitation = PreviewConnectionInvitation.Create("wss://127.0.0.1:19001/preview",
            new string('a', 64), new string('b', 64));
        var service = CreateTestClient();
        var server = ServeAsync(name, 3, (request, index) =>
        {
            received.Add(request);
            return index < 2 ? Metadata(id) : JsonSerializer.SerializeToElement(new { accepted = true });
        }, cancellation);
        var viewer = await service.QueryAsync(name, cancellation);
        await service.ConnectAsync(viewer, invitation, cancellation);
        await server;
        if (received.Count != 3 || received[2].GetProperty("invitation").GetString() != invitation ||
            received[2].GetProperty("instanceId").GetGuid() != id)
            throw new Exception("Discovery did not deliver the invitation to the selected viewer incarnation.");

        server = ServeAsync(name, 2, (_, index) => Metadata(index == 0 ? id : Guid.NewGuid()), cancellation);
        viewer = await service.QueryAsync(name, cancellation);
        try
        {
            await service.ConnectAsync(viewer, invitation, cancellation);
            throw new Exception("A restarted viewer accepted a stale selection.");
        }
        catch (InvalidDataException) { }
        await server;

        await using (var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            var accepted = pipe.WaitForConnectionAsync(cancellation);
            var rejected = new ViewerDiscovery().QueryAsync(name, cancellation);
            await accepted;
            try
            {
                await rejected;
                throw new Exception("The production client trusted a non-viewer test process.");
            }
            catch (UnauthorizedAccessException) { }
            if (await pipe.ReadAsync(new byte[1], cancellation) != 0)
                throw new Exception("Discovery sent data before authenticating the viewer process.");
        }
        try
        {
            await service.QueryAsync(ViewerDiscovery.Prefix + "0001", cancellation);
            throw new Exception("A noncanonical process identifier was accepted.");
        }
        catch (InvalidDataException) { }
    }

    private static JsonElement Metadata(Guid instance) => JsonSerializer.SerializeToElement(new
    {
        type = "librafx_viewer", version = 1, processId = Environment.ProcessId,
        instanceId = instance, engine = "Unreal", name = "Synthetic viewer", connected = false
    });

    private static ViewerDiscovery CreateTestClient()
    {
        var constructor = typeof(ViewerDiscovery).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(Func<NamedPipeClientStream, int, bool>)], null)!;
        return (ViewerDiscovery)constructor.Invoke(new object[]
        {
            (Func<NamedPipeClientStream, int, bool>)((_, _) => true)
        });
    }

    private static async Task ServeAsync(string name, int count,
        Func<JsonElement, int, JsonElement> respond, CancellationToken cancellation)
    {
        for (var index = 0; index < count; index++)
        {
            await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(cancellation);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 1024, true);
            var line = await reader.ReadLineAsync(cancellation) ?? throw new IOException("Missing viewer request.");
            using var json = JsonDocument.Parse(line);
            var reply = Encoding.UTF8.GetBytes(respond(json.RootElement.Clone(), index).GetRawText() + "\n");
            await pipe.WriteAsync(reply, cancellation);
            var acknowledgement = new byte[1];
            await pipe.ReadExactlyAsync(acknowledgement, cancellation);
            if (acknowledgement[0] != 6) throw new IOException("Missing viewer response acknowledgement.");
        }
    }
}
