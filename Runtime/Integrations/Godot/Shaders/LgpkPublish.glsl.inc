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
    if (index >= uint(imageSize(particleOutput).y)) return;
    Particle p = particles[index];
    imageStore(particleOutput, ivec2(0, int(index)), p.positionAge);
    imageStore(particleOutput, ivec2(1, int(index)), p.velocityLifetime);
    imageStore(particleOutput, ivec2(2, int(index)), p.color);
    imageStore(particleOutput, ivec2(3, int(index)), p.sizeRotation);
    imageStore(particleOutput, ivec2(4, int(index)), vec4(p.seedAlive));
}
)LGPK"
