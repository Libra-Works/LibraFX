#pragma once
namespace liberafx::godot_adapter::procedural_shader {
inline constexpr auto source = R"PFXGLSL(
#version 450
struct PixelOperation { uint kind, inputA, inputB, seed; vec4 p, q, r; };
layout(set=0,binding=0,std430) readonly buffer Operations { PixelOperation _Operations[]; };
layout(set=0,binding=1,rgba16f) uniform writeonly image2D _Output;
layout(set=0,binding=2) uniform sampler2D _Texture0;
layout(set=0,binding=3) uniform sampler2D _Texture1;
layout(set=0,binding=4) uniform sampler2D _Texture2;
layout(set=0,binding=5) uniform sampler2D _Texture3;
layout(push_constant,std430) uniform Parameters { uint count, outputRegister, resolution; float time; } pc;
#define _OperationCount pc.count
#define _OutputRegister pc.outputRegister
#define _Resolution pc.resolution
#define _LocalTime pc.time
float saturate(float x) { return clamp(x,0.0,1.0); }
vec4 saturate(vec4 x) { return clamp(x,vec4(0),vec4(1)); }
uint phash(ivec2 p, uint seed)
{
    uint h = (uint(p.x) & 4095u) * 0x8da6b343u ^ (uint(p.y) & 4095u) * 0xd8163841u ^ seed * 0x9e3779b9u;
    h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu;
    return h ^ (h >> 16);
}
bool pfinite(vec4 value)
{
    uvec4 bits=floatBitsToUint(value);
    return (bits.x & 0x7f800000u)!=0x7f800000u && (bits.y & 0x7f800000u)!=0x7f800000u &&
        (bits.z & 0x7f800000u)!=0x7f800000u && (bits.w & 0x7f800000u)!=0x7f800000u;
}
float punit(uint h) { return float(h >> 8) / 16777215.0; }
vec2 pfade(vec2 t) { return t*t*t*(t*(t*6-15)+10); }
float pvalue(vec2 p, uint seed)
{
    p = p - floor(p / 4096.0) * 4096.0;
    ivec2 i = ivec2(floor(p)); vec2 f = pfade(fract(p));
    return mix(mix(punit(phash(i,seed)),punit(phash(i+ivec2(1,0),seed)),f.x),
        mix(punit(phash(i+ivec2(0,1),seed)),punit(phash(i+ivec2(1,1),seed)),f.x),f.y);
}
vec2 pgradient(uint h)
{
    switch(h & 7u)
    {
    case 0: return vec2(1,0); case 1: return vec2(-1,0); case 2: return vec2(0,1); case 3: return vec2(0,-1);
    case 4: return vec2(.70710678118,.70710678118); case 5: return vec2(-.70710678118,.70710678118);
    case 6: return vec2(.70710678118,-.70710678118); default: return vec2(-.70710678118,-.70710678118);
    }
}
float pgrad(vec2 p, uint seed)
{
    p = p - floor(p / 4096.0) * 4096.0;
    ivec2 i = ivec2(floor(p)); vec2 f=fract(p), t=pfade(f);
    return saturate(.5+.70710678118*mix(
        mix(dot(pgradient(phash(i,seed)),f),dot(pgradient(phash(i+ivec2(1,0),seed)),f-vec2(1,0)),t.x),
        mix(dot(pgradient(phash(i+ivec2(0,1),seed)),f-vec2(0,1)),dot(pgradient(phash(i+ivec2(1,1),seed)),f-1),t.x),t.y));
}
float pcell(vec2 p, uint seed, bool edge)
{
    p = p - floor(p / 4096.0) * 4096.0;
    ivec2 i=ivec2(floor(p)); float nearest=16,second=16;
     for(int y=-1;y<=1;++y)
    {
         for(int x=-1;x<=1;++x)
        {
            ivec2 cell=i+ivec2(x,y);
            vec2 delta=vec2(cell)+vec2(punit(phash(cell,seed)),punit(phash(cell,seed^0x68bc21ebu)))-p;
            float d=dot(delta,delta);
            if(d<nearest) { second=nearest; nearest=d; } else if(d<second) { second=d; }
        }
    }
    return saturate(edge?sqrt(second)-sqrt(nearest):sqrt(nearest));
}
float psmooth(float lo,float hi,float value)
{
    float t=hi==lo?(value>=hi?1:0):saturate((value-lo)/(hi-lo)); return t*t*(3-2*t);
}
float pfractal(vec2 uv,PixelOperation op,bool ridged)
{
    float result=0,weight=1,total=0;
     for(uint i=0;i<min(uint(op.q.x),8u);++i)
    {
        float v=pvalue(uv,op.seed+i*0x9e3779b9u);
        if(ridged) v=1-abs(2*v-1);
        result+=weight*v; total+=weight; weight*=op.q.y; uv*=2;
    }
    return result/max(total,1e-6);
}
layout(local_size_x=8,local_size_y=8,local_size_z=1) in;
void main()
{
    uvec3 id=gl_GlobalInvocationID; if(any(greaterThanEqual(id.xy,uvec2(_Resolution)))) return;
    vec4 regs[33]; regs[0]=vec4((vec2(id.xy)+.5)/_Resolution,0,1);
     for(uint i=0;i<min(_OperationCount,32u);++i)
    {
        PixelOperation op=_Operations[i];
        vec4 a=regs[min(op.inputA,i)],b=regs[min(op.inputB,i)],v=a;
        vec2 uv=a.xy*op.p.xy+_LocalTime*op.p.zw;
        switch(op.kind)
        {
        case 0: v=regs[0]; break;
        case 1: v=op.p; break;
        case 2: v=vec4(_LocalTime); break;
        case 3:
        {
            float angle=op.q.x*.0174532925199433;
            vec2 p=(a.xy-op.q.yz)*op.p.xy;
            v.xy=vec2(cos(angle)*p.x-sin(angle)*p.y,sin(angle)*p.x+cos(angle)*p.y)+op.q.yz+op.p.zw;
            break;
        }
        case 4: v=vec4(dot(a.xy,op.p.xy)+op.p.z); break;
        case 5: v=vec4(length(a.xy-op.p.xy)*op.p.z); break;
        case 6: v=vec4(1-psmooth(op.p.w,op.p.w+max(op.q.x,1e-6),abs(length(a.xy-op.p.xy)-op.p.z))); break;
        case 7: v=vec4(.5+.5*sin((dot(a.xy,op.p.xy)+op.p.z)*6.28318530718)); break;
        case 8: v=vec4(mod(abs(floor(a.x*op.p.x)+floor(a.y*op.p.y)),2)); break;
        case 9: v=vec4(pvalue(uv,op.seed)); break;
        case 10: v=vec4(pgrad(uv,op.seed)); break;
        case 11: v=vec4(pcell(uv,op.seed,false)); break;
        case 12: v=vec4(pcell(uv,op.seed,true)); break;
        case 13: v=vec4(pfractal(uv,op,false)); break;
        case 14: v=vec4(pfractal(uv,op,true)); break;
        case 15: v=vec4((pvalue(uv+vec2(0,.01),op.seed)-pvalue(uv-vec2(0,.01),op.seed))/.02,
            -(pvalue(uv+vec2(.01,0),op.seed)-pvalue(uv-vec2(.01,0),op.seed))/.02,0,1); break;
        case 16: v.xy=a.xy+(b.xy-op.p.yy)*op.p.x; break;
        case 17: v=a+b; break;
        case 18: v=a*b; break;
        case 19: v=a-b; break;
        case 20: v=min(a,b); break;
        case 21: v=max(a,b); break;
        case 22: v=mix(a,b,op.p.x); break;
        case 23: v=pow(max(a,vec4(1e-6)),vec4(clamp(op.p.x,-32.0,32.0))); break;
        case 24: v=saturate(a); break;
        case 25: v=1-a; break;
        case 26: v=vec4(psmooth(op.p.x,op.p.y,a.x),psmooth(op.p.x,op.p.y,a.y),psmooth(op.p.x,op.p.y,a.z),psmooth(op.p.x,op.p.y,a.w)); break;
        case 27: v=mix(op.p,op.q,psmooth(op.r.x,op.r.y,a.x)); break;
        case 28: v=vec4(a[min(uint(op.p.x),3u)]); break;
        case 29: v.w=a.w*b.x; break;
        case 30:
            switch(uint(op.p.x))
            {
            case 0: v=textureLod(_Texture0,a.xy,0.0); break;
            case 1: v=textureLod(_Texture1,a.xy,0.0); break;
            case 2: v=textureLod(_Texture2,a.xy,0.0); break;
            default: v=textureLod(_Texture3,a.xy,0.0); break;
            }
            break;
        default: break;
        }
        // Half-float storage has a finite range, even when intermediate arithmetic overflows.
        regs[i+1]=pfinite(v)?clamp(v,vec4(-65504.0),vec4(65504.0)):vec4(0,0,0,0);
    }
    imageStore(_Output,ivec2(id.xy),regs[min(_OutputRegister,_OperationCount)]);
}

)PFXGLSL";
}
