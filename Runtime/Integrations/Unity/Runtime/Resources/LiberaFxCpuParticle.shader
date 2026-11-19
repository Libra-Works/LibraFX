Shader "LiberaFx/CPU Particle"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        [HideInInspector] _SrcBlend ("Source Blend", Float) = 5
        [HideInInspector] _DstBlend ("Destination Blend", Float) = 10
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "DisableBatching"="True" }
        Blend [_SrcBlend] [_DstBlend]
        ZWrite Off
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            struct Vertex
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float2 offset : TEXCOORD1;
                float4 color : COLOR;
            };
            struct Pixel
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };
            Pixel vert(Vertex input)
            {
                Pixel output;
                float3 viewCenter = UnityObjectToViewPos(input.vertex.xyz);
                output.position = mul(UNITY_MATRIX_P, float4(viewCenter.xy + input.offset, viewCenter.z, 1));
                output.uv = input.uv;
                output.color = input.color;
                return output;
            }
            float4 frag(Pixel input) : SV_Target { return tex2D(_MainTex, input.uv) * input.color; }
            ENDCG
        }
    }
}
