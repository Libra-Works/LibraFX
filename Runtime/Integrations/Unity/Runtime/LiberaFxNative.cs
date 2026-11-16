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

    [StructLayout(LayoutKind.Explicit, Pack = 8, Size = 40)]
    public struct NativeValue
    {
        [FieldOffset(0)] public double X;
        [FieldOffset(8)] public double Y;
        [FieldOffset(16)] public double Z;
        [FieldOffset(24)] public double W;
        [FieldOffset(32)] public uint ComponentCount;
        [FieldOffset(36)] public uint Reserved;
    }

    [StructLayout(LayoutKind.Explicit, Pack = 8, Size = 32)]
    public struct NativeLifetimeSample
    {
        [FieldOffset(0)] public double Age;
        [FieldOffset(8)] public double Remaining;
        [FieldOffset(16)] public double NormalizedAge;
        [FieldOffset(24)] public uint Expired;
        [FieldOffset(28)] public uint Infinite;
    }

    [StructLayout(LayoutKind.Explicit, Pack = 4, Size = 20)]
    public struct AtlasFrame
    {
        [FieldOffset(0)] public uint FrameIndex;
        [FieldOffset(4)] public float U;
        [FieldOffset(8)] public float V;
        [FieldOffset(12)] public float Width;
        [FieldOffset(16)] public float Height;
    }

    [StructLayout(LayoutKind.Explicit, Pack = 4, Size = 36)]
    public struct SpriteVertex
    {
        [FieldOffset(0)] public float X;
        [FieldOffset(4)] public float Y;
        [FieldOffset(8)] public float Z;
        [FieldOffset(12)] public float U;
        [FieldOffset(16)] public float V;
        [FieldOffset(20)] public float R;
        [FieldOffset(24)] public float G;
        [FieldOffset(28)] public float B;
        [FieldOffset(32)] public float A;
    }

    public static class NativeAbi
    {
        // Initialization-time contract test, never called by the frame loop.
        public static void ValidateLayout()
        {
            if (Marshal.SizeOf<NativeValue>() != 40 || Marshal.SizeOf<NativeLifetimeSample>() != 32 ||
                Marshal.OffsetOf<NativeLifetimeSample>(nameof(NativeLifetimeSample.Age)).ToInt32() != 0 ||
                Marshal.OffsetOf<NativeLifetimeSample>(nameof(NativeLifetimeSample.Remaining)).ToInt32() != 8 ||
                Marshal.OffsetOf<NativeLifetimeSample>(nameof(NativeLifetimeSample.NormalizedAge)).ToInt32() != 16 ||
                Marshal.OffsetOf<NativeLifetimeSample>(nameof(NativeLifetimeSample.Expired)).ToInt32() != 24 ||
                Marshal.OffsetOf<NativeLifetimeSample>(nameof(NativeLifetimeSample.Infinite)).ToInt32() != 28 ||
                Marshal.SizeOf<AtlasFrame>() != 20 ||
                Marshal.SizeOf<SpriteVertex>() != 36 ||
                Marshal.OffsetOf<NativeValue>(nameof(NativeValue.X)).ToInt32() != 0 ||
                Marshal.OffsetOf<NativeValue>(nameof(NativeValue.Y)).ToInt32() != 8 ||
                Marshal.OffsetOf<NativeValue>(nameof(NativeValue.Z)).ToInt32() != 16 ||
                Marshal.OffsetOf<NativeValue>(nameof(NativeValue.W)).ToInt32() != 24 ||
                Marshal.OffsetOf<NativeValue>(nameof(NativeValue.ComponentCount)).ToInt32() != 32 ||
                Marshal.OffsetOf<NativeValue>(nameof(NativeValue.Reserved)).ToInt32() != 36 ||
                Marshal.OffsetOf<AtlasFrame>(nameof(AtlasFrame.FrameIndex)).ToInt32() != 0 ||
                Marshal.OffsetOf<AtlasFrame>(nameof(AtlasFrame.U)).ToInt32() != 4 ||
                Marshal.OffsetOf<AtlasFrame>(nameof(AtlasFrame.V)).ToInt32() != 8 ||
                Marshal.OffsetOf<AtlasFrame>(nameof(AtlasFrame.Width)).ToInt32() != 12 ||
                Marshal.OffsetOf<AtlasFrame>(nameof(AtlasFrame.Height)).ToInt32() != 16 ||
                Marshal.OffsetOf<SpriteVertex>(nameof(SpriteVertex.X)).ToInt32() != 0 ||
                Marshal.OffsetOf<SpriteVertex>(nameof(SpriteVertex.Y)).ToInt32() != 4 ||
                Marshal.OffsetOf<SpriteVertex>(nameof(SpriteVertex.Z)).ToInt32() != 8 ||
                Marshal.OffsetOf<SpriteVertex>(nameof(SpriteVertex.U)).ToInt32() != 12 ||
                Marshal.OffsetOf<SpriteVertex>(nameof(SpriteVertex.V)).ToInt32() != 16 ||
                Marshal.OffsetOf<SpriteVertex>(nameof(SpriteVertex.R)).ToInt32() != 20 ||
                Marshal.OffsetOf<SpriteVertex>(nameof(SpriteVertex.G)).ToInt32() != 24 ||
                Marshal.OffsetOf<SpriteVertex>(nameof(SpriteVertex.B)).ToInt32() != 28 ||
                Marshal.OffsetOf<SpriteVertex>(nameof(SpriteVertex.A)).ToInt32() != 32)
                throw new TypeLoadException("LiberaFx POD layout does not match the native C ABI.");
        }
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
        internal static extern NativeResult libra_values_read(
            ulong instance, uint* bindings, uint count, NativeValue* values);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_effect_start(
            ulong instance, double duration, uint infinite, byte* onStart, uint budget, out NativeLifetimeSample sample);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_effect_start_captured(
            ulong instance, byte* lifeNode, byte* onStart, uint budget, out NativeLifetimeSample sample);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_effect_advance(ulong instance, double elapsed, out NativeLifetimeSample sample);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_effect_drain(ulong instance, double elapsed);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_effect_destroy(ulong instance, byte* node, uint budget);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_effect_finish(ulong instance);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_effect_stop(ulong instance);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_dispatch_signal(
            ulong instance, byte* id, double time, uint instructionBudget);
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
            NativeAbi.ValidateLayout();
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

    public sealed class NativeSignal
    {
        internal readonly byte[] Id;

        public NativeSignal(string nodeGuid)
        {
            var hex = Guid.Parse(nodeGuid).ToString("N");
            Id = new byte[16];
            for (var i = 0; i < 16; ++i)
                Id[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
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
            return new NativeSignal(guid).Id;
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
        public NativeResult ReadValues(uint[] bindings, NativeValue[] values)
        {
            if (bindings == null || values == null || values.Length < bindings.Length)
                return NativeResult.InvalidArgument;
            fixed (uint* input = bindings)
            fixed (NativeValue* output = values)
                return Native.libra_values_read(Alive, input, (uint)bindings.Length, output);
        }
        public NativeResult StartLifetime(double duration, bool infinite, NativeSignal onStart,
            uint instructionBudget, out NativeLifetimeSample sample)
        {
            fixed (byte* signal = onStart == null ? null : onStart.Id)
                return Native.libra_effect_start(Alive, duration, infinite ? 1u : 0u, signal, instructionBudget, out sample);
        }
        public NativeResult StartCapturedLifetime(NativeSignal lifeNode, NativeSignal onStart,
            uint instructionBudget, out NativeLifetimeSample sample)
        {
            if (lifeNode == null) { sample = default; return NativeResult.InvalidArgument; }
            fixed (byte* life = lifeNode.Id)
            fixed (byte* signal = onStart == null ? null : onStart.Id)
                return Native.libra_effect_start_captured(Alive, life, signal, instructionBudget, out sample);
        }
        public NativeResult AdvanceLifetime(double elapsed, out NativeLifetimeSample sample) =>
            Native.libra_effect_advance(Alive, elapsed, out sample);
        public NativeResult DrainLifetime(double elapsed) => Native.libra_effect_drain(Alive, elapsed);
        public NativeResult DispatchNaturalDestroy(NativeSignal signal, uint instructionBudget)
        {
            if (signal == null) return NativeResult.InvalidArgument;
            fixed (byte* id = signal.Id)
                return Native.libra_effect_destroy(Alive, id, instructionBudget);
        }
        public NativeResult FinishLifetime() => Native.libra_effect_finish(Alive);
        public NativeResult StopLifetime() => Native.libra_effect_stop(Alive);
        public NativeResult DispatchSignal(NativeSignal signal, double time, uint instructionBudget)
        {
            if (signal == null) return NativeResult.InvalidArgument;
            fixed (byte* id = signal.Id)
                return Native.libra_dispatch_signal(Alive, id, time, instructionBudget);
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
