using System;
using System.Runtime.InteropServices;

namespace LiberaFx.Unity
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PreparedHostOptions
    {
        public uint capacity, preparationWorkers, eventCapacity, instructionBudget;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct SharedEffectFrame
    {
        public ulong host;
        public double time, delta;
        public NativeResult result;
        public uint reserved;
    }
    internal static unsafe class PreparedNative
    {
        private const string Library = "liberafx_native";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_spawnable_host_pool_create(byte* bytes, ulong count, ref PreparedHostOptions options, out ulong pool);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_spawnable_host_pool_acquire(ulong pool, out ulong host);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_spawnable_host_pool_release(ulong pool);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_spawnable_host_worker_safe(ulong host, out uint safe);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_effect_world_create(uint maximumEffects, uint workers, out ulong world);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_effect_world_advance(ulong world, SharedEffectFrame* frames, uint count);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_effect_world_release(ulong world);
    }
    // Explicit loading-time preparation. Dispose hosts on their creating thread
    // before disposing the pool; Busy does not silently discard its handle.
    public sealed unsafe class NativePreparedHostPool : IDisposable
    {
        private ulong handle;
        private int active;
        private bool retiring;
        private Action returned;
        private readonly int owner = System.Threading.Thread.CurrentThread.ManagedThreadId;
        private NativePreparedHostPool(ulong value) { handle = value; returned = OnReturn; }
        private void OnReturn() { --active; if (retiring && active == 0) Release(); }
        internal void CloseWhenIdle() { retiring = true; if (active == 0) Release(); }
        public static NativeResult Create(byte[] bytes, PreparedHostOptions options, out NativePreparedHostPool pool)
        {
            pool = null;
            if (bytes == null || bytes.Length == 0) return NativeResult.InvalidArgument;
            NativeResult result;
            ulong value;
            fixed (byte* source = bytes)
                result = PreparedNative.libra_spawnable_host_pool_create(source, (ulong)bytes.Length, ref options, out value);
            if (result == NativeResult.Ok) pool = new NativePreparedHostPool(value);
            return result;
        }
        public NativePreparedHostPool(byte[] bytes, PreparedHostOptions options)
        {
            if (bytes == null || bytes.Length == 0) throw new ArgumentException("Empty effect package.");
            fixed (byte* source = bytes)
                NativeInstance.Check(PreparedNative.libra_spawnable_host_pool_create(source, (ulong)bytes.Length, ref options, out handle));
            returned = OnReturn;
        }
        public NativeResult Acquire(out NativeSpawnableHost host)
        {
            host = null;
            if (owner != System.Threading.Thread.CurrentThread.ManagedThreadId) return NativeResult.Busy;
            if (handle == 0) return NativeResult.InvalidHandle;
            if (retiring) return NativeResult.NotReady;
            var result = PreparedNative.libra_spawnable_host_pool_acquire(handle, out var value);
            if (result == NativeResult.Ok) { ++active; host = new NativeSpawnableHost(value, returned); }
            return result;
        }
        public NativeResult Release()
        {
            if (owner != System.Threading.Thread.CurrentThread.ManagedThreadId) return NativeResult.Busy;
            if (handle == 0) return NativeResult.Ok;
            var result = PreparedNative.libra_spawnable_host_pool_release(handle);
            if (result == NativeResult.Ok) handle = 0;
            return result;
        }
        public void Dispose() { NativeInstance.Check(Release()); }
    }
}
