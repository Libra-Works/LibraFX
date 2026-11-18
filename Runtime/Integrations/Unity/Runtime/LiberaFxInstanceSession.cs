using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace LiberaFx.Unity
{
    [StructLayout(LayoutKind.Explicit, Pack = 8, Size = 24)]
    public struct SessionId
    {
        [FieldOffset(0)] public ulong High;
        [FieldOffset(8)] public ulong Low;
        [FieldOffset(16)] public ulong Incarnation;
    }

    [StructLayout(LayoutKind.Explicit, Pack = 8, Size = 32)]
    public struct SessionRequestId
    {
        [FieldOffset(0)] public SessionId Session;
        [FieldOffset(24)] public ulong Sequence;
    }

    [StructLayout(LayoutKind.Explicit, Pack = 8, Size = 40)]
    public struct SessionInstanceId
    {
        [FieldOffset(0)] public SessionId Session;
        [FieldOffset(24)] public ulong Generation;
        [FieldOffset(32)] public ulong Slot;
    }

    public enum SessionRequestKind : uint { Create, NaturalEnd, Destroy, Replace }
    public enum SessionReplacementPolicy : uint { PreserveState, Restart }

    [StructLayout(LayoutKind.Explicit, Pack = 8, Size = 80)]
    public struct SessionCompletion
    {
        [FieldOffset(0)] public SessionRequestId Request;
        [FieldOffset(32)] public SessionInstanceId Instance;
        [FieldOffset(72)] public SessionRequestKind Kind;
        [FieldOffset(76)] public NativeResult Result;
    }

    [StructLayout(LayoutKind.Explicit, Pack = 8, Size = 48)]
    internal struct SessionOptions
    {
        [FieldOffset(0)] public uint StructSize;
        [FieldOffset(4)] public uint InstanceCapacity;
        [FieldOffset(8)] public uint RequestCapacity;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public ulong NonceHigh;
        [FieldOffset(24)] public ulong NonceLow;
        [FieldOffset(32)] public ulong MaxProgramBytes;
        [FieldOffset(40)] public ulong MaxOwnedBytes;
    }

    /// <summary>
    /// Actual native request API. Worker-safe Submit*/TakeCompletion; creator-only
    /// Process/Advance/Read/Close/Release. This class uses no Unity APIs and can
    /// also be used by a standalone native ABI contract runner.
    /// No finalizer: active effects are never implicitly force-stopped.
    /// </summary>
    public class NativeInstanceSession : IDisposable
    {
        private readonly SessionId id;
        private readonly int ownerThread;
        private int disposed;
        public SessionId Id => id;

        public NativeInstanceSession(bool developmentOptIn, uint instanceCapacity = 32,
            uint requestCapacity = 64, ulong maxOwnedBytes = 64UL * 1024 * 1024,
            ulong maxProgramBytes = 16UL * 1024 * 1024)
        {
            if (!developmentOptIn)
                throw new InvalidOperationException("Instance sessions require explicit development opt-in.");
            ValidateLayout();
            if (Native.libra_session_abi_version() != 1)
                throw new NotSupportedException("Unsupported native instance-session ABI.");
            var nonce = Guid.NewGuid().ToByteArray();
            var options = new SessionOptions {
                StructSize = 48, InstanceCapacity = instanceCapacity, RequestCapacity = requestCapacity,
                Flags = 1, NonceHigh = ReadNonce(nonce, 0), NonceLow = ReadNonce(nonce, 8),
                MaxProgramBytes = maxProgramBytes, MaxOwnedBytes = maxOwnedBytes
            };
            var result = Native.libra_session_create(in options, out var created);
            if (result != NativeResult.Ok)
                throw new InvalidOperationException("Could not create native instance session: " + result);
            id = created;
            ownerThread = Thread.CurrentThread.ManagedThreadId;
        }

        public NativeResult SubmitCreate(byte[] bytecode, out SessionRequestId request,
            double duration = 0, bool infinite = true) =>
            SubmitProgram(SessionRequestKind.Create, default(SessionInstanceId), bytecode,
                duration, infinite, SessionReplacementPolicy.PreserveState, out request);

        // Restart is intentionally explicit; caller resets elapsed to zero ONLY
        // after successful completion. Preservation is an unsupported result.
        public NativeResult SubmitReplace(SessionInstanceId instance, byte[] bytecode,
            SessionReplacementPolicy policy, out SessionRequestId request,
            double duration = 0, bool infinite = true) =>
            SubmitProgram(SessionRequestKind.Replace, instance, bytecode, duration, infinite, policy, out request);

        private NativeResult SubmitProgram(SessionRequestKind kind, SessionInstanceId target,
            byte[] bytes, double duration, bool infinite, SessionReplacementPolicy policy,
            out SessionRequestId request)
        {
            request = default(SessionRequestId);
            if (Volatile.Read(ref disposed) != 0) return NativeResult.InvalidHandle;
            if (bytes == null) return NativeResult.InvalidArgument;
            // Native copies before returning; never pin/retain caller memory for
            // queued work. Caller must not mutate bytes concurrently with call.
            return Native.libra_session_submit_program(id, kind, target, bytes, (ulong)bytes.LongLength,
                duration, infinite ? 1u : 0u, policy, out request);
        }

        public NativeResult SubmitNaturalEnd(SessionInstanceId instance, out SessionRequestId request) =>
            SubmitControl(SessionRequestKind.NaturalEnd, instance, out request);
        public NativeResult SubmitDestroy(SessionInstanceId instance, out SessionRequestId request) =>
            SubmitControl(SessionRequestKind.Destroy, instance, out request);

        private NativeResult SubmitControl(SessionRequestKind kind, SessionInstanceId target,
            out SessionRequestId request)
        {
            request = default(SessionRequestId);
            return Volatile.Read(ref disposed) != 0 ? NativeResult.InvalidHandle :
                Native.libra_session_submit_control(id, kind, target, out request);
        }

        public NativeResult TakeCompletion(SessionRequestId request, out SessionCompletion completion)
        {
            completion = default(SessionCompletion);
            return Volatile.Read(ref disposed) != 0 ? NativeResult.InvalidHandle :
                Native.libra_session_take_completion(id, request, out completion);
        }

        public NativeResult Process(uint limit, out uint processed)
        {
            RequireOwner();
            processed = 0;
            return Volatile.Read(ref disposed) != 0 ? NativeResult.InvalidHandle :
                Native.libra_session_process(id, limit, out processed);
        }

        public NativeResult Advance(SessionInstanceId instance, double elapsed, double frameDelta, out bool finished)
        {
            RequireOwner();
            finished = false;
            if (Volatile.Read(ref disposed) != 0) return NativeResult.InvalidHandle;
            var result = Native.libra_session_advance(id, instance, elapsed, frameDelta, out var value);
            if (result == NativeResult.Ok) finished = value != 0;
            return result;
        }

        public NativeResult ReadValue(SessionInstanceId instance, byte[] nodeId, out NativeValue value)
        {
            RequireOwner();
            value = default(NativeValue);
            if (Volatile.Read(ref disposed) != 0) return NativeResult.InvalidHandle;
            if (nodeId == null || nodeId.Length != 16) return NativeResult.InvalidArgument;
            return Native.libra_session_read_value(id, instance, nodeId, out value);
        }

        public NativeResult Close()
        {
            RequireOwner();
            return Volatile.Read(ref disposed) != 0 ? NativeResult.InvalidHandle : Native.libra_session_close(id);
        }

        public NativeResult TryRelease()
        {
            RequireOwner();
            if (Volatile.Read(ref disposed) != 0) return NativeResult.InvalidHandle;
            var result = Native.libra_session_release(id);
            if (result == NativeResult.Ok) Interlocked.Exchange(ref disposed, 1);
            return result;
        }

        public void Dispose()
        {
            RequireOwner();
            if (Volatile.Read(ref disposed) != 0) return;
            var result = TryRelease();
            if (result != NativeResult.Ok)
                throw new InvalidOperationException("Explicitly drain/destroy effects and consume completions before release: " + result);
        }

        private void RequireOwner()
        {
            if (Thread.CurrentThread.ManagedThreadId != ownerThread)
                throw new InvalidOperationException("Instance-session owner operation requires its creator thread.");
        }

        private static ulong ReadNonce(byte[] bytes, int offset)
        {
            ulong value = 0;
            for (var index = 0; index < 8; ++index) value = (value << 8) | bytes[offset + index];
            return value;
        }

        public static void ValidateLayout()
        {
            if (Marshal.SizeOf<SessionId>() != 24 || Marshal.SizeOf<SessionRequestId>() != 32 ||
                Marshal.SizeOf<SessionInstanceId>() != 40 || Marshal.SizeOf<SessionCompletion>() != 80 ||
                Marshal.SizeOf<SessionOptions>() != 48 ||
                Marshal.OffsetOf<SessionRequestId>(nameof(SessionRequestId.Sequence)).ToInt32() != 24 ||
                Marshal.OffsetOf<SessionInstanceId>(nameof(SessionInstanceId.Generation)).ToInt32() != 24 ||
                Marshal.OffsetOf<SessionInstanceId>(nameof(SessionInstanceId.Slot)).ToInt32() != 32 ||
                Marshal.OffsetOf<SessionCompletion>(nameof(SessionCompletion.Kind)).ToInt32() != 72 ||
                Marshal.OffsetOf<SessionCompletion>(nameof(SessionCompletion.Result)).ToInt32() != 76)
                throw new NotSupportedException("Native instance-session ABI layout mismatch.");
        }

        private static class Native
        {
#if UNITY_IOS && !UNITY_EDITOR
            private const string Library = "__Internal";
#else
            private const string Library = "liberafx_native";
#endif
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint libra_session_abi_version();
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern NativeResult libra_session_create(in SessionOptions options, out SessionId session);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern NativeResult libra_session_submit_program(SessionId session, SessionRequestKind kind,
                SessionInstanceId target, [In] byte[] bytes, ulong byteCount, double duration,
                uint infinite, SessionReplacementPolicy policy, out SessionRequestId request);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern NativeResult libra_session_submit_control(SessionId session, SessionRequestKind kind,
                SessionInstanceId target, out SessionRequestId request);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern NativeResult libra_session_take_completion(SessionId session,
                SessionRequestId request, out SessionCompletion completion);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern NativeResult libra_session_process(SessionId session, uint limit, out uint processed);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern NativeResult libra_session_advance(SessionId session, SessionInstanceId instance,
                double elapsed, double frameDelta, out uint finished);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern NativeResult libra_session_read_value(SessionId session, SessionInstanceId instance,
                [In] byte[] nodeId, out NativeValue value);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern NativeResult libra_session_close(SessionId session);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern NativeResult libra_session_release(SessionId session);
        }
    }

#if UNITY_5_3_OR_NEWER
    /// <summary>
    /// Unity entry point: construct on main thread, pump Process/Advance there;
    /// pass this object to workers only for submission/completion polling.
    /// Does not create GameObjects, materials or render resources.
    /// </summary>
    public sealed class UnityInstanceSession : NativeInstanceSession
    {
        private static int mainThread;
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void MarkRuntimeThread() => mainThread = Thread.CurrentThread.ManagedThreadId;
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void MarkEditorThread() => mainThread = Thread.CurrentThread.ManagedThreadId;
#endif
        public UnityInstanceSession(bool developmentOptIn, uint instanceCapacity = 32, uint requestCapacity = 64,
            ulong maxOwnedBytes = 64UL * 1024 * 1024, ulong maxProgramBytes = 16UL * 1024 * 1024)
            : base(developmentOptIn, CheckMainThread(instanceCapacity), requestCapacity, maxOwnedBytes, maxProgramBytes) {}
        public static void RequireMainThread()
        {
            if (mainThread == 0 || Thread.CurrentThread.ManagedThreadId != mainThread)
                throw new InvalidOperationException("Create UnityInstanceSession on the initialized Unity main thread.");
        }
        private static uint CheckMainThread(uint capacity)
        {
            RequireMainThread();
            return capacity;
        }
    }
#endif
}
