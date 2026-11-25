using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace LiberaFx.Unity
{
    // Managed identities shared by the package-backed Unity live-game adapter.
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

#if UNITY_5_3_OR_NEWER
    // Main-thread guard retained for Unity component APIs; no native session ABI exists.
    public static class UnityInstanceSession
    {
        private static int mainThread;
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void MarkRuntimeThread() => mainThread = Thread.CurrentThread.ManagedThreadId;
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void MarkEditorThread() => mainThread = Thread.CurrentThread.ManagedThreadId;
#endif
        public static void RequireMainThread()
        {
            if (mainThread == 0 || Thread.CurrentThread.ManagedThreadId != mainThread)
                throw new InvalidOperationException("LiberaFX Unity runtime operations require the initialized Unity main thread.");
        }
    }
#endif
}
