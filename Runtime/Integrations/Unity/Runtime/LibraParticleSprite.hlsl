// Particle sprite timeline records match liberafx/LbParticleSprite.h.
// The including shader supplies float4 LibraSpriteRead(uint index).
void LibraSpriteSample(uint part,float age,out float4 uv,out float2 offset,out float2 scale,out float angle)
{
    float4 h=LibraSpriteRead(part*3),k=LibraSpriteRead(part*3+1),flags=LibraSpriteRead(part*3+2);
    float ticks=max(0.0,age)*h.x;
    ticks=flags.x>0.5 ? fmod(ticks,h.y) : min(ticks,h.y);
    uint low=0,high=(uint)h.w-1;
    [loop] while(low<high)
    {
        uint middle=(low+high)/2;
        if(ticks<LibraSpriteRead((uint)h.z+middle*2).x) high=middle; else low=middle+1;
    }
    float4 f=LibraSpriteRead((uint)h.z+low*2),g=LibraSpriteRead((uint)h.z+low*2+1);
    uv=float4(f.yzw,g.x); offset=0;scale=1;angle=0;
    if(flags.y<0.5) return;
    scale=uv.zw;
    if(k.y<0.5) return;
    low=0;high=(uint)k.y-1;
    [loop] while(low<high)
    {
        uint middle=(low+high)/2;
        if(ticks<=LibraSpriteRead((uint)k.x+middle*2).x) high=middle;else low=middle+1;
    }
    uint right=(uint)k.x+low*2,left=low>0 ? right-2 : right;
    float4 a=LibraSpriteRead(left),b=LibraSpriteRead(right);
    float t=b.x>a.x ? saturate((ticks-a.x)/(b.x-a.x)) : 0;
    float4 pose=lerp(a,b,t),sizes=lerp(LibraSpriteRead(left+1),LibraSpriteRead(right+1),t);
    offset=pose.yz;angle=pose.w;scale*=sizes.xy;
}
