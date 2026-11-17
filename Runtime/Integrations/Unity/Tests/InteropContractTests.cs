using System;
using System.IO;

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
            Console.WriteLine("LiberaFx POD layout, P/Invoke and allocation contracts passed.");
        }
#if UNITY_EDITOR
        [UnityEditor.MenuItem("LiberaFx/Verify native ABI contracts")]
#endif
        public static void Run()
        {
            NativeAbi.ValidateLayout(); // Marshal sizes and every field offset.
            using (var program = new NativeProgram(LiberaFxSampleProgram.Create()))
            using (var instance = program.CreateInstance())
            {
                var bindings = new[] {
                    instance.BindValue(LiberaFxSampleProgram.MotionId),
                    instance.BindValue(LiberaFxSampleProgram.ColorId),
                    instance.BindValue(LiberaFxSampleProgram.TimeId)
                };
                var parameter = instance.BindParameter(LiberaFxSampleProgram.GainId, 1, 0);
                var values = new NativeValue[3];
                program.Dispose();
                Assert(instance.Evaluate(1, bindings, values) == NativeResult.Ok &&
                    values[0].X == 0.2 && values[1].ComponentCount == 4 &&
                    values[1].X == 0.4 && values[1].Y == 0.4 &&
                    values[1].Z == 0.4 && values[1].W == 0.4 && values[2].X == 1);
                Assert(instance.SetParameter(parameter, 0.3) == NativeResult.Ok);
                Assert(instance.GetParameter(parameter, out var gain) == NativeResult.Ok && gain == 0.3);
                Assert(instance.Evaluate(1, bindings, values) == NativeResult.Ok && values[1].W == 0.5);
                Assert(NativeInstance.SampleAtlas(-1, 4, 4, out var atlas) == NativeResult.Ok &&
                    atlas.FrameIndex == 15 && atlas.U == 0.75f && atlas.V == 0.75f &&
                    atlas.Width == 0.25f && atlas.Height == 0.25f);
                var quad = new SpriteVertex[4];
                Assert(NativeInstance.MakeQuad(in atlas, 2, 4, quad) == NativeResult.Ok &&
                    quad[0].X == -1 && quad[0].Y == 2 && quad[0].U == 0.75f &&
                    quad[2].X == 1 && quad[2].Y == -2 && quad[2].V == 1 &&
                    quad[2].R == 1 && quad[2].A == 1);
                for (var frame = 0; frame < 100; ++frame)
                    Assert(instance.Evaluate(frame, bindings, values) == NativeResult.Ok);
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                for (var frame = 0; frame < 10000; ++frame)
                    Assert(instance.Evaluate(frame, bindings, values) == NativeResult.Ok);
                Assert(GC.GetAllocatedBytesForCurrentThread() == allocated);
            }
            using (var program = new NativeProgram(LiberaFxSampleProgram.Create()))
            using (var instance = program.CreateInstance())
            {
                var bindings = new[] { instance.BindValue(LiberaFxSampleProgram.TimeId) };
                var values = new NativeValue[1];
                Assert(instance.StartLifetime(2, false, null, 0, out var initial) == NativeResult.Ok &&
                    initial.Age == 0 && initial.Remaining == 2 && initial.Expired == 0);
                Assert(instance.AdvanceLifetime(3, out var expired) == NativeResult.Ok &&
                    expired.Age == 2 && expired.Expired == 1 &&
                    instance.ReadValues(bindings, values) == NativeResult.Ok && values[0].X == 2);
                Assert(instance.DrainLifetime(4) == NativeResult.Ok &&
                    instance.ReadValues(bindings, values) == NativeResult.Ok && values[0].X == 4);
                Assert(instance.FinishLifetime() == NativeResult.Ok);
            }
            using (var program = new NativeProgram(LiberaFxSampleProgram.Create()))
            using (var instance = program.CreateInstance())
            {
                var life = new NativeSignal("00000000-0000-0000-0000-000000000003");
                Assert(instance.StartCapturedLifetime(life, null, 0, out var initial) == NativeResult.Ok &&
                    initial.Remaining == 0.2);
                Assert(instance.AdvanceLifetime(1, out var expired) == NativeResult.Ok &&
                    expired.Age == 0.2 && expired.Expired == 1);
                Assert(instance.StopLifetime() == NativeResult.Ok);
            }
            var clockFixture = Environment.GetEnvironmentVariable("LIBERAFX_UNITY_CLOCK_FIXTURE");
            if (!string.IsNullOrEmpty(clockFixture))
            {
                var ids = File.ReadAllLines(clockFixture + ".ids");
                using (var program = new NativeProgram(File.ReadAllBytes(clockFixture)))
                using (var instance = program.CreateInstance())
                {
                    var bindings = new[] { instance.BindValue(ids[0]), instance.BindValue(ids[1]), instance.BindValue(ids[2]) };
                    var values = new NativeValue[3];
                    Assert(instance.SetTimeContext(5, 0.125) == NativeResult.Ok);
                    Assert(instance.Evaluate(2, bindings, values) == NativeResult.Ok &&
                        values[0].X == 5 && values[1].X == 0.125 && values[2].X == 2);
                    Assert(instance.Evaluate(3, bindings, values) == NativeResult.NotReady);
                }
            }
            var fixture = Environment.GetEnvironmentVariable("LIBERAFX_UNITY_SIGNAL_FIXTURE");
            if (!string.IsNullOrEmpty(fixture))
            {
                using (var program = new NativeProgram(File.ReadAllBytes(fixture)))
                using (var instance = program.CreateInstance())
                {
                    var signal = new NativeSignal(File.ReadAllText(fixture + ".id"));
                    var missing = new NativeSignal("ffffffff-ffff-ffff-ffff-ffffffffffff");
                    Assert(instance.DispatchSignal(null, 0, 3) == NativeResult.InvalidArgument);
                    Assert(instance.DispatchSignal(signal, 0, 3) == NativeResult.NotReady);
                    Assert(instance.Evaluate(0, new uint[0], new NativeValue[0]) == NativeResult.Ok);
                    Assert(instance.DispatchSignal(missing, 0, 3) == NativeResult.NodeNotFound);
                    Assert(instance.DispatchSignal(signal, 0, 0) == NativeResult.InvalidArgument);
                    Assert(instance.DispatchSignal(signal, 0, 2) == NativeResult.InstructionBudgetExceeded);
                    Assert(instance.DispatchSignal(signal, 0, 3) == NativeResult.Ok);
                    for (var i = 0; i < 100; ++i)
                        Assert(instance.DispatchSignal(signal, 0, 3) == NativeResult.Ok);
                    var allocated = GC.GetAllocatedBytesForCurrentThread();
                    for (var i = 0; i < 10000; ++i)
                        Assert(instance.DispatchSignal(signal, 0, 3) == NativeResult.Ok);
                    Assert(GC.GetAllocatedBytesForCurrentThread() == allocated);
                }
#if UNITY_EDITOR
                UnityEngine.Debug.Log("LiberaFx compiler-generated Signal interop contract passed.");
#endif
            }
            var spawnableFixture = Environment.GetEnvironmentVariable("LIBERAFX_UNITY_SPAWNABLE_FIXTURE");
            if (!string.IsNullOrEmpty(spawnableFixture))
                VerifySpawnableHost(spawnableFixture);
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
            foreach (var suffix in new[] { ".legacy.lfxp", ".root.lfxp" })
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

        private static unsafe void VerifyRootPayloads(byte[] bytes)
        {
            var legacy = bytes[4] == 1;
            using (var host = new NativeSpawnableHost(bytes))
            {
                foreach (var kind in new[] { SpawnableTemplatePayloadKind.RenderConfiguration,
                    SpawnableTemplatePayloadKind.GpuContainers })
                {
                    Assert(host.GetRootPayload(kind, out var owned) == NativeResult.Ok);
                    Assert(legacy ? owned.Length == 0 : owned.Length > 0);
                    if (owned.Length == 0) continue;
                    Assert(owned[0] == (kind == SpawnableTemplatePayloadKind.RenderConfiguration ? '{' : '['));
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
