Shader "Libra/Fluid 2D"
{
    Properties
    {
        _MainTex ("Dye", 2D) = "black" {}
        _LibraFluidFields ("Velocity, density/depth, temperature", 2D) = "black" {}
        _LibraFluidModel ("Simulation model", Float) = 0
        _LibraFluidInitialDepth ("Rest depth", Float) = .25
        _LibraFluidIgnition ("Ignition temperature", Float) = .5
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Cull Off ZWrite Off Blend One OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            sampler2D _LibraFluidFields;
            float4 _LibraFluidFields_TexelSize;
            float _LibraFluidModel, _LibraFluidInitialDepth, _LibraFluidIgnition;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; float3 world : TEXCOORD1; float3 normal : TEXCOORD2; };
            float depthAt(float2 uv) { return tex2Dlod(_LibraFluidFields, float4(saturate(uv), 0, 0)).b; }
            v2f vert(appdata v)
            {
                v2f o;
                float3 normal = float3(0, 0, 1);
                if (_LibraFluidModel > 1.5 && _LibraFluidModel < 2.5)
                {
                    v.vertex.y += depthAt(v.uv) - _LibraFluidInitialDepth;
                    float2 texel = _LibraFluidFields_TexelSize.xy;
                    float du = (depthAt(v.uv + float2(texel.x, 0)) - depthAt(v.uv - float2(texel.x, 0))) / max(2 * texel.x, .000001);
                    float dv = (depthAt(v.uv + float2(0, texel.y)) - depthAt(v.uv - float2(0, texel.y))) / max(2 * texel.y, .000001);
                    normal = normalize(float3(-du, 1, -dv));
                }
                o.vertex = UnityObjectToClipPos(v.vertex); o.uv = v.uv;
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.normal = UnityObjectToWorldNormal(normal);
                return o;
            }
            half4 frag(v2f i) : SV_Target
            {
                float4 dye = tex2D(_MainTex, i.uv);
                if (_LibraFluidModel < .5) return dye;
                float4 field = tex2D(_LibraFluidFields, i.uv);
                if (_LibraFluidModel > 2.5)
                {
                    float heat = max(field.a - _LibraFluidIgnition, 0);
                    float3 fire = saturate(float3(heat * 2, heat * .7, heat * .2));
                    float3 smoke = float3(.08, .08, .09);
                    return float4((smoke + fire * (1 + min(heat, 8))) * dye.a, dye.a);
                }
                float3 normal = normalize(i.normal);
                float3 view = normalize(_WorldSpaceCameraPos.xyz - i.world);
                float fresnel = .02 + .98 * pow(1 - saturate(abs(dot(view, normal))), 5);
                float3 tint = dye.rgb / max(dye.a, .000001);
                float light = .45 + .55 * saturate(dot(normal, normalize(float3(.3, .8, .5))));
                float3 water = lerp(tint * light, float3(.45, .65, .85), fresnel);
                float edge = _LibraFluidModel < 1.5 ? saturate(fwidth(field.b) * 4) : saturate(length(ddx(normal)) + length(ddy(normal)));
                float foam = saturate(edge * (.3 + length(field.rg) * .2));
                water = lerp(water, float3(.9, .96, 1), foam);
                float3 halfVector = normalize(view + normalize(float3(.3, .8, .5)));
                water += pow(saturate(dot(normal, halfVector)), 48) * .5;
                return float4(water * dye.a, dye.a);
            }
            ENDCG
        }
    }
}
