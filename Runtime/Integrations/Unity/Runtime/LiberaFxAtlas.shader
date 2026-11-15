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
