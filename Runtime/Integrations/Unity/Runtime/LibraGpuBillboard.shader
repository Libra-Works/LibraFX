Shader "Libra/GPU Particle Billboard"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _LibraGpuTexturePremultiplied ("Premultiplied texture", Float) = 0
        _LibraGpuTextureFlipY ("Flip texture Y", Float) = 0
        [HDR] _Color ("Color", Color) = (1,1,1,1)
        _LibraGpuSizeScale ("Libra GPU Size Scale", Float) = 1
        _LibraSpriteEnabled ("Sprite editor animation", Float) = 0
        _LibraGpuDrawMatrixRow0 ("Libra GPU Draw Matrix Row 0", Vector) = (1,0,0,0)
        _LibraGpuDrawMatrixRow1 ("Libra GPU Draw Matrix Row 1", Vector) = (0,1,0,0)
        _LibraGpuDrawMatrixRow2 ("Libra GPU Draw Matrix Row 2", Vector) = (0,0,1,0)
        _LibraGpuDrawMatrixRow3 ("Libra GPU Draw Matrix Row 3", Vector) = (0,0,0,1)
        [HideInInspector] _SrcBlend ("Source Blend", Float) = 5
        [HideInInspector] _DstBlend ("Destination Blend", Float) = 10
    }
    SubShader
    {
        PackageRequirements { "com.unity.render-pipelines.universal" }
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Blend [_SrcBlend] [_DstBlend]
        ZWrite Off
        Cull Off
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct Particle { float4 positionAge; float4 velocityLifetime; float4 color; float4 sizeRotation; uint4 seedAlive; };
            StructuredBuffer<Particle> _LibraParticles;
            StructuredBuffer<float4> _LibraSpriteData;
            float _LibraSpriteEnabled;
            float4 LibraSpriteRead(uint index) { return _LibraSpriteData[index]; }
            #include "LibraParticleSprite.hlsl"
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            float4 _Color;
            float _LibraGpuTexturePremultiplied, _LibraGpuTextureFlipY;
            float _LibraGpuSizeScale;
            float4 _LibraGpuDrawMatrixRow0, _LibraGpuDrawMatrixRow1;
            float4 _LibraGpuDrawMatrixRow2, _LibraGpuDrawMatrixRow3;
            struct Input { float4 vertex : POSITION; float2 uv : TEXCOORD0; float2 part : TEXCOORD1; uint instanceID : SV_InstanceID; };
            struct Output { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR0; };
            Output vert(Input v)
            {
                Particle p = _LibraParticles[v.instanceID];
                Output o;
                float s, c;
                float4 spriteUv=float4(0,0,1,1);float2 spriteOffset=0,spriteScale=1;float spriteAngle=0;
                if (_LibraSpriteEnabled>0.5) LibraSpriteSample((uint)v.part.x,p.positionAge.w,spriteUv,spriteOffset,spriteScale,spriteAngle);
                float cs=cos(spriteAngle),ss=sin(spriteAngle);
                float2 corner=v.vertex.xy*spriteScale*p.sizeRotation.xy;
                corner=float2(cs*corner.x-ss*corner.y,ss*corner.x+cs*corner.y)+spriteOffset*p.sizeRotation.xy;
                sincos(p.sizeRotation.z, s, c);
                float2 local = corner * _LibraGpuSizeScale;
                local = float2(c * local.x - s * local.y, s * local.x + c * local.y);
                float3 right = UNITY_MATRIX_I_V._m00_m10_m20;
                float3 up = UNITY_MATRIX_I_V._m01_m11_m21;
                float4 position = float4(p.positionAge.xyz, 1);
                float3 center = float3(dot(_LibraGpuDrawMatrixRow0, position),
                    dot(_LibraGpuDrawMatrixRow1, position), dot(_LibraGpuDrawMatrixRow2, position));
                float sx = length(float3(_LibraGpuDrawMatrixRow0.x, _LibraGpuDrawMatrixRow1.x, _LibraGpuDrawMatrixRow2.x));
                float sy = length(float3(_LibraGpuDrawMatrixRow0.y, _LibraGpuDrawMatrixRow1.y, _LibraGpuDrawMatrixRow2.y));
                float3 offset = right * local.x * sx + up * local.y * sy;
                float3 world = center + offset;
                o.position = mul(UNITY_MATRIX_VP, float4(world, 1));
                o.uv = float2(spriteUv.x+v.uv.x*spriteUv.z,1.0-spriteUv.y-(1.0-v.uv.y)*spriteUv.w);
                o.color = p.color * _Color;
                if (p.seedAlive.y == 0 || p.velocityLifetime.w <= 0 || p.positionAge.w >= p.velocityLifetime.w)
                    o.color.a = 0;
                return o;
            }
            half4 frag(Output i) : SV_Target
            {
                float2 uv = i.uv;
                if (_LibraGpuTextureFlipY > 0.5) uv.y = 1.0 - uv.y;
                float4 texel = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
                if (_LibraGpuTexturePremultiplied > 0.5) texel.rgb /= max(texel.a, 0.000001);
                return half4(texel * i.color);
            }
            ENDHLSL
        }
    }
    SubShader
    {
        PackageRequirements { "com.unity.render-pipelines.high-definition" }
        Tags { "RenderPipeline"="HDRenderPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Blend [_SrcBlend] [_DstBlend]
        ZWrite Off
        Cull Off
        Pass
        {
            Tags { "LightMode"="ForwardOnly" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
            struct Particle { float4 positionAge; float4 velocityLifetime; float4 color; float4 sizeRotation; uint4 seedAlive; };
            StructuredBuffer<Particle> _LibraParticles;
            StructuredBuffer<float4> _LibraSpriteData;
            float _LibraSpriteEnabled;
            float4 LibraSpriteRead(uint index) { return _LibraSpriteData[index]; }
            #include "LibraParticleSprite.hlsl"
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            float4 _Color;
            float _LibraGpuTexturePremultiplied, _LibraGpuTextureFlipY;
            float _LibraGpuSizeScale;
            float4 _LibraGpuDrawMatrixRow0, _LibraGpuDrawMatrixRow1;
            float4 _LibraGpuDrawMatrixRow2, _LibraGpuDrawMatrixRow3;
            struct Input { float4 vertex : POSITION; float2 uv : TEXCOORD0; float2 part : TEXCOORD1; uint instanceID : SV_InstanceID; };
            struct Output { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR0; };
            Output vert(Input v)
            {
                Particle p = _LibraParticles[v.instanceID];
                Output o;
                float s, c;
                float4 spriteUv=float4(0,0,1,1);float2 spriteOffset=0,spriteScale=1;float spriteAngle=0;
                if (_LibraSpriteEnabled>0.5) LibraSpriteSample((uint)v.part.x,p.positionAge.w,spriteUv,spriteOffset,spriteScale,spriteAngle);
                float cs=cos(spriteAngle),ss=sin(spriteAngle);
                float2 corner=v.vertex.xy*spriteScale*p.sizeRotation.xy;
                corner=float2(cs*corner.x-ss*corner.y,ss*corner.x+cs*corner.y)+spriteOffset*p.sizeRotation.xy;
                sincos(p.sizeRotation.z, s, c);
                float2 local = corner * _LibraGpuSizeScale;
                local = float2(c * local.x - s * local.y, s * local.x + c * local.y);
                float3 right = UNITY_MATRIX_I_V._m00_m10_m20;
                float3 up = UNITY_MATRIX_I_V._m01_m11_m21;
                float4 position = float4(p.positionAge.xyz, 1);
                float3 center = float3(dot(_LibraGpuDrawMatrixRow0, position),
                    dot(_LibraGpuDrawMatrixRow1, position), dot(_LibraGpuDrawMatrixRow2, position));
                float sx = length(float3(_LibraGpuDrawMatrixRow0.x, _LibraGpuDrawMatrixRow1.x, _LibraGpuDrawMatrixRow2.x));
                float sy = length(float3(_LibraGpuDrawMatrixRow0.y, _LibraGpuDrawMatrixRow1.y, _LibraGpuDrawMatrixRow2.y));
                float3 offset = right * local.x * sx + up * local.y * sy;
                float3 world = center + offset;
                o.position = mul(UNITY_MATRIX_VP, float4(world, 1));
                o.uv = float2(spriteUv.x+v.uv.x*spriteUv.z,1.0-spriteUv.y-(1.0-v.uv.y)*spriteUv.w);
                o.color = p.color * _Color;
                if (p.seedAlive.y == 0 || p.velocityLifetime.w <= 0 || p.positionAge.w >= p.velocityLifetime.w)
                    o.color.a = 0;
                return o;
            }
            half4 frag(Output i) : SV_Target
            {
                float2 uv = i.uv;
                if (_LibraGpuTextureFlipY > 0.5) uv.y = 1.0 - uv.y;
                float4 texel = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
                if (_LibraGpuTexturePremultiplied > 0.5) texel.rgb /= max(texel.a, 0.000001);
                return half4(texel * i.color);
            }
            ENDHLSL
        }
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Blend [_SrcBlend] [_DstBlend]
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
            StructuredBuffer<float4> _LibraSpriteData;
            float _LibraSpriteEnabled;
            float4 LibraSpriteRead(uint index) { return _LibraSpriteData[index]; }
            #include "LibraParticleSprite.hlsl"
            sampler2D _MainTex;
            float4 _Color;
            float _LibraGpuTexturePremultiplied, _LibraGpuTextureFlipY;
            float _LibraGpuSizeScale;
            float4 _LibraGpuDrawMatrixRow0, _LibraGpuDrawMatrixRow1;
            float4 _LibraGpuDrawMatrixRow2, _LibraGpuDrawMatrixRow3;
            struct Input { float4 vertex : POSITION; float2 uv : TEXCOORD0; float2 part : TEXCOORD1; uint instanceID : SV_InstanceID; };
            struct Output { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR0; };
            Output vert(Input v)
            {
                Particle p = _LibraParticles[v.instanceID];
                Output o;
                float s, c;
                float4 spriteUv=float4(0,0,1,1);float2 spriteOffset=0,spriteScale=1;float spriteAngle=0;
                if (_LibraSpriteEnabled>0.5) LibraSpriteSample((uint)v.part.x,p.positionAge.w,spriteUv,spriteOffset,spriteScale,spriteAngle);
                float cs=cos(spriteAngle),ss=sin(spriteAngle);
                float2 corner=v.vertex.xy*spriteScale*p.sizeRotation.xy;
                corner=float2(cs*corner.x-ss*corner.y,ss*corner.x+cs*corner.y)+spriteOffset*p.sizeRotation.xy;
                sincos(p.sizeRotation.z, s, c);
                float2 local = corner * _LibraGpuSizeScale;
                local = float2(c * local.x - s * local.y, s * local.x + c * local.y);
                float3 right = UNITY_MATRIX_I_V._m00_m10_m20;
                float3 up = UNITY_MATRIX_I_V._m01_m11_m21;
                float4 position = float4(p.positionAge.xyz, 1);
                float3 center = float3(dot(_LibraGpuDrawMatrixRow0, position),
                    dot(_LibraGpuDrawMatrixRow1, position), dot(_LibraGpuDrawMatrixRow2, position));
                float sx = length(float3(_LibraGpuDrawMatrixRow0.x, _LibraGpuDrawMatrixRow1.x, _LibraGpuDrawMatrixRow2.x));
                float sy = length(float3(_LibraGpuDrawMatrixRow0.y, _LibraGpuDrawMatrixRow1.y, _LibraGpuDrawMatrixRow2.y));
                float3 offset = right * local.x * sx + up * local.y * sy;
                float3 world = center + offset;
                o.position = mul(UNITY_MATRIX_VP, float4(world, 1));
                o.uv = float2(spriteUv.x+v.uv.x*spriteUv.z,1.0-spriteUv.y-(1.0-v.uv.y)*spriteUv.w);
                o.color = p.color * _Color;
                if (p.seedAlive.y == 0 || p.velocityLifetime.w <= 0 || p.positionAge.w >= p.velocityLifetime.w)
                    o.color.a = 0;
                return o;
            }
            float4 frag(Output i) : SV_Target
            {
                float2 uv = i.uv;
                if (_LibraGpuTextureFlipY > 0.5) uv.y = 1.0 - uv.y;
                float4 texel = tex2D(_MainTex, uv);
                if (_LibraGpuTexturePremultiplied > 0.5) texel.rgb /= max(texel.a, 0.000001);
                return texel * i.color;
            }
            ENDCG
        }
    }
}
