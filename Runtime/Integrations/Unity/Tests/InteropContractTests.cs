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
        }
    }
}
