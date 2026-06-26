Shader "Custom/VideoSphereStereo"
{
    Properties
    {
        _MainTex ("Video Texture", 2D) = "black" {}
        _Layout ("Layout: 1=OverUnder, 2=SideBySide", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Background" }
        Cull Off
        ZWrite Off

        Pass
        {
            Name "UnlitStereoVideo"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float _Layout;

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                float2 uv = IN.uv;
                bool rightEye = (unity_StereoEyeIndex == 1);

                if (_Layout < 1.5)
                {
                    // Over-under / top-bottom stereo: top half = left eye, bottom half = right eye.
                    uv.y = rightEye ? uv.y * 0.5 : 0.5 + uv.y * 0.5;
                }
                else
                {
                    // Side-by-side stereo: left half = left eye, right half = right eye.
                    uv.x = rightEye ? 0.5 + uv.x * 0.5 : uv.x * 0.5;
                }

                return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
