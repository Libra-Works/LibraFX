using System;

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
        }
    }
}
