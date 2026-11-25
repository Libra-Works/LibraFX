using System;
using System.IO;
using System.Text;
using System.Globalization;

namespace LiberaFx.Unity.Tests
{
    public static class InteropContractTests
    {
        private static void Assert(bool condition)
        {
            if (!condition) throw new InvalidOperationException("LiberaFx native ABI contract failed.");
        }
        private static void Main()
        {
            Run();
#if NET8_0
            var fixtureDirectory = Environment.GetEnvironmentVariable("LIBERAFX_SPRITE_COMPOSITION_FIXTURE_DIR");
            if (!string.IsNullOrEmpty(fixtureDirectory)) VerifyCompiledSpriteComposition(fixtureDirectory);
            var instanceDirectory = Environment.GetEnvironmentVariable("LIBERAFX_INSTANCE_GAMEIO_FIXTURES");
            if (!string.IsNullOrEmpty(instanceDirectory)) VerifyInstanceGameIo(instanceDirectory);
            var worldFixture = Environment.GetEnvironmentVariable("LIBERAFX_EFFECT_WORLD_FIXTURE");
            if (!string.IsNullOrEmpty(worldFixture)) VerifyPreparedWorld(worldFixture);
#endif
            Console.WriteLine("LiberaFx POD layout, P/Invoke and allocation contracts passed.");
        }

#if NET8_0
        private static unsafe void VerifyPreparedWorld(string path)
        {
            Assert(System.Runtime.InteropServices.Marshal.SizeOf<SharedEffectFrame>() == 32 &&
                System.Runtime.InteropServices.Marshal.SizeOf<PreparedHostOptions>() == 16);
            var options = new PreparedHostOptions { capacity = 2, preparationWorkers = 2, eventCapacity = 128, instructionBudget = 10000 };
            Assert(NativePreparedHostPool.Create(File.ReadAllBytes(path), options, out var pool) == NativeResult.Ok);
            using (pool)
            {
                Assert(pool.Acquire(out var first) == NativeResult.Ok);
                Assert(pool.Acquire(out var second) == NativeResult.Ok);
                Assert(pool.Acquire(out var excess) == NativeResult.InsufficientWork && excess == null);
                Assert(pool.Release() == NativeResult.Busy);
                using (first) using (second)
                {
                    Assert(PreparedNative.libra_effect_world_create(2, 2, out var world) == NativeResult.Ok);
                    try
                    {
                        var frames = new SharedEffectFrame[2];
                        for (var step = 0; step < 60; ++step)
                        {
                            frames[0] = new SharedEffectFrame { host = first.RuntimeHandle, time = step / 60d, delta = step == 0 ? 0 : 1d / 60 };
                            frames[1] = new SharedEffectFrame { host = second.RuntimeHandle, time = step / 60d, delta = step == 0 ? 0 : 1d / 60 };
                            fixed (SharedEffectFrame* data = frames)
                                Assert(PreparedNative.libra_effect_world_advance(world, data, 2) == NativeResult.Ok);
                            Assert(frames[0].result == NativeResult.Ok && frames[1].result == NativeResult.Ok);
                            Assert(first.GetTimeSnapshot(out var a) == NativeResult.Ok && second.GetTimeSnapshot(out var b) == NativeResult.Ok &&
                                a.realTime == b.realTime && a.effectTime == b.effectTime);
                        }
                    }
                    finally { Assert(PreparedNative.libra_effect_world_release(world) == NativeResult.Ok); }
                }
                Assert(pool.Acquire(out var recycled) == NativeResult.Ok);
                using (recycled) Assert(recycled.Advance(0, 0) == NativeResult.Ok);
            }
            Console.WriteLine("Managed prepared-host pooling and shared-frame P/Invoke passed.");
        }

        private static void VerifyInstanceGameIo(string directory)
        {
            using var host = new NativeSpawnableHost(File.ReadAllBytes(Path.Combine(directory, "InstanceGameIo.lfxp")));
            var container = File.ReadAllBytes(Path.Combine(directory, "InstanceGameIo.container"));
            Assert(host.Advance(0, 0) == NativeResult.Ok && host.FireGameSignal("spawn") == NativeResult.Ok);
            Assert(host.PopEvent(out var spawned) == NativeResult.Ok && spawned != null && spawned.InstanceId != 0);
            Assert(host.GetInstanceTarget(container, spawned.InstanceId, out var generation) == NativeResult.Ok);
            Assert(host.SetInstanceProperty(generation, container, spawned.InstanceId, "speed", new[] { 17d }) == NativeResult.Ok);
            Assert(host.Advance(1, 1) == NativeResult.Ok &&
                host.GetInstanceProperty(generation, container, spawned.InstanceId, "result", out var values) == NativeResult.Ok &&
                values.Length == 1 && values[0] == 17);
            Assert(host.FireInstanceSignal(generation, container, spawned.InstanceId, "ping") == NativeResult.Ok &&
                host.PopGameIoEvent(out var signal) == NativeResult.Ok && signal != null &&
                signal.InstanceId == spawned.InstanceId && signal.PortName == "pong");
            Assert(host.SetInstanceProperty(generation, container, spawned.InstanceId, "speed\0wrong", new[] { 5d }) ==
                NativeResult.InvalidArgument);
        }

        private static void VerifyCompiledSpriteComposition(string directory)
        {
            using var host = new NativeSpawnableHost(File.ReadAllBytes(Path.Combine(directory, "composition.lfxp")));
            using var scene = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "render-config.json")));
            using var expected = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "expected.json")));
            foreach (var draw in scene.RootElement.GetProperty("nodes").EnumerateArray())
            {
                var drawId = draw.GetProperty("nodeId").GetGuid();
                var clip = draw.GetProperty("spriteAnimation");
                var frames = clip.GetProperty("frames");
                var durations = new double[frames.GetArrayLength()];
                for (var index = 0; index < durations.Length; index++)
                    durations[index] = frames[index].GetProperty("durationFrames").GetDouble();
                var keys = new System.Collections.Generic.List<double>();
                foreach (var key in clip.GetProperty("keys").EnumerateArray())
                    foreach (var name in new[] { "frame", "x", "y", "rotationDegrees", "scaleX", "scaleY" })
                        keys.Add(key.GetProperty(name).GetDouble());
                var keyArray = keys.ToArray();
                foreach (var frame in expected.RootElement.GetProperty("frames").EnumerateArray())
                foreach (var part in frame.GetProperty("nodes").EnumerateArray())
                {
                    if (part.GetProperty("nodeId").GetGuid() != drawId) continue;
                    Assert(NativeInstance.SampleSpriteComposition(frame.GetProperty("timeSeconds").GetDouble(),
                        clip.GetProperty("fps").GetDouble(), clip.GetProperty("durationFrames").GetDouble(),
                        clip.GetProperty("loop").GetBoolean(), durations, keyArray, out var selected, out var pose) == NativeResult.Ok);
                    var cell = clip.GetProperty("cells")[frames[(int)selected].GetProperty("cellIndex").GetInt32()];
                    double Value(string name, int index) => part.GetProperty(name)[index].GetDouble();
                    Assert(selected == part.GetProperty("frameIndex").GetUInt32() &&
                        pose.x == Value("position", 0) && pose.y == Value("position", 1) &&
                        pose.rotation == Value("rotation", 2) &&
                        pose.sx * cell.GetProperty("width").GetInt32() / 100 == Value("scale", 0) &&
                        pose.sy * cell.GetProperty("height").GetInt32() / 100 == Value("scale", 1));
                }
                for (var index = 0; index < 100; index++)
                    Assert(NativeInstance.SampleSpriteComposition(0, 2, 4, true, durations, keyArray, out _, out _) == NativeResult.Ok);
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                for (var index = 0; index < 10000; index++)
                    Assert(NativeInstance.SampleSpriteComposition(0, 2, 4, true, durations, keyArray, out _, out _) == NativeResult.Ok);
                Assert(GC.GetAllocatedBytesForCurrentThread() == allocated);
            }
            Assert(host.Advance(0, 0) == NativeResult.Ok);
            Console.WriteLine("Compiled sprite package and v2 native pose/rectangle fixture parity passed.");
        }
#endif
#if UNITY_EDITOR
        [UnityEditor.MenuItem("LiberaFx/Verify native ABI contracts")]
#endif
        public static void Run()
        {
            NativeAbi.ValidateLayout();
            var atlasResult = NativeInstance.SampleAtlas(-1, 4, 4, out var atlas);
            Assert(atlasResult == NativeResult.Ok && atlas.FrameIndex == 15 &&
                atlas.U == 0.75f && atlas.V == 0.75f && atlas.Width == 0.25f && atlas.Height == 0.25f);
            var quad = new SpriteVertex[4];
            Assert(NativeInstance.MakeQuad(in atlas, 2, 4, quad) == NativeResult.Ok &&
                quad[0].X == -1 && quad[0].Y == 2 && quad[2].X == 1 && quad[2].Y == -2);
            var fixture = Environment.GetEnvironmentVariable("LIBERAFX_UNITY_SPAWNABLE_FIXTURE");
            if (!string.IsNullOrEmpty(fixture)) VerifySpawnableHost(fixture);
            var payloadFixture = Environment.GetEnvironmentVariable("LIBERAFX_UNITY_SPAWNABLE_PAYLOAD_FIXTURE");
            if (!string.IsNullOrEmpty(payloadFixture)) VerifyPackagePayloadFixtures(payloadFixture);
            var gameIoFixture = Environment.GetEnvironmentVariable("LIBERAFX_UNITY_GAMEIO_FIXTURE");
            if (!string.IsNullOrEmpty(gameIoFixture)) VerifyGameIoPortNames(gameIoFixture);
#if UNITY_EDITOR
            UnityEngine.Debug.Log("LiberaFx package interop and POD contracts passed.");
#endif
        }

        private static unsafe void VerifyGameIoPortNames(string fixture)
        {
            var packageBytes = File.ReadAllBytes(fixture);
            VerifyRawGameIoNameAbi(packageBytes);
            using (var host = new NativeSpawnableHost(packageBytes))
            {
                Assert(host.GetGameIoPortName(0, out var position) == NativeResult.Ok && position == "position");
                Assert(host.GetGameIoPortName(1, out var spawn) == NativeResult.Ok && spawn == "spawn");
                Assert(host.GetGameIoPortName(2, out var echo) == NativeResult.Ok && echo == "echo");
                Assert(host.GetGameIoPortName(3, out _) == NativeResult.InvalidArgument);

                Assert(host.SetGameProperty("position", new[] { 2.0 }) == NativeResult.Ok);
                Assert(host.Advance(0, 0) == NativeResult.Ok);
                var sawEcho = false;
                while (host.PopGameIoEvent(out var gameIoEvent) == NativeResult.Ok && gameIoEvent != null)
                    sawEcho |= gameIoEvent.PortIndex == 2 && gameIoEvent.PortName == "echo" &&
                        gameIoEvent.Values[0] == 2.0;
                Assert(sawEcho);
            }
        }

        private static unsafe void VerifyRawGameIoNameAbi(byte[] packageBytes)
        {
            ulong host = 0;
            fixed (byte* package = packageBytes)
                Assert(Native.libra_spawnable_host_create(package, (ulong)packageBytes.LongLength,
                    32, 4 * 1024 * 1024, 128, 10000, out host) == NativeResult.Ok && host != 0);

            try
            {
                Assert(Native.libra_spawnable_host_copy_game_io_port_name(
                    host, 0, null, 0, out var required) == NativeResult.Ok && required == 8);

                Assert(Native.libra_spawnable_host_copy_game_io_port_name(
                    host, 0, null, 1, out var mismatchCount) == NativeResult.InvalidArgument &&
                    mismatchCount == 0);

                var shortBuffer = new byte[7];
                for (var index = 0; index < shortBuffer.Length; ++index) shortBuffer[index] = 0xa5;
                fixed (byte* destination = shortBuffer)
                    Assert(Native.libra_spawnable_host_copy_game_io_port_name(
                        host, 0, destination, (ulong)shortBuffer.Length, out var shortCount) ==
                        NativeResult.InsufficientWork && shortCount == 8);
                foreach (var value in shortBuffer) Assert(value == 0xa5);

                var exactBuffer = new byte[8];
                fixed (byte* destination = exactBuffer)
                    Assert(Native.libra_spawnable_host_copy_game_io_port_name(
                        host, 0, destination, (ulong)exactBuffer.Length, out var copied) ==
                        NativeResult.Ok && copied == 8);
                Assert(System.Text.Encoding.UTF8.GetString(exactBuffer) == "position");
                Assert(Native.libra_spawnable_host_copy_game_io_port_name(
                    host, 3, null, 0, out var invalidIndexCount) == NativeResult.InvalidArgument &&
                    invalidIndexCount == 0);

                var staleHost = host;
                Assert(Native.libra_spawnable_host_release(host) == NativeResult.Ok);
                host = 0;
                Assert(Native.libra_spawnable_host_copy_game_io_port_name(
                    staleHost, 0, null, 0, out var staleCount) == NativeResult.InvalidHandle &&
                    staleCount == 0);
            }
            finally
            {
                if (host != 0) Native.libra_spawnable_host_release(host);
            }
        }

        private static void VerifySpawnableHost(string fixture)
        {
            var folder = Path.GetDirectoryName(fixture);
            var bytes = File.ReadAllBytes(fixture);
            var identities = File.ReadAllBytes(Path.Combine(folder, "Fireworks.native-fixture"));
            Assert(identities.Length == 8 + 11 * 16);
            VerifyTemplatePayloads(bytes);
            VerifyTemplatePayloadBuffers(bytes);
            VerifyRootPayloads(bytes);
            foreach (var suffix in new[] { ".legacy.lfxp", ".legacy-v2.lfxp", ".root.lfxp",
                         ".gpu-programs.lfxp", ".adapter.lfxp", ".adapter-gpu.lfxp" })
            {
                var additional = Path.ChangeExtension(fixture, suffix);
                if (File.Exists(additional)) VerifyRootPayloads(File.ReadAllBytes(additional));
            }
            using (var host = new NativeSpawnableHost(bytes))
            {
                Assert(host.Advance(0, 0) == NativeResult.Ok);
                Assert(host.GetActiveCount(out var active) == NativeResult.Ok && active == 1);
                Assert(host.PopEvent(out var started) == NativeResult.Ok &&
                    started != null && started.Kind == 1 && started.Time == 0 &&
                    started.ContainerId[0] == identities[24]);
                Assert(host.Advance(2, 1) == NativeResult.Ok);
                Assert(host.PopEvent(out var destroyed) == NativeResult.Ok && destroyed != null &&
                    destroyed.Kind == 3 && destroyed.Time == 2);
                Assert(host.PopEvent(out var stopped) == NativeResult.Ok && stopped != null &&
                    stopped.Kind == 2 && stopped.Time == 2);
                Assert(host.PopEvent(out var burst) == NativeResult.Ok && burst != null &&
                    burst.Kind == 1 && burst.Time == 2 && burst.ContainerId[0] == identities[104]);
                Assert(host.Advance(3.5, 1.5) == NativeResult.Ok);
                Assert(host.PopEvent(out var burstDestroyed) == NativeResult.Ok &&
                    burstDestroyed != null && burstDestroyed.Kind == 3 && burstDestroyed.Time == 3.5);
                Assert(host.PopEvent(out var burstStopped) == NativeResult.Ok &&
                    burstStopped != null && burstStopped.Kind == 2 && burstStopped.Time == 3.5);
                Assert(host.IsRootFinished(out var finished) == NativeResult.Ok && finished);
            }
            using (var host = new NativeSpawnableHost(bytes))
            {
                Assert(host.Advance(0, 0) == NativeResult.Ok);
                Assert(host.PopEvent(out var started) == NativeResult.Ok && started != null);
                Assert(host.Stop() == NativeResult.Ok);
                Assert(host.PopEvent(out var stopped) == NativeResult.Ok &&
                    stopped != null && stopped.Kind == 2);
            }
        }

        private static void VerifyTemplatePayloads(byte[] bytes)
        {
            byte[] owned = null;
            byte first = 0;
            using (var host = new NativeSpawnableHost(bytes))
            {
                Assert(host.GetTemplateCount(out var count) == NativeResult.Ok && count == 2);
                for (ulong index = 0; index < count; ++index)
                {
                    Assert(host.GetTemplateId(index, out var id) == NativeResult.Ok);
                    Assert(host.GetTemplatePayload(id, SpawnableTemplatePayloadKind.RenderConfiguration,
                        out var render) == NativeResult.Ok && render.Length > 0);
                    Assert(System.Text.Encoding.UTF8.GetString(render).Contains("\"nodes\""));
                    first = render[0];
                    render[0] ^= 0xff;
                    Assert(host.GetTemplatePayload(id, SpawnableTemplatePayloadKind.RenderConfiguration,
                        out owned) == NativeResult.Ok && owned[0] == first);
                    Assert(host.GetTemplatePayload(id, SpawnableTemplatePayloadKind.GpuContainers,
                        out var gpu) == NativeResult.Ok && System.Text.Encoding.UTF8.GetString(gpu) == "[]");
                    Assert(host.GetTemplatePayload(id, SpawnableTemplatePayloadKind.GpuPrograms,
                        out var programs) == NativeResult.Ok &&
                        System.Text.Encoding.UTF8.GetString(programs).StartsWith("{\"version\":4,\"programs\":[]}", StringComparison.Ordinal));
                    Assert(host.GetTemplatePayload(id, (SpawnableTemplatePayloadKind)99, out _) ==
                        NativeResult.InvalidArgument);
                }
                Assert(host.GetTemplateId(count, out _) == NativeResult.InvalidArgument);
                Assert(host.GetTemplatePayload(new byte[15], SpawnableTemplatePayloadKind.RenderConfiguration,
                    out _) == NativeResult.InvalidArgument);
                Assert(host.GetTemplatePayload(new byte[16], SpawnableTemplatePayloadKind.RenderConfiguration,
                    out _) == NativeResult.NodeNotFound);
                Assert(host.Stop() == NativeResult.Ok);
                Assert(host.GetTemplateId(0, out var stoppedId) == NativeResult.Ok &&
                    host.GetTemplatePayload(stoppedId, SpawnableTemplatePayloadKind.RenderConfiguration,
                        out _) == NativeResult.Ok);
            }
            Assert(owned != null && owned[0] == first);
        }

        private static unsafe void VerifyTemplatePayloadBuffers(byte[] bytes)
        {
            ulong host;
            fixed (byte* source = bytes)
                Assert(Native.libra_spawnable_host_create(source, (ulong)bytes.Length,
                    32, 4 * 1024 * 1024, 128, 10000, out host) == NativeResult.Ok);
            try
            {
                var id = new byte[16];
                fixed (byte* container = id)
                {
                    Assert(Native.libra_spawnable_host_template_id(host, 0, container) == NativeResult.Ok);
                    Assert(Native.libra_spawnable_host_copy_template_payload(host, container,
                        SpawnableTemplatePayloadKind.RenderConfiguration, null, 0, out var required) ==
                        NativeResult.Ok && required > 1);
                    var buffer = new byte[checked((int)required)];
                    for (var index = 0; index < buffer.Length; ++index) buffer[index] = 0x5a;
                    fixed (byte* destination = buffer)
                    {
                        Assert(Native.libra_spawnable_host_copy_template_payload(host, container,
                            SpawnableTemplatePayloadKind.RenderConfiguration, destination, required - 1,
                            out var actual) == NativeResult.InsufficientWork && actual == required);
                        foreach (var value in buffer) Assert(value == 0x5a);
                        Assert(Native.libra_spawnable_host_copy_template_payload(host, container,
                            SpawnableTemplatePayloadKind.RenderConfiguration, destination, required,
                            out actual) == NativeResult.Ok && actual == required && buffer[0] == '{');
                    }
                    Assert(Native.libra_spawnable_host_copy_template_payload(host, container,
                        SpawnableTemplatePayloadKind.RenderConfiguration, null, 1, out var invalidCount) ==
                        NativeResult.InvalidArgument && invalidCount == 0);
                }
            }
            finally { Assert(Native.libra_spawnable_host_release(host) == NativeResult.Ok); }
            Assert(Native.libra_spawnable_host_template_count(host, out var staleCount) ==
                NativeResult.InvalidHandle && staleCount == 0);
        }

        private static void VerifyPackagePayloadFixtures(string fixture)
        {
            VerifyPackagePayloads(File.ReadAllBytes(fixture));
            foreach (var suffix in new[] { ".legacy.lfxp", ".legacy-v2.lfxp", ".gpu-programs.lfxp",
                         ".adapter.lfxp", ".adapter-gpu.lfxp", ".gpu.lfxp" })
            {
                var additional = Path.ChangeExtension(fixture, suffix);
                if (File.Exists(additional)) VerifyPackagePayloads(File.ReadAllBytes(additional));
            }
        }

        private static unsafe void VerifyPackagePayloads(byte[] bytes)
        {
            using (var host = new NativeSpawnableHost(bytes))
            {
                Assert(host.GetTemplateCount(out var count) == NativeResult.Ok && count > 0);
                for (ulong index = 0; index < count; ++index)
                {
                    Assert(host.GetTemplateId(index, out var id) == NativeResult.Ok);
                    Assert(host.GetTemplatePayload(id, SpawnableTemplatePayloadKind.GpuPrograms,
                        out var programs) == NativeResult.Ok);
                    if (bytes[4] < 3) Assert(programs.Length == 0);
                    else
                    {
                        var json = System.Text.Encoding.UTF8.GetString(programs);
                        Assert(json.Contains("\"version\":4", StringComparison.Ordinal) &&
                            json.Contains("\"programs\":[", StringComparison.Ordinal));
                    }
                }
                Assert(host.GetRootPayload(SpawnableTemplatePayloadKind.GpuPrograms, out var rootPrograms) ==
                    NativeResult.Ok);
                if (bytes[4] < 3) Assert(rootPrograms.Length == 0);
                else
                {
                    var json = System.Text.Encoding.UTF8.GetString(rootPrograms);
                    Assert(json.Contains("\"version\":4", StringComparison.Ordinal) &&
                        json.Contains("\"programs\":[", StringComparison.Ordinal));
                }
            }

            ulong handle;
            fixed (byte* source = bytes)
                Assert(Native.libra_spawnable_host_create(source, (ulong)bytes.Length,
                    32, 4 * 1024 * 1024, 128, 10000, out handle) == NativeResult.Ok);
            try
            {
                var id = new byte[16];
                fixed (byte* container = id)
                {
                    Assert(Native.libra_spawnable_host_template_id(handle, 0, container) == NativeResult.Ok);
                    Assert(Native.libra_spawnable_host_copy_template_payload(handle, container,
                        SpawnableTemplatePayloadKind.GpuPrograms, null, 0, out var required) == NativeResult.Ok);
                    if (required != 0)
                    {
                        var copied = new byte[checked((int)required)];
                        fixed (byte* destination = copied)
                            Assert(Native.libra_spawnable_host_copy_template_payload(handle, container,
                                SpawnableTemplatePayloadKind.GpuPrograms, destination, required,
                                out var actual) == NativeResult.Ok && actual == required && copied[0] == '{');
                    }
                }
                Assert(Native.libra_spawnable_host_copy_root_payload(handle,
                    SpawnableTemplatePayloadKind.GpuPrograms, null, 0, out var rootRequired) == NativeResult.Ok);
                if (rootRequired != 0)
                {
                    var copiedRoot = new byte[checked((int)rootRequired)];
                    fixed (byte* destination = copiedRoot)
                        Assert(Native.libra_spawnable_host_copy_root_payload(handle,
                            SpawnableTemplatePayloadKind.GpuPrograms, destination, rootRequired,
                            out var actual) == NativeResult.Ok && actual == rootRequired && copiedRoot[0] == '{');
                }
            }
            finally { Assert(Native.libra_spawnable_host_release(handle) == NativeResult.Ok); }
        }

        private static unsafe void VerifyRootPayloads(byte[] bytes)
        {
            var legacy = bytes[4] == 1;
            using (var host = new NativeSpawnableHost(bytes))
            {
                foreach (var kind in new[] { SpawnableTemplatePayloadKind.RenderConfiguration,
                    SpawnableTemplatePayloadKind.GpuContainers, SpawnableTemplatePayloadKind.GpuPrograms })
                {
                    Assert(host.GetRootPayload(kind, out var owned) == NativeResult.Ok);
                    Assert((bytes[4] == 1 ||
                            (bytes[4] == 2 && kind == SpawnableTemplatePayloadKind.GpuPrograms))
                        ? owned.Length == 0 : owned.Length > 0);
                    if (owned.Length == 0) continue;
                    var expectedStart = kind == SpawnableTemplatePayloadKind.GpuContainers ? '[' : '{';
                    Assert(owned[0] == expectedStart);
                    if (kind == SpawnableTemplatePayloadKind.GpuPrograms)
                        Assert(System.Text.Encoding.UTF8.GetString(owned).Contains("\"programs\"", StringComparison.Ordinal));
                    var first = owned[0];
                    owned[0] ^= 0xff;
                    Assert(host.GetRootPayload(kind, out var second) == NativeResult.Ok && second[0] == first);
                }
                Assert(host.GetRootPayload((SpawnableTemplatePayloadKind)99, out _) == NativeResult.InvalidArgument);
                Assert(host.Stop() == NativeResult.Ok &&
                    host.GetRootPayload(SpawnableTemplatePayloadKind.RenderConfiguration, out var stopped) ==
                    NativeResult.Ok && (legacy ? stopped.Length == 0 : stopped[0] == '{'));
            }
            ulong handle;
            fixed (byte* source = bytes)
                Assert(Native.libra_spawnable_host_create(source, (ulong)bytes.Length,
                    32, 4 * 1024 * 1024, 128, 10000, out handle) == NativeResult.Ok);
            var buffer = new byte[1];
            try
            {
                Assert(Native.libra_spawnable_host_copy_root_payload(handle,
                    SpawnableTemplatePayloadKind.RenderConfiguration, null, 0, out var size) == NativeResult.Ok);
                if (size > 0)
                {
                    buffer = new byte[checked((int)size)];
                    for (var index = 0; index < buffer.Length; ++index) buffer[index] = 0x5a;
                    fixed (byte* destination = buffer)
                    {
                        Assert(Native.libra_spawnable_host_copy_root_payload(handle,
                            SpawnableTemplatePayloadKind.RenderConfiguration, destination, size - 1, out var actual) ==
                            NativeResult.InsufficientWork && actual == size);
                        foreach (var value in buffer) Assert(value == 0x5a);
                        Assert(Native.libra_spawnable_host_copy_root_payload(handle,
                            SpawnableTemplatePayloadKind.RenderConfiguration, destination, size, out actual) ==
                            NativeResult.Ok && actual == size && buffer[0] == '{');
                    }
                }
                Assert(Native.libra_spawnable_host_copy_root_payload(handle,
                    SpawnableTemplatePayloadKind.RenderConfiguration, null, 1, out var invalid) ==
                    NativeResult.InvalidArgument && invalid == 0);
            }
            finally { Assert(Native.libra_spawnable_host_release(handle) == NativeResult.Ok); }
            Assert(Native.libra_spawnable_host_copy_root_payload(handle,
                SpawnableTemplatePayloadKind.RenderConfiguration, null, 0, out var stale) ==
                NativeResult.InvalidHandle && stale == 0);
            Assert(buffer.Length > 0 && (legacy || buffer[0] == '{'));
        }
    }
}
