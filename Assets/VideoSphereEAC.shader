Shader "Custom/VideoSphereEAC"
{
    // Renders a YouTube Equi-Angular Cubemap (EAC) 360 video onto the inside of a sphere.
    //
    // YouTube packs 360 into a 3x2 atlas of cube faces with TWO twists vs a plain cubemap:
    //   1. Each face uses EQUI-ANGULAR sampling: the in-face coordinate is remapped by atan, so equal
    //      pixels cover equal ANGLES (less waste than a linear cubemap). We invert that with tan() here.
    //   2. The bottom row of faces is rotated 90 degrees relative to the top row.
    //
    // Atlas layout (YouTube's standard EAC):
    //   Top row    (v in 0.5..1.0):  [ Left ][ Front ][ Right ]
    //   Bottom row (v in 0.0..0.5):  [ Down ][ Back  ][ Up    ]   (each rotated 90 deg)
    //
    // We sample by the fragment's DIRECTION from the sphere centre (object-space position), NOT by the
    // mesh's baked equirect UVs — so the same inward sphere mesh is reused; its UVs are ignored here.
    Properties
    {
        _MainTex ("Video Texture (EAC atlas)", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Background" }
        Cull Off
        ZWrite Off

        Pass
        {
            Name "UnlitEAC"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 dirOS : TEXCOORD0; // direction from sphere centre to this fragment (object space)
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                // The sphere is centred at the origin in object space, so the vertex position IS the
                // outward direction; interpolated across the triangle it gives the per-pixel view dir.
                OUT.dirOS = IN.positionOS.xyz;
                return OUT;
            }

            // EAC inverse remap: face-local coord in [-1,1] (equi-angular) -> linear cube coord in [-1,1].
            // Forward EAC packs linear c as a = atan(c) * 4/PI; we invert with c = tan(a * PI/4).
            float EacUnwarp(float a)
            {
                return tan(a * (3.14159265 / 4.0));
            }

            // Given a face-local linear (u,v) in [-1,1] plus the atlas cell origin, return the atlas UV.
            // tileOrigin is the bottom-left of the cell in 0..1 atlas space; cells are 1/3 wide, 1/2 tall.
            float2 CellUV(float2 local, float2 tileOrigin)
            {
                float2 cell = (local * 0.5 + 0.5);            // [-1,1] -> [0,1] within the face
                return tileOrigin + float2(cell.x / 3.0, cell.y / 2.0);
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 d = normalize(IN.dirOS);
                float3 ad = abs(d);

                float2 tileOrigin; // bottom-left of the chosen cell in atlas space
                float2 local;      // equi-angular face coords in [-1,1] BEFORE unwarp
                bool bottomRow = false;

                // Pick the dominant axis = the cube face the direction points at.
                if (ad.x >= ad.y && ad.x >= ad.z)
                {
                    if (d.x > 0.0) { // +X = Right (top row, cell 2)
                        local = float2(-d.z, d.y) / ad.x;
                        tileOrigin = float2(2.0/3.0, 0.5);
                    } else {         // -X = Left  (top row, cell 0)
                        local = float2(d.z, d.y) / ad.x;
                        tileOrigin = float2(0.0, 0.5);
                    }
                }
                else if (ad.y >= ad.x && ad.y >= ad.z)
                {
                    bottomRow = true;
                    if (d.y > 0.0) { // +Y = Up   (bottom row, cell 2)
                        local = float2(d.x, -d.z) / ad.y;
                        tileOrigin = float2(2.0/3.0, 0.0);
                    } else {         // -Y = Down (bottom row, cell 0)
                        local = float2(d.x, d.z) / ad.y;
                        tileOrigin = float2(0.0, 0.0);
                    }
                }
                else
                {
                    if (d.z > 0.0) { // +Z = Front (top row, cell 1)
                        local = float2(d.x, d.y) / ad.z;
                        tileOrigin = float2(1.0/3.0, 0.5);
                    } else {         // -Z = Back  (bottom row, cell 1)
                        bottomRow = true;
                        local = float2(-d.x, d.y) / ad.z;
                        tileOrigin = float2(1.0/3.0, 0.0);
                    }
                }

                // Equi-angular -> linear within the face.
                local = float2(EacUnwarp(local.x), EacUnwarp(local.y));

                // Bottom-row faces are stored rotated 90 deg clockwise; rotate our coords to match.
                if (bottomRow)
                {
                    local = float2(local.y, -local.x);
                }

                float2 uv = CellUV(local, tileOrigin);
                return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
