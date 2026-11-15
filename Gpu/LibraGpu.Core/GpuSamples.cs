namespace LibraGpu.Core;

public static class GpuSamples
{
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
