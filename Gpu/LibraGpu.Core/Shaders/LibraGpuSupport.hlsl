#ifndef LIBRA_GPU_SUPPORT_INCLUDED
#define LIBRA_GPU_SUPPORT_INCLUDED

// Original engine-neutral algorithms. No native bindings or external includes.
uint LibraGpuHash(uint x) { x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; return x ^ (x >> 16); }
float LibraGpuRandom(uint x) { return float(LibraGpuHash(x) >> 8) * (1.0f / 16777216.0f); }
bool LibraGpuIsFinite(float a) { return (asuint(a) & 0x7f800000u) != 0x7f800000u; }
bool LibraGpuIsFinite(float2 a) { return all((asuint(a) & 0x7f800000u) != 0x7f800000u); }
bool LibraGpuIsFinite(float3 a) { return all((asuint(a) & 0x7f800000u) != 0x7f800000u); }
bool LibraGpuIsFinite(float4 a) { return all((asuint(a) & 0x7f800000u) != 0x7f800000u); }
float LibraGpuFiniteOrZero(float a) { return LibraGpuIsFinite(a) ? a : 0.0f; }
float3 LibraGpuFiniteOrZero(float3 a) { return LibraGpuIsFinite(a) ? a : (float3)0; }
int LibraGpuDiv(int a, int b) { if (b == 0) return 0; if (a == (-2147483647 - 1) && b == -1) return (-2147483647 - 1); return a / b; }
uint LibraGpuDiv(uint a, uint b) { return b == 0u ? 0u : a / b; }
float LibraGpuDiv(float a, float b) { return b == 0.0f ? 0.0f : a / b; }
float2 LibraGpuDiv(float2 a, float2 b) { return float2(LibraGpuDiv(a.x,b.x),LibraGpuDiv(a.y,b.y)); }
float3 LibraGpuDiv(float3 a, float3 b) { return float3(LibraGpuDiv(a.x,b.x),LibraGpuDiv(a.y,b.y),LibraGpuDiv(a.z,b.z)); }
float4 LibraGpuDiv(float4 a, float4 b) { return float4(LibraGpuDiv(a.x,b.x),LibraGpuDiv(a.y,b.y),LibraGpuDiv(a.z,b.z),LibraGpuDiv(a.w,b.w)); }
float2 LibraGpuNormalize(float2 a) { if (!LibraGpuIsFinite(a)) return (float2)0; float m=max(abs(a.x),abs(a.y)); if (!(m>0.0f)) return (float2)0; float2 s=a/m; return s*rsqrt(dot(s,s)); }
float3 LibraGpuNormalize(float3 a) { if (!LibraGpuIsFinite(a)) return (float3)0; float m=max(max(abs(a.x),abs(a.y)),abs(a.z)); if (!(m>0.0f)) return (float3)0; float3 s=a/m; return s*rsqrt(dot(s,s)); }
float4 LibraGpuNormalize(float4 a) { if (!LibraGpuIsFinite(a)) return (float4)0; float m=max(max(max(abs(a.x),abs(a.y)),abs(a.z)),abs(a.w)); if (!(m>0.0f)) return (float4)0; float4 s=a/m; return s*rsqrt(dot(s,s)); }

float LibraGpuAbs(float a) { return LibraGpuIsFinite(a) ? abs(a) : 0.0f; }
int LibraGpuAbs(int a) { return a == (-2147483647 - 1) ? a : abs(a); }
uint LibraGpuAbs(uint a) { return a; }
float LibraGpuFloor(float a) { return LibraGpuIsFinite(a) ? floor(a) : 0.0f; }
float LibraGpuCeil(float a) { return LibraGpuIsFinite(a) ? ceil(a) : 0.0f; }
float LibraGpuFrac(float a) { return LibraGpuIsFinite(a) ? a-floor(a) : 0.0f; }
float LibraGpuSign(float a) { return LibraGpuIsFinite(a) ? (a>0.0f ? 1.0f : a<0.0f ? -1.0f : 0.0f) : 0.0f; }
int LibraGpuSign(int a) { return a>0 ? 1 : a<0 ? -1 : 0; }
uint LibraGpuSign(uint a) { return a>0u ? 1u : 0u; }
float LibraGpuRound(float a)
{
    if (!LibraGpuIsFinite(a)) return 0.0f;
    float b=floor(abs(a)), f=abs(a)-b;
    if (f>0.5f || (f==0.5f && fmod(b,2.0f)!=0.0f)) b+=1.0f;
    return a<0.0f ? -b : b;
}
int LibraGpuMod(int a, int b) { if (b==0 || (a==(-2147483647 - 1) && b==-1)) return 0; return a%b; }
uint LibraGpuMod(uint a, uint b) { return b==0u ? 0u : a%b; }
float LibraGpuMod(float a, float b) { if (!LibraGpuIsFinite(a) || !LibraGpuIsFinite(b) || b==0.0f) return 0.0f; return LibraGpuFiniteOrZero(fmod(a,b)); }
float LibraGpuSqrt(float a) { return LibraGpuIsFinite(a) && a>0.0f ? sqrt(a) : 0.0f; }
float LibraGpuExp(float a) { return LibraGpuIsFinite(a) ? LibraGpuFiniteOrZero(exp(a)) : 0.0f; }
float LibraGpuLog(float a) { return LibraGpuIsFinite(a) && a>0.0f ? log(a) : 0.0f; }
float LibraGpuPow(float a, float b)
{
    if (!LibraGpuIsFinite(a) || !LibraGpuIsFinite(b)) return 0.0f;
    if (a==0.0f) return b==0.0f ? 1.0f : 0.0f;
    if (a<0.0f && b!=floor(b)) return 0.0f;
    float v=LibraGpuFiniteOrZero(pow(abs(a),b));
    return a<0.0f && fmod(abs(b),2.0f)==1.0f ? -v : v;
}
float LibraGpuLerp(float a, float b, float amount)
{
    if (!LibraGpuIsFinite(a) || !LibraGpuIsFinite(b) || !LibraGpuIsFinite(amount)) return 0.0f;
    return LibraGpuFiniteOrZero(a+(b-a)*amount);
}
float LibraGpuSaturate(float a) { return LibraGpuIsFinite(a) ? clamp(a,0.0f,1.0f) : 0.0f; }
float LibraGpuSmoothstep(float edge0, float edge1, float a)
{
    if (!LibraGpuIsFinite(edge0) || !LibraGpuIsFinite(edge1) || !LibraGpuIsFinite(a) || edge0==edge1) return 0.0f;
    float t=LibraGpuSaturate(LibraGpuFiniteOrZero(LibraGpuDiv(a-edge0,edge1-edge0)));
    return t*t*(3.0f-2.0f*t);
}
float LibraGpuRemap(float a, float2 sourceRange, float2 targetRange)
{
    if (!LibraGpuIsFinite(a) || !LibraGpuIsFinite(sourceRange) || !LibraGpuIsFinite(targetRange) || sourceRange.x==sourceRange.y) return 0.0f;
    float t=LibraGpuDiv(a-sourceRange.x,sourceRange.y-sourceRange.x);
    return LibraGpuFiniteOrZero(targetRange.x+t*(targetRange.y-targetRange.x));
}
float LibraGpuDot(float2 a, float2 b) { return LibraGpuIsFinite(a) && LibraGpuIsFinite(b) ? LibraGpuFiniteOrZero(dot(a,b)) : 0.0f; }
float LibraGpuDot(float3 a, float3 b) { return LibraGpuIsFinite(a) && LibraGpuIsFinite(b) ? LibraGpuFiniteOrZero(dot(a,b)) : 0.0f; }
float LibraGpuDot(float4 a, float4 b) { return LibraGpuIsFinite(a) && LibraGpuIsFinite(b) ? LibraGpuFiniteOrZero(dot(a,b)) : 0.0f; }
float3 LibraGpuCross(float3 a, float3 b) { return LibraGpuIsFinite(a) && LibraGpuIsFinite(b) ? LibraGpuFiniteOrZero(cross(a,b)) : (float3)0; }
float LibraGpuDistance(float a, float b) { return LibraGpuIsFinite(a) && LibraGpuIsFinite(b) ? LibraGpuFiniteOrZero(abs(a-b)) : 0.0f; }
float LibraGpuDistance(float2 a, float2 b) { return LibraGpuIsFinite(a) && LibraGpuIsFinite(b) ? LibraGpuFiniteOrZero(length(a-b)) : 0.0f; }
float LibraGpuDistance(float3 a, float3 b) { return LibraGpuIsFinite(a) && LibraGpuIsFinite(b) ? LibraGpuFiniteOrZero(length(a-b)) : 0.0f; }
float LibraGpuDistance(float4 a, float4 b) { return LibraGpuIsFinite(a) && LibraGpuIsFinite(b) ? LibraGpuFiniteOrZero(length(a-b)) : 0.0f; }

// Gradient lattice uses six unit axis directions; convex interpolation bounds value to [-1,1].
// Quintic interpolation and its exact derivative give a C2 scalar potential.
// Interpreters inline many noise opcodes in one shader; unrolling nested loops can overwhelm FXC.
#if defined(LIBRA_GPU_INTERPRETER)
#define LIBRA_GPU_NOISE_LOOP [loop]
#define LIBRA_GPU_OCTAVE_LIMIT octaves
#else
#define LIBRA_GPU_NOISE_LOOP [unroll]
#define LIBRA_GPU_OCTAVE_LIMIT 8
#endif

float3 LibraGpuLatticeGradient(int3 cell, uint seed)
{
    uint h=LibraGpuHash(uint(cell.x)^LibraGpuHash(uint(cell.y)^0x9e3779b9u)^LibraGpuHash(uint(cell.z)^0x85ebca6bu)^LibraGpuHash(seed));
    uint direction=h%6u;
    float signValue=(direction&1u)==0u ? 1.0f : -1.0f;
    if (direction<2u) return float3(signValue,0.0f,0.0f);
    if (direction<4u) return float3(0.0f,signValue,0.0f);
    return float3(0.0f,0.0f,signValue);
}
bool LibraGpuNoiseDomain(float3 p) { return LibraGpuIsFinite(p) && max(max(abs(p.x),abs(p.y)),abs(p.z))<=1048576.0f; }
float4 LibraGpuGradientNoiseDerivative3D(float3 p, uint seed)
{
    if (!LibraGpuNoiseDomain(p)) return (float4)0;
    float3 baseCell=floor(p), f=p-baseCell;
    int3 cell=(int3)baseCell;
    float3 t=f*f*f*(f*(f*6.0f-15.0f)+10.0f);
    float3 dt=30.0f*f*f*(f-1.0f)*(f-1.0f);
    float value=0.0f; float3 gradient=(float3)0;
    LIBRA_GPU_NOISE_LOOP for (int corner=0;corner<8;corner++)
    {
        int3 offset=int3(corner&1,(corner>>1)&1,(corner>>2)&1);
        float3 weights=float3(offset.x!=0?t.x:1.0f-t.x,offset.y!=0?t.y:1.0f-t.y,offset.z!=0?t.z:1.0f-t.z);
        float3 derivatives=float3(offset.x!=0?dt.x:-dt.x,offset.y!=0?dt.y:-dt.y,offset.z!=0?dt.z:-dt.z);
        float3 g=LibraGpuLatticeGradient(cell+offset,seed);
        float v=dot(g,f-(float3)offset), w=weights.x*weights.y*weights.z;
        value+=w*v;
        gradient+=w*g+v*float3(derivatives.x*weights.y*weights.z,weights.x*derivatives.y*weights.z,weights.x*weights.y*derivatives.z);
    }
    return float4(value,gradient);
}
float LibraGpuGradientNoise3D(float3 p, uint seed) { return clamp(LibraGpuGradientNoiseDerivative3D(p,seed).x,-1.0f,1.0f); }
bool LibraGpuFractalDomain(float3 p, int octaves, float gain)
{
    if (octaves<1 || octaves>8 || !LibraGpuIsFinite(gain) || gain<0.0f || gain>1.0f || !LibraGpuNoiseDomain(p)) return false;
    float highestFrequency=float(1u<<uint(octaves-1));
    return max(max(abs(p.x),abs(p.y)),abs(p.z))<=1048576.0f/highestFrequency;
}
float4 LibraGpuFbmDerivative3D(float3 p, uint seed, int octaves, float gain)
{
    if (!LibraGpuFractalDomain(p,octaves,gain)) return (float4)0;
    float value=0.0f, amplitude=1.0f, frequency=1.0f, weight=0.0f; float3 gradient=(float3)0;
    LIBRA_GPU_NOISE_LOOP for (int octave=0;octave<LIBRA_GPU_OCTAVE_LIMIT;octave++)
    {
        if (octave<octaves)
        {
            float4 n=LibraGpuGradientNoiseDerivative3D(p*frequency,LibraGpuHash(seed^uint(octave)));
            value+=amplitude*n.x; gradient+=amplitude*frequency*n.yzw; weight+=amplitude;
            amplitude*=gain; frequency*=2.0f;
        }
    }
    return float4(value/weight,gradient/weight);
}
float LibraGpuFbm(float3 p, uint seed, int octaves, float gain) { return clamp(LibraGpuFbmDerivative3D(p,seed,octaves,gain).x,-1.0f,1.0f); }
float LibraGpuTurbulence(float3 p, uint seed, int octaves, float gain)
{
    if (!LibraGpuFractalDomain(p,octaves,gain)) return 0.0f;
    float value=0.0f, amplitude=1.0f, frequency=1.0f, weight=0.0f;
    LIBRA_GPU_NOISE_LOOP for (int octave=0;octave<LIBRA_GPU_OCTAVE_LIMIT;octave++)
    {
        if (octave<octaves)
        {
            value+=amplitude*abs(LibraGpuGradientNoiseDerivative3D(p*frequency,LibraGpuHash(seed^uint(octave))).x);
            weight+=amplitude; amplitude*=gain; frequency*=2.0f;
        }
    }
    return clamp(value/weight,0.0f,1.0f);
}
float3 LibraGpuCurlNoise(float3 p, uint seed, int octaves, float gain)
{
    if (!LibraGpuFractalDomain(p,octaves,gain)) return (float3)0;
    float3 dx=LibraGpuFbmDerivative3D(p,LibraGpuHash(seed^0xa511e9b3u),octaves,gain).yzw;
    float3 dy=LibraGpuFbmDerivative3D(p,LibraGpuHash(seed^0x63d83595u),octaves,gain).yzw;
    float3 dz=LibraGpuFbmDerivative3D(p,LibraGpuHash(seed^0xb8d4c729u),octaves,gain).yzw;
    float weight=0.0f, frequencyWeight=0.0f, amplitude=1.0f, frequency=1.0f;
    LIBRA_GPU_NOISE_LOOP for (int octave=0;octave<LIBRA_GPU_OCTAVE_LIMIT;octave++)
    {
        if (octave<octaves) { weight+=amplitude; frequencyWeight+=amplitude*frequency; amplitude*=gain; frequency*=2.0f; }
    }
    // Gradient components are bounded by 4.75; using 8 gives conservative roundoff margin.
    // This parameter-only scale (not a position-dependent vector normalization) preserves div(curl)=0.
    return float3(dz.y-dy.z,dx.z-dz.x,dy.x-dx.y)*(weight/(16.0f*frequencyWeight));
}
#undef LIBRA_GPU_OCTAVE_LIMIT
#undef LIBRA_GPU_NOISE_LOOP
#endif
