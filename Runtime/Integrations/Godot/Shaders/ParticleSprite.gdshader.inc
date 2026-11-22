R"SPRITE(
uniform sampler2D sprite_timeline : filter_nearest, repeat_disable;
uniform bool sprite_enabled=false;
vec4 sprite_read(int i){return texelFetch(sprite_timeline,ivec2(i%256,i/256),0);}
void sprite_sample(int part,float age,out vec4 uv,out vec2 offset,out vec2 size,out float angle){
    vec4 h=sprite_read(part*3),k=sprite_read(part*3+1),flags=sprite_read(part*3+2);
    float ticks=max(0.0,age)*h.x;
    ticks=flags.x>0.5 ? mod(ticks,h.y) : min(ticks,h.y);
    int low=0;int high=int(h.w)-1;
    while(low<high){int middle=(low+high)/2;if(ticks<sprite_read(int(h.z)+middle*2).x) high=middle;else low=middle+1;}
    vec4 f=sprite_read(int(h.z)+low*2),g=sprite_read(int(h.z)+low*2+1);
    uv=vec4(f.yzw,g.x);offset=vec2(0.0);size=vec2(1.0);angle=0.0;
    if(flags.y>0.5){
        size=uv.zw;
        if(k.y>0.5){
            low=0;high=int(k.y)-1;
            while(low<high){int middle=(low+high)/2;if(ticks<=sprite_read(int(k.x)+middle*2).x) high=middle;else low=middle+1;}
            int right=int(k.x)+low*2;int left=low>0 ? right-2 : right;
            vec4 a=sprite_read(left),b=sprite_read(right);
            float t=b.x>a.x ? clamp((ticks-a.x)/(b.x-a.x),0.0,1.0) : 0.0;
            vec4 pose=mix(a,b,t);vec4 scales=mix(sprite_read(left+1),sprite_read(right+1),t);
            offset=pose.yz;angle=pose.w;size*=scales.xy;
        }
    }
}
)SPRITE"
