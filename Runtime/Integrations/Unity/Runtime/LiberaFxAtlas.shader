Shader "LiberaFx/Atlas Unlit"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _AtlasRect ("Atlas UV offset/scale", Vector) = (0,0,1,1)
    }
    SubShader
    {
        PackageRequirements { "com.unity.render-pipelines.universal" }
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
            float4 _Color, _AtlasRect;
            CBUFFER_END
            struct Vertex { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct Pixel { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
            Pixel vert(Vertex input)
            {
                Pixel output;
                output.position = TransformObjectToHClip(input.vertex.xyz);
                output.uv = _AtlasRect.xy + input.uv * _AtlasRect.zw;
                return output;
            }
            half4 frag(Pixel input) : SV_Target { return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv) * _Color; }
            ENDHLSL
        }
    }
    SubShader
    {
        PackageRequirements { "com.unity.render-pipelines.high-definition" }
        Tags { "RenderPipeline"="HDRenderPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
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
            float4 _Color, _AtlasRect;
            CBUFFER_END
            struct Vertex { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct Pixel { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
            Pixel vert(Vertex input)
            {
                Pixel output;
                output.position = TransformObjectToHClip(input.vertex.xyz);
                output.uv = _AtlasRect.xy + input.uv * _AtlasRect.zw;
                return output;
            }
            float4 frag(Pixel input) : SV_Target { return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv) * _Color; }
            ENDHLSL
        }
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
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _Color, _AtlasRect;
            struct Vertex { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct Pixel { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
            Pixel vert(Vertex input)
            {
                Pixel output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.uv = _AtlasRect.xy + input.uv * _AtlasRect.zw;
                return output;
            }
            float4 frag(Pixel input) : SV_Target { return tex2D(_MainTex, input.uv) * _Color; }
            ENDCG
        }
    }
}
