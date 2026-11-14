Shader "Custom/2D/Wind2D"
{
    // Streaks of wind-blown dust across an orthographic 2D scene, for the Strong Wind weather. Built on the
    // same baked noise as Custom/2D/Fog2D (Tools/Fog2D/Bake Noise Texture), but where Fog2D moves its noise
    // by speed * time, here the distance travelled is accumulated on the CPU (Wind2D.cs) and passed in as
    // _WindOffset. That lets the wind speed up, die down and reverse without the pattern jumping. Streaks
    // are stretched along a fixed axis, so reversing the wind never re-orients them either.
    // Values are driven per-instance by Wind2D (MaterialPropertyBlock), already blended between its
    // breeze and gust presets.
    Properties
    {
        [HideInInspector] _MainTex ("Unused", 2D) = "white" {}
        [NoScaleOffset] _NoiseTex ("Baked Noise (R detail, GB swirl, A banks)", 2D) = "gray" {}
        [Toggle(FOG2D_LOW)] _LowQuality ("Low Quality (no swirl, one layer)", Float) = 0

        [Header(Color)]
        _Color ("Color (light)", Color) = (0.86,0.89,0.92,1)
        _ShadowColor ("Color (dense core)", Color) = (0.60,0.65,0.70,1)

        [Header(Amount)]
        _Density ("Density", Range(0,1)) = 0.3
        _Coverage ("Coverage", Range(0,1)) = 0.35
        _Softness ("Edge Softness", Range(0.01,0.5)) = 0.25

        [Header(Motion)]
        _WindAxis ("Streak Axis (world XY)", Vector) = (1,0,0,0)
        _WindOffset ("Distance Travelled (world XY, set by Wind2D)", Vector) = (0,0,0,0)
        _Layer2Speed ("Layer 2 Speed Mult", Range(0,3)) = 1.6
        _Stretch ("Stretch Along Axis", Range(1,8)) = 4
        _Evolve ("Shape Evolve Speed", Float) = 0.05

        [Header(Shape)]
        _NoiseScale ("Puff Size (world units)", Float) = 1.2
        _Layer2Scale ("Layer 2 Scale Mult", Range(0.25,4)) = 1.8
        _Layer2Weight ("Layer 2 Weight", Range(0,1)) = 0.5
        _Warp ("Turbulence", Range(0,2)) = 0.3

        [Header(Banks)]
        _BankSize ("Bank Size (world units)", Float) = 6
        _BankAmount ("Bank Gaps", Range(0,1)) = 0.6

        [Header(Clear Zone)]
        _ClearCenter ("Clear Center (world XY)", Vector) = (1,-1,0,0)
        _ClearSize ("Clear Radii (world XY)", Vector) = (2.2,2.2,0,0)
        _ClearFeather ("Clear Feather", Range(0.01,1)) = 0.6
        _ClearStrength ("Clear Strength", Range(0,1)) = 0.6

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

            // Must match Fog2DNoiseBaker (same texture as Fog2D).
            #define DETAIL_CELLS 8.0
            #define BANK_CELLS   4.0

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color  : COLOR;
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                float4 color    : COLOR;
                float2 worldPos : TEXCOORD0;
            };

            sampler2D _NoiseTex;
            fixed4 _Color;
            fixed4 _ShadowColor;
            float  _Density;
            float  _Coverage;
            float  _Softness;
            float4 _WindAxis;
            float4 _WindOffset;
            float  _Layer2Speed;
            float  _Stretch;
            float  _Evolve;
            float  _NoiseScale;
            float  _Layer2Scale;
            float  _Layer2Weight;
            float  _Warp;
            float  _BankSize;
            float  _BankAmount;
            float4 _ClearCenter;
            float4 _ClearSize;
            float  _ClearFeather;
            float  _ClearStrength;
            float  _PixelSize;
            float  _Steps;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex   = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xy;
                o.color    = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 wp = i.worldPos;
                if (_PixelSize > 0) wp = (floor(wp / _PixelSize) + 0.5) * _PixelSize;

                float t = _Time.y;

                // Axis frame: x runs along the streaks, y across them. The offset is in world space, so it is
                // projected into the same frame; its sign (which way the wind blows) never flips the frame.
                float2 axis = normalize(_WindAxis.xy + float2(1e-5, 0));
                float2 perp = float2(-axis.y, axis.x);
                float2 local  = float2(dot(wp, axis), dot(wp, perp));
                float2 travel = float2(dot(_WindOffset.xy, axis), dot(_WindOffset.xy, perp));

                float2 stretch = float2(max(_Stretch, 1.0), 1.0);
                float2 tile    = max(_NoiseScale, 1e-3) * stretch * DETAIL_CELLS;   // world units per tile
                float2 uv1     = (local - travel) / tile;

                float n;
            #ifdef FOG2D_LOW
                n = tex2D(_NoiseTex, uv1).r;
            #else
                // Turbulence rides with layer 1 and slowly evolves, so the streaks curl as they travel.
                fixed2 q = tex2D(_NoiseTex, uv1 * 0.5 + float2(0.37, 0.21) * _Evolve * t).gb;
                float2 warp = (q - 0.5) * 2.0 * _Warp / DETAIL_CELLS;

                float2 uv2 = (local - travel * _Layer2Speed) / tile * _Layer2Scale + float2(0.31, 0.67);
                float n1 = tex2D(_NoiseTex, uv1 + warp).r;
                float n2 = tex2D(_NoiseTex, uv2 + warp * _Layer2Scale).r;
                n = lerp(n1, n2, _Layer2Weight);
            #endif

                float threshold = 1.0 - _Coverage;
                float dust = smoothstep(threshold - _Softness, threshold + _Softness, n);

                // Banks: large masses with gaps between them, so the direction of travel reads.
                float2 uvb = (local - travel) / (max(_BankSize, 1e-3) * stretch * BANK_CELLS) + 0.71;
                float bank = smoothstep(0.35, 0.65, tex2D(_NoiseTex, uvb).a);
                dust *= lerp(1.0, bank, _BankAmount);

                float alpha = saturate(dust * _Density);

                // Elliptical clear zone over the board, so the wind never hides gameplay.
                float d = length((wp - _ClearCenter.xy) / max(_ClearSize.xy, 1e-3));
                alpha *= lerp(1.0, smoothstep(1.0 - _ClearFeather, 1.0, d), _ClearStrength);

                if (_Steps >= 1) alpha = floor(alpha * _Steps + 0.5) / _Steps;

                fixed3 rgb = lerp(_Color.rgb, _ShadowColor.rgb, saturate(dust * n));
                fixed4 c = fixed4(rgb, alpha * _Color.a) * i.color;
                c.rgb *= c.a;
                return c;
            }
            ENDCG
        }
    }
}
