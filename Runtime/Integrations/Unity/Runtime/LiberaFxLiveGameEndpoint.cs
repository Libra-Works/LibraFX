#if UNITY_5_3_OR_NEWER
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace LiberaFx.Unity
{
    /// <summary>
    /// Explicit local-only isolated spool, NOT the editor preview/TLS endpoint.
    /// No socket, discovery broadcast, token/credential, upload, or arbitrary path
    /// in a command. The caller provisions a private same-user session directory.
    /// </summary>
    public sealed class LiberaFxLiveGameEndpoint : MonoBehaviour
    {
        [SerializeField] private bool developmentOptIn;
        [SerializeField] private LiberaFxUnityBindingAsset engineBindings;
        [SerializeField] private string isolatedSessionRoot;
        public LiberaFxLiveGameSession Session { get; private set; }
        public string EndpointDirectory { get; private set; }
        public string LastError { get; private set; }
        [Serializable] public sealed class Command
        {
            public int protocol = 1;
            public string session, sequence, operation, target, packageBase64, label, policy, debugOwner;
        }
        [Serializable] public sealed class Instance
        {
            public string target, label;
            public bool finished;
            public int renderCount;
        }
        [Serializable] public sealed class Response
        {
            public int protocol = 1;
            public string session, sequence, result, error, target;
            public Instance[] instances;
        }
        [Serializable] private sealed class Descriptor
        {
            public int protocol = 1;
            public string session, engine = "Unity", transport = "isolated-local-spool";
            public string supportedUpdate = "restart-root-render-v1-v2";
            public string debugDisconnect = "debug-lease-v1";
            public int process;
        }
        private ulong lastSequence;
        private Command waiting;
        private SessionRequestId waitingRequest;
        private bool hasWaiting;
        private string identity;
        private LiberaFx.Transport.LiveGameDebugLease debugLease;

        // Explicit setup for NEW inactive isolated test owner only.
        public void Configure(bool optIn, string privateSessionRoot, LiberaFxUnityBindingAsset bindings)
        {
            UnityInstanceSession.RequireMainThread();
            if (Session != null || gameObject.activeInHierarchy) throw new InvalidOperationException("Configure an inactive endpoint.");
            developmentOptIn = optIn;
            isolatedSessionRoot = privateSessionRoot;
            engineBindings = bindings;
        }
        private void OnEnable()
        {
            if (!developmentOptIn) return;
            try
            {
                if (string.IsNullOrEmpty(isolatedSessionRoot) || !Path.IsPathRooted(isolatedSessionRoot) ||
                    !Directory.Exists(isolatedSessionRoot) ||
                    (File.GetAttributes(isolatedSessionRoot) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Supply an existing private absolute isolated session root (not a project or link).");
                Session = new LiberaFxLiveGameSession(true, transform, engineBindings);
                lastSequence = 0;
                hasWaiting = false;
                waiting = null;
                identity = Session.Id.High.ToString("x16") + Session.Id.Low.ToString("x16");
                debugLease = new LiberaFx.Transport.LiveGameDebugLease(identity);
                Session.DisconnectPreviewDebugDraw(); // legacy transfers cannot enable debug
                EndpointDirectory = Path.Combine(isolatedSessionRoot, "unity-" + identity);
                Directory.CreateDirectory(EndpointDirectory);
                Publish("endpoint.json", JsonUtility.ToJson(new Descriptor {
                    session = identity, process = Process.GetCurrentProcess().Id }));
            }
            catch (Exception error) { LastError = error.Message; Session?.ShutdownAndForceDestroy(); Session = null; }
        }
        private void Update() => Pump();
        public void Pump()
        {
            if (Session == null) return;
            Command handling = null;
            try
            {
                Session.Process();
                if (hasWaiting)
                {
                    var taken = Session.TakeCompletion(waitingRequest, out var completion);
                    if (taken == NativeResult.NotReady) return;
                    Reply(waiting, taken == NativeResult.Ok ? completion.Result : taken,
                        taken == NativeResult.Ok ? completion.Error : "Completion is no longer available.",
                        taken == NativeResult.Ok && completion.Result == NativeResult.Ok ? Target(completion.Instance) : null);
                    hasWaiting = false;
                    waiting = null;
                }
                var path = Path.Combine(EndpointDirectory, "request.json");
                if (!File.Exists(path)) return;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                    new FileInfo(path).Length > 24 * 1024 * 1024)
                    throw new InvalidOperationException("Linked or oversized request rejected.");
                var text = File.ReadAllText(path);
                File.Delete(path);
                var command = JsonUtility.FromJson<Command>(text);
                handling = command;
                if (command == null || command.protocol != 1 || command.session != identity ||
                    !ulong.TryParse(command.sequence, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) ||
                    sequence <= lastSequence)
                { Reply(command, NativeResult.InvalidHandle, "Wrong session/protocol or replayed sequence."); return; }
                lastSequence = sequence;
                if (command.operation == "list")
                { Reply(command, NativeResult.Ok, null); return; }
                if (command.operation == "debug-attach")
                {
                    if (!LiberaFx.Transport.LiveGameDebugLease.Valid(command.debugOwner))
                    { Reply(command, NativeResult.InvalidArgument, "Invalid debug transfer owner."); return; }
                    var clear = Session.DisconnectPreviewDebugDraw();
                    var attached = clear == NativeResult.Ok ? Session.AttachPreviewDebugDraw() : clear;
                    if (attached == NativeResult.Ok) debugLease.Attach(command.session, command.debugOwner);
                    Reply(command, attached, attached == NativeResult.Ok ? null : "Debug transfer attach failed.");
                    return;
                }
                if (command.operation == "debug-disconnect")
                {
                    if (!debugLease.Matches(command.session, command.debugOwner))
                    { Reply(command, NativeResult.InvalidHandle, "Stale debug transfer owner; no playback was modified."); return; }
                    var cleared = Session.DisconnectPreviewDebugDraw();
                    if (cleared == NativeResult.Ok) debugLease.Detach(command.session, command.debugOwner);
                    Reply(command, cleared, cleared == NativeResult.Ok ? null : "Debug cleanup failed.");
                    return;
                }
                if (!debugLease.AllowsMutation(command.session, command.debugOwner))
                { Reply(command, NativeResult.InvalidHandle, "Stale debug transfer owner; reconnect explicitly."); return; }
                var target = default(SessionInstanceId);
                if (command.operation != "create" && !TryTarget(command.target, out target))
                { Reply(command, NativeResult.InvalidHandle, "Invalid selected target."); return; }
                NativeResult result;
                var request = default(SessionRequestId);
                switch (command.operation)
                {
                    case "create":
                        result = Session.SubmitCreate(Decode(command.packageBase64), command.label, out request); break;
                    case "replace":
                        if (command.policy != "restart")
                        { Reply(command, NativeResult.UnsupportedBinary, "Explicit restart policy is required; state migration is unsupported."); return; }
                        result = Session.SubmitReplace(target, Decode(command.packageBase64), SessionReplacementPolicy.Restart, out request); break;
                    case "destroy": result = Session.SubmitDestroy(target, out request); break;
                    case "end": result = Session.SubmitNaturalEnd(target, out request); break;
                    default: Reply(command, NativeResult.InvalidArgument, "Unknown operation."); return;
                }
                if (result != NativeResult.Ok) { Reply(command, result, "Request was not accepted."); return; }
                waiting = command;
                waitingRequest = request;
                hasWaiting = true;
            }
            catch (Exception error)
            {
                LastError = error.Message;
                // A malformed local command must not stop previous playback.
                try { Reply(handling, NativeResult.InvalidArgument, LastError); }
                catch (Exception publishError) { LastError = error.Message + "; response failed: " + publishError.Message; }
            }
        }
        private static byte[] Decode(string value)
        {
            if (value == null || value.Length > 22369624) throw new ArgumentException("Supply at most 16 MiB compiled LFXP bytes.");
            return Convert.FromBase64String(value);
        }
        private void Reply(Command command, NativeResult result, string error, string target = null)
        {
            var live = Session.List();
            var instances = new Instance[live.Length];
            for (var index = 0; index < live.Length; ++index)
                instances[index] = new Instance { target = Target(live[index].Id), label = live[index].Label,
                    finished = live[index].Finished, renderCount = live[index].RenderScopes };
            LastError = error;
            Publish("response.json", JsonUtility.ToJson(new Response { session = identity,
                sequence = command?.sequence, result = result.ToString(), error = error, target = target, instances = instances }));
        }
        private static string Target(SessionInstanceId id) => id.Generation.ToString("x16") + ":" + id.Slot.ToString("x16");
        private bool TryTarget(string text, out SessionInstanceId id)
        {
            id = default;
            var parts = text?.Split(':');
            if (parts == null || parts.Length != 2 ||
                !ulong.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var generation) ||
                !ulong.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var slot)) return false;
            id = new SessionInstanceId { Session = Session.Id, Generation = generation, Slot = slot };
            return true;
        }
        private void Publish(string name, string text)
        {
            var path = Path.Combine(EndpointDirectory, name);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, text);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
        }
        private void OnDisable()
        {
            Session?.ShutdownAndForceDestroy(); // explicit endpoint owner teardown
            Session = null;
            if (!string.IsNullOrEmpty(EndpointDirectory))
            {
                var descriptor = Path.Combine(EndpointDirectory, "endpoint.json");
                if (File.Exists(descriptor)) File.Delete(descriptor);
            }
        }
    }
}
#endif
