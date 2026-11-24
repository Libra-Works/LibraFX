Shader "Libra/Generated Texture"
{
    Properties
    {
        _MainTex ("Generated texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _AtlasRect ("Atlas rect", Vector) = (0,0,1,1)
        _LibraGeneratedPremultiplied ("Premultiplied input", Float) = 0
        _LibraGeneratedFields ("Display raw field density", Float) = 0
    }
    SubShader
    {
        PackageRequirements { "com.unity.render-pipelines.universal" }
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Cull Off ZWrite Off Blend SrcAlpha OneMinusSrcAlpha
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
            float _LibraGeneratedPremultiplied, _LibraGeneratedFields;
            CBUFFER_END
            struct Vertex { float4 position : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct Pixel { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            Pixel vert(Vertex input)
            {
                Pixel output; output.position = TransformObjectToHClip(input.position.xyz);
                output.uv = _AtlasRect.xy + input.uv * _AtlasRect.zw;
                output.color = input.color * _Color;
                return output;
            }
            half4 frag(Pixel input) : SV_Target
            {
                float4 value = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                if (_LibraGeneratedFields > .5) value = float4(value.bbb, saturate(value.b));
                else if (_LibraGeneratedPremultiplied > .5) value.rgb /= max(value.a, .000001);
                return half4(value * input.color);
            }
            ENDHLSL
        }
    }
    SubShader
    {
        PackageRequirements { "com.unity.render-pipelines.high-definition" }
        Tags { "RenderPipeline"="HDRenderPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Cull Off ZWrite Off Blend SrcAlpha OneMinusSrcAlpha
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
            float _LibraGeneratedPremultiplied, _LibraGeneratedFields;
            CBUFFER_END
            struct Vertex { float4 position : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct Pixel { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            Pixel vert(Vertex input)
            {
                Pixel output; output.position = TransformObjectToHClip(input.position.xyz);
                output.uv = _AtlasRect.xy + input.uv * _AtlasRect.zw;
                output.color = input.color * _Color;
                return output;
            }
            half4 frag(Pixel input) : SV_Target
            {
                float4 value = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                if (_LibraGeneratedFields > .5) value = float4(value.bbb, saturate(value.b));
                else if (_LibraGeneratedPremultiplied > .5) value.rgb /= max(value.a, .000001);
                return half4(value * input.color);
            }
            ENDHLSL
        }
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Cull Off ZWrite Off Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _Color, _AtlasRect;
            float _LibraGeneratedPremultiplied, _LibraGeneratedFields;
            struct Vertex { float4 position : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct Pixel { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            Pixel vert(Vertex input)
            {
                Pixel output; output.position = UnityObjectToClipPos(input.position);
                output.uv = _AtlasRect.xy + input.uv * _AtlasRect.zw;
                output.color = input.color * _Color;
                return output;
            }
            half4 frag(Pixel input) : SV_Target
            {
                float4 value = tex2D(_MainTex, input.uv);
                if (_LibraGeneratedFields > .5) value = float4(value.bbb, saturate(value.b));
                else if (_LibraGeneratedPremultiplied > .5) value.rgb /= max(value.a, .000001);
                return value * input.color;
            }
            ENDCG
        }
    }
}
