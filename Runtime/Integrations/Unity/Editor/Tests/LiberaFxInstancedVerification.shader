Shader "Hidden/LiberaFx/Verification/InstancedUnlit"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
        _ZWrite ("Z Write", Float) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Cull [_Cull]
        ZWrite [_ZWrite]
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            float4 _Color;

            struct VertexInput
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct FragmentInput
            {
                float4 vertex : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            FragmentInput vert(VertexInput input)
            {
                FragmentInput output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                output.vertex = UnityObjectToClipPos(input.vertex);
                return output;
            }

            fixed4 frag(FragmentInput input) : SV_Target
            {
                return _Color;
            }
            ENDCG
        }
    }
}
