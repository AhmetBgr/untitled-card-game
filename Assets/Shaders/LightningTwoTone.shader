Shader "Hidden/LightningTwoTone"
{
    // Full-screen pass for LightningTwoToneFeature: remaps the camera image to two colours by brightness.
    // Globals (_LTT_*) are set by LightningFlash2D through LightningTwoToneFeature.Set.
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off Cull Off ZTest Always

        Pass
        {
            Name "LightningTwoTone"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float  _LTT_Strength;
            float4 _LTT_Thresholds;   // x = near threshold, y = far threshold, z = radius (viewport heights), w = aspect
            float4 _LTT_Center;      // xy = strike point (viewport uv), z = 1: only inside the radius, w = edge falloff (0..1 of radius)
            float4 _LTT_Light;
            float4 _LTT_Dark;
            float  _LTT_Invert;
            float4 _LTT_Alpha;       // x = min strength (edge and beyond), y = max strength (strike point)

            half4 Frag (Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half4 c = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, input.texcoord);

                // Threshold rises with distance from the strike: more of the scene lights up near the bolt.
                float2 dv = (input.texcoord - _LTT_Center.xy) * float2(_LTT_Thresholds.w, 1.0);
                float d = saturate(length(dv) / _LTT_Thresholds.z);
                float threshold = lerp(_LTT_Thresholds.x, _LTT_Thresholds.y, d);

                // Perceived brightness of the (display-encoded) colour.
                float3 srgb = LinearToSRGB(saturate(c.rgb));
                float lum = dot(srgb, float3(0.299, 0.587, 0.114));
                float lit = step(threshold, lum);
                lit = lerp(lit, 1.0 - lit, _LTT_Invert);

                float3 tone = lerp(_LTT_Dark.rgb, _LTT_Light.rgb, lit);
                // Local mode: fade out toward the edge of the radius, untouched beyond it.
                float edge = 1.0 - saturate(_LTT_Center.w);
                float fade = d >= 1.0 ? 0.0 : (_LTT_Center.w <= 0.0 ? 1.0 : 1.0 - smoothstep(edge, 1.0, d));
                float mask = lerp(_LTT_Alpha.y, lerp(_LTT_Alpha.x, _LTT_Alpha.y, fade), _LTT_Center.z);
                return half4(lerp(c.rgb, tone, _LTT_Strength * mask), c.a);
            }
            ENDHLSL
        }
    }
}
