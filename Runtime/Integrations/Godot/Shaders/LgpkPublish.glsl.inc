R"LGPK(
#version 450
layout(local_size_x = 64) in;
struct Particle {
    vec4 positionAge;
    vec4 velocityLifetime;
    vec4 color;
    vec4 sizeRotation;
    uvec4 seedAlive;
};
layout(set = 0, binding = 0, std430) readonly buffer ParticleBuffer { Particle particles[]; };
layout(set = 0, binding = 1, rgba32f) uniform writeonly image2D particleOutput;
void main() {
    uint index = gl_GlobalInvocationID.x;
    if (index >= uint(particles.length())) return;
    ivec2 base = ivec2(int(index % 64u) * 5, int(index / 64u));
    Particle p = particles[index];
    imageStore(particleOutput, base, p.positionAge);
    imageStore(particleOutput, base + ivec2(1,0), p.velocityLifetime);
    imageStore(particleOutput, base + ivec2(2,0), p.color);
    imageStore(particleOutput, base + ivec2(3,0), p.sizeRotation);
    imageStore(particleOutput, base + ivec2(4,0), vec4(p.seedAlive));
}
)LGPK"
