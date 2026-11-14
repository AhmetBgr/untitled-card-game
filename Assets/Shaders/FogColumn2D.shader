Shader "Custom/2D/FogColumn2D"
{
    // A band of fog filling its quad (e.g. one board column), built from the same baked noise and wind
    // model as Custom/2D/Fog2D so the two read as the same weather. Instead of Fog2D's clear zone and screen
    // vignette, the fog fades out toward the quad's edges, and those edges are broken up by the noise so the
    // band has wispy sides rather than a ruled line. A base haze keeps the whole band visibly fogged even
    // between puffs. Values are normally driven per-instance by FogColumn2D (MaterialPropertyBlock).
    Properties
    {
        [HideInInspector] _MainTex ("Unused", 2D) = "white" {}
        [NoScaleOffset] _NoiseTex ("Baked Noise (R detail, GB swirl, A banks)", 2D) = "gray" {}
        [Toggle(FOG2D_LOW)] _LowQuality ("Low Quality (no swirl, one layer)", Float) = 0

        [Header(Color)]
        _Color ("Fog Color (light)", Color) = (0.62,0.68,0.70,1)
        _ShadowColor ("Fog Color (dense core)", Color) = (0.30,0.35,0.37,1)

        [Header(Amount)]
        _Density ("Density", Range(0,1)) = 0.6
        _Haze ("Base Haze", Range(0,1)) = 0.3
        _Coverage ("Coverage", Range(0,1)) = 0.55
        _Softness ("Edge Softness", Range(0.01,0.5)) = 0.3

        [Header(Wind)]
        _WindDir ("Wind Direction (world XY)", Vector) = (0,1,0,0)
        _WindSpeed ("Wind Speed (world u/s)", Float) = 0.35
        _Layer2Speed ("Layer 2 Speed Mult", Range(0,3)) = 1.5
        _Stretch ("Stretch Along Wind", Range(1,4)) = 1.6
        _GustStrength ("Gust Strength", Range(0,1)) = 0.3
        _GustFrequency ("Gusts Per Second", Float) = 0.15
        _Evolve ("Shape Evolve Speed", Float) = 0.03

        [Header(Shape)]
        _NoiseScale ("Puff Size (world units)", Float) = 0.8
        _Layer2Scale ("Layer 2 Scale Mult", Range(0.25,4)) = 1.8
        _Layer2Weight ("Layer 2 Weight", Range(0,1)) = 0.5
        _Warp ("Turbulence", Range(0,2)) = 0.5

        [Header(Band Edges)]
        _SideFeather ("Side Fade (fraction of width)", Range(0.01,0.5)) = 0.3
        _EndFeather ("End Fade (fraction of height)", Range(0.01,0.5)) = 0.12
        _EdgeNoise ("Edge Raggedness", Range(0,1)) = 0.5

        [Header(Pixel Art)]
        _PixelSize ("Pixel Snap (world units, 0 = off)", Float) = 0
        _Steps ("Alpha Steps (0 = smooth)", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue"           = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType"      = "Transparent"
            "PreviewType"     = "Plane"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend One OneMinusSrcAlpha   // premultiplied alpha, same as Sprites-Default

        Pass
        {
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma shader_feature_local _ FOG2D_LOW
            #include "UnityCG.cginc"

            #define TAU 6.28318530718
            // Must match Fog2DNoiseBaker (same texture as Fog2D).
            #define DETAIL_CELLS 8.0
            #define BANK_CELLS   4.0

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color  : COLOR;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                float4 color    : COLOR;
                float2 uv       : TEXCOORD0;
                float2 worldPos : TEXCOORD1;
            };

            sampler2D _NoiseTex;
            fixed4 _Color;
            fixed4 _ShadowColor;
            float  _Density;
            float  _Haze;
            float  _Coverage;
            float  _Softness;
            float4 _WindDir;
            float  _WindSpeed;
            float  _Layer2Speed;
            float  _Stretch;
            float  _GustStrength;
            float  _GustFrequency;
            float  _Evolve;
            float  _NoiseScale;
            float  _Layer2Scale;
            float  _Layer2Weight;
            float  _Warp;
            float  _SideFeather;
            float  _EndFeather;
            float  _EdgeNoise;
            float  _PixelSize;
            float  _Steps;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex   = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xy;
                o.uv       = v.uv;
                o.color    = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 wp = i.worldPos;
                if (_PixelSize > 0) wp = (floor(wp / _PixelSize) + 0.5) * _PixelSize;

                float t = _Time.y;

                // Wind frame: x runs along the wind, y across it. Same motion model as Fog2D.
                float2 dir  = normalize(_WindDir.xy + float2(1e-5, 0));
                float2 perp = float2(-dir.y, dir.x);
                float2 local = float2(dot(wp, dir), dot(wp, perp));

                float w = TAU * _GustFrequency;
                float travel = _WindSpeed * (t + _GustStrength * sin(w * t) / max(w, 1e-4));

                float2 stretch = float2(max(_Stretch, 1.0), 1.0);
                float2 tile    = max(_NoiseScale, 1e-3) * stretch * DETAIL_CELLS;
                float2 uv1     = (local - float2(travel, 0)) / tile;

                float n;
            #ifdef FOG2D_LOW
                n = tex2D(_NoiseTex, uv1).r;
            #else
                fixed2 q = tex2D(_NoiseTex, uv1 * 0.5 + float2(0.37, 0.21) * _Evolve * t).gb;
                float2 warp = (q - 0.5) * 2.0 * _Warp / DETAIL_CELLS;

                float2 uv2 = (local - float2(travel * _Layer2Speed, 0)) / tile * _Layer2Scale + float2(0.31, 0.67);
                float n1 = tex2D(_NoiseTex, uv1 + warp).r;
                float n2 = tex2D(_NoiseTex, uv2 + warp * _Layer2Scale).r;
                n = lerp(n1, n2, _Layer2Weight);
            #endif

                float threshold = 1.0 - _Coverage;
                float fog = smoothstep(threshold - _Softness, threshold + _Softness, n);

                // Banks travel with the wind and thin the fog in places, so its drift reads.
                float2 uvb = (local - float2(travel, 0)) / (max(_NoiseScale * 3.0, 1e-3) * stretch * BANK_CELLS) + 0.71;
                float bank = smoothstep(0.25, 0.75, tex2D(_NoiseTex, uvb).a);
                fog *= lerp(1.0, bank, 0.4);

                // Band mask: fade toward each edge of the quad. The noise pushes the edge in and out, so the
                // sides are ragged wisps that move with the fog rather than a straight cut.
                float rag = (n - 0.5) * _EdgeNoise;
                float sideDist = min(i.uv.x, 1.0 - i.uv.x) + rag * _SideFeather;
                float endDist  = min(i.uv.y, 1.0 - i.uv.y) + rag * _EndFeather;
                float mask = smoothstep(0.0, _SideFeather, sideDist) * smoothstep(0.0, _EndFeather, endDist);

                float alpha = saturate(_Haze + fog * _Density) * mask;

                if (_Steps >= 1) alpha = floor(alpha * _Steps + 0.5) / _Steps;

                fixed3 rgb = lerp(_Color.rgb, _ShadowColor.rgb, saturate(fog * n));
                fixed4 c = fixed4(rgb, alpha * _Color.a) * i.color;
                c.rgb *= c.a;
                return c;
            }
            ENDCG
        }
    }
}
