using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace LiberaFx.Unity
{
    public enum NativeResult : int
    {
        Ok, InvalidArgument, InvalidBinary, UnsupportedBinary, InsufficientWork,
        Busy, QueueRejected, NotReady, NodeNotFound, DivideByZero, NonFinite,
        InvalidValueType, UnknownCustomNode, InvalidHandle, InstructionBudgetExceeded,
        OutOfMemory, InternalError
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeValue
    {
        public double X, Y, Z, W;
        public uint ComponentCount, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AtlasFrame
    {
        public uint FrameIndex;
        public float U, V, Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SpriteVertex
    {
        public float X, Y, Z, U, V, R, G, B, A;
    }

    internal static unsafe class Native
    {
#if UNITY_IOS && !UNITY_EDITOR
        private const string Library = "__Internal";
#else
        private const string Library = "liberafx_native";
#endif
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint libra_abi_version();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_program_create(byte* bytes, ulong count, out ulong program);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_program_release(ulong program);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_instance_create(ulong program, out ulong instance);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_instance_release(ulong instance);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_bind_value(ulong instance, byte* id, out uint binding);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_bind_parameter(
            ulong instance, byte* id, uint slot, uint component, out uint binding);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_parameter_get(ulong instance, uint binding, out double value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_parameter_set(ulong instance, uint binding, double value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_evaluate(
            ulong instance, double time, uint* bindings, uint count, NativeValue* values);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_sample_sprite_atlas(
            double frame, uint columns, uint rows, out AtlasFrame atlas);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_make_sprite_quad(
            in AtlasFrame atlas, float width, float height, SpriteVertex* vertices);
    }

    public sealed unsafe class NativeProgram : IDisposable
    {
        internal ulong Handle { get; private set; }
        public NativeProgram(byte[] compiledBytes)
        {
            if (compiledBytes == null) throw new ArgumentNullException(nameof(compiledBytes));
            if (Native.libra_abi_version() != 1) throw new NotSupportedException("LiberaFx ABI version mismatch.");
            fixed (byte* bytes = compiledBytes)
            {
                NativeInstance.Check(Native.libra_program_create(bytes, (ulong)compiledBytes.LongLength, out var handle));
                Handle = handle;
            }
        }
        public NativeInstance CreateInstance()
        {
            if (Handle == 0) throw new ObjectDisposedException(nameof(NativeProgram));
            return new NativeInstance(Handle);
        }
        public void Dispose()
        {
            if (Handle != 0) { Native.libra_program_release(Handle); Handle = 0; }
            GC.SuppressFinalize(this);
        }
        ~NativeProgram() { Dispose(); }
    }

    public sealed unsafe class NativeInstance : IDisposable
    {
        private ulong handle;
        internal NativeInstance(ulong program)
        {
            Check(Native.libra_instance_create(program, out handle));
        }
        private ulong Alive
        {
            get
            {
                if (handle == 0) throw new ObjectDisposedException(nameof(NativeInstance));
                return handle;
            }
        }
        internal static void Check(NativeResult result)
        {
            if (result != NativeResult.Ok) throw new InvalidOperationException("LiberaFx: " + result);
        }
        // Parsing occurs at binding time only. Guid.ToByteArray() uses mixed-endian
        // Windows layout and must not be sent directly to this ABI.
        private static byte[] NodeId(string guid)
        {
            var hex = Guid.Parse(guid).ToString("N");
            var bytes = new byte[16];
            for (var i = 0; i < 16; ++i)
                bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return bytes;
        }
        public uint BindValue(string nodeGuid)
        {
            var id = NodeId(nodeGuid);
            fixed (byte* pointer = id)
            {
                Check(Native.libra_bind_value(Alive, pointer, out var binding));
                return binding;
            }
        }
        public uint BindParameter(string nodeGuid, uint slot = 0, uint component = 0)
        {
            var id = NodeId(nodeGuid);
            fixed (byte* pointer = id)
            {
                Check(Native.libra_bind_parameter(Alive, pointer, slot, component, out var binding));
                return binding;
            }
        }
        public NativeResult SetParameter(uint binding, double value) =>
            Native.libra_parameter_set(Alive, binding, value);
        public NativeResult GetParameter(uint binding, out double value) =>
            Native.libra_parameter_get(Alive, binding, out value);
        // Caller caches both arrays. Pinning blittable PODs avoids array marshalling
        // copies and reverse callbacks, including in IL2CPP players.
        public NativeResult Evaluate(double time, uint[] bindings, NativeValue[] values)
        {
            if (bindings == null || values == null || values.Length < bindings.Length)
                return NativeResult.InvalidArgument;
            fixed (uint* input = bindings)
            fixed (NativeValue* output = values)
                return Native.libra_evaluate(Alive, time, input, (uint)bindings.Length, output);
        }
        public static NativeResult SampleAtlas(double frame, uint columns, uint rows, out AtlasFrame atlas) =>
            Native.libra_sample_sprite_atlas(frame, columns, rows, out atlas);
        public static NativeResult MakeQuad(in AtlasFrame atlas, float width, float height, SpriteVertex[] vertices)
        {
            if (vertices == null || vertices.Length < 4) return NativeResult.InvalidArgument;
            fixed (SpriteVertex* output = vertices)
                return Native.libra_make_sprite_quad(in atlas, width, height, output);
        }
        public void Dispose()
        {
            if (handle != 0) { Native.libra_instance_release(handle); handle = 0; }
            GC.SuppressFinalize(this);
        }
        ~NativeInstance() { Dispose(); }
    }
}
