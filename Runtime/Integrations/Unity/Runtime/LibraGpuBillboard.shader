Shader "Libra/GPU Particle Billboard"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        [HDR] _Color ("Color", Color) = (1,1,1,1)
        _LibraGpuSizeScale ("Libra GPU Size Scale", Float) = 1
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct Particle { float4 positionAge; float4 velocityLifetime; float4 color; float4 sizeRotation; uint4 seedAlive; };
            StructuredBuffer<Particle> _LibraParticles;
            sampler2D _MainTex;
            float4 _Color;
            float _LibraGpuSizeScale;
            struct Input { float4 vertex : POSITION; float2 uv : TEXCOORD0; uint instanceID : SV_InstanceID; };
            struct Output { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR0; };
            Output vert(Input v)
            {
                Particle p = _LibraParticles[v.instanceID];
                Output o;
                float s, c;
                sincos(p.sizeRotation.z, s, c);
                float2 local = v.vertex.xy * p.sizeRotation.xy * _LibraGpuSizeScale;
                local = float2(c * local.x - s * local.y, s * local.x + c * local.y);
                float3 right = UNITY_MATRIX_I_V._m00_m10_m20;
                float3 up = UNITY_MATRIX_I_V._m01_m11_m21;
                float3 world = p.positionAge.xyz + right * local.x + up * local.y;
                o.position = mul(UNITY_MATRIX_VP, float4(world, 1));
                o.uv = v.uv;
                o.color = p.color * _Color;
                if (p.seedAlive.y == 0 || p.velocityLifetime.w <= 0 || p.positionAge.w >= p.velocityLifetime.w)
                    o.color.a = 0;
                return o;
            }
            float4 frag(Output i) : SV_Target { return tex2D(_MainTex, i.uv) * i.color; }
            ENDCG
        }
    }
}
