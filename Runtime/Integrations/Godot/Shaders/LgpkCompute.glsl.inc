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
layout(set = 0, binding = 0, std430) buffer ParticleBuffer { Particle particles[]; };
layout(set = 0, binding = 1, std430) readonly buffer PacketBuffer { uvec4 code[]; };
layout(set = 0, binding = 2, std430) readonly buffer FlowMapBuffer { uint flowData[]; };
layout(push_constant, std430) uniform Frame {
    uvec4 range; // capacity, count, spawnStart, spawnCount
    vec2 timing; // delta, time
    uint seed;
    uint frameIndex;
    vec4 emitterPosition;
} frame;

// Each lane owns its own SSA registers; no cross-lane memory or barriers.
// The host bounds the program to this many registers before creating RIDs.
uvec4 values[128];

uint hashSeed(uint x) {
    x ^= x >> 16;
    x *= 0x7feb352du;
    x ^= x >> 15;
    x *= 0x846ca68bu;
    return x ^ (x >> 16);
}
bool finite4(vec4 v) {
    return !any(isnan(v)) && !any(isinf(v));
}
bool finiteParticle(Particle p) {
    return finite4(p.positionAge) && finite4(p.velocityLifetime) &&
        finite4(p.color) && finite4(p.sizeRotation);
}
uvec4 attributeValue(Particle p, uint slot) {
    switch (slot) {
        case 0u: return floatBitsToUint(vec4(p.positionAge.xyz, 0));
        case 1u: return floatBitsToUint(vec4(p.velocityLifetime.xyz, 0));
        case 2u: return floatBitsToUint(p.color);
        case 3u: return floatBitsToUint(vec4(p.sizeRotation.xy, 0, 0));
        case 4u: return uvec4(floatBitsToUint(p.sizeRotation.z), 0, 0, 0);
        case 5u: return uvec4(floatBitsToUint(p.positionAge.w), 0, 0, 0);
        case 6u: return uvec4(floatBitsToUint(p.velocityLifetime.w), 0, 0, 0);
        case 7u: return uvec4(uint(p.seedAlive.y != 0u), 0, 0, 0);
        default: return uvec4(p.seedAlive.x, 0, 0, 0);
    }
}
void storeAttribute(inout Particle p, uint slot, uvec4 bits) {
    vec4 v = uintBitsToFloat(bits);
    switch (slot) {
        case 0u: p.positionAge.xyz = v.xyz; break;
        case 1u: p.velocityLifetime.xyz = v.xyz; break;
        case 2u: p.color = v; break;
        case 3u: p.sizeRotation.xy = v.xy; break;
        case 4u: p.sizeRotation.z = v.x; break;
        case 5u: p.positionAge.w = v.x; break;
        case 6u: p.velocityLifetime.w = v.x; break;
        case 7u: p.seedAlive.y = uint(bits.x != 0u); break;
        default: p.seedAlive.x = bits.x; break;
    }
}
uvec4 readRegister(uint index) {
    if (index == 0xffffffffu) {
        return uvec4(0);
    }
    return values[index];
}
vec4 floatInput(uvec4 bits, uint index, uint destinationType) {
    vec4 v = uintBitsToFloat(bits);
    return code[5u + index * 4u].y == 0u && destinationType >= 4u ? v.xxxx : v;
}
float safeDiv(float a, float b) {
    return b == 0.0 ? 0.0 : a / b;
}
uint intArithmetic(uint op, uint type, uint a, uint b, uint c) {
    switch (op) {
        case 3u: return a + b;
        case 4u: return a - b;
        case 5u: return a * b;
        case 6u: {
            if (b == 0u) {
                return 0u;
            }
            if (type == 2u) {
                return a / b;
            }
            if (a == 0x80000000u && b == 0xffffffffu) {
                return a;
            }
            return uint(int(a) / int(b));
        }
        case 9u: return type == 1u ? uint(min(max(int(a), int(b)), int(c))) : min(max(a, b), c);
        case 10u: return type == 1u ? uint(min(int(a), int(b))) : min(a, b);
        default: return type == 1u ? uint(max(int(a), int(b))) : max(a, b);
    }
}
vec4 floatArithmetic(uint op, vec4 a, vec4 b, vec4 c) {
    switch (op) {
        case 3u: return a + b;
        case 4u: return a - b;
        case 5u: return a * b;
        case 6u: return vec4(safeDiv(a.x,b.x), safeDiv(a.y,b.y), safeDiv(a.z,b.z), safeDiv(a.w,b.w));
        case 9u: return min(max(a, b), c);
        case 10u: return min(a, b);
        default: return max(a, b);
    }
}

bool finite1(float v) { return !isnan(v) && !isinf(v); }
float finiteOrZero(float v) { return finite1(v) ? v : 0.0; }
float extendedScalar(uint op, float a, float b, float c, vec2 source, vec2 target) {
    if (op == 19u) return finite1(a) ? abs(a) : 0.0;
    if (op == 20u) return finite1(a) ? floor(a) : 0.0;
    if (op == 21u) return finite1(a) ? ceil(a) : 0.0;
    if (op == 22u) {
        if (!finite1(a)) return 0.0;
        float base = floor(abs(a)), fraction = abs(a) - base;
        if (fraction > 0.5 || (fraction == 0.5 && mod(base, 2.0) != 0.0)) base += 1.0;
        return a < 0.0 ? -base : base;
    }
    if (op == 23u) return finite1(a) ? a - floor(a) : 0.0;
    if (op == 24u) return finite1(a) && finite1(b) && b != 0.0 ? finiteOrZero(a - b * trunc(a / b)) : 0.0;
    if (op == 25u) return finite1(a) ? sign(a) : 0.0;
    if (op == 26u) {
        if (!finite1(a) || !finite1(b)) return 0.0;
        if (a == 0.0) return b == 0.0 ? 1.0 : 0.0;
        if (a < 0.0 && b != floor(b)) return 0.0;
        float value = finiteOrZero(pow(abs(a), b));
        return a < 0.0 && mod(abs(b), 2.0) == 1.0 ? -value : value;
    }
    if (op == 27u) return finite1(a) && a > 0.0 ? sqrt(a) : 0.0;
    if (op == 28u) return finite1(a) ? finiteOrZero(exp(a)) : 0.0;
    if (op == 29u) return finite1(a) && a > 0.0 ? log(a) : 0.0;
    if (op == 30u) return finite1(a) && finite1(b) && finite1(c) ? finiteOrZero(a + (b - a) * c) : 0.0;
    if (op == 31u) {
        if (!finite1(a) || !finite1(b) || !finite1(c) || a == b) return 0.0;
        float t = clamp(finiteOrZero(safeDiv(c - a, b - a)), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }
    if (op == 32u) {
        if (!finite1(a) || !finite1(source.x) || !finite1(source.y) ||
            !finite1(target.x) || !finite1(target.y) || source.x == source.y) return 0.0;
        return finiteOrZero(target.x + safeDiv(a - source.x, source.y - source.x) * (target.y - target.x));
    }
    return finite1(a) ? clamp(a, 0.0, 1.0) : 0.0;
}
vec3 latticeGradient(ivec3 cell, uint seed) {
    uint h = hashSeed(uint(cell.x) ^ hashSeed(uint(cell.y) ^ 0x9e3779b9u) ^
        hashSeed(uint(cell.z) ^ 0x85ebca6bu) ^ hashSeed(seed));
    uint direction = h % 6u;
    float s = (direction & 1u) == 0u ? 1.0 : -1.0;
    if (direction < 2u) return vec3(s, 0, 0);
    if (direction < 4u) return vec3(0, s, 0);
    return vec3(0, 0, s);
}
bool noiseDomain(vec3 p) {
    return finite4(vec4(p, 0)) && max(max(abs(p.x), abs(p.y)), abs(p.z)) <= 1048576.0;
}
vec4 gradientNoiseDerivative(vec3 p, uint seed) {
    if (!noiseDomain(p)) return vec4(0);
    vec3 base = floor(p), f = p - base;
    ivec3 cell = ivec3(base);
    vec3 t = f*f*f*(f*(f*6.0-15.0)+10.0);
    vec3 dt = 30.0*f*f*(f-1.0)*(f-1.0);
    float value = 0.0;
    vec3 gradient = vec3(0);
    for (int corner = 0; corner < 8; ++corner) {
        ivec3 offset = ivec3(corner & 1, (corner >> 1) & 1, (corner >> 2) & 1);
        vec3 weights = vec3(offset.x != 0 ? t.x : 1.0-t.x, offset.y != 0 ? t.y : 1.0-t.y, offset.z != 0 ? t.z : 1.0-t.z);
        vec3 deriv = vec3(offset.x != 0 ? dt.x : -dt.x, offset.y != 0 ? dt.y : -dt.y, offset.z != 0 ? dt.z : -dt.z);
        vec3 g = latticeGradient(cell + offset, seed);
        float v = dot(g, f - vec3(offset)), w = weights.x * weights.y * weights.z;
        value += w * v;
        gradient += w*g + v*vec3(deriv.x*weights.y*weights.z, weights.x*deriv.y*weights.z, weights.x*weights.y*deriv.z);
    }
    return vec4(value, gradient);
}
bool fractalDomain(vec3 p, int octaves, float gain) {
    return octaves >= 1 && octaves <= 8 && finite1(gain) && gain >= 0.0 && gain <= 1.0 &&
        noiseDomain(p) && max(max(abs(p.x), abs(p.y)), abs(p.z)) <= 1048576.0 / float(1u << uint(octaves - 1));
}
vec4 fbmDerivative(vec3 p, uint seed, int octaves, float gain) {
    if (!fractalDomain(p, octaves, gain)) return vec4(0);
    float value = 0.0, amplitude = 1.0, frequency = 1.0, weight = 0.0;
    vec3 gradient = vec3(0);
    for (int octave = 0; octave < octaves; ++octave) {
        vec4 n = gradientNoiseDerivative(p * frequency, hashSeed(seed ^ uint(octave)));
        value += amplitude*n.x; gradient += amplitude*frequency*n.yzw; weight += amplitude;
        amplitude *= gain; frequency *= 2.0;
    }
    return vec4(value/weight, gradient/weight);
}
float turbulence(vec3 p, uint seed, int octaves, float gain) {
    if (!fractalDomain(p, octaves, gain)) return 0.0;
    float value = 0.0, amplitude = 1.0, frequency = 1.0, weight = 0.0;
    for (int octave = 0; octave < octaves; ++octave) {
        value += amplitude * abs(gradientNoiseDerivative(p * frequency, hashSeed(seed ^ uint(octave))).x);
        weight += amplitude; amplitude *= gain; frequency *= 2.0;
    }
    return clamp(value / weight, 0.0, 1.0);
}
vec3 curlNoise(vec3 p, uint seed, int octaves, float gain) {
    if (!fractalDomain(p, octaves, gain)) return vec3(0);
    vec3 dx = fbmDerivative(p, hashSeed(seed ^ 0xa511e9b3u), octaves, gain).yzw;
    vec3 dy = fbmDerivative(p, hashSeed(seed ^ 0x63d83595u), octaves, gain).yzw;
    vec3 dz = fbmDerivative(p, hashSeed(seed ^ 0xb8d4c729u), octaves, gain).yzw;
    float weight = 0.0, frequencyWeight = 0.0, amplitude = 1.0, frequency = 1.0;
    for (int octave = 0; octave < octaves; ++octave) {
        weight += amplitude; frequencyWeight += amplitude * frequency; amplitude *= gain; frequency *= 2.0;
    }
    return vec3(dz.y-dy.z, dx.z-dz.x, dy.x-dx.y) * (weight / (16.0 * frequencyWeight));
}

vec2 flowTexel(uint offset, uint width, ivec2 pixel) {
    uint address = offset + (uint(pixel.y) * width + uint(pixel.x)) * 2u;
    return uintBitsToFloat(uvec2(flowData[address], flowData[address + 1u]));
}

vec2 sampleFlow(uint slot, vec2 uv) {
    uint header = slot * 4u;
    uint offset = flowData[header];
    if (offset == 0u || any(isnan(uv))) {
        return vec2(0.0);
    }
    uvec2 size = uvec2(flowData[header + 1u], flowData[header + 2u]);
    vec2 p = clamp(uv, 0.0, 1.0) * vec2(size) - 0.5;
    ivec2 last = ivec2(size) - 1;
    ivec2 lower = ivec2(floor(p));
    ivec2 a = clamp(lower, ivec2(0), last);
    ivec2 b = clamp(lower + 1, ivec2(0), last);
    vec2 t = fract(p);
    vec2 top = mix(flowTexel(offset, size.x, a), flowTexel(offset, size.x, ivec2(b.x, a.y)), t.x);
    vec2 bottom = mix(flowTexel(offset, size.x, ivec2(a.x, b.y)), flowTexel(offset, size.x, b), t.x);
    return mix(top, bottom, t.y) * 2.0 - 1.0;
}

)LGPK"
R"LGPK(
void main() {
    uint i = gl_GlobalInvocationID.x;
    if (i >= frame.range.x) {
        return;
    }
    Particle p = particles[i];
    bool spawn = code[1].y == 0u;
    if (spawn) {
        uint offset = (i + frame.range.x - frame.range.z) % frame.range.x;
        if (offset >= frame.range.w || p.seedAlive.y != 0u) {
            return;
        }
        p.positionAge = vec4(frame.emitterPosition.xyz, 0);
        p.velocityLifetime = vec4(0, 0, 0, 1);
        p.color = vec4(1);
        p.sizeRotation = vec4(1, 1, 0, 0);
        p.seedAlive = uvec4(hashSeed(i ^ frame.seed ^ hashSeed(frame.frameIndex)), 1, 0, 0);
    } else {
        if (i >= frame.range.y || p.seedAlive.y == 0u) {
            return;
        }
        if (!finiteParticle(p) || !(p.velocityLifetime.w > 0.0)) {
            particles[i].seedAlive.y = 0u;
            return;
        }
        if (code[3].x != 0u) {
            p.positionAge.w += frame.timing.x;
        }
    }
    for (uint n = 0u; n < code[0].w; ++n) {
        uint base = 4u + n * 4u;
        uvec4 first = code[base], second = code[base + 1u];
        uint op = first.x, dst = first.y, type = second.y, aux = second.z;
        uvec4 a = readRegister(first.z), b = readRegister(first.w), c = readRegister(second.x);
        uvec4 d = op == 41u && type == 5u ? uvec4(0) : readRegister(second.w);
        uvec4 result = uvec4(0);
        if (op == 0u) {
            result = code[base + (type >= 1u && type <= 3u ? 3u : 2u)];
        } else if (op == 1u) {
            result = attributeValue(p, aux);
        } else if (op == 2u) {
            vec4 value = vec4(frame.emitterPosition.xyz, 0);
            if (aux < 2u) {
                value = vec4(frame.timing[aux], 0, 0, 0);
            }
            result = floatBitsToUint(value);
        } else if ((op >= 3u && op <= 6u) || (op >= 9u && op <= 11u)) {
            if (type == 1u || type == 2u) {
                result.x = intArithmetic(op, type, a.x, b.x, c.x);
            } else {
                vec4 z = vec4(0);
                if (op == 9u) {
                    z = floatInput(c, second.x, type);
                }
                result = floatBitsToUint(floatArithmetic(op, floatInput(a, first.z, type),
                    floatInput(b, first.w, type), z));
            }
        } else if (op == 7u) {
            result = floatBitsToUint(sin(uintBitsToFloat(a)));
        } else if (op == 8u) {
            result = floatBitsToUint(cos(uintBitsToFloat(a)));
        } else if (op == 12u || op == 13u) {
            vec4 v = uintBitsToFloat(a);
            uint sourceType = code[5u + first.z * 4u].y;
            if (sourceType < 6u) {
                v.w = 0.0;
            }
            if (sourceType < 5u) {
                v.z = 0.0;
            }
            if (op == 13u) {
                result.x = floatBitsToUint(length(v));
            } else if (finite4(v)) {
                vec4 m = abs(v);
                float scale = max(max(m.x, m.y), max(m.z, m.w));
                if (scale > 0.0) {
                    v /= scale;
                    result = floatBitsToUint(v * inversesqrt(dot(v, v)));
                }
            }
        } else if (op == 14u) {
            result = uvec4(a.x, b.x, c.x, d.x);
        } else if (op == 15u) {
            result = a.x != 0u ? b : c;
        } else if (op == 16u) {
            result.x = floatBitsToUint(float(hashSeed(a.x) >> 8u) * (1.0 / 16777216.0));
        } else if (op == 17u) {
            result.x = a[aux];
        } else if (op == 18u) {
            storeAttribute(p, aux, a);
        } else if ((op >= 19u && op <= 32u) || op == 36u) {
            if (type == 1u) {
                int x = int(a.x), y = int(b.x);
                if (op == 19u) result.x = x == (-2147483647 - 1) ? a.x : uint(abs(x));
                else if (op == 25u) result.x = uint(x > 0 ? 1 : x < 0 ? -1 : 0);
                else result.x = y == 0 || (x == (-2147483647 - 1) && y == -1) ? 0u : uint(x % y);
            } else if (type == 2u) {
                if (op == 19u) result.x = a.x;
                else if (op == 25u) result.x = uint(a.x != 0u);
                else result.x = b.x == 0u ? 0u : a.x % b.x;
            } else {
                vec4 x = uintBitsToFloat(a), y = uintBitsToFloat(b), z = uintBitsToFloat(c);
                if (op == 24u || op == 26u || op == 30u || op == 31u) {
                    x = floatInput(a, first.z, type);
                    y = floatInput(b, first.w, type);
                    if (op == 30u || op == 31u) z = floatInput(c, second.x, type);
                }
                result = floatBitsToUint(vec4(
                    extendedScalar(op, x.x, y.x, z.x, uintBitsToFloat(b.xy), uintBitsToFloat(c.xy)),
                    extendedScalar(op, x.y, y.y, z.y, uintBitsToFloat(b.xy), uintBitsToFloat(c.xy)),
                    extendedScalar(op, x.z, y.z, z.z, uintBitsToFloat(b.xy), uintBitsToFloat(c.xy)),
                    extendedScalar(op, x.w, y.w, z.w, uintBitsToFloat(b.xy), uintBitsToFloat(c.xy))));
            }
        } else if (op == 33u || op == 35u) {
            vec4 x = uintBitsToFloat(a), y = uintBitsToFloat(b);
            uint sourceType = code[5u + first.z * 4u].y;
            if (sourceType < 6u) { x.w = 0.0; y.w = 0.0; }
            if (sourceType < 5u) { x.z = 0.0; y.z = 0.0; }
            if (sourceType == 0u) { x.y = 0.0; y.y = 0.0; }
            float value = 0.0;
            if (finite4(x) && finite4(y)) {
                value = finiteOrZero(op == 33u ? dot(x,y) :
                    sourceType == 0u ? abs(x.x-y.x) : length(x-y));
            }
            result.x = floatBitsToUint(value);
        } else if (op == 34u) {
            vec3 x = uintBitsToFloat(a.xyz), y = uintBitsToFloat(b.xyz);
            vec3 value = finite4(vec4(x,0)) && finite4(vec4(y,0)) ? cross(x,y) : vec3(0);
            if (!finite4(vec4(value,0))) value = vec3(0);
            result = floatBitsToUint(vec4(value, 0));
        } else if (op == 37u) {
            result.x = floatBitsToUint(clamp(gradientNoiseDerivative(uintBitsToFloat(a.xyz), b.x).x, -1.0, 1.0));
        } else if (op == 38u) {
            result.x = floatBitsToUint(clamp(fbmDerivative(uintBitsToFloat(a.xyz), b.x, int(c.x), uintBitsToFloat(d.x)).x, -1.0, 1.0));
        } else if (op == 39u) {
            result.x = floatBitsToUint(turbulence(uintBitsToFloat(a.xyz), b.x, int(c.x), uintBitsToFloat(d.x)));
        } else if (op == 40u) {
            result = floatBitsToUint(vec4(curlNoise(uintBitsToFloat(a.xyz), b.x, int(c.x), uintBitsToFloat(d.x)), 0));
        }
        if (op == 41u) {
            vec4 value = uintBitsToFloat(a);
            vec4 flow = vec4(0.0);
            if (type == 4u) {
                flow.xy = sampleFlow(aux, value.xy);
            } else {
                uint u = second.w & 3u;
                uint v = (second.w >> 2u) & 3u;
                vec4 bounds = uintBitsToFloat(code[base + 2u]);
                vec2 mapped = sampleFlow(aux, (vec2(value[u], value[v]) - bounds.xy) / bounds.zw);
                flow[u] = mapped.x;
                flow[v] = mapped.y;
            }
            result = floatBitsToUint(flow);
        }
        if (op != 18u) {
            values[dst] = result;
        }
    }
    bool valid = finiteParticle(p);
    p.positionAge.w = max(p.positionAge.w, 0.0);
    if (!valid || !(p.velocityLifetime.w > 0.0) || p.positionAge.w >= p.velocityLifetime.w) {
        p.seedAlive.y = 0u;
    }
    particles[i] = p;
}
)LGPK"
