namespace LiberaFx.Models.Graph;

public static class CpuParticleNodeCatalog
{
    public static Dictionary<Guid, string> WithBuiltInIds(Dictionary<Guid, string> ids)
    {
        var entries = new (string Suffix, string Name)[]
        {
            ("001", "CpuParticle"), ("002", "CpuParticleSpawn"), ("003", "CpuParticleUpdate"),
            ("004", "CpuParticleSpriteDraw"), ("005", "CpuParticleStageEvent"),
            ("010", "CpuParticlePosition"), ("011", "CpuParticleVelocity"), ("012", "CpuParticleColor"),
            ("013", "CpuParticleSize"), ("014", "CpuParticleLife"), ("015", "CpuParticleAge"),
            ("016", "CpuParticleId"), ("017", "CpuParticleSeed"),
            ("018", "CpuParticleStepDelta"), ("019", "CpuParticleCustomRead"),
            ("020", "CpuParticleSetPosition"), ("021", "CpuParticleSetVelocity"),
            ("022", "CpuParticleSetColor"), ("023", "CpuParticleSetSize"), ("024", "CpuParticleSetLife"),
            ("025", "CpuParticleMove"), ("026", "CpuParticleSetAge"), ("027", "CpuParticleCustomWrite"),
            ("030", "CpuParticleInputFloat"), ("031", "CpuParticleOutputFloat"),
            ("032", "CpuParticleInputVector3"), ("033", "CpuParticleOutputVector3"),
            ("034", "CpuParticleInputVector4"), ("035", "CpuParticleOutputVector4"),
            ("036", "CpuParticleInputSignal"), ("037", "CpuParticleOutputSignal"),
            ("038", "CpuParticleInputBool"), ("039", "CpuParticleOutputBool"),
            ("040", "EmitterRate"), ("041", "EmitterBurst"), ("042", "CpuParticleEmitterBox"),
            ("043", "CpuParticleEmitterSphere"), ("044", "CpuParticleEmitterCircle"),
            ("045", "CpuParticleMeshPosition"), ("046", "CpuParticleMeshNormal"),
            ("047", "CpuParticleMeshUV"), ("048", "CpuParticleMeshColor"),
            ("050", "CpuParticleAttributesInput"), ("051", "CpuParticleAttributesOutput")
        };
        foreach (var (suffix, name) in entries)
        {
            ids.Add(Guid.Parse("7c7e1000-2521-4bdb-b8a0-210000000" + suffix), name);
        }
        return ids;
    }
}
