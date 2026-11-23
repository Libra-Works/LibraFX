namespace LibraGpu.Core;

public static class GpuSamples
{
    public static GpuProgram CreateCurlCloud(uint capacity = 100_000)
    {
        var program = CreateEmitter(capacity);
        var spawn = GpuDsl.Compile("""
            @Position = emitterPosition;
            @Velocity = float3(0, 0, 0);
            @Lifetime = 12;
            @Size = float2(0.025, 0.025);
            @Color = float4(0.12, 0.65, 1, 0.4);
            """, "spawn", KernelStage.Spawn);
        var update = GpuDsl.Compile("""
            let frequency = 0.65;
            let strength = 1.2;
            let drift = float3(0, 0.3, 0);
            let animatedPosition = @Position * frequency + float3(0, time * 0.1, 0);
            @Velocity = curlnoise(animatedPosition, 17u, 3i, 0.5) * strength + drift;
            @Position += @Velocity * dt;
            @Color.a = 0.4 * clamp(@Age, 0, 1) * clamp(@Lifetime - @Age, 0, 1);
            """, "update", KernelStage.Update);
        return program with { Id = "curlCloud100k", SpawnRate = capacity / 8d,
            Kernels = [spawn, update], Flow = program.Flow.Select(flow => flow.Kind == FlowKind.Spawn
                ? flow with { SpawnShape = new() { Kind = GpuSpawnShapeKind.Sphere, Radius = 1.5 } } : flow).ToList() };
    }
    public static GpuProgram CreateCurlTurbulence()
    {
        var program = CreateCurlCloud(4096);
        var update = GpuDsl.Compile("""
            let frequency = 0.65;
            let strength = 1.2;
            let drift = float3(0, 0.3, 0);
            let animatedPosition = @Position * frequency + float3(0, time * 0.1, 0);
            let variation = turbulence(animatedPosition, 31u, 3i, 0.5);
            @Velocity = curlnoise(animatedPosition, 17u, 3i, 0.5) * strength * (0.35 + 0.65 * variation) + drift;
            @Position += @Velocity * dt;
            @Color.a = 0.4 * clamp(@Age, 0, 1) * clamp(@Lifetime - @Age, 0, 1);
            """, "update", KernelStage.Update);
        return program with { Id = "curlTurbulence", SpawnRate = 240,
            Kernels = [program.Kernels.Single(kernel => kernel.Stage == KernelStage.Spawn), update] };
    }
    public static GpuProgram CreateFluidVelocityParticles()
    {
        var program = CreateEmitter(2048);
        var resources = program.Resources.Append(new ResourceDeclaration
        {
            Id = "fluidVelocity",
            Kind = ResourceKind.FlowMap2D,
            FlowMap = new() { Encoding = FlowMapEncoding.FloatVelocity, Coordinates = FlowMapCoordinates.GeneratedPlane }
        }).ToList();
        var spawn = GpuDsl.Compile("""
            @Position = emitterPosition + float3(0, -0.38, 0);
            @Velocity = float3(0, 0, 0);
            @Lifetime = 4;
            @Size = float2(0.025, 0.025);
            @Color = float4(1, 0.8, 0.2, 0.8);
            """, "spawn", KernelStage.Spawn);
        var update = GpuDsl.Compile("""
            @Velocity = sampleFlowMap("fluidVelocity", @Position);
            @Position += @Velocity * dt;
            @Color.a = 0.8 * clamp(@Age, 0, 1) * clamp(@Lifetime - @Age, 0, 1);
            """, "update", KernelStage.Update, resources: resources);
        return program with
        {
            Id = "fluidVelocityParticles", SpawnRate = 100, Resources = resources, Kernels = [spawn, update],
            Flow = program.Flow.Select(flow => flow.Kind == FlowKind.Spawn
                ? flow with { SpawnShape = new() { Kind = GpuSpawnShapeKind.Circle, Radius = 0.06 } } : flow).ToList()
        };
    }
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
                new() { Id="spawnStep",Kind=FlowKind.Spawn,Kernel="spawn",SpawnShape=new() },
                new() { Id="updateStep",Kind=FlowKind.Update,Kernel="update" },
                new() { Id="renderStep",Kind=FlowKind.RenderBillboard,Resource="billboardTexture" }
            ]
        };
    }
}
