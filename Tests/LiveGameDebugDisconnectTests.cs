using LiberaFx.Transport;
using LiberaFx.Services;
using System.Text.Json;

// Pure managed contract: no UI initialization, windows, engine, native library,
// filesystem, network or background service. Source only; execution by the user.
internal static class LiveGameDebugDisconnectTests
{
    public static void Run()
    {
        const string session = "0123456789abcdef0123456789abcdef";
        const string otherSession = "1123456789abcdef0123456789abcdef";
        const string first = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string second = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var lease = new LiveGameDebugLease(session);
        Require(lease.AllowsMutation(session, ""), "Initial ownerless legacy operations were broken.");
        Require(!lease.Attach(otherSession, first) && !lease.Attach(session, "invalid"), "Bad attach was accepted.");
        Require(lease.Attach(session, first) && lease.Matches(session, first), "First owner was not attached.");
        Require(lease.AllowsMutation(session, first) && !lease.AllowsMutation(session, ""), "Attached owner was not enforced.");
        Require(lease.Attach(session, second), "Explicit takeover failed.");
        // Replace preserves the target generation. Ownership must independently
        // prevent an old window from clearing/replacing the new transfer.
        Require(!lease.Detach(session, first) && lease.Matches(session, second), "Late disconnect cleared a newer owner.");
        Require(!lease.AllowsMutation(session, first), "Late replacement was allowed.");
        Require(!lease.Detach(otherSession, second) && !lease.Matches(otherSession, second), "Old endpoint session matched.");
        Require(lease.Detach(session, second), "Current owner could not disconnect.");
        Require(!lease.Detach(session, second) && !lease.AllowsMutation(session, second) &&
            !lease.AllowsMutation(session, ""), "Detached window could recreate debug output.");
        Require(lease.Attach(session, first) && lease.AllowsMutation(session, first), "Explicit reconnection failed.");
        Require(!LiveGameDebugLease.Valid(new string('f', 33)) &&
            !LiveGameDebugLease.Valid(new string('F', 32)), "Malformed/noncanonical owner accepted.");
        var oldEndpoint = JsonSerializer.Deserialize<LiveGameEndpoint>(
            """{"protocol":1,"session":"0123456789abcdef0123456789abcdef","engine":"Unity","transport":"isolated-local-spool","supportedUpdate":"restart-root-render-v1-v2","process":1}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Require(oldEndpoint.DebugDisconnect is null, "Legacy descriptor acquired a false cleanup capability.");
        Require(oldEndpoint != (oldEndpoint with { DebugDisconnect = "debug-lease-v1" }),
            "Capability change must require an explicit reconnect.");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
