namespace LibraGpu.Core;

public static class GpuSamples
{
    public static GpuProgram CreateNoiseProbe(uint capacity = 256)
    {
        var k = GpuDsl.Compile("""
            let p = @Position;
            let s = @Seed;
            @Velocity = float3(gradientnoise3d(p,s),fbm(p,s,3i,0.5),turbulence(p,s,3i,0.5));
            @Position = curlnoise(p,s,3i,0.5);
            @Color = float4(gradientnoise3d(p,s+1u),fbm(p,s,8i,1),turbulence(p,s,8i,1),1);
            @Size = float2(fbm(p,s,1i,0),turbulence(p,s,1i,0));
            @Age = 0;
            @Lifetime = 100;
            """, "noise", KernelStage.Update);
        return new() { Id = "noiseProbe", Capacity = capacity, Kernels = [k], Flow = [new() { Id = "evaluateNoise", Kind = FlowKind.Update, Kernel = k.Id }] };
    }
    public static GpuProgram CreateNoiseGuardProbe(uint capacity = 256)
    {
        var k = GpuDsl.Compile("""
            let p = @Position;
            let s = @Seed;
            @Position = curlnoise(p,s,0i+0i,0.5);
            @Velocity = float3(fbm(p,s,8i+1i,0.5),turbulence(p,s,4i,0.5+1),gradientnoise3d(p*1e38,s));
            @Color = float4(0,0,0,1);
            @Age = 0;
            @Lifetime = 100;
            """, "noiseGuards", KernelStage.Update);
        return new() { Id = "noiseGuardProbe", Capacity = capacity, Kernels = [k], Flow = [new() { Id = "evaluateNoiseGuards", Kind = FlowKind.Update, Kernel = k.Id }] };
    }
    public static GpuProgram CreateMathProbe(uint capacity = 256)
    {
        var kernels = new List<KernelGraph>();
        foreach (var op in GpuOperationRegistry.Operations.Keys.Where(op => op is >= GpuOp.Abs and <= GpuOp.Saturate))
        {
            var name = GpuOperationRegistry.Operations[op].DslName;
            string expression = op switch
            {
                GpuOp.Mod or GpuOp.Pow => $"{name}(a,b)",
                GpuOp.Lerp => "lerp(a,b,0.25)",
                GpuOp.Smoothstep => "smoothstep(-1,1,a)",
                GpuOp.Remap => "remap(a,float2(-1,1),float2(0,10))",
                GpuOp.Dot or GpuOp.Cross or GpuOp.Distance => $"{name}(@Position,@Velocity)",
                _ => $"{name}(a)"
            };
            string target = op == GpuOp.Cross ? "Position" : op is GpuOp.Dot or GpuOp.Distance ? "Rotation" : "Color";
            kernels.Add(GpuDsl.Compile($"let a = @Color; let b = float4(0.5,2,-2,0); @{target} = {expression}; @Age = 0; @Lifetime = 100;", name, KernelStage.Update));
        }
        return new() { Id = "mathProbe", Capacity = capacity, Kernels = kernels, Flow = [new() { Id = "evaluateMath", Kind = FlowKind.Update, Kernel = "abs" }] };
    }
    public static GpuProgram CreateEmitter(uint capacity = 4096)
    {
        var spawn = GpuDsl.Compile("""
            @Position = emitterPosition;
            @Velocity = float3(0, 3, 0);
            @Lifetime = 2;
            @Size = float2(0.1, 0.1);
            @Color = float4(1, 0.7, 0.2, 1);
            """, "spawn", KernelStage.Spawn);
        var update = GpuDsl.Compile("""
            @Velocity += float3(0, -9.81, 0) * dt;
            @Position += @Velocity * dt;
            let fade = clamp(1 - @Age / @Lifetime, 0, 1);
            @Color.a = fade;
            """, "update", KernelStage.Update);
        return new()
        {
            Id = "sampleEmitter",
            Capacity = capacity,
            Kernels = [spawn, update],
            Resources = [new() { Id = "billboardTexture", Kind = ResourceKind.Texture2D }],
            Flow =
            [
                new() { Id="spawnStep",Kind=FlowKind.Spawn,Kernel="spawn" },
                new() { Id="updateStep",Kind=FlowKind.Update,Kernel="update" },
                new() { Id="renderStep",Kind=FlowKind.RenderBillboard,Resource="billboardTexture" }
            ]
        };
    }
}
