Shader "Libra/Procedural"
{
    Properties
    {
        _MainTex ("Generated texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Emission ("Emission", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Destination blend", Float) = 10
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
    }
    SubShader
    {
        PackageRequirements { "com.unity.render-pipelines.universal" }
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Cull [_Cull] ZWrite Off Blend SrcAlpha [_DstBlend]
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
            float4 _Color;
            float _Emission;
            CBUFFER_END
            struct Vertex { float4 position : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct Pixel { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            Pixel vert(Vertex input)
            {
                Pixel output; output.position = TransformObjectToHClip(input.position.xyz);
                output.uv = input.uv; output.color = input.color * _Color; return output;
            }
            half4 frag(Pixel input) : SV_Target
            {
                float4 value = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv) * input.color;
                return half4(value.rgb * _Emission, value.a);
            }
            ENDHLSL
        }
    }
    SubShader
    {
        PackageRequirements { "com.unity.render-pipelines.high-definition" }
        Tags { "RenderPipeline"="HDRenderPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Cull [_Cull] ZWrite Off Blend SrcAlpha [_DstBlend]
        Pass
        {
            Tags { "LightMode"="ForwardOnly" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
            float4 _Color;
            float _Emission;
            CBUFFER_END
            struct Vertex { float4 position : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct Pixel { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            Pixel vert(Vertex input)
            {
                Pixel output; output.position = TransformObjectToHClip(input.position.xyz);
                output.uv = input.uv; output.color = input.color * _Color; return output;
            }
            half4 frag(Pixel input) : SV_Target
            {
                float4 value = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv) * input.color;
                return half4(value.rgb * _Emission, value.a);
            }
            ENDHLSL
        }
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Cull [_Cull] ZWrite Off Blend SrcAlpha [_DstBlend]
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _Color;
            float _Emission;
            struct Vertex { float4 position : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct Pixel { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            Pixel vert(Vertex input)
            {
                Pixel output; output.position=UnityObjectToClipPos(input.position);
                output.uv=input.uv; output.color=input.color*_Color; return output;
            }
            half4 frag(Pixel input) : SV_Target
            {
                float4 value=tex2D(_MainTex,input.uv)*input.color;
                return float4(value.rgb*_Emission,value.a);
            }
            ENDCG
        }
    }
}
