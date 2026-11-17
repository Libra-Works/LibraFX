using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace LiberaFx.Unity
{
    public enum NativeResult : int
    {
        Ok, InvalidArgument, InvalidBinary, UnsupportedBinary, InsufficientWork,
        Busy, QueueRejected, NotReady, NodeNotFound, DivideByZero, NonFinite,
        InvalidValueType, UnknownCustomNode, InvalidHandle, InstructionBudgetExceeded,
        OutOfMemory, InternalError
    }

    public enum SpawnableTemplatePayloadKind : uint
    {
        RenderConfiguration, GpuContainers, GpuPrograms = 2
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

    public sealed class SpawnableHostEvent
    {
        public uint Kind { get; }
        public byte[] ContainerId { get; }
        public ulong InstanceId { get; }
        public double Time { get; }
        public ulong Seed { get; }
        public byte[] SignalNodeId { get; }

        internal SpawnableHostEvent(uint kind, byte[] containerId, ulong instanceId,
            double time, ulong seed, byte[] signalNodeId)
        {
            Kind = kind;
            ContainerId = containerId;
            InstanceId = instanceId;
            Time = time;
            Seed = seed;
            SignalNodeId = signalNodeId;
        }
    }

    public sealed class GameIoEvent
    {
        public byte[] ContainerId { get; }
        public ulong InstanceId { get; }
        public uint PortIndex { get; }
        public string PortName { get; }
        public byte Kind { get; }
        public byte Width { get; }
        public bool IsColor { get; }
        public double[] Values { get; }

        internal GameIoEvent(byte[] containerId, ulong instanceId, uint portIndex,
            byte kind, byte width, bool isColor, double[] values, string portName = null)
        {
            ContainerId = containerId;
            InstanceId = instanceId;
            PortIndex = portIndex;
            PortName = portName;
            Kind = kind;
            Width = width;
            IsColor = isColor;
            Values = values;
        }
    }

    [StructLayout(LayoutKind.Explicit, Pack = 8, Size = 64)]
    internal unsafe struct NativeSpawnableHostEvent
    {
        [FieldOffset(0)] public uint Kind;
        [FieldOffset(4)] public uint Reserved;
        [FieldOffset(8)] public fixed byte ContainerId[16];
        [FieldOffset(24)] public ulong InstanceId;
        [FieldOffset(32)] public double Time;
        [FieldOffset(40)] public ulong Seed;
        [FieldOffset(48)] public fixed byte SignalNodeId[16];
    }

    [StructLayout(LayoutKind.Explicit, Pack = 8, Size = 160)]
    internal unsafe struct NativeGameIoEvent
    {
        [FieldOffset(0)] public fixed byte ContainerId[16];
        [FieldOffset(16)] public ulong InstanceId;
        [FieldOffset(24)] public uint PortIndex;
        [FieldOffset(28)] public byte Kind;
        [FieldOffset(29)] public byte Width;
        [FieldOffset(30)] public byte IsColor;
        [FieldOffset(31)] public byte Reserved;
        [FieldOffset(32)] public fixed double Values[16];
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
                Marshal.OffsetOf<SpriteVertex>(nameof(SpriteVertex.A)).ToInt32() != 32 ||
                Marshal.SizeOf<NativeSpawnableHostEvent>() != 64 ||
                Marshal.OffsetOf<NativeSpawnableHostEvent>(nameof(NativeSpawnableHostEvent.ContainerId)).ToInt32() != 8 ||
                Marshal.OffsetOf<NativeSpawnableHostEvent>(nameof(NativeSpawnableHostEvent.InstanceId)).ToInt32() != 24 ||
                Marshal.OffsetOf<NativeSpawnableHostEvent>(nameof(NativeSpawnableHostEvent.Time)).ToInt32() != 32 ||
                Marshal.OffsetOf<NativeSpawnableHostEvent>(nameof(NativeSpawnableHostEvent.Seed)).ToInt32() != 40 ||
                Marshal.OffsetOf<NativeSpawnableHostEvent>(nameof(NativeSpawnableHostEvent.SignalNodeId)).ToInt32() != 48 ||
                Marshal.SizeOf<NativeGameIoEvent>() != 160 ||
                Marshal.OffsetOf<NativeGameIoEvent>(nameof(NativeGameIoEvent.Values)).ToInt32() != 32)
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
        internal static extern NativeResult libra_instance_set_time_context(ulong instance, double effectTime, double frameDelta);
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
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_spawnable_host_create(
            byte* bytes, ulong count, uint maxActivePerTemplate, ulong memoryBudgetBytesPerTemplate,
            uint eventCapacity, uint instructionBudget, out ulong host);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_spawnable_host_release(ulong host);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_spawnable_host_advance(ulong host, double time, double frameDelta);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_spawnable_host_dispatch_signal(ulong host, byte* nodeId);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_spawnable_host_fire_game_signal(ulong host, byte* name);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_spawnable_host_set_game_property(
            ulong host, byte* name, double* values, uint width);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_spawnable_host_set_owner_matrix(ulong host, double* values);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_spawnable_host_request_natural_end(ulong host);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_spawnable_host_stop(ulong host);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_spawnable_host_root_value(
            ulong host, byte* nodeId, out NativeValue value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_spawnable_host_child_value(
            ulong host, byte* containerId, ulong instance, byte* nodeId, out NativeValue value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_spawnable_host_next_event(
            ulong host, out NativeSpawnableHostEvent value, out uint hasEvent);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_spawnable_host_next_game_io_event(
            ulong host, out NativeGameIoEvent value, out uint hasEvent);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_spawnable_host_copy_game_io_port_name(
            ulong host, uint index, byte* destination, ulong capacity, out ulong count);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_spawnable_host_active_count(ulong host, out ulong count);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_spawnable_host_root_finished(ulong host, out uint finished);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_spawnable_host_template_count(ulong host, out ulong count);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_spawnable_host_template_id(
            ulong host, ulong index, byte* containerId);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_spawnable_host_copy_template_payload(
            ulong host, byte* containerId, SpawnableTemplatePayloadKind kind,
            byte* destination, ulong capacity, out ulong count);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeResult libra_spawnable_host_copy_root_payload(
            ulong host, SpawnableTemplatePayloadKind kind, byte* destination,
            ulong capacity, out ulong count);
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

            public class NativeSpawnableHostBase : IDisposable
            {
                private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
                private ulong handle;
                private readonly Dictionary<uint, string> gameIoPortNames = new Dictionary<uint, string>();
                public NativeSpawnableHostBase(byte[] compiledPackage, uint maxActivePerTemplate = 32,
                    ulong memoryBudgetBytesPerTemplate = 4 * 1024 * 1024, uint eventCapacity = 128,
                    uint instructionBudget = 10000)
                {
                    if (compiledPackage == null) throw new ArgumentNullException(nameof(compiledPackage));
                    NativeAbi.ValidateLayout();
                    if (Native.libra_abi_version() != 1) throw new NotSupportedException("LiberaFx ABI version mismatch.");
                    fixed (byte* bytes = compiledPackage)
                    {
                        NativeInstance.Check(Native.libra_spawnable_host_create(bytes,
                            (ulong)compiledPackage.LongLength, maxActivePerTemplate,
                            memoryBudgetBytesPerTemplate, eventCapacity, instructionBudget, out handle));
                    }
                }

                private ulong Alive
                {
                    get
                    {
                        if (handle == 0) throw new ObjectDisposedException(nameof(NativeSpawnableHostBase));
                        return handle;
                    }
                }

                public NativeResult Advance(double time, double frameDelta) =>
                    Native.libra_spawnable_host_advance(Alive, time, frameDelta);

                public NativeResult DispatchSignal(string nodeGuid)
                {
                    var id = new NativeSignal(nodeGuid).Id;
                    fixed (byte* pointer = id)
                        return Native.libra_spawnable_host_dispatch_signal(Alive, pointer);
                }

                public NativeResult FireGameSignal(string name)
                {
                    if (name == null) return NativeResult.InvalidArgument;
                    var bytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
                    fixed (byte* pointer = bytes)
                        return Native.libra_spawnable_host_fire_game_signal(Alive, pointer);
                }

                public NativeResult SetGameProperty(string name, double[] values)
                {
                    if (name == null || values == null) return NativeResult.InvalidArgument;
                    var bytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
                    fixed (byte* pointer = bytes)
                    fixed (double* lanes = values)
                        return Native.libra_spawnable_host_set_game_property(Alive, pointer, lanes, (uint)values.Length);
                }

                public NativeResult SetOwnerMatrix(double[] values)
                {
                    if (values == null || values.Length != 16) return NativeResult.InvalidArgument;
                    fixed (double* lanes = values)
                        return Native.libra_spawnable_host_set_owner_matrix(Alive, lanes);
                }

                public NativeResult RequestNaturalEnd() => Native.libra_spawnable_host_request_natural_end(Alive);
                public NativeResult Stop() => Native.libra_spawnable_host_stop(Alive);

                public NativeResult ReadRootValue(string nodeGuid, out NativeValue value)
                {
                    var id = new NativeSignal(nodeGuid).Id;
                    fixed (byte* pointer = id)
                        return Native.libra_spawnable_host_root_value(Alive, pointer, out value);
                }

                public NativeResult ReadChildValue(byte[] containerId, ulong instance, string nodeGuid,
                    out NativeValue value)
                {
                    if (containerId == null || containerId.Length != 16)
                    {
                        value = default;
                        return NativeResult.InvalidArgument;
                    }
                    var id = new NativeSignal(nodeGuid).Id;
                    fixed (byte* container = containerId)
                    fixed (byte* node = id)
                        return Native.libra_spawnable_host_child_value(Alive, container, instance, node, out value);
                }

                public NativeResult PopEvent(out SpawnableHostEvent value)
                {
                    var result = Native.libra_spawnable_host_next_event(Alive, out var item, out var has);
                    if (result != NativeResult.Ok) { value = null; return result; }
                    if (has == 0) { value = null; return NativeResult.Ok; }
                    var container = new byte[16];
                    var signalNode = new byte[16];
                    byte* containerSource = item.ContainerId;
                    fixed (byte* destination = container)
                        Buffer.MemoryCopy(containerSource, destination, 16, 16);
                    byte* signalSource = item.SignalNodeId;
                    fixed (byte* destination = signalNode)
                        Buffer.MemoryCopy(signalSource, destination, 16, 16);
                    value = new SpawnableHostEvent(item.Kind, container, item.InstanceId,
                        item.Time, item.Seed, signalNode);
                    return NativeResult.Ok;
                }

                public NativeResult PopGameIoEvent(out GameIoEvent value)
                {
                    var result = Native.libra_spawnable_host_next_game_io_event(Alive, out var item, out var has);
                    if (result != NativeResult.Ok) { value = null; return result; }
                    if (has == 0) { value = null; return NativeResult.Ok; }
                    if (!gameIoPortNames.TryGetValue(item.PortIndex, out var portName))
                    {
                        result = GetGameIoPortName(item.PortIndex, out portName);
                        if (result != NativeResult.Ok) { value = null; return result; }
                        gameIoPortNames.Add(item.PortIndex, portName);
                    }
                    var container = new byte[16];
                    var lanes = new double[16];
                    byte* containerSource = item.ContainerId;
                    fixed (byte* destination = container)
                        Buffer.MemoryCopy(containerSource, destination, 16, 16);
                    double* valuesSource = item.Values;
                    fixed (double* destination = lanes)
                        Buffer.MemoryCopy(valuesSource, destination, 16 * sizeof(double), 16 * sizeof(double));
                    value = new GameIoEvent(container, item.InstanceId, item.PortIndex,
                        item.Kind, item.Width, item.IsColor != 0, lanes, portName);
                    return NativeResult.Ok;
                }

                public NativeResult GetGameIoPortName(uint index, out string name)
                {
                    name = null;
                    var current = Alive;
                    var result = Native.libra_spawnable_host_copy_game_io_port_name(
                        current, index, null, 0, out var byteCount);
                    if (result != NativeResult.Ok) return result;
                    if (byteCount > int.MaxValue) return NativeResult.OutOfMemory;
                    var bytes = new byte[(int)byteCount];
                    fixed (byte* destination = bytes)
                        result = Native.libra_spawnable_host_copy_game_io_port_name(
                            current, index, destination, byteCount, out byteCount);
                    if (result != NativeResult.Ok) return result;
                    try { name = StrictUtf8.GetString(bytes); }
                    catch (DecoderFallbackException) { return NativeResult.InvalidBinary; }
                    return NativeResult.Ok;
                }

                public NativeResult GetActiveCount(out ulong count) =>
                    Native.libra_spawnable_host_active_count(Alive, out count);

                public NativeResult GetTemplateCount(out ulong count) =>
                    Native.libra_spawnable_host_template_count(Alive, out count);

                public NativeResult GetTemplateId(ulong index, out byte[] containerId)
                {
                    var id = new byte[16];
                    NativeResult result;
                    fixed (byte* pointer = id)
                        result = Native.libra_spawnable_host_template_id(Alive, index, pointer);
                    containerId = result == NativeResult.Ok ? id : null;
                    return result;
                }

                public NativeResult GetTemplatePayload(byte[] containerId,
                    SpawnableTemplatePayloadKind kind, out byte[] payload)
                {
                    payload = null;
                    if (containerId == null || containerId.Length != 16)
                        return NativeResult.InvalidArgument;
                    fixed (byte* id = containerId)
                        return GetPayload(id, kind, out payload);
                }

                public NativeResult GetRootPayload(SpawnableTemplatePayloadKind kind, out byte[] payload) =>
                    GetPayload(null, kind, out payload);

                private NativeResult GetPayload(byte* containerId, SpawnableTemplatePayloadKind kind,
                    out byte[] payload)
                {
                    payload = null;
                    var current = Alive;
                    var result = CopyPayload(current, containerId, kind, null, 0, out var count);
                    if (result != NativeResult.Ok) return result;
                    if (count > int.MaxValue) return NativeResult.OutOfMemory;
                    var bytes = new byte[(int)count];
                    fixed (byte* destination = bytes)
                        result = CopyPayload(current, containerId, kind, destination, count, out count);
                    if (result == NativeResult.Ok) payload = bytes;
                    return result;
                }

                private static NativeResult CopyPayload(ulong host, byte* containerId,
                    SpawnableTemplatePayloadKind kind, byte* destination, ulong capacity, out ulong count) =>
                    containerId == null
                        ? Native.libra_spawnable_host_copy_root_payload(host, kind, destination, capacity, out count)
                        : Native.libra_spawnable_host_copy_template_payload(host, containerId, kind,
                            destination, capacity, out count);

                public NativeResult IsRootFinished(out bool finished)
                {
                    var result = Native.libra_spawnable_host_root_finished(Alive, out var value);
                    finished = value != 0;
                    return result;
                }

                public void Dispose()
                {
                    if (handle != 0) { Native.libra_spawnable_host_release(handle); handle = 0; }
                    GC.SuppressFinalize(this);
                }

                ~NativeSpawnableHostBase() { Dispose(); }
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
        public NativeResult SetTimeContext(double effectTime, double frameDelta) =>
            Native.libra_instance_set_time_context(Alive, effectTime, frameDelta);
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

    public sealed class NativeSpawnableHost : NativeInstance.NativeSpawnableHostBase
    {
        public NativeSpawnableHost(byte[] compiledPackage, uint maxActivePerTemplate = 32,
            ulong memoryBudgetBytesPerTemplate = 4 * 1024 * 1024, uint eventCapacity = 128,
            uint instructionBudget = 10000)
            : base(compiledPackage, maxActivePerTemplate, memoryBudgetBytesPerTemplate,
                eventCapacity, instructionBudget) { }
    }
}
