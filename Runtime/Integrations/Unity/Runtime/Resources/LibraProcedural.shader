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
