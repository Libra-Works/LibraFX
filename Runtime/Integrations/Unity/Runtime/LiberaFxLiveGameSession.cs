#if UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LiberaFx.Unity
{
    /// <summary>
    /// Real component instances, not NativeInstanceSession's numerical facade.
    /// Construct/pump/list/close on main thread; Submit/TakeCompletion on workers.
    /// Keep this object alive until producers have joined and completions drained.
    /// </summary>
    public sealed class LiberaFxLiveGameSession
    {
        public sealed class Completion
        {
            public SessionRequestId Request;
            public SessionInstanceId Instance;
            public NativeResult Result;
            public string Error;
        }
        public sealed class LiveInstance
        {
            public SessionInstanceId Id;
            public string Label;
            public bool Finished;
            public int RenderScopes;
        }
        private sealed class Playback
        {
            public GameObject Owner;
            public LiberaFxSpawnableAsset Asset;
            public LiberaFxSpawnableComponent Component;
            public bool Restartable;
            public string Label;
            public int ByteCount;
            public void Release()
            {
                if (Component) Component.Stop();
                if (Owner) { Owner.SetActive(false); Destroy(Owner); }
                if (Asset) Destroy(Asset);
            }
        }
        private sealed class Slot { public ulong Generation = 1; public Playback Playback; }
        private sealed class Request
        {
            public SessionRequestKind Kind;
            public SessionRequestId Id;
            public SessionInstanceId Target;
            public byte[] Bytes;
            public string Label;
            public SessionReplacementPolicy Policy;
            public Completion Completion;
        }
        private readonly object gate = new object();
        private readonly Queue<Request> pending = new Queue<Request>();
        private readonly Dictionary<ulong, Request> requests = new Dictionary<ulong, Request>();
        private readonly Slot[] slots;
        private readonly Transform parent;
        private readonly LiberaFxUnityBindingAsset bindings;
        private readonly int ownerThread = Thread.CurrentThread.ManagedThreadId;
        private readonly int requestCapacity;
        private readonly long maxOwnedBytes;
        private long queuedBytes, activeBytes;
        private ulong sequence;
        private bool closing, closed, processing;
        public SessionId Id { get; }
        public const int MaximumPackageBytes = 16 * 1024 * 1024;

        public LiberaFxLiveGameSession(bool developmentOptIn, Transform parent,
            LiberaFxUnityBindingAsset bindings, int instanceCapacity = 32, int requestCapacity = 64,
            long maxOwnedBytes = 64 * 1024 * 1024)
        {
            UnityInstanceSession.RequireMainThread();
            if (!developmentOptIn || !(Application.isEditor || Debug.isDebugBuild))
                throw new InvalidOperationException("Live game sessions require development build and explicit opt-in.");
            // Accessing parent here is intentional: construction is an engine-thread operation.
            if (!parent || !parent.gameObject.activeInHierarchy)
                throw new ArgumentException("Supply an active, isolated game owner.");
            if (instanceCapacity < 1 || instanceCapacity > 256 || requestCapacity < 1 ||
                requestCapacity > 1024 || maxOwnedBytes < MaximumPackageBytes)
                throw new ArgumentOutOfRangeException(nameof(instanceCapacity));
            this.parent = parent;
            this.bindings = bindings;
            this.requestCapacity = requestCapacity;
            this.maxOwnedBytes = maxOwnedBytes;
            var nonce = Guid.NewGuid().ToByteArray();
            Id = new SessionId { High = BitConverter.ToUInt64(nonce, 0), Low = BitConverter.ToUInt64(nonce, 8), Incarnation = 1 };
            slots = new Slot[instanceCapacity];
            for (var index = 0; index < slots.Length; ++index) slots[index] = new Slot();
        }

        public NativeResult SubmitCreate(byte[] package, string label, out SessionRequestId request) =>
            Submit(SessionRequestKind.Create, default, package, label, SessionReplacementPolicy.Restart, out request);
        public NativeResult SubmitReplace(SessionInstanceId target, byte[] package, SessionReplacementPolicy policy,
            out SessionRequestId request) => Submit(SessionRequestKind.Replace, target, package, null, policy, out request);
        public NativeResult SubmitDestroy(SessionInstanceId target, out SessionRequestId request) =>
            Submit(SessionRequestKind.Destroy, target, null, null, default, out request);
        public NativeResult SubmitNaturalEnd(SessionInstanceId target, out SessionRequestId request) =>
            Submit(SessionRequestKind.NaturalEnd, target, null, null, default, out request);

        private NativeResult Submit(SessionRequestKind kind, SessionInstanceId target, byte[] bytes,
            string label, SessionReplacementPolicy policy, out SessionRequestId request)
        {
            request = default;
            lock (gate)
            {
                if (closed || (closing && kind != SessionRequestKind.Destroy)) return NativeResult.NotReady;
                if (kind != SessionRequestKind.Create && !Matches(target.Session)) return NativeResult.InvalidHandle;
                if (bytes != null && (bytes.Length == 0 || bytes.Length > MaximumPackageBytes)) return NativeResult.InvalidArgument;
                if ((kind == SessionRequestKind.Create || kind == SessionRequestKind.Replace) && bytes == null)
                    return NativeResult.InvalidArgument;
                if (requests.Count >= requestCapacity || (bytes != null && activeBytes + queuedBytes + bytes.Length > maxOwnedBytes))
                    return NativeResult.QueueRejected;
                if (sequence == ulong.MaxValue) return NativeResult.CounterOverflow;
                var item = new Request { Kind = kind, Target = target, Bytes = bytes == null ? null : (byte[])bytes.Clone(),
                    Label = string.IsNullOrEmpty(label) ? "Live effect" : label.Substring(0, Math.Min(label.Length, 128)), Policy = policy };
                item.Id = new SessionRequestId { Session = Id, Sequence = ++sequence };
                requests.Add(sequence, item);
                pending.Enqueue(item);
                queuedBytes += item.Bytes?.Length ?? 0;
                request = item.Id;
                return NativeResult.Ok;
            }
        }

        public NativeResult TakeCompletion(SessionRequestId request, out Completion completion)
        {
            completion = null;
            lock (gate)
            {
                if (!Matches(request.Session) || !requests.TryGetValue(request.Sequence, out var item))
                    return NativeResult.InvalidHandle;
                if (item.Completion == null) return NativeResult.NotReady;
                completion = item.Completion;
                requests.Remove(request.Sequence);
                return NativeResult.Ok;
            }
        }
        public NativeResult Process(int limit = 16)
        {
            RequireOwner();
            if (processing) return NativeResult.Busy;
            if (limit < 0) return NativeResult.InvalidArgument;
            processing = true;
            try
            {
                for (var index = 0; index < limit; ++index)
                {
                    Request item;
                    lock (gate) { if (pending.Count == 0) break; item = pending.Dequeue(); }
                    var completion = new Completion { Request = item.Id, Instance = item.Target };
                    try { completion.Result = Execute(item, completion); }
                    catch (Exception error) { completion.Result = NativeResult.InternalError; completion.Error = error.Message; }
                    lock (gate)
                    {
                        queuedBytes -= item.Bytes?.Length ?? 0;
                        item.Bytes = null;
                        item.Completion = completion;
                    }
                }
                return NativeResult.Ok;
            }
            finally { processing = false; }
        }
        private NativeResult Execute(Request item, Completion completion)
        {
            var slot = item.Kind == SessionRequestKind.Create ? null : Find(item.Target);
            if (item.Kind != SessionRequestKind.Create && slot == null) return NativeResult.InvalidHandle;
            if (item.Kind == SessionRequestKind.Destroy)
            {
                slot.Playback.Release();
                lock (gate) activeBytes -= slot.Playback.ByteCount;
                slot.Playback = null;
                // Zero permanently exhausts a slot rather than reusing a wrapped generation.
                slot.Generation = slot.Generation == ulong.MaxValue ? 0 : slot.Generation + 1;
                return NativeResult.Ok;
            }
            if (item.Kind == SessionRequestKind.NaturalEnd) return slot.Playback.Component.RequestNaturalEnd();
            if (item.Kind == SessionRequestKind.Replace &&
                (item.Policy != SessionReplacementPolicy.Restart || !slot.Playback.Restartable ||
                 ValidateRestart(item.Bytes) != NativeResult.Ok))
            {
                completion.Error = "Only explicit Restart of root-render numeric/manual-lifetime packages is supported; state, children, signals, I/O and GPU migration are rejected.";
                return NativeResult.UnsupportedBinary;
            }
            if (item.Kind == SessionRequestKind.Create)
            {
                foreach (var available in slots)
                    if (available.Playback == null && available.Generation != 0) { slot = available; break; }
                if (slot == null) return NativeResult.QueueRejected;
            }
            var candidate = new Playback { Label = item.Kind == SessionRequestKind.Create ? item.Label : slot.Playback.Label,
                ByteCount = item.Bytes.Length };
            try
            {
                candidate.Owner = new GameObject("LIBERA FX Live: " + candidate.Label);
                candidate.Owner.SetActive(false);
                candidate.Owner.transform.SetParent(parent, false);
                candidate.Asset = ScriptableObject.CreateInstance<LiberaFxSpawnableAsset>();
                candidate.Asset.SetPackageBytes(item.Bytes); // exclusively owned request copy
                candidate.Component = candidate.Owner.AddComponent<LiberaFxSpawnableComponent>();
                var result = candidate.Component.ConfigureForSession(candidate.Asset, bindings);
                if (result == NativeResult.Ok) result = candidate.Component.Play();
                if (result != NativeResult.Ok)
                {
                    completion.Error = candidate.Component.LastError;
                    return result;
                }
                candidate.Restartable = ValidateRestart(item.Bytes) == NativeResult.Ok;
                var old = slot.Playback;
                lock (gate) activeBytes += candidate.ByteCount - (old?.ByteCount ?? 0);
                slot.Playback = candidate;
                candidate = null;
                old?.Release();
                slot.Playback.Owner.SetActive(true);
                completion.Instance = IdFor(Array.IndexOf(slots, slot));
                return NativeResult.Ok;
            }
            finally { candidate?.Release(); }
        }

        public LiveInstance[] List()
        {
            RequireOwner();
            var result = new List<LiveInstance>();
            for (var index = 0; index < slots.Length; ++index)
            {
                var playback = slots[index].Playback;
                if (playback == null) continue;
                result.Add(new LiveInstance { Id = IdFor(index), Label = playback.Label,
                    Finished = !playback.Component || playback.Component.IsRootFinished,
                    RenderScopes = playback.Component ? playback.Component.ActiveRenderScopeCount : 0 });
            }
            return result.ToArray();
        }
        // Owner-only diagnostic/test hook, never serialize this reference.
        public LiberaFxSpawnableComponent ResolveComponent(SessionInstanceId id)
        { RequireOwner(); return Find(id)?.Playback.Component; }

        /// <summary>Stops accepting creates/replaces. Never converts natural lifetime to force termination.</summary>
        public NativeResult Close()
        {
            RequireOwner();
            if (processing) return NativeResult.Busy;
            lock (gate)
            {
                closing = true;
                foreach (var slot in slots) if (slot.Playback != null) return NativeResult.NotReady;
                if (requests.Count != 0) return NativeResult.NotReady;
                closed = true;
                return NativeResult.Ok;
            }
        }
        // Engine owner teardown is EXPLICIT forced destruction. Pending creates
        // become failed completions; workers can still consume them after teardown.
        public NativeResult ShutdownAndForceDestroy()
        {
            RequireOwner();
            if (processing) return NativeResult.Busy;
            lock (gate)
            {
                closing = closed = true;
                while (pending.Count != 0)
                {
                    var item = pending.Dequeue();
                    item.Bytes = null;
                    item.Completion = new Completion { Request = item.Id, Instance = item.Target,
                        Result = NativeResult.NotReady, Error = "Engine owner shut down before application." };
                }
                queuedBytes = 0;
            }
            foreach (var slot in slots) { slot.Playback?.Release(); slot.Playback = null; }
            lock (gate) activeBytes = 0;
            return NativeResult.Ok;
        }
        private Slot Find(SessionInstanceId id) => Matches(id.Session) && id.Slot < (ulong)slots.Length &&
            slots[(int)id.Slot].Playback != null && slots[(int)id.Slot].Generation == id.Generation ? slots[(int)id.Slot] : null;
        private SessionInstanceId IdFor(int slot) => new SessionInstanceId { Session = Id, Slot = (ulong)slot, Generation = slots[slot].Generation };
        private bool Matches(SessionId id) => id.High == Id.High && id.Low == Id.Low && id.Incarnation == Id.Incarnation;
        private void RequireOwner()
        {
            if (Thread.CurrentThread.ManagedThreadId != ownerThread)
                throw new InvalidOperationException("Live game session SDK operation requires its main-thread owner.");
        }
        private static void Destroy(Object value)
        {
            if (Application.isPlaying) Object.Destroy(value); else Object.DestroyImmediate(value);
        }
#if UNITY_IOS && !UNITY_EDITOR
        private const string Library = "__Internal";
#else
        private const string Library = "liberafx_native";
#endif
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libra_session_validate_live_restart")]
        private static extern NativeResult ValidateRestart([In] byte[] bytes, ulong count);
        private static NativeResult ValidateRestart(byte[] bytes) => ValidateRestart(bytes, (ulong)bytes.LongLength);
    }
}
#endif
