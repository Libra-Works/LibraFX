using System.Text;
using LiberaFx.Services;

internal static class PreviewAcknowledgementTests
{
    public static void Run()
    {
        var telemetry = new PreviewTelemetryStore();
        var peer = Guid.NewGuid();
        telemetry.Connect(peer, 3, _ => Task.CompletedTask);
        Require(telemetry.Receive(peer, Encoding.UTF8.GetBytes(
            """{"type":"hello","version":1,"engine":"Unreal","role":"editor","capabilities":["assets","rendering"]}""")),
            "The acknowledgement fixture must negotiate an editor peer.");
        var assets = new PreviewAssetStore(telemetry);
        telemetry.ExpectProgramAcknowledgement(peer, 10);
        Require(telemetry.Clients(peer).Single().AwaitingProgramAcknowledgement,
            "A local send must not count as a remote acknowledgement.");
        telemetry.AcknowledgeProgram(peer, 11);
        Require(telemetry.Clients(peer).Single().AcknowledgedProgramSequence is null,
            "An unrelated/future acknowledgement must not confirm the sent program.");
        Require(assets.Receive(peer, Status(10)) &&
            telemetry.Clients(peer).Single() is { AcknowledgedProgramSequence: 10, AwaitingProgramAcknowledgement: false },
            "Only actual matching remote status should confirm delivery.");
        telemetry.ExpectProgramAcknowledgement(peer, 11);
        Require(assets.Receive(peer, Status(9)) &&
            assets.RenderStatus(peer)!.Value.GetProperty("programSequence").GetInt64() == 10 &&
            telemetry.Clients(peer).Single().AwaitingProgramAcknowledgement,
            "Old feedback must not overwrite newer status or acknowledge a newer program.");
        Require(assets.Receive(peer, Status(11)) && !telemetry.Clients(peer).Single().AwaitingProgramAcknowledgement,
            "A new matching status should clear the pending acknowledgement.");
        telemetry.Disconnect(peer);
        telemetry.Connect(peer, 3, _ => Task.CompletedTask);
        Require(telemetry.Clients(peer).Single() is { SentProgramSequence: null, AcknowledgedProgramSequence: null },
            "Reconnect must not inherit acknowledgements from a previous connection.");
        Require(telemetry.Receive(peer, Encoding.UTF8.GetBytes(
            """{"type":"hello","version":1,"engine":"Unreal","role":"viewer","capabilities":["values"]}""")),
            "The receive-only fixture must negotiate a viewer.");
        telemetry.ExpectProgramAcknowledgement(peer, 12);
        Require(!assets.Supports(peer) && !assets.Receive(peer, Status(12)),
            "A receive-only viewer must not acquire editor-only asset/status operations.");
        Require(telemetry.Receive(peer, Encoding.UTF8.GetBytes(
            """{"type":"values","programSequence":12,"evaluationTime":0,"values":[]}""")) &&
            !telemetry.Clients(peer).Single().AwaitingProgramAcknowledgement,
            "Receive-only peers may acknowledge through valid matching telemetry without asset privileges.");
    }

    private static byte[] Status(int sequence) => Encoding.UTF8.GetBytes(
        $$"""{"type":"render_status","programSequence":{{sequence}},"timeSeconds":0,"nodes":[],"errors":[]}""");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
