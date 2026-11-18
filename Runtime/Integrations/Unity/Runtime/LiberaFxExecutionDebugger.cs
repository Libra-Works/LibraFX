using System;
using System.Runtime.InteropServices;

namespace LiberaFx.Unity
{
    public sealed unsafe partial class NativeInstance
    {
        public partial class NativeSpawnableHostBase
        {
            public NativeResult DebugGeneration(out ulong generation) =>
                DebugInterop.libra_spawnable_debug_generation(handle, out generation);
            public NativeResult AttachDebugger(ulong generation, byte[] container, ulong instance, string invitation)
            {
                if (container != null && container.Length != 16) return NativeResult.InvalidArgument;
                return DebugInterop.libra_spawnable_debug_attach(handle, generation, container, instance, invitation);
            }
            public NativeResult SubmitDebugger(byte operation, double time = 0, double delta = 0,
                byte[] container = null, ulong instance = 0, string node = null, string name = null)
            {
                if (container != null && container.Length != 16) return NativeResult.InvalidArgument;
                if (node != null && !Guid.TryParse(node, out _)) return NativeResult.InvalidArgument;
                var id = node == null ? null : new NativeSignal(node).Id;
                var utf8 = name == null ? null : System.Text.Encoding.UTF8.GetBytes(name + "\0");
                if (name != null && name.IndexOf('\0') >= 0) return NativeResult.InvalidArgument;
                return DebugInterop.libra_spawnable_debug_submit(handle, operation, time, delta, container, instance, id, utf8);
            }
            public NativeResult PollDebugger(out NativeResult result) => DebugInterop.libra_spawnable_debug_poll(handle, out result);
            public NativeResult DetachDebugger() => DebugInterop.libra_spawnable_debug_detach(handle);
        }
        public NativeResult AttachExecutionDebugger(string invitation) =>
            DebugInterop.libra_debug_attach(Alive, invitation);
        public NativeResult DetachExecutionDebugger() => DebugInterop.libra_debug_detach(Alive);
        public NativeResult ScheduleDebugEvaluation(double time) => DebugInterop.libra_debug_schedule(Alive, time);
        public NativeResult FinishDebugEvaluation(uint[] bindings, NativeValue[] values)
        {
            if (bindings == null || values == null || bindings.Length != values.Length)
                return NativeResult.InvalidArgument;
            fixed (uint* input = bindings)
            fixed (NativeValue* output = values)
                return DebugInterop.libra_debug_finish(Alive, input, (uint)bindings.Length, output);
        }

        private static class DebugInterop
        {
            private const string Library = "liberafx_native";
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern NativeResult libra_debug_attach(ulong instance, string invitation);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern NativeResult libra_debug_detach(ulong instance);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern NativeResult libra_debug_schedule(ulong instance, double time);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern NativeResult libra_debug_finish(ulong instance, uint* bindings, uint count, NativeValue* values);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern NativeResult libra_spawnable_debug_generation(ulong host, out ulong generation);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            internal static extern NativeResult libra_spawnable_debug_attach(ulong host, ulong generation, byte[] container, ulong instance, string invitation);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern NativeResult libra_spawnable_debug_submit(ulong host, byte operation, double time,
                double delta, byte[] container, ulong instance, byte[] node, byte[] name);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern NativeResult libra_spawnable_debug_poll(ulong host, out NativeResult result);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern NativeResult libra_spawnable_debug_detach(ulong host);
        }
    }
}
