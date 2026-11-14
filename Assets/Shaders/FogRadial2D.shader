Shader "Custom/2D/FogRadial2D"
{
    // A swirling ring of fog/dust centred on its quad (e.g. a sandstorm circling the board), built from the
    // same baked noise as Custom/2D/Fog2D and FogColumn2D so the three read as one family. The noise is read
    // in polar coordinates around the quad's centre, so the puffs circle the centre and can spiral in or out
    // instead of drifting in a straight line. The fog fades out past the outer radius and thins inside the
    // inner radius (the "eye"), and both edges are broken up by the noise. Values are normally driven
    // per-instance by FogRadial2D (MaterialPropertyBlock).
    Properties
    {
        [HideInInspector] _MainTex ("Unused", 2D) = "white" {}
        [NoScaleOffset] _NoiseTex ("Baked Noise (R detail, GB swirl, A banks)", 2D) = "gray" {}
        [Toggle(FOG2D_LOW)] _LowQuality ("Low Quality (no swirl, one layer)", Float) = 0

        [Header(Color)]
        _Color ("Fog Color (light)", Color) = (0.90,0.76,0.52,1)
        _ShadowColor ("Fog Color (dense core)", Color) = (0.62,0.45,0.26,1)

        [Header(Amount)]
        _Density ("Density", Range(0,1)) = 0.6
        _Haze ("Base Haze", Range(0,1)) = 0.2
        _Coverage ("Coverage", Range(0,1)) = 0.55
        _Softness ("Edge Softness", Range(0.01,0.5)) = 0.3

        [Header(Swirl)]
        _SwirlSpeed ("Swirl Speed (turns per second, - = clockwise)", Float) = 0.03
        _InflowSpeed ("Inflow Speed (world u/s, - = outward)", Float) = 0.15
        _Twist ("Spiral Twist (turns per world unit)", Float) = 0.08
        _Layer2Speed ("Layer 2 Speed Mult", Range(0,3)) = 1.5
        _GustStrength ("Gust Strength", Range(0,1)) = 0.3
        _GustFrequency ("Gusts Per Second", Float) = 0.15
        _Evolve ("Shape Evolve Speed", Float) = 0.03

        [Header(Shape)]
        _NoiseScale ("Puff Size (world units, radial)", Float) = 0.8
        _ArcRepeats ("Arc Repeats (x8 puffs around the ring)", Float) = 3
        _Layer2Scale ("Layer 2 Scale Mult", Range(0.25,4)) = 1.8
        _Layer2Weight ("Layer 2 Weight", Range(0,1)) = 0.5
        _Warp ("Turbulence", Range(0,2)) = 0.5

        [Header(Ring)]
        _Radii ("Outer Radii (world units XY)", Vector) = (4,4,0,0)
        _OuterFeather ("Outer Fade (fraction of radius)", Range(0.01,1)) = 0.35
        _InnerRadius ("Eye Radius (fraction of radius)", Range(0,1)) = 0.4
        _InnerFeather ("Eye Fade (fraction of radius)", Range(0.01,1)) = 0.3
        _InnerOpacity ("Eye Opacity", Range(0,1)) = 0.2
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

            // Polar UVs jump from 1 back to 0 where the angle wraps, which would make the GPU pick the
            // smallest mip along that line and draw a visible seam. Mip 0 only: the puffs are large, so the
            // lost filtering doesn't show.
            #define NOISE(uv) tex2Dlod(_NoiseTex, float4((uv), 0, 0))

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
                float2 localPos : TEXCOORD0;   // relative to the ring's centre, in world units
            };

            sampler2D _NoiseTex;
            fixed4 _Color;
            fixed4 _ShadowColor;
            float  _Density;
            float  _Haze;
            float  _Coverage;
            float  _Softness;
            float  _SwirlSpeed;
            float  _InflowSpeed;
            float  _Twist;
            float  _Layer2Speed;
            float  _GustStrength;
            float  _GustFrequency;
            float  _Evolve;
            float  _NoiseScale;
            float  _ArcRepeats;
            float  _Layer2Scale;
            float  _Layer2Weight;
            float  _Warp;
            float4 _Radii;
            float  _OuterFeather;
            float  _InnerRadius;
            float  _InnerFeather;
            float  _InnerOpacity;
            float  _EdgeNoise;
            float  _PixelSize;
            float  _Steps;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                float2 world  = mul(unity_ObjectToWorld, v.vertex).xy;
                float2 centre = mul(unity_ObjectToWorld, float4(0, 0, 0, 1)).xy;
                o.localPos = world - centre;
                o.color    = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 p = i.localPos;
                if (_PixelSize > 0) p = (floor(p / _PixelSize) + 0.5) * _PixelSize;

                float t = _Time.y;

                // Elliptical polar frame: rn is 1 on the outer radius, theta runs 0..1 around the ring.
                float2 radii = max(_Radii.xy, 1e-3);
                float2 e     = p / radii;
                float  rn    = length(e);
                float  theta = atan2(e.y, e.x) / TAU + 0.5;
                float  r     = rn * 0.5 * (radii.x + radii.y);   // world-ish distance from the centre

                // Gusts speed the whole storm up and slow it down; integrated so the motion never lurches.
                float w    = TAU * _GustFrequency;
                float gust = t + _GustStrength * sin(w * t) / max(w, 1e-4);

                // u wraps around the ring (integer repeats keep the wrap seamless), v runs outward.
                float  repeats = max(1.0, round(_ArcRepeats));
                float  radialTile = max(_NoiseScale, 1e-3) * DETAIL_CELLS;
                float2 uv1 = float2((theta + r * _Twist + _SwirlSpeed * gust) * repeats,
                                    (r + _InflowSpeed * gust) / radialTile);

                float n;
            #ifdef FOG2D_LOW
                n = NOISE(uv1).r;
            #else
                // The swirl field is read at half scale, but its arc count must stay a whole number too, or
                // the angle wrap lands mid-tile and draws a seam out from the centre.
                float  swirlRepeats = max(1.0, round(repeats * 0.5));
                float2 uvq = float2(uv1.x / repeats * swirlRepeats, uv1.y * 0.5);
                fixed2 q = NOISE(uvq + float2(0.37, 0.21) * _Evolve * t).gb;
                float2 warp = (q - 0.5) * 2.0 * _Warp / DETAIL_CELLS;

                // Layer 2 swirls at its own speed for depth. Its arc count stays an integer so it wraps too.
                float  repeats2 = max(1.0, round(repeats * _Layer2Scale));
                float2 uv2 = float2((theta + r * _Twist + _SwirlSpeed * _Layer2Speed * gust) * repeats2,
                                    (r + _InflowSpeed * _Layer2Speed * gust) / radialTile * _Layer2Scale)
                             + float2(0.31, 0.67);
                float n1 = NOISE(uv1 + warp).r;
                float n2 = NOISE(uv2 + warp * _Layer2Scale).r;
                n = lerp(n1, n2, _Layer2Weight);
            #endif

                float threshold = 1.0 - _Coverage;
                float fog = smoothstep(threshold - _Softness, threshold + _Softness, n);

                // Banks: big masses that circle with the storm and thin it in places, so the swirl reads.
                float2 uvb = float2(uv1.x / repeats, uv1.y * DETAIL_CELLS / (3.0 * BANK_CELLS)) + 0.71;
                float bank = smoothstep(0.25, 0.75, NOISE(uvb).a);
                fog *= lerp(1.0, bank, 0.4);

                // Ring mask: fade out past the outer radius and down to the eye's opacity inside the inner
                // radius. The noise pushes both edges in and out so they are ragged wisps, not circles.
                float rag   = (n - 0.5) * _EdgeNoise;
                float outer = 1.0 - smoothstep(1.0 - _OuterFeather, 1.0, rn - rag * _OuterFeather);
                float inner = smoothstep(_InnerRadius, _InnerRadius + _InnerFeather, rn + rag * _InnerFeather);
                float mask  = outer * lerp(_InnerOpacity, 1.0, inner);

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
